using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace DSHShell
{
    internal static class Program
    {
        // 单实例互斥：重复双击时只唤醒已有窗口，不重复启动。
        private const string SingleInstanceMutexName = @"Local\DeepSeekHarness.Shell.SingleInstance";
        private const string ShowEventName = @"Local\DeepSeekHarness.Shell.ShowWindow";

        [STAThread]
        private static int Main(string[] args)
        {
            var selfTest = Array.IndexOf(args, "--test-quit-after") >= 0;
            var instanceSuffix = selfTest ? ".SelfTest." + Environment.ProcessId : string.Empty;
            var restart = false;
            using (var mutex = new Mutex(true, SingleInstanceMutexName + instanceSuffix, out bool createdNew))
            {
                if (!createdNew)
                {
                    // 已有实例在运行：通知它显示窗口，然后本进程直接退出。
                    try
                    {
                        using (var ev = EventWaitHandle.OpenExisting(ShowEventName))
                            ev.Set();
                    }
                    catch { }
                    return 0;
                }

                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName + instanceSuffix))
                {
                    var form = new MainForm(args);

                    // 监听后续实例发来的“显示窗口”请求。
                    var showThread = new Thread(() =>
                    {
                        while (true)
                        {
                            try
                            {
                                if (!showEvent.WaitOne()) return;
                                form.BeginInvoke(new Action(form.ShowFromTray));
                            }
                            catch
                            {
                                return; // 窗口已销毁，线程退出
                            }
                        }
                    })
                    { IsBackground = true, Name = "DSH-ShowListener" };
                    showThread.Start();

                    Application.Run(form);
                    restart = form.RestartRequested;
                }
            }

            // 换了后端启动方式后的重启：必须等互斥体释放之后再拉新进程，
            // 否则新实例会被单实例逻辑当成“重复双击”，唤一下窗口就退了，程序整个消失。
            if (restart) StartNewInstance(args);
            return 0;
        }

        private static void StartNewInstance(string[] args)
        {
            try
            {
                // 单文件发布下 Environment.ProcessPath 才是真正的 DSH.exe
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) exe = Application.ExecutablePath;

                var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = true };
                foreach (var arg in args) psi.ArgumentList.Add(arg);
                Process.Start(psi);
            }
            catch { }   // 拉不起来就算了：用户手动双击图标即可，设置已经存好了
        }
    }
}
