# DSH-Shell —— DeepSeek Harness 桌面版

把 [DeepSeek Harness（dsh）](https://www.npmjs.com/package/@deepseek-ai/dsh) 的 Web 界面装进一个原生 Windows 窗口：
双击 `DSH.exe` 即可使用，**不需要打开终端或浏览器**。后台 `dsh web` 服务自动拉起、自动回收，托盘常驻。

> 本项目是社区制作的非官方桌面外壳，与 DeepSeek 官方无关。dsh 本体请通过 npm 安装。

## 前置条件

- Windows 10/11 x64
- [.NET 6 桌面运行时](https://dotnet.microsoft.com/download/dotnet/6.0)（若未安装，首次运行会提示下载）
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)（Windows 11 自带）
- 已全局安装 dsh：`npm install -g @deepseek-ai/dsh`

## 用法

| 操作 | 效果 |
|---|---|
| 双击 `DSH.exe` | 静默启动后台服务（无任何黑色窗口）→ 弹出应用窗口 |
| 窗口外观 | 网页表面延伸到窗口顶边（无独立白色顶栏），四角圆角 |
| 拖动窗口 | 按住页面顶部 32px 的空白区域；页面内容会自动避开这条拖拽带 |
| 窗口按钮 | 右上角原生风格的 ─ ❐ ✕，悬停着色并跟随页面浅色/深色主题 |
| ✕ / ❐ / ─ | ✕ 最小化到托盘（任务继续运行）/ ❐ 最大化·还原 / ─ 最小化到任务栏 |
| 双击托盘鲸鱼图标 | 重新打开窗口 |
| 托盘图标 → 右键 | 显示窗口 / 在浏览器中打开 / 退出 |
| 托盘右键 → 退出 | 直接完全退出：连它自己拉起的后台服务一起关掉（正在进行的任务会终止） |

托盘提示只在整个生命周期弹一次（跨启动记忆）。

## 设计要点

- **无终端**：后台 `dsh web` 以完全隐藏的方式运行（`CreateNoWindow`），stdout/stderr 写入日志
  `%LOCALAPPDATA%\DeepSeekHarness\dsh-web.log`。
- **端口复用，绝不误杀**：若 3080 端口已有服务在跑（比如你自己在终端里跑的 `dsh web`），
  程序直接复用，不会重复启动；退出时也**不会**去关它。
- **单实例**：程序已运行时再双击图标，只会把已有窗口唤到前台。
- **窗口位置记忆**：窗口大小/位置/最大化状态保存在 `%LOCALAPPDATA%\DeepSeekHarness\window.json`。
- **外部链接**：网页里的外链（如搜索结果来源）交给系统默认浏览器打开，不干扰主窗口。
- **壳层隔离**：标题栏拖拽与内容避让由 WebView 启动时注入，只作用于桌面窗口，不修改 DSH 网页源码；
  注入脚本只依赖 dsh-web 稳定的 `data-slot` 锚点与 `data-ds-dark-theme` 属性，dsh 升级后即使锚点变化，
  也只是拖拽带避让降级，主功能不受影响。

## 更新 dsh 本体

```powershell
npm install -g @deepseek-ai/dsh@latest
```

壳程序每次启动都从 PATH 重新解析 `dsh` 命令，更新 dsh 后无需重新安装本程序。

## 从源码构建

```powershell
dotnet publish DSH-Shell.csproj -c Release -o publish
```

产物为单文件 `publish\DSH.exe`。可为它创建快捷方式放到桌面/开始菜单，右键可“固定到任务栏”。

图标由 `IconRender` 小工具从官方 favicon.svg 重新生成：

```powershell
dotnet run --project IconRender -- favicon.svg .
```

## 卸载

托盘图标右键 → 退出，然后删除 `DSH.exe` 即可（数据目录在 `%LOCALAPPDATA%\DeepSeekHarness`）。

## 自测参数（正常使用请忽略）

- `DSH.exe --test-quit-after <秒>`：以独立测试实例启动，完成后延迟指定秒数自动走“完全退出”路径（含回收自己拉起的服务），用于不打断正常实例的自动化验证。
- 环境变量 `DSH_SHELL_PORT`：覆盖服务端口（默认 3080），仅测试用。
- 环境变量 `DSH_SHELL_SANDBOX_ARGS=1`：给 WebView2 浏览器进程加 `--no-sandbox` 等放宽参数，仅受限沙箱环境用。

## 许可

[MIT](LICENSE)。DeepSeek 名称与鲸鱼图标归其权利人所有，仅用于指代被包装的官方工具。
