using System;
using System.Diagnostics;
using System.Drawing;
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
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_NCLBUTTONUP = 0x00A2;
        private const int WM_NCLBUTTONDBLCLK = 0x00A3;
        private const int WM_LBUTTONUP = 0x0202;

        // ---- 命中测试码 ----
        private const int HTNOWHERE = 0;
        private const int HTCLIENT = 1;
        private const int HTCAPTION = 2;
        private const int HTMINBUTTON = 8;
        private const int HTMAXBUTTON = 9;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        /// <summary>系统不处理这个码，正好拿来当“设置”按钮的自定义命中区。</summary>
        private const int HTOBJECT = 19;
        private const int HTCLOSE = 20;

        private const int SM_CYSIZEFRAME = 33;
        private const int SM_CXPADDEDBORDER = 92;

        private const int SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_FRAMECHANGED = 0x0020;

        // ---- 标题栏尺寸（逻辑像素，Windows 11 标准：栏高 32，按钮 46×32）----
        private const int TitleBarDip = 32;
        private const int CaptionButtonDip = 46;
        private const int ResizeBorderDip = 6;
        private const int ResizeCornerDip = 14;

        /// <summary>右上角按钮个数：设置 ─ ❐ ✕（注入脚本里的 captionButtons 必须与之一致）。</summary>
        private const int CaptionButtonCount = 4;

        private string WebUrl => _server.WebUrl;
        private const string AppTitle = "DeepSeek Harness";

        private readonly string[] _args;
        private readonly AppSettings _settings = AppSettings.Load();
        private readonly ServerManager _server;
        private readonly WebView2 _webView = new WebView2 { Dock = DockStyle.Fill };
        private readonly Panel _splash;
        private readonly Label _splashStatus;
        private readonly NotifyIcon _tray = new NotifyIcon();
        private readonly bool _testMode;
        private bool _reallyExit;
        private bool _dark;
        private bool _settingsOpen;

        /// <summary>用户在设置里改了启动方式并选择立即重启；由 Program 在互斥体释放后拉起新实例。</summary>
        public bool RestartRequested { get; private set; }

        // WebView2 非客户区不可用时，顶层窗口命中测试仍可处理三个按钮。
        private int _pressedButton = HTNOWHERE;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, int flags);

        public MainForm(string[] args)
        {
            _args = args;
            _testMode = Array.IndexOf(args, "--test-quit-after") >= 0;
            _server = new ServerManager(_settings);
            Text = AppTitle;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1280, 840);
            MinimumSize = new Size(840, 560);
            BackColor = Theme.LightBg;
            DoubleBuffered = true;

            _webView.DefaultBackgroundColor = Theme.LightBg;
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
                SetSplash("正在获取后台服务的网页入口…");
                await _server.ResolveWebUrlAsync();
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
                // 起不来的常见原因就是 dsh 装在了另一边（Windows / WSL），顺手把设置递到手上
                var answer = MessageBox.Show(this,
                    "DeepSeek Harness 启动失败：\r\n\r\n" + ex.Message +
                    "\r\n\r\n日志文件：" + ServerManager.LogFilePath +
                    "\r\n\r\n当前启动方式：" + (_settings.LaunchMode == LaunchMode.Wsl ? "WSL" : "Windows") +
                    "。要打开设置换一种方式吗？",
                    AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Error);
                if (answer == DialogResult.Yes) OpenSettings();

                // 用户在设置里选了立即重启的话，窗口已经在关了，别再动它
                if (!RestartRequested && !IsDisposed)
                {
                    _reallyExit = true;
                    Close();
                }
            }
        }

        private async Task StartServerAsync()
        {
            if (_server.IsPortOpen())
            {
                SetSplash("检测到后台服务已在运行，直接连接…");
                return;
            }

            SetSplash(_server.Mode == LaunchMode.Wsl
                ? "正在 WSL 中启动后台服务（dsh web）…"
                : "正在启动后台服务（dsh web）…");
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
                    throw new Exception("等待后台服务就绪超时（120 秒）。" +
                        (_server.Mode == LaunchMode.Wsl
                            ? "若服务在 WSL 里其实已经起来了，多半是 WSL 的 localhost 转发没生效"
                              + "（检查 %USERPROFILE%\\.wslconfig 里的 localhostForwarding）。"
                            : "") +
                        "\r\n\r\n请查看日志：" + ServerManager.LogFilePath);
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
            try { _webView.CoreWebView2.Settings.IsNonClientRegionSupportEnabled = true; }
            catch { } // 较老的 WebView2 Runtime 不支持 app-region，窗口按钮仍可正常使用。

            // 页面主题桥：跟随 body[data-ds-dark-theme] 切换悬浮按钮/拖拽条配色
            _webView.WebMessageReceived += (sender, args) =>
            {
                try
                {
                    switch (args.TryGetWebMessageAsString())
                    {
                        case "dark": ApplyTheme(true); break;
                        case "light": ApplyTheme(false); break;
                        case "chrome:minimize": WindowState = FormWindowState.Minimized; break;
                        case "chrome:maximize": ToggleMaximize(); break;
                        case "chrome:close": Close(); break;
                        case "chrome:settings": OpenSettings(); break;
                    }
                }
                catch { }
            };
            await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ThemeBridgeScript);
            await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(DesktopChromeScript);

            // 页面里的外部链接（如搜索来源）交给系统默认浏览器打开，不抢当前窗口。
            _webView.CoreWebView2.NewWindowRequested += (sender, args) =>
            {
                args.Handled = true;
                try { Process.Start(new ProcessStartInfo(args.Uri) { UseShellExecute = true }); }
                catch { }
            };

            _webView.NavigationCompleted += (sender, args) =>
            {
                HideSplash();
                if (args.HttpStatusCode == 401 && !_testMode)
                {
                    // WebView2 回调内不能直接打开模态对话框，先退出回调再进入消息循环。
                    BeginInvoke(new Action(() =>
                    {
                        if (!IsDisposed && !_reallyExit) PromptForWebUrl();
                    }));
                    return;
                }
                PublishWindowState();
            };

            _webView.Source = new Uri(WebUrl);
        }

        private void PromptForWebUrl()
        {
            using var dialog = new Form
            {
                Text = "连接 dsh 后台服务", ClientSize = new Size(580, 180),
                StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false
            };
            var message = new Label
            {
                Text = "当前服务需要认证。请从启动它的终端复制 dsh web: 后的完整地址（含 ?token=...）。\r\n" +
                       "也可以先退出该服务，再重新启动本程序，由本程序自动连接。",
                Location = new Point(16, 16), Size = new Size(548, 60)
            };
            var input = new TextBox { Location = new Point(16, 80), Width = 548 };
            var connect = new Button { Text = "连接", Location = new Point(388, 130), Width = 80 };
            var cancel = new Button { Text = "取消", Location = new Point(484, 130), Width = 80, DialogResult = DialogResult.Cancel };
            connect.Click += (sender, args) =>
            {
                if (!_server.TrySetWebUrl(input.Text))
                {
                    message.Text = $"请输入本机端口 {ServerManager.Port} 的完整 dsh 地址，例如：\r\nhttp://127.0.0.1:{ServerManager.Port}/?token=...";
                    return;
                }
                dialog.DialogResult = DialogResult.OK;
            };
            dialog.Controls.AddRange(new Control[] { message, input, connect, cancel });
            dialog.AcceptButton = connect;
            dialog.CancelButton = cancel;
            if (dialog.ShowDialog(this) == DialogResult.OK)
                _webView.CoreWebView2.Navigate(WebUrl);
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
  const install = () => {
    send()
    try {
      new MutationObserver(send).observe(document.body, { attributes: true, attributeFilter: ['data-ds-dark-theme'] })
    } catch {}
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', install, { once: true })
  else install()
})()";

        // 仅注入桌面壳中的 WebView：页面表面铺到顶边，内容避开拖拽带与右上角窗口按钮。
        // 不依赖 DSH 的类名或构建产物，只使用槽渲染器稳定的 data-slot 锚点。
        private const string DesktopChromeScript = @"
