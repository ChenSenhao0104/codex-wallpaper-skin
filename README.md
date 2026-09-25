# Codex Wallpaper Skin

[English](README.en.md) | 简体中文

一个独立运行的 Windows 11 x64 桌面程序，用本地图片、视频和已安装的 Wallpaper Engine 项目为 Codex Desktop 更换背景。当前 GUI 开发版本为 `0.7.1`，要求官方 x64 `OpenAI.Codex` Store/MSIX 桌面包。

程序通过仅绑定 `127.0.0.1` 的 Chrome DevTools Protocol（CDP），在真实 Codex 页面中添加可逆的背景层。Image/Video 由 Codex 独立加载；复杂 Scene 由本机 Wallpaper Engine 在隔离窗口中原生渲染后传给 Codex。程序不修改 `WindowsApps`、`app.asar`、官方签名、聊天内容或登录数据。

> 本项目不是 OpenAI、Valve 或 Wallpaper Engine 的官方产品，也不受其认可或赞助。

## GUI 当前能力

- 加载 PNG、JPEG、WebP、GIF、MP4 和 WebM。
- 扫描本机 Steam 当前仍订阅且已下载的 Wallpaper Engine Image、Video、Scene 和 Web 项目；重新扫描会移除已取消订阅的残留目录条目，但不影响 **Add local** 壁纸。
- Image/Video 使用项目中的原始本地媒体；不超过 256 MiB 的视频直接传给 Codex，较大的 Wallpaper Engine 视频改由官方 `playInWindow` 播放并走原生捕获链路，避免整文件上传和巨额内存副本。
- Scene 优先通过本机已安装的 Wallpaper Engine 官方 `playInWindow` 后端渲染，再把离屏画面传给 Codex；Puppet Warp、粒子、作者脚本和音频响应由 Wallpaper Engine 保持原生语义。当前稳定 H.264 链路不复刻鼠标互动。
- 官方高保真后端不可用时才使用受限内置 2D 渲染器，并明确提示兼容性降级；最终还可安全回退到包内原始纹理或经过验证的 Workshop 预览。
- Web 项目不执行网页代码，只允许使用安全的 GIF 或静态预览；Application 项目始终拒绝。
- 一次性 32×32 取色可协调半透明面板、强调色和继承文字；代码、终端、警告和状态色保持原样。
- 可调填充、焦点、不透明度、黑色遮罩、亮度、对比度、饱和度、取色强度、面板透明度、文字协调、模糊、动画速度、Scene FPS 和渲染比例；点击参数右侧数字可精确输入。可创建、命名、修改和删除多组视觉预设，一键应对不同亮度与色调的壁纸；应用后所有参数仍能继续微调。
- 记住最后一次成功应用的壁纸；调节器下次打开、重新连接或 Windows 登录时可自动恢复。若 Codex 已普通启动，软件会提示用户手动关闭 Codex，再点击 **Start / reconnect Codex**，绝不自动关闭用户任务。点击 **Restore Codex background** 会清除壁纸和待应用状态。
- 提供个人收藏夹、技术类型筛选、即时搜索和仅限本软件的重命名；重新扫描不会清除整理结果，也不会改动工坊文件。
- 顶部提供中英文一键切换并记住选择；**Start / reconnect Codex** 是唯一的 Codex 启动与重连入口。
- 媒体解码成功后原子切换；失败保留旧背景；支持隐藏暂停、异常清理和 **Restore Codex background** 一键恢复。
- 动态流看门狗会检查 Codex 最近一次真正显示画面的时间；捕获、编码、传输或解码呈现静默停止时最多自动恢复两次，隐藏暂停不会触发误恢复。
- 60 FPS H.264 帧按编码时间戳与显示刷新节奏逐帧呈现，不再把整批帧瞬间画完后停顿；队列落后时丢弃旧帧以保持低延迟。高画质 60 FPS 的本地码率上限提高到 60 Mbps，并复用大型 NV12 转换缓冲区以减少内存抖动。
- **Doctor** 会显示当前看门狗健康分类、画面计数、实际捕获/编码/显示 FPS、分辨率、平均耗时、码率、恢复次数及最近恢复原因；复制和导出的报告会隐藏用户名、绝对路径、页面标识符、会话 URL 参数和可能包含任务内容的窗口标题，ZIP 内日志也会经过同一套脱敏。
- 提供无需管理员权限的当前用户安装器；升级保留壁纸库、预设和设置，卸载时可选择保留或彻底删除本软件数据，且安装/卸载不会关闭 Codex。
- 窗口标题显示实际程序版本；发布流程会生成包含文件大小、SHA-256、源码提交和 Authenticode 状态的机器可读清单。

