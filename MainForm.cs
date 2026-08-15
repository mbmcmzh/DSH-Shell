using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DSHShell
{
    public sealed class MainForm : Form
    {
        // ---- 窗口消息 ----
        private const int WM_NCCALCSIZE = 0x0083;
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_NCMOUSEMOVE = 0x00A0;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_NCLBUTTONUP = 0x00A2;
        private const int WM_NCLBUTTONDBLCLK = 0x00A3;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_NCMOUSELEAVE = 0x02A2;

        // ---- 命中测试码 ----
        private const int HTNOWHERE = 0;
        private const int HTCLIENT = 1;
        private const int HTCAPTION = 2;
        private const int HTMINBUTTON = 8;
        private const int HTMAXBUTTON = 9;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTCLOSE = 20;

        private const int SM_CYSIZEFRAME = 33;
        private const int SM_CXPADDEDBORDER = 92;

        private const int TME_LEAVE = 0x0002;
        private const int TME_NONCLIENT = 0x0010;

        private const int SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_FRAMECHANGED = 0x0020;

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        // ---- 标题栏尺寸（逻辑像素，Windows 11 标准：栏高 32，按钮 46×32）----
        private const int TitleBarDip = 32;
        private const int CaptionButtonDip = 46;
        private const int GlyphSizeDip = 10;
        private const int ResizeBorderDip = 6;
        private const int ResizeCornerDip = 14;

        // DSW 设计令牌（浅色/深色），与 dsh-web 的 design-platform.css 对齐
        private static readonly Color LightBg = Color.FromArgb(255, 255, 255);          // --dsw-static-neutral-bluish-00
        private static readonly Color DarkBg = Color.FromArgb(21, 21, 23);              // --dsw-static-neutral-bluish-950
        // 标题栏字形：活动窗口用近黑/近白，非活动窗口变淡（与系统标题栏一致）
        private static readonly Color LightGlyph = Color.FromArgb(26, 26, 26);
        private static readonly Color DarkGlyph = Color.FromArgb(232, 234, 237);
        private static readonly Color LightGlyphIdle = Color.FromArgb(140, 143, 148);
        private static readonly Color DarkGlyphIdle = Color.FromArgb(118, 122, 127);
        // 悬停/按下叠加色（半透明，直接叠在页面底色上）
        private static readonly Color LightHover = Color.FromArgb(20, 0, 0, 0);
        private static readonly Color LightPress = Color.FromArgb(38, 0, 0, 0);
        private static readonly Color DarkHover = Color.FromArgb(24, 255, 255, 255);
        private static readonly Color DarkPress = Color.FromArgb(40, 255, 255, 255);
        private static readonly Color CloseHover = Color.FromArgb(196, 43, 28);         // Windows 11 关闭红 #C42B1C
        private static readonly Color ClosePress = Color.FromArgb(176, 39, 25);

        private string WebUrl => $"http://127.0.0.1:{_server.Port}/";
        private const string AppTitle = "DeepSeek Harness";

        private readonly string[] _args;
        private readonly ServerManager _server = new ServerManager();
        private readonly WebView2 _webView = new WebView2 { Dock = DockStyle.Fill };
        private readonly Panel _splash;
        private readonly Label _splashStatus;
        private readonly NotifyIcon _tray = new NotifyIcon();
        private readonly bool _testMode;
        private bool _reallyExit;
        private bool _dark;

        // 标题栏交互状态
        private Font _glyphFont;
        private int _hotButton = HTNOWHERE;
        private int _pressedButton = HTNOWHERE;
        private bool _ncTracking;
        private bool _windowActive = true;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TRACKMOUSEEVENT
        {
            public int cbSize;
            public int dwFlags;
            public IntPtr hwndTrack;
            public int dwHoverTime;
        }

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, int flags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

        public MainForm(string[] args)
        {
            _args = args;
            _testMode = Array.IndexOf(args, "--test-quit-after") >= 0;
            Text = AppTitle;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1280, 840);
            MinimumSize = new Size(840, 560);
            BackColor = LightBg;
            DoubleBuffered = true;

            // 客户区顶部让出一条标题栏；网页与启动画面都排在它下面（Dock 会尊重 Padding）
            Padding = new Padding(0, TitleBarHeight, 0, 0);
            _glyphFont = CreateGlyphFont();

            _webView.DefaultBackgroundColor = LightBg;
            Controls.Add(_webView);

            _splash = BuildSplash(out _splashStatus);
            Controls.Add(_splash);
            _splash.BringToFront();

            BuildTray();
            RestoreWindowBounds();
            ApplyTheme(_dark);
        }

        // ---------- 启动流程 ----------

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                await StartServerAsync();
                await InitWebViewAsync();
                HandleArgs();
            }
            catch (Exception ex)
            {
                HideSplash();
                if (_testMode)
                {
                    // 自测模式：静默退出，便于自动化验证（退出时仍会回收本程序拉起的服务）。
                    try { File.AppendAllText(ServerManager.LogFilePath, $"[DSH-Shell] startup failed: {ex}\r\n"); }
                    catch { }
                    _reallyExit = true;
                    Close();
                    return;
                }
                MessageBox.Show(this,
                    "DeepSeek Harness 启动失败：\r\n\r\n" + ex.Message +
                    "\r\n\r\n日志文件：" + ServerManager.LogFilePath,
                    AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                _reallyExit = true;
                Close();
            }
        }

        private async Task StartServerAsync()
        {
            if (_server.IsPortOpen())
            {
                SetSplash("检测到后台服务已在运行，直接连接…");
                return;
            }

            SetSplash("正在启动后台服务（dsh web）…");
            _server.Start(ServerManager.LogFilePath);

            var sw = Stopwatch.StartNew();
            while (true)
            {
                if (_server.IsPortOpen())
                {
                    SetSplash("后台服务已就绪，正在加载界面…");
                    return;
                }
                if (_server.HasExited)
                {
                    // 进程没了不等于服务没起来：再探一次端口，确认之后才报错。
                    if (_server.IsPortOpen())
                    {
                        SetSplash("后台服务已就绪，正在加载界面…");
                        return;
                    }
                    throw new Exception(_server.DescribeEarlyExit());
                }
                if (sw.Elapsed > TimeSpan.FromSeconds(120))
                    throw new Exception("等待后台服务就绪超时（120 秒），请查看日志：" + ServerManager.LogFilePath);
                SetSplash($"正在等待后台服务就绪…（{sw.Elapsed.TotalSeconds:0} 秒）");
                await Task.Delay(500);
            }
        }

        private async Task InitWebViewAsync()
        {
            var userData = Path.Combine(ServerManager.DataDir, "WebView2");
            try
            {
                CoreWebView2EnvironmentOptions options = null;
                // 仅在受限测试环境（沙箱）下放宽浏览器进程限制；正常使用不带任何额外参数。
                if (Environment.GetEnvironmentVariable("DSH_SHELL_SANDBOX_ARGS") == "1")
                    options = new CoreWebView2EnvironmentOptions("--no-sandbox --disable-gpu --disable-crash-reporter");

                var env = await CoreWebView2Environment.CreateAsync(null, userData, options);
                await EnsureWebViewWithTimeout(env);
            }
            catch
            {
                // 自定义数据目录不可用时退回默认目录。
                await EnsureWebViewWithTimeout(null);
            }

            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;

            // 页面主题桥：跟随 body[data-ds-dark-theme] 切换悬浮按钮/拖拽条配色
            _webView.WebMessageReceived += (sender, args) =>
            {
                try { ApplyTheme(args.TryGetWebMessageAsString() == "dark"); }
                catch { }
            };
            await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ThemeBridgeScript);

            // 页面里的外部链接（如搜索来源）交给系统默认浏览器打开，不抢当前窗口。
            _webView.CoreWebView2.NewWindowRequested += (sender, args) =>
            {
                args.Handled = true;
                try { Process.Start(new ProcessStartInfo(args.Uri) { UseShellExecute = true }); }
                catch { }
            };

            _webView.NavigationCompleted += (sender, args) => HideSplash();

            _webView.Source = new Uri(WebUrl);
        }

        /// <summary>初始化 WebView2，带 45 秒超时保护（任何环境下都不允许无限挂起）。</summary>
        private async Task EnsureWebViewWithTimeout(CoreWebView2Environment env)
        {
            var initTask = _webView.EnsureCoreWebView2Async(env);
            var done = await Task.WhenAny(initTask, Task.Delay(TimeSpan.FromSeconds(45)));
            if (done != initTask)
                throw new TimeoutException("WebView2 初始化超时（45 秒）");
            await initTask; // 观察并抛出真正的初始化异常
        }

        private const string ThemeBridgeScript = @"
