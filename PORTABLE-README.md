# Codex Wallpaper Skin 便携版

这是 Windows 11 x64 的自包含调节器，无需安装 Python、PyYAML 或 .NET。当前版本要求官方 x64 `OpenAI.Codex` Store/MSIX 桌面包；非打包版、改名副本、ARM64 和 x86 版本不受支持。请只从本项目的可信 Release 页面下载，并将 ZIP 完整解压后再运行 `CodexWallpaperSkin.exe`。

完整的逐步说明、参数表和排障流程见 [usage-process.md](usage-process.md)。

## 使用 Wallpaper Engine 壁纸

1. 在 Wallpaper Engine 中订阅或选择壁纸，等待素材下载完成。
2. 运行 `CodexWallpaperSkin.exe`，依次点击 **Detect app**、**Activate with CDP**，等待 Codex 打开后点击 **Connect**。
3. 若 Codex 已经普通启动，直接选择壁纸并点击 **Apply selected**：软件会安全排队。需要立即生效时，先保存或暂停工作，再点击 **Restart Codex normally and apply now**；软件只请求正常关闭并自动重开，绝不会强制结束进程。
4. 点击 **Scan Wallpaper Engine**，选择列表中的同名项目，确认自动配色、模糊、静音等参数，然后点击 **Apply selected**。
5. 点击 **Restore Codex background** 可移除本项目的页面层。恢复不会关闭 CDP 端口；完全退出该 Codex 进程才会关闭端口。

成功 Apply 后会记住最后一张壁纸。勾选 **Install ‘Codex with remembered wallpaper’ on the Desktop** 后，桌面会新增一个独立入口；以后从该入口打开 Codex，会先准备启动时壁纸通道、自动恢复上一次壁纸，并在后台维持动态播放，无需先打开调节窗口。官方 Codex 图标不会被替换，因为 Chromium 不能在进程已经启动后补加这条通道；若误先用官方图标打开，再点击新入口，软件会询问是否正常关闭并重开，绝不会强制结束进程。建议同时启用 Windows 登录恢复。移动便携版文件夹后，应重新勾选一次以更新桌面入口。点击参数右侧数字可输入精确值。**Restore Codex background** 会清除已记住的壁纸与等待队列。

列表上方的 **Search**、类型和收藏夹筛选可快速缩小结果。选择壁纸后，**Rename** 只修改本软件中的显示名称，**Set collection** 可建立“治愈”“动漫”“风景”等个人收藏夹；重新扫描不会丢失这些整理信息，也不会改动 Steam 或本地素材。

Image/Video 项目直接使用已安装的原始素材（视频安全上限 256 MiB）。Scene 项目优先由 Wallpaper Engine 自身在私有离屏窗口中渲染；该窗口保持可捕获，但不会出现在任务栏或 Alt+Tab，也不会抢占焦点。Wallpaper Engine 需保持后台可用。为保证稳定性，当前 H.264 路径不复刻鼠标互动。原生后端不可用时才明确降级到受限内置渲染器或安全预览。Web 项目只使用安全预览，Application 项目永不执行。

## 命令行与校验

在此目录打开 PowerShell：

```powershell
.\CodexWallpaperSkin.exe --doctor --json
.\CodexWallpaperSkin.exe --self-test
.\CodexWallpaperSkin.exe --restore
Get-FileHash ..\CodexWallpaperSkin-win-x64.zip -Algorithm SHA256
```

将最后一条结果与 Release 页面旁的 `.sha256` 文件比较。不要全局关闭 PowerShell 执行策略或 Windows 安全功能。

动态视频和 Scene 会增加解码、Windows Graphics Capture/D3D11 抓取、GPU、CPU 与电池消耗；Scene 以 60 FPS 为目标，在性能不足或 Wallpaper Engine 全局限制较低时降到 30 FPS 或实际限制，WGC/硬件 H.264 不可用时会明确回退兼容抓取。**Brighter high-clarity preset** 会启用完整捕获比例、移除遮罩与模糊、降低面板压暗并作轻微显示补偿；它无法消除 H.264 4:2:0 相对 Wallpaper Engine 直接桌面合成的全部差距。静态图、`0` 模糊、较低 Scene 比例和隐藏时暂停最省资源。画面只经本机受控通道传入 Codex 渲染器内存，不会由本软件发往互联网；但同一 Windows 用户下的其他进程也可能访问未认证的 CDP 端口，请只在可信环境中使用。

本项目不附带 Wallpaper Engine 素材。你必须拥有 Wallpaper Engine，并遵守壁纸作者的许可。安全问题请按随包 `SECURITY.md` 的私密报告流程处理。