复杂 Scene 不再由本项目猜测其私有格式，而由 Wallpaper Engine 自身渲染。此模式要求 Wallpaper Engine 已安装并在播放期间保持后台运行；关闭窗口会隐藏到托盘并继续播放，使用托盘中的 **Remove wallpaper and exit** 会快速尝试移除壁纸并彻底退出，Codex 已关闭或不可用时也不会阻塞退出。当前 H.264 路径为稳定性不复刻鼠标互动；内置 2D 渲染器只作为明确标注的兼容后备。

## 快速开始

详细步骤见 [具体操作流程](usage-process.md)。普通用户优先下载 `CodexWallpaperSkin-Setup-vX.Y.Z-win-x64.exe` 和对应 `.sha256`，校验后运行安装器；也可下载便携 ZIP，完整解压后运行 `CodexWallpaperSkin.exe`。两种版本都是自包含程序，不需要安装 Python、PyYAML、Node.js 或 .NET。

1. 运行软件，点击 **Start / reconnect Codex**；程序会自动检测、启动或重新连接，普通使用无需填写 CDP 或 AUMID。
2. 如果 Codex 已普通启动，请先手动关闭 Codex，再点击 **Start / reconnect Codex**；软件不会自动关闭或重启 Codex。
3. 点击 **Scan Wallpaper Engine**，选择壁纸并检查右侧参数。
4. 点击 **Apply selected**；不再使用时点击 **Restore Codex background**。端口和应用身份只放在折叠的高级设置中。

首次状态默认开启自动配色、文字协调、静音和隐藏暂停；背景不透明度、亮度、对比度、饱和度均为原值，黑色遮罩和模糊为 `0`。点击 **Original color / clarity** 可把影响画面色彩和清晰度的参数恢复为中性值。

首次成功应用后，软件默认记住该壁纸。Windows 不允许把 Chromium 调试参数动态补到已运行的 Codex 进程，因此无法在不重启的前提下安全注入背景；本程序会保存所选壁纸，但不会替用户关闭 Codex。请手动关闭后点击 **Start / reconnect Codex**。Windows 登录自动恢复为可选项。

## Wallpaper Engine 标签

| 标签 | 实际行为 |
|---|---|
| `[IMAGE]` | 独立加载项目原始图片。 |
| `[VIDEO]` | 独立循环播放项目 MP4/WebM。 |
| `[WE NATIVE VIDEO]` | 由 Wallpaper Engine 播放大型 MP4/WebM，再经原生捕获与硬件 H.264 链路显示。 |
| `[WE LIVE SCENE]` | 优先由 Wallpaper Engine 原生渲染并桥接动画/鼠标交互；失败时明确降级。 |
| `[ANIMATED PREVIEW]` | 包不可用时使用经过验证的 GIF 预览。 |
| `[STATIC FALLBACK]` | 使用安全静态预览。 |
| `[REJECTED]` | 项目类型或素材未通过安全检查，不能应用。 |

## 性能与安全