(() => {
  const send = () => {
    try {
      if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) {
        window.chrome.webview.postMessage(document.body.hasAttribute('data-ds-dark-theme') ? 'dark' : 'light')
      }
    } catch {}
  }
  send()
  try {
    new MutationObserver(send).observe(document.body, { attributes: true, attributeFilter: ['data-ds-dark-theme'] })
  } catch {}
})()";

        // ---------- 窗口骨架：原生框架 + 自绘标题栏 ----------
        //
        // 做法与 Edge / VS Code 一致：保留系统窗口框架（WS_CAPTION/THICKFRAME/SYSMENU 都在），
        // 只在 WM_NCCALCSIZE 里把客户区顶边扩展到窗口顶边，把标题栏“借”过来自己画。
        // 这样原生阴影、Windows 11 平滑圆角、贴靠布局、系统菜单、双击最大化全部保留。

        private int Dip(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

        private int TitleBarHeight => Dip(TitleBarDip);
        private int CaptionButtonWidth => Dip(CaptionButtonDip);

        /// <summary>最大化时窗口会外溢一圈边框厚度，客户区顶边要补回来，否则标题栏被切掉。</summary>
        private int FrameThickness => GetSystemMetrics(SM_CYSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);

        private Rectangle CaptionBarRect => new Rectangle(0, 0, ClientSize.Width, TitleBarHeight);
        private Rectangle CloseButtonRect => CaptionButtonAt(0);
        private Rectangle MaxButtonRect => CaptionButtonAt(1);
        private Rectangle MinButtonRect => CaptionButtonAt(2);

        /// <summary>从右往左第 index 个按钮：0=关闭 1=最大化 2=最小化（即左→右 ─ ❐ ✕）。</summary>
        private Rectangle CaptionButtonAt(int index)
        {
            var w = CaptionButtonWidth;
            return new Rectangle(ClientSize.Width - w * (index + 1), 0, w, TitleBarHeight);
        }

        private static bool IsCaptionButton(int hit) =>
            hit == HTMINBUTTON || hit == HTMAXBUTTON || hit == HTCLOSE;

        /// <summary>标题栏字形用系统自带的图标字体，和真正的 Windows 标题栏逐像素一致。</summary>
        private Font CreateGlyphFont()
        {
            foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            {
                try
                {
                    using (new FontFamily(name)) { }   // 字体不存在会抛异常
                    return new Font(name, Dip(GlyphSizeDip), GraphicsUnit.Pixel);
                }
                catch { }
            }
            return null;   // 两个都没有时退回手绘（见 DrawFallbackGlyph）
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Padding = new Padding(0, TitleBarHeight, 0, 0);
            TrySetDwmAttribute(DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);
            ApplyTheme(_dark);
            // 让系统按新的 WM_NCCALCSIZE 结果重算一次窗口框架
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
        }

        private void TrySetDwmAttribute(int attribute, int value)
        {
            if (!IsHandleCreated) return;
            try { DwmSetWindowAttribute(Handle, attribute, ref value, sizeof(int)); }
            catch { }   // 老系统不认这些属性，忽略即可
        }

        private void ApplyTheme(bool dark)
        {
            _dark = dark;
            var bg = dark ? DarkBg : LightBg;
            BackColor = bg;
            try { _webView.DefaultBackgroundColor = bg; }
            catch { }
            TrySetDwmAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE, dark ? 1 : 0);   // 窗口描边跟着换深浅
            InvalidateCaption();
        }

        private void InvalidateCaption()
        {
            if (IsHandleCreated) Invalidate(CaptionBarRect, false);
        }

        private void ToggleMaximize()
        {
            WindowState = WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal
                : FormWindowState.Maximized;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            InvalidateCaption();   // 按钮靠右、最大化/还原字形会变，尺寸一变就重画
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            _windowActive = true;
            InvalidateCaption();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            _windowActive = false;
            _pressedButton = HTNOWHERE;
            _hotButton = HTNOWHERE;
            InvalidateCaption();
        }

        // ---------- 标题栏：消息处理 ----------

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_NCCALCSIZE:
                    if (m.WParam != IntPtr.Zero)
                    {
                        // 先让系统算出默认非客户区（左/右/下缩放边框保持原生），再把顶边还原，
                        // 客户区就一路顶到窗口顶边，系统标题栏随之消失。
                        var before = Marshal.PtrToStructure<RECT>(m.LParam);
                        base.WndProc(ref m);
                        var after = Marshal.PtrToStructure<RECT>(m.LParam);
                        after.Top = IsZoomed(Handle) ? before.Top + FrameThickness : before.Top;
                        Marshal.StructureToPtr(after, m.LParam, false);
                        m.Result = IntPtr.Zero;
                        return;
                    }
                    break;

                case WM_NCHITTEST:
                {
                    base.WndProc(ref m);
                    if ((int)m.Result != HTCLIENT) return;   // 系统判定的边框/角，保持原生

                    var pt = PointToClient(new Point(
                        unchecked((short)(long)m.LParam),
                        unchecked((short)((long)m.LParam >> 16))));

                    // 顶边缩放带：上边框现在归客户区管，得自己报 HTTOP
                    if (!IsZoomed(Handle) && pt.Y >= 0 && pt.Y < Dip(ResizeBorderDip))
                    {
                        var corner = Dip(ResizeCornerDip);
                        m.Result = (IntPtr)(pt.X < corner ? HTTOPLEFT
                            : pt.X >= ClientSize.Width - corner ? HTTOPRIGHT
                            : HTTOP);
                        return;
                    }

                    if (pt.Y >= 0 && pt.Y < TitleBarHeight)
                    {
                        // 报出 HTMINBUTTON/HTMAXBUTTON/HTCLOSE，就能白拿系统提示气泡；
                        // 其中 HTMAXBUTTON 是 Windows 11 贴靠布局浮层的触发条件。
                        if (CloseButtonRect.Contains(pt)) m.Result = (IntPtr)HTCLOSE;
                        else if (MaxButtonRect.Contains(pt)) m.Result = (IntPtr)(MaximizeBox ? HTMAXBUTTON : HTCAPTION);
                        else if (MinButtonRect.Contains(pt)) m.Result = (IntPtr)HTMINBUTTON;
                        else m.Result = (IntPtr)HTCAPTION;   // 其余整条都能拖动/双击最大化/右键系统菜单
                    }
                    return;
                }

                case WM_NCMOUSEMOVE:
                    SetHotButton((int)m.WParam);
                    TrackNcMouseLeave();
                    break;   // 继续交给系统：贴靠布局浮层依赖它

                case WM_NCMOUSELEAVE:
                    _ncTracking = false;
                    SetHotButton(HTNOWHERE);
                    break;

                case WM_MOUSEMOVE:
                    SetHotButton(HTNOWHERE);
                    break;

                case WM_NCLBUTTONDOWN:
                case WM_NCLBUTTONDBLCLK:
                    if (IsCaptionButton((int)m.WParam))
                    {
                        _pressedButton = (int)m.WParam;
                        _hotButton = _pressedButton;
                        InvalidateCaption();
                        m.Result = IntPtr.Zero;
                        return;   // 自己管按下态，避免和系统默认绘制打架
                    }
                    break;

                case WM_NCLBUTTONUP:
                    if (IsCaptionButton((int)m.WParam))
                    {
                        var hit = (int)m.WParam;
                        var wasPressed = _pressedButton;
                        _pressedButton = HTNOWHERE;
                        InvalidateCaption();
                        m.Result = IntPtr.Zero;
                        if (wasPressed == hit) InvokeCaptionAction(hit);
                        return;
                    }
                    _pressedButton = HTNOWHERE;
                    break;

                case WM_LBUTTONUP:
                    if (_pressedButton != HTNOWHERE)
                    {
                        _pressedButton = HTNOWHERE;
                        InvalidateCaption();
                    }
                    break;
            }

            base.WndProc(ref m);
        }

        private void SetHotButton(int hit)
        {
            if (!IsCaptionButton(hit)) hit = HTNOWHERE;
            if (_hotButton == hit) return;
            _hotButton = hit;
            InvalidateCaption();
        }

        /// <summary>订阅一次 WM_NCMOUSELEAVE，指针离开标题栏时才能清掉悬停高亮。</summary>
        private void TrackNcMouseLeave()
        {
            if (_ncTracking || !IsHandleCreated) return;
            var tme = new TRACKMOUSEEVENT
            {
                cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(),
                dwFlags = TME_LEAVE | TME_NONCLIENT,
                hwndTrack = Handle,
                dwHoverTime = 0
            };
            _ncTracking = TrackMouseEvent(ref tme);
        }

        private void InvokeCaptionAction(int hit)
        {
            switch (hit)
            {
                case HTMINBUTTON: WindowState = FormWindowState.Minimized; break;
                case HTMAXBUTTON: ToggleMaximize(); break;
                case HTCLOSE: Close(); break;   // 走 OnFormClosing → 收到托盘，和 Alt+F4 行为一致
            }
        }

        // ---------- 托盘与窗口行为 ----------

        private void BuildTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示 DeepSeek Harness", null, (sender, e) => ShowFromTray());
            menu.Items.Add("在浏览器中打开", null, (sender, e) => OpenInBrowser());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (sender, e) => ReallyExit());

            _tray.Icon = Icon;
            _tray.Text = AppTitle + " — 正在运行";
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (sender, e) => ShowFromTray();
            _tray.Visible = true;
        }

        public void ShowFromTray()
        {
            if (IsDisposed) return;
            ShowInTaskbar = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        private static string TrayTipFlagPath => Path.Combine(ServerManager.DataDir, "tray-tip-shown");

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
            // 托盘提示：整个生命周期只弹一次（跨启动持久化标记文件）
            if (!File.Exists(TrayTipFlagPath))
            {
                try { File.WriteAllText(TrayTipFlagPath, "1"); }
                catch { }
                _tray.BalloonTipTitle = AppTitle;
                _tray.BalloonTipText = "已最小化到系统托盘，任务仍在继续运行。\r\n双击托盘图标即可重新打开窗口。";
                _tray.ShowBalloonTip(3000);
            }
        }

        private void OpenInBrowser()
        {
            try { Process.Start(new ProcessStartInfo(WebUrl) { UseShellExecute = true }); }
            catch { }
        }

        private void ReallyExit()
        {
            var msg = _server.StartedByUs
                ? "确定要退出 DeepSeek Harness 吗？\r\n\r\n本程序启动的后台服务将一并关闭（正在进行的任务会被终止）。"
                : "确定要退出 DeepSeek Harness 吗？\r\n\r\n（后台服务由其他进程托管，不会被关闭。）";
            if (MessageBox.Show(this, msg, AppTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
            _reallyExit = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveWindowBounds();
            if (!_reallyExit)
            {
                // 关闭只隐藏到托盘；真正的退出走托盘菜单“退出”。
                e.Cancel = true;
                HideToTray();
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _server.Stop(); // 只收掉本程序启动的服务
            base.OnFormClosed(e);
        }

        // ---------- 启动画面（DSW 浅色风格） ----------

        private static Panel BuildSplash(out Label status)
        {
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = LightBg };
            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                ColumnCount = 1,
                RowCount = 6
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 42f));
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 58f));

            var iconBox = new PictureBox
            {
                Image = Icon.ExtractAssociatedIcon(Application.ExecutablePath)?.ToBitmap(),
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(96, 96),
                Margin = new Padding(0, 0, 0, 18),
                Anchor = AnchorStyles.None
            };
            var title = new Label
            {
                Text = AppTitle,
                AutoSize = true,
                Anchor = AnchorStyles.None,
                Font = new Font("Segoe UI", 18f, FontStyle.Bold),
                ForeColor = Color.FromArgb(15, 17, 21)   // --dsw-alias-label-primary (light)
            };
            status = new Label
            {
                Text = "正在启动后台服务…",
                AutoSize = false,
                Size = new Size(440, 30),
                Anchor = AnchorStyles.None,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = Color.FromArgb(129, 133, 140) // --dsw-alias-label-tertiary (light)
            };
            var bar = new ProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 26,
                Size = new Size(300, 10),
                Anchor = AnchorStyles.None
            };

            table.Controls.Add(iconBox, 0, 1);
            table.Controls.Add(title, 0, 2);
            table.Controls.Add(status, 0, 3);
            table.Controls.Add(bar, 0, 4);
            panel.Controls.Add(table);
            return panel;
        }

        private void SetSplash(string text)
        {
            if (_splashStatus != null && !_splashStatus.IsDisposed)
                _splashStatus.Text = text;
        }

        private void HideSplash()
        {
            if (_splash == null || _splash.IsDisposed) return;
            _splash.Dispose();
        }

        // ---------- 窗口位置记忆 ----------

        private void RestoreWindowBounds()
        {
            try
            {
                var path = Path.Combine(ServerManager.DataDir, "window.json");
                if (!File.Exists(path)) return;
                var parts = File.ReadAllText(path).Split('|');
                if (parts.Length != 5) return;

                var maximized = parts[0] == "Maximized";
                var left = int.Parse(parts[1]);
                var top = int.Parse(parts[2]);
                var width = Math.Max(int.Parse(parts[3]), MinimumSize.Width);
                var height = Math.Max(int.Parse(parts[4]), MinimumSize.Height);
                var rect = new Rectangle(left, top, width, height);

                var visible = false;
                foreach (var screen in Screen.AllScreens)
                    if (screen.WorkingArea.IntersectsWith(rect)) { visible = true; break; }
                if (!visible) return;

                if (maximized) WindowState = FormWindowState.Maximized;
                else { StartPosition = FormStartPosition.Manual; Bounds = rect; }
            }
            catch { }
        }

        private void SaveWindowBounds()
        {
            try
            {
                var state = WindowState == FormWindowState.Maximized ? "Maximized" : "Normal";
                var rect = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                File.WriteAllText(
                    Path.Combine(ServerManager.DataDir, "window.json"),
                    $"{state}|{rect.Left}|{rect.Top}|{rect.Width}|{rect.Height}");
            }
            catch { }
        }

        // ---------- 自测钩子（正常使用请忽略） ----------

        /// <summary>
        /// 仅用于自动化验证：DSH.exe --test-quit-after &lt;秒&gt;
        /// 启动完成后延迟指定秒数走“真正退出”路径（连同收掉自己拉起的服务）。
        /// </summary>
        private void HandleArgs()
        {
            for (var i = 0; i < _args.Length - 1; i++)
            {
                if (_args[i] != "--test-quit-after") continue;
                if (!double.TryParse(_args[i + 1], out var seconds)) continue;
                var delay = TimeSpan.FromSeconds(seconds);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(delay);
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            _reallyExit = true;
                            Close();
                        }));
                    }
                    catch { }
                });
                break;
            }
        }

        // ---------- 标题栏：绘制 ----------

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!e.ClipRectangle.IntersectsWith(CaptionBarRect)) return;

            DrawCaptionButton(e.Graphics, MinButtonRect, HTMINBUTTON);
            DrawCaptionButton(e.Graphics, MaxButtonRect, HTMAXBUTTON);
            DrawCaptionButton(e.Graphics, CloseButtonRect, HTCLOSE);
        }

        private void DrawCaptionButton(Graphics g, Rectangle rect, int id)
        {
            var pressed = _pressedButton == id && _hotButton == id;
            var hot = _pressedButton == HTNOWHERE ? _hotButton == id : pressed;

            // Windows 11 规格：整块方形填充、贴边无间隙、关闭键悬停变红
            if (pressed || hot)
            {
                Color fill;
                if (id == HTCLOSE) fill = pressed ? ClosePress : CloseHover;
                else if (pressed) fill = _dark ? DarkPress : LightPress;
                else fill = _dark ? DarkHover : LightHover;

                using (var brush = new SolidBrush(fill))
                    g.FillRectangle(brush, rect);
            }

            Color glyphColor;
            if (id == HTCLOSE && (hot || pressed)) glyphColor = Color.White;
            else if (_windowActive) glyphColor = _dark ? DarkGlyph : LightGlyph;
            else glyphColor = _dark ? DarkGlyphIdle : LightGlyphIdle;

            if (_glyphFont != null)
            {
                // Segoe Fluent Icons 码位：ChromeMinimize / ChromeMaximize / ChromeRestore / ChromeClose
                var glyph = id == HTMINBUTTON ? ""
                    : id == HTMAXBUTTON ? (WindowState == FormWindowState.Maximized ? "" : "")
                    : "";
                TextRenderer.DrawText(g, glyph, _glyphFont, rect, glyphColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
            else
            {
                DrawFallbackGlyph(g, rect, id, glyphColor);
            }
        }

        /// <summary>系统图标字体缺失时的兜底手绘（开抗锯齿，避免斜线发毛）。</summary>
        private void DrawFallbackGlyph(Graphics g, Rectangle rect, int id, Color color)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var s = Dip(GlyphSizeDip);
            var cx = rect.X + rect.Width / 2f;
            var cy = rect.Y + rect.Height / 2f;
            var r = s / 2f;

            using (var pen = new Pen(color, Math.Max(1f, Dip(1))))
            {
                switch (id)
                {
                    case HTMINBUTTON:
                        g.DrawLine(pen, cx - r, cy, cx + r, cy);
                        break;
                    case HTMAXBUTTON:
                        if (WindowState == FormWindowState.Maximized)
                        {
                            g.DrawRectangle(pen, cx - r + 2, cy - r, s - 2, s - 2);
                            g.DrawRectangle(pen, cx - r, cy - r + 2, s - 2, s - 2);
                        }
                        else
                        {
                            g.DrawRectangle(pen, cx - r, cy - r, s, s);
                        }
                        break;
                    case HTCLOSE:
                        g.DrawLine(pen, cx - r, cy - r, cx + r, cy + r);
                        g.DrawLine(pen, cx - r, cy + r, cx + r, cy - r);
                        break;
                }
            }
            g.SmoothingMode = old;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _glyphFont?.Dispose();
            base.Dispose(disposing);
        }
    }
}
