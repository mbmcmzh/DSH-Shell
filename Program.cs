using System;
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
            using (var mutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew))
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

                using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
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
                }
            }
            return 0;
        }
    }
}