静态图负担最低。视频直接在 Codex 解码；高保真 Scene 同时使用 Wallpaper Engine 渲染、Windows Graphics Capture/D3D11、硬件 H.264 编码和回环传输，并受 Wallpaper Engine 的全局帧率限制与 50%–100% 渲染比例控制。低负担建议是模糊 `0`、使用 30 FPS 省电方案、较低 Scene 比例并开启隐藏暂停。

所有画面只通过回环 CDP 在本机内存中传入 Codex，不发送到互联网或局域网。高保真 Scene 由用户已安装的 Wallpaper Engine 执行并遵循其安全/性能设置；本程序自身不解释或执行 SceneScript。Web 壁纸代码与 Application 壁纸始终不会运行。同一 Windows 用户下的其他进程仍可能访问未认证的 CDP 端口，因此只应在可信会话中使用；完全退出以 CDP 参数启动的 Codex 才会关闭端口。

本项目不附带或重新分发 Wallpaper Engine 壁纸。用户必须拥有 Wallpaper Engine 并遵守壁纸作者许可。漏洞报告方式见 [SECURITY.md](SECURITY.md)。

## 开发与验证

构建需要 .NET 8 SDK；完整运行时测试需要 Node.js，安装器构建还需要 Inno Setup 7。第三方依赖与许可证见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

```powershell
pwsh -NoProfile -File .\scripts\build-companion.ps1
dotnet .\companion\bin\Debug\net8.0-windows10.0.19041.0\win-x64\CodexWallpaperSkin.dll --self-test
node .\scripts\runtime-smoke-test.mjs
dotnet .\companion\bin\Debug\net8.0-windows10.0.19041.0\win-x64\CodexWallpaperSkin.dll --wgc-smoke-test
```

安装了 Wallpaper Engine 的开发机还可运行真实 Scene WebGL 兼容性测试：

```powershell
node .\scripts\scene-render-smoke-test.mjs
dotnet .\companion\bin\Debug\net8.0-windows10.0.19041.0\win-x64\CodexWallpaperSkin.dll --we-capture-smoke-test "D:\...\project.json"
```

生成最终自包含程序、便携 ZIP 和 SHA-256：

```powershell
pwsh -NoProfile -File .\scripts\build-companion.ps1 -Configuration Release -Publish
```

安装 Inno Setup 7 后，可在完成全部便携版验证的同时生成当前用户安装器及 SHA-256：

```powershell
pwsh -NoProfile -File .\scripts\build-installer.ps1
```

退出正在运行的控制器后，可用上一稳定版和当前候选版安装器做真实跨版本验证。该测试会依次完成旧版安装、原位升级、新版自检和卸载，并确认 `state.json` 与 `library.json` 始终未被改写：

```powershell
pwsh -NoProfile -File .\scripts\installer-cross-version-smoke-test.ps1 `
  -PreviousSetupPath ".\dist\CodexWallpaperSkin-Setup-v0.6.2-win-x64.exe" `
  -CurrentSetupPath ".\dist\CodexWallpaperSkin-Setup-v0.7.1-win-x64.exe"
```

如已在当前用户证书存储中配置代码签名证书，可同时签署内层程序和最终安装器；私钥不会进入仓库或发布目录：

```powershell
pwsh -NoProfile -File .\scripts\build-installer.ps1 `
  -CertificateThumbprint "40位证书指纹" `
  -TimestampUrl "https://你的证书服务商时间戳地址"
```

未提供证书时仍可生成测试包，`release-manifest.json` 会明确记录 `NotSigned`，不会把未签名文件伪装成已签名版本。`.github/workflows/release-candidate.yml` 可在 Windows GitHub Actions 自动从 `main` 构建上一稳定版，并运行稳定版到候选版的隔离安装、原位升级、自检和卸载测试，但不会自动发布 Release。

`SKILL.md`、`agents/` 和 `references/` 仅作为未来可能的 Codex 集成入口保留，不属于当前 GUI 交付，也不会被便携版运行。许可证为 [Apache-2.0](LICENSE)，第三方代码说明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
