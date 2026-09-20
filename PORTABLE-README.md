# Codex Wallpaper Skin 便携版

这是 Windows 11 x64 的自包含调节器，无需安装 Python、PyYAML 或 .NET。当前版本要求官方 x64 `OpenAI.Codex` Store/MSIX 桌面包；非打包版、改名副本、ARM64 和 x86 版本不受支持。请只从本项目的可信 Release 页面下载，并将 ZIP 完整解压后再运行 `CodexWallpaperSkin.exe`。

完整的逐步说明、参数表和排障流程见 [usage-process.md](usage-process.md)。

## 使用 Wallpaper Engine 壁纸

1. 在 Wallpaper Engine 中订阅或选择壁纸，等待素材下载完成。
2. 运行 `CodexWallpaperSkin.exe`，依次点击 **Detect app**、**Activate with CDP**，等待 Codex 打开后点击 **Connect**。
3. 若 Codex 已经普通启动，直接选择壁纸并点击 **Apply selected**：软件会排队等待你日后自然关闭 Codex，不会强制结束当前任务。若希望立即生效，请先保存内容并自行正常退出 Codex。
4. 点击 **Scan Wallpaper Engine**，选择列表中的同名项目，确认自动配色、模糊、静音等参数，然后点击 **Apply selected**。
5. 点击 **Restore Codex background** 可移除本项目的页面层。恢复不会关闭 CDP 端口；完全退出该 Codex 进程才会关闭端口。

成功 Apply 后会记住最后一张壁纸；以后打开调节器或重新 Connect 会自动恢复。右侧可选择“Windows 登录时恢复”。点击参数右侧数字可输入精确值。**Restore Codex background** 会清除已记住的壁纸与等待队列。若 Codex 已在没有 CDP 参数的情况下运行，软件会立即识别并延迟应用，不再等待 30 秒报错。

Image/Video 项目直接使用已安装的原始素材（视频安全上限 256 MiB）。Scene 项目优先由 Wallpaper Engine 自身在私有离屏窗口中渲染；该窗口保持可捕获，但不会出现在任务栏或 Alt+Tab，也不会抢占焦点。Codex 鼠标坐标会转发给它，因此水波、Puppet Warp、粒子、脚本和音频响应保留原效果；Wallpaper Engine 需保持后台可用。原生后端不可用时才明确降级到受限内置渲染器或安全预览。Web 项目只使用安全预览，Application 项目永不执行。

## 命令行与校验

在此目录打开 PowerShell：

```powershell
.\CodexWallpaperSkin.exe --doctor --json
.\CodexWallpaperSkin.exe --self-test
.\CodexWallpaperSkin.exe --restore
Get-FileHash ..\CodexWallpaperSkin-win-x64.zip -Algorithm SHA256
```

将最后一条结果与 Release 页面旁的 `.sha256` 文件比较。不要全局关闭 PowerShell 执行策略或 Windows 安全功能。

动态视频和 Scene 会增加解码、Windows Graphics Capture/D3D11 抓取、GPU、CPU 与电池消耗；高保真 Scene 传输上限为 15 FPS，WGC 不可用时会明确回退兼容抓取。静态图、`0` 模糊、较低 Scene 比例和隐藏时暂停最省资源。画面只经 `127.0.0.1` 传入 Codex 渲染器内存，不会由本软件发往互联网；但同一 Windows 用户下的其他进程也可能访问未认证的 CDP 端口，请只在可信环境中使用。

本项目不附带 Wallpaper Engine 素材。你必须拥有 Wallpaper Engine，并遵守壁纸作者的许可。安全问题请按随包 `SECURITY.md` 的私密报告流程处理。
