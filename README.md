# DSH-Shell —— DeepSeek Harness 桌面版

把 [DeepSeek Harness（dsh）](https://www.npmjs.com/package/@deepseek-ai/dsh) 的 Web 界面装进一个原生 Windows 窗口：
双击 `DSH.exe` 即可使用，**不需要打开终端或浏览器**。后台 `dsh web` 服务自动拉起、自动回收，托盘常驻。

> 本项目是社区制作的非官方桌面外壳，与 DeepSeek 官方无关。dsh 本体请通过 npm 安装。

## 前置条件

- Windows 10/11 x64
- [.NET 6 桌面运行时](https://dotnet.microsoft.com/download/dotnet/6.0)（若未安装，首次运行会提示下载）
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)（Windows 11 自带）
- 已安装 dsh，二选一：
  - Windows 侧：`npm install -g @deepseek-ai/dsh`
  - 或 WSL 侧：在发行版里装好 Node 与 dsh，然后在设置里把启动方式切到 WSL（见[后端启动方式](#后端启动方式windows--wsl)）

## 用法

| 操作 | 效果 |
|---|---|
| 双击 `DSH.exe` | 静默启动后台服务（无任何黑色窗口）→ 弹出应用窗口 |
| 窗口外观 | 网页表面延伸到窗口顶边（无独立白色顶栏），四角圆角 |
| 拖动窗口 | 按住页面顶部 32px 的空白区域；页面内容会自动避开这条拖拽带 |
| 窗口按钮 | 右上角原生风格的 ⚙ ─ ❐ ✕，悬停着色并跟随页面浅色/深色主题 |
| ✕ / ❐ / ─ | ✕ 最小化到托盘（任务继续运行）/ ❐ 最大化·还原 / ─ 最小化到任务栏 |
| ⚙ | 打开设置：选择后台 `dsh web` 跑在 Windows 还是 WSL 里 |
| 双击托盘鲸鱼图标 | 重新打开窗口 |
| 托盘图标 → 右键 | 显示窗口 / 在浏览器中打开 / 设置 / 退出 |
| 托盘右键 → 退出 | 直接完全退出：连它自己拉起的后台服务一起关掉（正在进行的任务会终止） |

托盘提示只在整个生命周期弹一次（跨启动记忆）。

## 后端启动方式（Windows / WSL）

标题栏 ⚙（或托盘右键 → 设置）里可以选后台 `dsh web` 在哪儿跑，对话框底部实时显示将要执行的命令：

| 启动方式 | 实际执行 |
|---|---|
| Windows（PowerShell / cmd） | `cmd /s /c "<PATH 里的 dsh> web --no-open --host 127.0.0.1 --port 3080"` |
| WSL（适用于 Linux 的 Windows 子系统） | `wsl -d <发行版> --cd ~ -e /bin/bash -lic "exec dsh web --no-open --host 127.0.0.1 --port 3080"` |

两种方式都会加 `--no-open`：新版 `dsh web` 默认会拉起系统默认浏览器，而本程序用内嵌 WebView2，
不需要再另开浏览器。

新版 dsh 会输出带 `?token=...` 的启动地址。壳会自动读取该地址，等待认证入口就绪后再加载窗口；
托盘的“在浏览器中打开”也使用同一入口。认证完成后由 dsh 设置 cookie 并跳转到干净的首页地址。
Windows 与 WSL 启动方式均支持，也兼容无需认证的旧版 dsh。

如果复用已经运行的服务，壳会尝试从已有日志恢复入口，并向当前服务验证它是否仍然有效。
若没有可用入口且窗口没有认证 cookie，会提示粘贴该服务启动时 `dsh web:` 后的完整地址。
启动 token 不会额外写入设置；现有 `dsh-web.log` 包含 dsh 的启动输出，分享日志前请遮盖 token。

改完点确定会提示需要重启，可以选择立即重启（程序自己拉起新实例，旧的后台服务照常回收）。
设置存在 `%LOCALAPPDATA%\DeepSeekHarness\settings.json`。

WSL 侧的几点说明：

- **发行版下拉框**列出 `wsl -l -q` 的结果（Docker Desktop 的工具发行版会滤掉），选“默认发行版”就用 WSL 的默认项。
- **需要 Node 20+ 与装在该发行版里的 dsh**（`npm install -g @deepseek-ai/dsh`）。Node 18 会直接报
  `node:util does not provide an export named 'parseEnv'`。
- **用登录 + 交互式 shell 启动**（`bash -lic`），所以 nvm 之类只写在 `~/.bashrc` 里的 PATH 也认。
- **服务绑在 WSL 的 127.0.0.1**，Windows 侧靠 WSL 自带的 localhost 转发访问，不往 `0.0.0.0` 上绑，
  端口不会暴露到网络上。若一直等不到服务就绪，检查 `%USERPROFILE%\.wslconfig` 里的 `localhostForwarding`。
- **工作目录是 Linux 侧的用户主目录**（`--cd ~`），不是 `/mnt/c/...`：dsh 会拿工作目录当项目根，
  而且跨文件系统访问很慢。
- 退出时按进程树回收 `wsl.exe`，WSL 里的 dsh 会跟着结束。

启动失败时（例如 dsh 其实装在另一边），错误框会直接问你要不要打开设置换一种方式。

## 设计要点

- **无终端**：后台 `dsh web` 以完全隐藏的方式运行（`CreateNoWindow`），stdout/stderr 写入日志
  `%LOCALAPPDATA%\DeepSeekHarness\dsh-web.log`，日志第一行就是这次用的启动命令。
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

壳程序每次启动都从 PATH 重新解析 `dsh` 命令（WSL 方式则每次都在登录 shell 里重新解析），更新 dsh 后无需重新安装本程序。

## 从源码构建

```powershell
dotnet publish DSH-Shell.csproj -c Release -o publish
```

产物为单文件 `publish\DSH.exe`。可为它创建快捷方式放到桌面/开始菜单，右键可“固定到任务栏”。

认证兼容回归验证（无需额外测试包）：

```powershell
dotnet run --project Tests/DSH-Shell.Tests.csproj
# 可选：使用 PATH 中的真实 dsh，以临时 DSH_HOME 和独立端口验证认证及退出流程
dotnet run --project Tests/DSH-Shell.Tests.csproj -- --real
```

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
