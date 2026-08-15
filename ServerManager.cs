using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace DSHShell
{
    /// <summary>
    /// 负责后台 dsh web 服务的生命周期：
    /// 端口已占用则直接复用（绝不误杀别人的服务）；否则无窗口拉起服务，
    /// 并且只在“本程序启动的”情况下才在退出时整棵进程树收掉。
    /// </summary>
    public sealed class ServerManager
    {
        private const string Host = "127.0.0.1";
        private const int DefaultPort = 3080;

        private Process _proc;
        private StreamWriter _log;
        private readonly StringBuilder _tail = new StringBuilder();

        /// <summary>服务端口；默认 3080，可用环境变量 DSH_SHELL_PORT 覆盖（仅测试用）。</summary>
        public int Port { get; } = int.TryParse(Environment.GetEnvironmentVariable("DSH_SHELL_PORT"), out var p) ? p : DefaultPort;

        /// <summary>应用数据目录（%LOCALAPPDATA%\DeepSeekHarness，失败时回退到 %TEMP%）。</summary>
        public static string DataDir { get; } = TryCreateDataDir();

        public static string LogFilePath => Path.Combine(DataDir, "dsh-web.log");

        /// <summary>后台服务是否由本程序启动（决定退出时是否收掉它）。</summary>
        public bool StartedByUs => _proc != null;

        /// <summary>拉起的服务进程是否已退出。</summary>
        public bool HasExited => _proc != null && _proc.HasExited;

        /// <summary>端口是否已有服务在监听（400ms 探测）。</summary>
        public bool IsPortOpen()
        {
            try
            {
                using (var client = new TcpClient())
                using (var cts = new CancellationTokenSource(400))
                {
                    var task = client.ConnectAsync(Host, Port);
                    if (!task.Wait(400, cts.Token)) return false;
                    return client.Connected;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 在 PATH 里解析出 dsh 启动器的全路径。
        /// 必须用全路径：cmd 解析裸命令时会**先查当前目录**，而本程序自己就叫 DSH.exe，
        /// 从安装目录启动时 "dsh" 会命中 DSH.exe 自己——单实例逻辑让它秒退且零输出，
        /// 表现为“dsh web 启动后立即退出 + 空日志”，极难排查。
        /// </summary>
        private static string ResolveDshLauncher()
        {
            var exts = new[] { ".cmd", ".bat", ".exe" };
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

            foreach (var raw in path.Split(Path.PathSeparator))
            {
                var dir = raw.Trim().Trim('"');
                if (dir.Length == 0) continue;
                foreach (var ext in exts)
                {
                    try
                    {
                        var candidate = Path.Combine(dir, "dsh" + ext);
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch { }   // PATH 里可能有非法路径，跳过
                }
            }
            return null;
        }

        /// <summary>
        /// 以完全隐藏的方式启动 dsh web（无控制台窗口）。
        /// 输出由本进程接管重定向后写入日志：cmd 自带的 "> 文件" 在父进程没有控制台时，
        /// 子进程一旦早退就什么都留不下（曾出现过“秒退 + 0 字节日志”的无线索故障）。
        /// </summary>
        public void Start(string logPath)
        {
            if (_proc != null) return;

            var launcher = ResolveDshLauncher();
            if (launcher == null)
                throw new Exception("在 PATH 里找不到 dsh，请确认已通过 npm 全局安装 @deepseek-ai/dsh。");

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                // /s + 整体外层引号：cmd 会剥掉首尾引号后原样执行，路径带空格也安全
                Arguments = $"/s /c \"\"{launcher}\" web --host {Host} --port {Port}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // 给子进程一个正常的 stdin 管道，而不是继承 GUI 进程的无效句柄（保持打开，别让它读到 EOF）
                RedirectStandardInput = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            try
            {
                _log = new StreamWriter(
                    new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                { AutoFlush = true };
            }
            catch
            {
                _log = null;   // 日志写不了不影响服务本身
            }

            try
            {
                _proc = new Process { StartInfo = psi };
                _proc.OutputDataReceived += (s, e) => Capture(e.Data);
                _proc.ErrorDataReceived += (s, e) => Capture(e.Data);
                _proc.Start();
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                _proc = null;
                throw new Exception("无法启动 dsh web（请确认已通过 npm 全局安装 @deepseek-ai/dsh）：" + ex.Message, ex);
            }
        }

        private void Capture(string line)
        {
            if (line == null) return;
            // stdout 与 stderr 的回调在不同线程上，写日志和攒尾巴都要串起来
            lock (_tail)
            {
                try { if (_log != null) _log.WriteLine(line); }
                catch { }

                _tail.AppendLine(line);
                if (_tail.Length > 4000) _tail.Remove(0, _tail.Length - 4000);
            }
        }

        /// <summary>服务进程早退时，拼一条能直接看出原因的说明（退出码 + 它自己的输出）。</summary>
        public string DescribeEarlyExit()
        {
            var code = "未知";
            try { if (_proc != null) code = _proc.ExitCode.ToString(); }
            catch { }

            string output;
            lock (_tail) output = _tail.ToString().Trim();

            return "dsh web 启动后立即退出（退出码 " + code + "）。\r\n\r\n它的输出：\r\n" +
                   (output.Length == 0 ? "（没有任何输出）" : output) +
                   "\r\n\r\n完整日志：" + LogFilePath;
        }

        /// <summary>结束本程序拉起的服务进程树（若服务不是本程序启动的则什么都不做）。</summary>
        public void Stop()
        {
            if (_proc == null) return;

            var proc = _proc;
            _proc = null;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = $"/T /F /PID {proc.Id}",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                var killer = Process.Start(psi);
                if (killer != null) killer.WaitForExit(5000);
            }
            catch { }

            try
            {
                if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            }
            catch { }

            try
            {
                lock (_tail)
                {
                    if (_log != null) { _log.Dispose(); _log = null; }
                }
            }
            catch { }
        }

        private static string TryCreateDataDir()
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekHarness");
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch
            {
                return Path.GetTempPath();
            }
        }
    }
}