(() => {
  const titleBar = 32
  const captionButtons = 184
  let sidebar = null
  let sidebarClass = ''
  let header = null
  let headerClass = ''
  let resizeObserver = null

  const resetInset = (element, property) => {
    element.style.removeProperty(property)
    const value = Number.parseFloat(getComputedStyle(element).getPropertyValue(property)) || 0
    element.style.setProperty(property, `${value + titleBar}px`)
  }

  const adjust = () => {
    const nextSidebar = document.querySelector('[data-slot=""sidebar""]')?.firstElementChild ?? null
    const nextSidebarClass = nextSidebar?.className ?? ''
    if (nextSidebar instanceof HTMLElement && (nextSidebar !== sidebar || nextSidebarClass !== sidebarClass)) {
      sidebar = nextSidebar
      sidebarClass = nextSidebarClass
      resetInset(sidebar, 'padding-top')
    }

    const nextHeader = document.querySelector('[data-slot=""conversation.session.header""] header')
      ?? document.querySelector('[data-slot=""conversation.session.header""]')?.firstElementChild
      ?? null
    const nextHeaderClass = nextHeader?.className ?? ''
    if (nextHeader instanceof HTMLElement && (nextHeader !== header || nextHeaderClass !== headerClass)) {
      resizeObserver?.disconnect()
      header = nextHeader
      headerClass = nextHeaderClass
      resetInset(header, 'padding-top')
      resizeObserver = new ResizeObserver(adjustHeaderRight)
      resizeObserver.observe(header)
    }
    adjustHeaderRight()
  }

  const adjustHeaderRight = () => {
    if (!(header instanceof HTMLElement)) return
    header.style.removeProperty('padding-right')
    const base = Number.parseFloat(getComputedStyle(header).paddingRight) || 0
    const overlap = Math.max(0, header.getBoundingClientRect().right - (innerWidth - captionButtons))
    header.style.paddingRight = `${base + overlap}px`
  }

  const install = () => {
    if (document.getElementById('dsh-shell-drag-region')) return
    document.documentElement.setAttribute('data-dsh-desktop-shell', '')

    const style = document.createElement('style')
    style.textContent = `
      #dsh-shell-caption-controls {
        position: fixed;
        z-index: 2147483647;
        top: 0;
        right: 0;
        display: grid;
        grid-template-columns: repeat(4, 46px);
        width: 184px;
        height: 32px;
        color: var(--dsw-alias-label-primary, #1a1a1a);
        background: var(--dsw-alias-bg-base, #fff);
        app-region: no-drag;
        -webkit-app-region: no-drag;
      }
      #dsh-shell-caption-controls button {
        display: grid;
        place-items: center;
        width: 46px;
        height: 32px;
        margin: 0;
        padding: 0;
        border: 0;
        border-radius: 0;
        color: inherit;
        background: transparent;
        font-family: 'Segoe Fluent Icons', 'Segoe MDL2 Assets';
        font-size: 10px;
        line-height: 1;
      }
      #dsh-shell-caption-controls button:hover { background: rgb(0 0 0 / 8%); }
      #dsh-shell-caption-controls button:active { background: rgb(0 0 0 / 15%); }
      html:has(body[data-ds-dark-theme]) #dsh-shell-caption-controls button:hover {
        background: rgb(255 255 255 / 10%);
      }
      html:has(body[data-ds-dark-theme]) #dsh-shell-caption-controls button:active {
        background: rgb(255 255 255 / 16%);
      }
      #dsh-shell-caption-controls button[data-action='close']:hover { color: #fff; background: #c42b1c; }
      #dsh-shell-caption-controls button[data-action='close']:active { color: #fff; background: #b02719; }
      /* 齿轮笔画比 ─ ❐ ✕ 细，字号给大一点才配得上旁边的窗口按钮 */
      #dsh-shell-caption-controls button[data-action='settings'] { font-size: 13px; }
    `
    document.head.append(style)

    const controls = document.createElement('div')
    controls.id = 'dsh-shell-caption-controls'
    const definitions = [
      ['settings', '\uE713', '设置'],
      ['minimize', '\uE921', '最小化'],
      ['maximize', '\uE922', '最大化'],
      ['close', '\uE8BB', '关闭'],
    ]
    for (const [action, glyph, label] of definitions) {
      const button = document.createElement('button')
      button.type = 'button'
      button.dataset.action = action
      button.setAttribute('aria-label', label)
      button.title = label
      button.textContent = glyph
      button.addEventListener('click', () => {
        try { window.chrome.webview.postMessage(`chrome:${action}`) } catch {}
      })
      button.addEventListener('dblclick', event => { event.preventDefault(); event.stopPropagation() })
      controls.append(button)
    }
    window.chrome?.webview?.addEventListener('message', event => {
      if (event.data !== 'chrome:maximized' && event.data !== 'chrome:restored') return
      const maximize = controls.querySelector('[data-action=""maximize""]')
      if (!(maximize instanceof HTMLButtonElement)) return
      const maximized = event.data === 'chrome:maximized'
      maximize.textContent = maximized ? '\uE923' : '\uE922'
      maximize.setAttribute('aria-label', maximized ? '还原' : '最大化')
      maximize.title = maximized ? '还原' : '最大化'
    })
    document.body.append(controls)

    const dragRegion = document.createElement('div')
    dragRegion.id = 'dsh-shell-drag-region'
    dragRegion.setAttribute('aria-hidden', 'true')
    Object.assign(dragRegion.style, {
      position: 'fixed',
      zIndex: '2147483646',
      top: '0',
      left: '0',
      right: `${captionButtons}px`,
      height: `${titleBar}px`,
      background: 'transparent',
      userSelect: 'none',
    })
    dragRegion.style.setProperty('app-region', 'drag')
    dragRegion.style.setProperty('-webkit-app-region', 'drag')
    document.body.append(dragRegion)

    new MutationObserver(adjust).observe(document.body, {
      childList: true,
      subtree: true,
      attributes: true,
      attributeFilter: ['class'],
    })
    window.addEventListener('resize', adjustHeaderRight)
    adjust()
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', install, { once: true })
  else install()
})()";

        // ---------- 窗口骨架：原生框架 + WebView 标题栏 ----------
        //
        // 做法与 Edge / VS Code 一致：保留系统窗口框架（WS_CAPTION/THICKFRAME/SYSMENU 都在），
        // 只在 WM_NCCALCSIZE 里把客户区顶边扩展到窗口顶边，标题栏由 WebView 注入层呈现。
        // 这样原生阴影、Windows 11 平滑圆角、贴靠布局、系统菜单、双击最大化全部保留。

        private int Dip(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

        private int TitleBarHeight => Dip(TitleBarDip);
        private int CaptionButtonWidth => Dip(CaptionButtonDip);

        /// <summary>最大化时窗口会外溢一圈边框厚度，客户区顶边要补回来，否则标题栏被切掉。</summary>
        private int FrameThickness => GetSystemMetrics(SM_CYSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);

        private Rectangle CloseButtonRect => CaptionButtonAt(0);
        private Rectangle MaxButtonRect => CaptionButtonAt(1);
        private Rectangle MinButtonRect => CaptionButtonAt(2);
        private Rectangle SettingsButtonRect => CaptionButtonAt(3);

        /// <summary>从右往左第 index 个按钮：0=关闭 1=最大化 2=最小化 3=设置（即左→右 ⚙ ─ ❐ ✕）。</summary>
        private Rectangle CaptionButtonAt(int index)
        {
            var w = CaptionButtonWidth;
            return new Rectangle(ClientSize.Width - w * (index + 1), 0, w, TitleBarHeight);
        }

        private static bool IsCaptionButton(int hit) =>
            hit == HTMINBUTTON || hit == HTMAXBUTTON || hit == HTCLOSE || hit == HTOBJECT;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyRoundedCorners(Handle);
            ApplyTheme(_dark);
            // 让系统按新的 WM_NCCALCSIZE 结果重算一次窗口框架
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
        }

        private void ApplyTheme(bool dark)
        {
            _dark = dark;
            var bg = Theme.Bg(dark);
            BackColor = bg;
            try { _webView.DefaultBackgroundColor = bg; }
            catch { }
            // 注意别在这里碰 Handle 触发提前建窗：构造期就会调到这个方法
            if (IsHandleCreated) Theme.ApplyDarkTitleBar(Handle, dark);   // 窗口描边跟着换深浅
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
            PublishWindowState();
        }

        private void PublishWindowState()
        {
            try
            {
                _webView.CoreWebView2?.PostWebMessageAsString(
                    WindowState == FormWindowState.Maximized ? "chrome:maximized" : "chrome:restored");
            }
            catch { }
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
                        // 设置没有对应的系统命中码，借 HTOBJECT 占位：系统不管它，正好由我们自己处理
                        else if (SettingsButtonRect.Contains(pt)) m.Result = (IntPtr)HTOBJECT;
                        else m.Result = (IntPtr)HTCAPTION;   // 其余整条都能拖动/双击最大化/右键系统菜单
                    }
                    return;
                }

                case WM_NCLBUTTONDOWN:
                case WM_NCLBUTTONDBLCLK:
                    if (IsCaptionButton((int)m.WParam))
                    {
                        _pressedButton = (int)m.WParam;
                        m.Result = IntPtr.Zero;
                        return;
                    }
                    break;

                case WM_NCLBUTTONUP:
                    if (IsCaptionButton((int)m.WParam))
                    {
                        var hit = (int)m.WParam;
                        var wasPressed = _pressedButton;
                        _pressedButton = HTNOWHERE;
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
                    }
                    break;
            }

            base.WndProc(ref m);
        }

        private void InvokeCaptionAction(int hit)
        {
            switch (hit)
            {
                case HTMINBUTTON: WindowState = FormWindowState.Minimized; break;
                case HTMAXBUTTON: ToggleMaximize(); break;
                case HTCLOSE: Close(); break;   // 走 OnFormClosing → 收到托盘，和 Alt+F4 行为一致
                case HTOBJECT: OpenSettings(); break;
            }
        }

        // ---------- 托盘与窗口行为 ----------

        private void BuildTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示 DeepSeek Harness", null, (sender, e) => ShowFromTray());
            menu.Items.Add("在浏览器中打开", null, (sender, e) => OpenInBrowser());
            menu.Items.Add("设置…", null, (sender, e) => OpenSettings());
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
            _reallyExit = true;
            Close();
        }

        // ---------- 设置 ----------

        /// <summary>
        /// 打开设置对话框。启动方式只在启动时读一次，所以这里改完不做热切换，只提示重启。
        /// </summary>
        private void OpenSettings()
        {
            if (_settingsOpen) return;   // 网页按钮与原生命中区可能同时投递，别开出两个框
            _settingsOpen = true;
            try
            {
                ShowSettingsDialog();
            }
            catch (Exception ex)
            {
                // 调用方是网页消息回调，那边会吞异常；这里不自己报就是点了毫无反应
                MessageBox.Show(this, "打不开设置：\r\n\r\n" + ex.Message,
                    AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _settingsOpen = false;
            }
        }

        private void ShowSettingsDialog()
        {
            // 可能是从托盘菜单进来的：先把窗口露出来，模态框才有归属，不会藏到别的窗口后面
            if (!Visible) ShowFromTray();

            using (var dialog = new SettingsForm(_settings, _dark))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (dialog.Result.SameAs(_settings)) return;

                _settings.LaunchMode = dialog.Result.LaunchMode;
                _settings.WslDistro = dialog.Result.WslDistro;
                if (!_settings.Save())
                {
                    MessageBox.Show(this,
                        "设置没能写入 " + ServerManager.DataDir + "，改动只在本次运行内有效。",
                        AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var answer = MessageBox.Show(this,
                    "启动方式已保存，重启 " + AppTitle + " 后生效。\r\n\r\n现在就重启吗？（正在进行的任务会终止）",
                    AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer == DialogResult.Yes) RestartApp();
            }
        }

        /// <summary>关掉自己并让 Program 在互斥体释放后拉起新实例（后台服务照常回收）。</summary>
        private void RestartApp()
        {
            RestartRequested = true;
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
            var startedByUs = _server.StartedByUs;
            _server.Stop(); // 只收掉本程序启动的服务
            // 重启时多等一下端口真正关掉：新实例探到还没咽气的旧服务就会连上一个即将消失的后端
            if (RestartRequested && startedByUs) _server.WaitForPortClosed(TimeSpan.FromSeconds(5));
            base.OnFormClosed(e);
        }

        // ---------- 启动画面（DSW 浅色风格） ----------

        private static Panel BuildSplash(out Label status)
        {
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.LightBg };
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
                ForeColor = Theme.LightLabel
            };
            status = new Label
            {
                Text = "正在启动后台服务…",
                AutoSize = false,
                Size = new Size(440, 30),
                Anchor = AnchorStyles.None,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = Theme.LightLabelTertiary
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

    }
}
