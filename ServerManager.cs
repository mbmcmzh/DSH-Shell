using System;
using System.Collections.Generic;
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
    /// 服务跑在 Windows 还是 WSL 里由 <see cref="AppSettings.LaunchMode"/> 决定。
    /// </summary>
    public sealed class ServerManager
    {
        private const string Host = "127.0.0.1";
        private const int DefaultPort = 3080;

        /// <summary>不带 BOM 的 UTF-8：读 WSL 侧输出用（BOM 会变成日志行首的怪字符）。</summary>
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private readonly AppSettings _settings;
        private Process _proc;
        private StreamWriter _log;
        private string _commandLine = string.Empty;
        private readonly StringBuilder _tail = new StringBuilder();

        public ServerManager(AppSettings settings)
        {
            // 存一份快照：设置对话框改动的是同一个对象，而后端跑在哪儿是启动那一刻定死的
            _settings = (settings ?? new AppSettings()).Clone();
        }

        /// <summary>本次会话使用的启动方式（设置改动要重启才生效，所以这里读一次就够）。</summary>
        public LaunchMode Mode => _settings.LaunchMode;

        /// <summary>服务端口；默认 3080，可用环境变量 DSH_SHELL_PORT 覆盖（仅测试用）。</summary>
        public static int Port { get; } = int.TryParse(Environment.GetEnvironmentVariable("DSH_SHELL_PORT"), out var p) ? p : DefaultPort;

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
        /// 等端口彻底关掉（最多 timeout）。重启换启动方式时用：
        /// 新实例起来得太快会探到还没咽气的旧服务，然后连上一个即将消失的后端。
        /// </summary>
        public void WaitForPortClosed(TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (!IsPortOpen()) return;
                Thread.Sleep(100);
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

        /// <summary>固定取 System32\wsl.exe：本程序是 x64，不涉及 Sysnative 重定向。未装 WSL 时返回 null。</summary>
        private static string ResolveWslLauncher()
        {
            try
            {
                var candidate = Path.Combine(Environment.SystemDirectory, "wsl.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
            return null;
        }

        /// <summary>Windows 侧：cmd 调 PATH 里的 dsh。</summary>
        private static ProcessStartInfo BuildWindowsStartInfo()
        {
            var launcher = ResolveDshLauncher();
            if (launcher == null)
                throw new Exception(
                    "在 PATH 里找不到 dsh，请确认已通过 npm 全局安装 @deepseek-ai/dsh；" +
                    "若 dsh 装在 WSL 里，请在设置里把启动方式改成 WSL。");

            return new ProcessStartInfo { FileName = "cmd.exe", Arguments = BuildWindowsArguments(launcher) };
        }

        // /s + 整体外层引号：cmd 会剥掉首尾引号后原样执行，路径带空格也安全
        // --no-open：新版 dsh web 默认拉起系统默认浏览器，本壳用内嵌 WebView2，不需要它再开浏览器
        private static string BuildWindowsArguments(string launcher) =>
            $"/s /c \"\"{launcher}\" web --no-open --host {Host} --port {Port}\"";

        /// <summary>
        /// WSL 侧：wsl.exe 里跑 Linux 的 dsh。服务仍然绑 127.0.0.1，
        /// 靠 WSL 的 localhost 转发从 Windows 访问——不用 0.0.0.0，免得把端口暴露到网络上。
        /// </summary>
        private ProcessStartInfo BuildWslStartInfo()
        {
            var wsl = ResolveWslLauncher();
            if (wsl == null)
                throw new Exception(
                    "找不到 wsl.exe：本机没有安装 WSL。请先安装（管理员 PowerShell 执行 wsl --install），" +
                    "或在设置里把启动方式改回 Windows。");

            var psi = new ProcessStartInfo { FileName = wsl, Arguments = BuildWslArguments(_settings) };
            // wsl.exe 自己的提示在重定向时默认是 UTF-16LE，混进 Linux 侧的 UTF-8 输出里就是一片乱码；
            // WSL_UTF8=1 让它也输出 UTF-8，日志和报错才是人能读的。
            psi.Environment["WSL_UTF8"] = "1";
            psi.StandardOutputEncoding = Utf8NoBom;
            psi.StandardErrorEncoding = Utf8NoBom;
            return psi;
        }

        private static string BuildWslArguments(AppSettings settings)
        {
            var distro = (settings.WslDistro ?? string.Empty).Trim();
            var args = new StringBuilder();
            // 发行版名不能加引号：wsl.exe 不按 argv 规则剥引号，-d "Ubuntu" 会被当成带引号的名字，
            // 直接报 WSL_E_DISTRO_NOT_FOUND（名字里带空格的发行版因此也没法指定，只能用默认项）
            if (distro.Length > 0) args.Append("-d ").Append(distro).Append(' ');
            // --cd ~：不指定的话工作目录会是本程序所在的 Windows 目录（/mnt/c/...），
            // dsh 会拿它当项目根，而且 9p 跨文件系统访问很慢
            args.Append("--cd ~ ");
            // -lic：登录 + 交互式 shell 才会读 ~/.bashrc，nvm 装的 node/dsh 全靠这一步才在 PATH 里；
            // exec：让 dsh 顶掉 bash 本身，wsl.exe 被收掉时信号直达 dsh；
            // --no-open：新版 dsh web 默认拉起系统默认浏览器，壳内已有 WebView2，不需要它再开
            args.Append("-e /bin/bash -lic \"exec dsh web --no-open --host ").Append(Host)
                .Append(" --port ").Append(Port).Append('"');
            return args.ToString();
        }

        /// <summary>
        /// 某份设置会执行的命令，给设置对话框做预览用——和真正启动共用同一套拼装，
        /// 所以预览里出现“找不到 dsh / 没装 WSL”时，按下确定后也一定会失败。
        /// </summary>
        public static string DescribeCommand(AppSettings settings)
        {
            if (settings != null && settings.LaunchMode == LaunchMode.Wsl)
            {
                return ResolveWslLauncher() == null
                    ? "找不到 wsl.exe：本机未安装 WSL"
                    : "wsl.exe " + BuildWslArguments(settings);
            }

            var launcher = ResolveDshLauncher();
            return launcher == null
                ? "在 PATH 里找不到 dsh"
                : "cmd.exe " + BuildWindowsArguments(launcher);
        }


        /// <summary>
        /// 以完全隐藏的方式启动 dsh web（无控制台窗口）。
        /// 输出由本进程接管重定向后写入日志：cmd 自带的 "> 文件" 在父进程没有控制台时，
        /// 子进程一旦早退就什么都留不下（曾出现过“秒退 + 0 字节日志”的无线索故障）。
        /// </summary>
        public void Start(string logPath)
        {
            if (_proc != null) return;

            var psi = _settings.LaunchMode == LaunchMode.Wsl ? BuildWslStartInfo() : BuildWindowsStartInfo();
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            // 给子进程一个正常的 stdin 管道，而不是继承 GUI 进程的无效句柄（保持打开，别让它读到 EOF）
            psi.RedirectStandardInput = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            _commandLine = psi.FileName + " " + psi.Arguments;

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

            // 日志第一行永远是这次的启动命令：换了启动方式之后排查全靠它
            try { if (_log != null) _log.WriteLine("[DSH-Shell] " + _commandLine); }
            catch { }

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
                throw new Exception("无法启动 dsh web（" + TroubleshootHint + "）：" + ex.Message, ex);
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

        /// <summary>按当前启动方式给出的排查提示，拼进各种失败消息里。</summary>
        public string TroubleshootHint => Mode == LaunchMode.Wsl
            ? "请确认所选 WSL 发行版里能直接跑 dsh（Node 20+，且 dsh 装在该发行版里而不是 Windows 侧）"
            : "请确认 Windows 上能直接跑 dsh（npm install -g @deepseek-ai/dsh）";

        /// <summary>服务进程早退时，拼一条能直接看出原因的说明（结论 + 启动命令 + 它自己的输出）。</summary>
        public string DescribeEarlyExit()
        {
            var code = "未知";
            try { if (_proc != null) code = _proc.ExitCode.ToString(); }
            catch { }

            string output;
            lock (_tail) output = _tail.ToString().Trim();

            return "dsh web 启动后立即退出（退出码 " + code + "）。\r\n\r\n" +
                   Diagnose(output) +
                   "\r\n\r\n启动命令：\r\n" + _commandLine +
                   "\r\n\r\n它的输出：\r\n" +
                   (output.Length == 0 ? "（没有任何输出）" : output) +
                   "\r\n\r\n完整日志：" + LogFilePath;
        }

        /// <summary>
        /// 从它自己的输出里判断到底卡在哪一步。
        /// 摸不准就别乱指路——“请先安装 dsh”这种话对已经装好的人只会帮倒忙。
        /// </summary>
        private string Diagnose(string output)
        {
            if (output.IndexOf("WSL_E_DISTRO_NOT_FOUND", StringComparison.OrdinalIgnoreCase) >= 0 ||
                output.Contains("不存在具有所提供名称的分发"))
                return "WSL 里没有设置中选的这个发行版，去设置里重新选一个。";

            if (output.Contains("parseEnv") ||
                output.IndexOf("Unsupported engine", StringComparison.OrdinalIgnoreCase) >= 0 ||
                output.Contains("SyntaxError"))
                return "dsh 已经找到并跑起来了，卡在 Node 版本上——dsh 需要 Node 20 以上。" + WslToolReport();

            if (output.Contains("command not found") || output.Contains("未找到命令") ||
                output.Contains("No such file or directory") || output.Contains("不是内部或外部命令"))
                return Mode == LaunchMode.Wsl
                    ? "该发行版的登录 shell 里没有 dsh 这个命令。" + WslToolReport()
                    : "PATH 里的 dsh 没能执行起来，检查 npm 全局目录是否还在 PATH 上。";

            if (output.Contains("EADDRINUSE") || output.Contains("address already in use"))
                return "端口 " + Port + " 被别的程序占着，dsh 起不来。";

            return TroubleshootHint + "。" + WslToolReport();
        }

        /// <summary>
        /// 去所选发行版里实地问一句 dsh 与 node 是什么——“我明明装了”的时候，
        /// 这两行往往直接点破真相（比如 dsh 解析到了 /mnt/c 下 Windows 的那份）。
        /// </summary>
        private string WslToolReport()
        {
            if (Mode != LaunchMode.Wsl) return string.Empty;

            var wsl = ResolveWslLauncher();
            if (wsl == null) return string.Empty;

            var distro = (_settings.WslDistro ?? string.Empty).Trim();
            var args = (distro.Length > 0 ? "-d " + distro + " " : "") +
                       "-e /bin/bash -lic \"command -v dsh || echo '(没有)'; node -v || echo '(没有)'\"";

            var lines = new List<string>();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = wsl,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Utf8NoBom,
                    StandardErrorEncoding = Utf8NoBom,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                psi.Environment["WSL_UTF8"] = "1";

                using (var proc = new Process { StartInfo = psi })
                {
                    proc.OutputDataReceived += (s, e) =>
                    {
                        if (string.IsNullOrWhiteSpace(e.Data)) return;
                        lock (lines) lines.Add(e.Data.Trim());
                    };
                    proc.ErrorDataReceived += (s, e) => { };
                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    if (!proc.WaitForExit(6000))
                    {
                        try { proc.Kill(entireProcessTree: true); } catch { }
                        return string.Empty;
                    }
                    proc.WaitForExit();
                }
            }
            catch
            {
                return string.Empty;
            }

            if (lines.Count < 2) return string.Empty;

            var dshPath = lines[0];
            var nodeVersion = lines[1];
            var report = "\r\n\r\n该发行版里实测：dsh = " + dshPath + "，node = " + nodeVersion;
            // /mnt/ 开头说明它找到的是 Windows 那份（走 interop PATH 混进来的），不是 WSL 里装的
            if (dshPath.StartsWith("/mnt/", StringComparison.Ordinal))
                report += "\r\n注意：这个 dsh 是 Windows 里那份（经 /mnt 映射进来的），并不是装在 WSL 里的；" +
                          "在该发行版里执行 npm install -g @deepseek-ai/dsh 才是它自己的。";
            return report;
        }

        /// <summary>
        /// 列出可用的 WSL 发行版（供设置里的下拉框用）；没装 WSL 或查询失败时返回空列表。
        /// wsl.exe 重定向时默认吐 UTF-16LE，靠 WSL_UTF8=1 + UTF-8 解码才能拿到干净的名字。
        /// </summary>
        public static List<string> ListWslDistros()
        {
            var names = new List<string>();
            var wsl = ResolveWslLauncher();
            if (wsl == null) return names;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = wsl,
                    Arguments = "--list --quiet",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Utf8NoBom,
                    StandardErrorEncoding = Utf8NoBom,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                psi.Environment["WSL_UTF8"] = "1";

                using (var proc = new Process { StartInfo = psi })
                {
                    // 用事件收输出而不是 ReadToEnd：后者在 UI 线程上等两条管道容易互相卡死
                    proc.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data == null) return;
                        var name = e.Data.Trim().Trim('\0');
                        if (name.Length == 0) return;
                        // docker-desktop 是 Docker Desktop 的工具发行版，跑不了 dsh，别摆出来误导人
                        if (name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase)) return;
                        lock (names) names.Add(name);
                    };
                    proc.ErrorDataReceived += (s, e) => { };   // 排掉 wsl.exe 的提示，别把管道写满
                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    if (!proc.WaitForExit(5000))
                    {
                        try { proc.Kill(entireProcessTree: true); } catch { }
                        lock (names) names.Clear();
                        return names;
                    }
                    proc.WaitForExit();   // 等异步读取真正收尾，否则可能少最后一行
                }
            }
            catch
            {
                lock (names) names.Clear();
            }
            return names;
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
