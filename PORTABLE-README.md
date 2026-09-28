# Codex Wallpaper Skin 便携版

这是 Windows 11 x64 的自包含调节器，无需安装 Python、PyYAML 或 .NET。当前版本只支持官方 x64 `OpenAI.Codex` Store/MSIX 桌面包；macOS、Linux、Windows ARM64、Windows x86、非打包版和改名副本均不受支持。请只从本项目的可信 Release 页面下载，并将 ZIP 完整解压后再运行 `CodexWallpaperSkin.exe`。

本目录是便携版，不会注册卸载器。希望使用开始菜单、原位升级和标准卸载流程的用户应改用同版本的 `CodexWallpaperSkin-Setup-vX.Y.Z-win-x64.exe`。

完整的逐步说明、参数表和排障流程见 [usage-process.md](usage-process.md)。

## 使用 Wallpaper Engine 壁纸

1. 在 Wallpaper Engine 中订阅或选择壁纸，等待素材下载完成。
2. 运行 `CodexWallpaperSkin.exe`，点击 **Start / reconnect Codex**；软件会自动检测、启动或重新连接 Codex。
3. 若 Codex 已经普通启动，请先手动关闭 Codex，再点击 **Start / reconnect Codex**。软件不会自动关闭或重启 Codex。
4. 点击 **Scan Wallpaper Engine**，选择列表中的同名项目，确认自动配色、模糊、静音等参数，然后点击 **Apply selected**。
5. 点击 **Restore Codex background** 可移除本项目的页面层。恢复不会关闭 CDP 端口；完全退出该 Codex 进程才会关闭端口。

成功 Apply 后会记住最后一张壁纸。以后需要使用壁纸时请从本调节器启动或连接 Codex；若 Codex 已普通启动，请手动关闭后点击 **Start / reconnect Codex**。旧版创建的 `Codex with remembered wallpaper` 桌面快捷方式会在本版首次启动时安全清除，官方 Codex 图标不受影响。点击顶部语言按钮可切换中英文，选择会自动记忆。**Restore Codex background** 会清除已记住的壁纸与待应用状态。

列表上方的 **Search**、类型和收藏夹筛选可快速缩小结果。选择壁纸后，**Rename** 只修改本软件中的显示名称，**Set collection** 可建立“治愈”“动漫”“风景”等个人收藏夹；重新扫描不会丢失这些整理信息，也不会改动 Steam 或本地素材。

右侧 **Performance profile** 提供省电、均衡和高画质档位；单独调整参数后会自动显示为自定义。可用 **Assign preset to wallpaper** 把当前视觉预设绑定到所选壁纸。顶部 **Doctor** 支持导出诊断 ZIP；其中不包含壁纸媒体、完整状态文件或 Codex 任务标题。

Image 与不超过 256 MiB 的 Video 项目直接使用已安装的原始素材；更大的 Wallpaper Engine Video 和 Scene 项目由 Wallpaper Engine 在私有离屏窗口中播放，再走原生捕获链路。该窗口不会出现在任务栏或 Alt+Tab，也不会抢占焦点。Wallpaper Engine 需保持后台可用。为保证稳定性，当前 H.264 路径不复刻鼠标互动。原生后端不可用时才明确降级到受限内置渲染器或安全预览。Web 项目只使用安全预览，Application 项目永不执行。

## 命令行与校验

在此目录打开 PowerShell：

```powershell
.\CodexWallpaperSkin.exe --doctor --json
.\CodexWallpaperSkin.exe --self-test
.\CodexWallpaperSkin.exe --restore
Get-FileHash ..\CodexWallpaperSkin-v0.8.1-portable-win-x64.zip -Algorithm SHA256
Get-Content ..\CodexWallpaperSkin-v0.8.1-portable-win-x64.zip.sha256
```

将最后一条结果与 Release 页面旁的 `.sha256` 文件比较。不要全局关闭 PowerShell 执行策略或 Windows 安全功能。

动态视频和 Scene 会增加解码、Windows Graphics Capture/D3D11 抓取、GPU、CPU 与电池消耗；Scene 以 60 FPS 为目标，在性能不足或 Wallpaper Engine 全局限制较低时降到 30 FPS 或实际限制。支持的显卡会直接在 GPU 中把捕获纹理转换并送入硬件 H.264 编码器；不支持时会自动回退到兼容的 CPU 转换路径，WGC/硬件 H.264 不可用时再明确回退兼容抓取。可在 **Visual preset** 中选择多组命名方案，用 **Manage presets…** 创建和编辑，再用 **Apply preset** 套用；右侧参数仍可继续微调。预设无法消除 H.264 4:2:0 相对 Wallpaper Engine 直接桌面合成的全部差距。静态图、`0` 模糊、较低 Scene 比例和隐藏时暂停最省资源。画面只经本机受控通道传入 Codex 渲染器内存，不会由本软件发往互联网；但同一 Windows 用户下的其他进程也可能访问未认证的 CDP 端口，请只在可信环境中使用。

本项目不附带 Wallpaper Engine 素材。你必须拥有 Wallpaper Engine，并遵守壁纸作者的许可。安全问题请按随包 `SECURITY.md` 的私密报告流程处理。

## Beta 已知限制

- 仅支持 Windows 11 x64 和官方 x64 `OpenAI.Codex` Store/MSIX 桌面包；macOS、Linux、Windows ARM64 和 Windows x86 均不支持。
- Scene 与大型 Wallpaper Engine Video 需要用户已安装 Wallpaper Engine；动态背景要求本控制器保持在托盘运行。
- 如果 Codex 已普通启动，请手动关闭后再点击 **Start / reconnect Codex**；本软件不会自动关闭 Codex。
- `60 FPS` 是目标而非保证。实际清晰度、帧率和资源占用取决于壁纸、Wallpaper Engine、显卡、分辨率和系统负载；Doctor 数据用于诊断。
- 稳定 H.264 4:2:0 路径不转发鼠标互动，也不保证与桌面直接合成逐像素一致。
- 此 Beta 尚未代码签名。Windows 可能显示未知发布者或 SmartScreen 提示；只从可信 Release 下载并核对 SHA-256，不要全局关闭 Windows 安全功能。
