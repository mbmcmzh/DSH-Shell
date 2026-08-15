# DeepSeek Harness 桌面版（DSH-Shell）

双击 `DSH.exe`（或“DeepSeek Harness.lnk”快捷方式），即可在独立窗口中使用 DeepSeek Harness，
**不再需要打开 PowerShell 或浏览器**。

## 用法

| 操作 | 效果 |
|---|---|
| 双击图标 | 静默启动后台服务（无任何黑色窗口）→ 弹出应用窗口 |
| 窗口外观 | 网页占满整个窗口（无独立顶栏），**四角圆角**（连网页内容一起裁圆） |
| 拖动窗口 | 按住窗口**顶部边缘 10px**（隐形拖拽条，落在页面自身空白顶边距内） |
| 窗口按钮 | 右上角**悬浮** ✕ ❐ ─（自左向右），透明底、悬停着色，配色跟随页面浅色/深色主题自动切换 |
| ✕ / ❐ / ─ | ✕ 最小化到托盘（任务继续运行）/ ❐ 最大化·还原 / ─ 最小化到任务栏 |
| 调整大小 | 拖动窗口边缘（原生缩放边框） |
| 双击托盘鲸鱼图标 | 重新打开窗口 |
| 托盘图标 → 右键 | 显示窗口 / 在浏览器中打开 / 退出 |
| 托盘右键 → 退出 | 完全退出：连它自己拉起的后台服务一起关掉（正在进行的任务会终止） |

托盘提示只在整个生命周期弹一次（跨启动记忆）。

## 设计要点

- **无终端**：后台 `dsh web` 以完全隐藏的方式运行（`CreateNoWindow`），stdout/stderr 写入日志
  `%LOCALAPPDATA%\DeepSeekHarness\dsh-web.log`。
- **端口复用，绝不误杀**：若 3080 端口已有服务在跑（比如你自己在终端里跑的 `dsh web`），
  程序直接复用，不会重复启动；退出时也**不会**去关它。
- **单实例**：程序已运行时再双击图标，只会把已有窗口唤到前台。
- **窗口位置记忆**：窗口大小/位置/最大化状态保存在 `%LOCALAPPDATA%\DeepSeekHarness\window.json`。
- **外部链接**：网页里的外链（如搜索结果来源）交给系统默认浏览器打开，不干扰主窗口。

## 文件

- `DSH-Shell\publish\DSH.exe` —— 单文件成品（自带鲸鱼图标）
- `DeepSeek Harness.lnk` —— 快捷方式（可复制到桌面/开始菜单，右键可“固定到任务栏”）
- `DSH-Shell\` —— 源代码（`dotnet publish` 可重新构建）

## 卸载

托盘图标右键 → 退出，然后删除上述文件即可（数据目录在 `%LOCALAPPDATA%\DeepSeekHarness`）。

## 自测参数（正常使用请忽略）

- `DSH.exe --test-quit-after <秒>`：启动完成后延迟指定秒数自动走“完全退出”路径（含回收自己拉起的服务），用于自动化验证。
- 环境变量 `DSH_SHELL_PORT`：覆盖服务端口（默认 3080），仅测试用。
- 环境变量 `DSH_SHELL_SANDBOX_ARGS=1`：给 WebView2 浏览器进程加 `--no-sandbox` 等放宽参数，仅受限沙箱环境用。

## 重新构建

```powershell
$env:NUGET_PACKAGES='C:\Users\26290\Desktop\1\nuget-cache'
dotnet publish 'C:\Users\26290\Desktop\1\DSH-Shell\DSH-Shell.csproj' -c Release -o 'C:\Users\26290\Desktop\1\DSH-Shell\publish'
```

（图标由 `IconRender` 小工具从官方 favicon.svg 重新生成到 `icon.ico`。）
