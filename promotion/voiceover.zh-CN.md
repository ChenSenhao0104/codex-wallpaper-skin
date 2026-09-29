# Chinese voice-over master

Target duration: 2 minutes 35 seconds. Read conversationally at approximately 210–230 Chinese characters per minute, leaving room for visual pauses.

## Script

Codex 的工作区，为什么只能是现在这个样子？

我原本只想给 Codex 换张背景，结果最后做出了一套能加载 Wallpaper Engine 动态场景的独立控制器。

它叫 Codex Wallpaper Skin，是一个开源的 Windows 工具。它可以让本地图片、视频，甚至 Wallpaper Engine 的动态场景，成为 Codex 自己的背景。

它不是把桌面壁纸透进 Codex。Codex 和桌面可以使用完全不同的画面，互不影响。

使用过程只有三步：连接 Codex，扫描 Wallpaper Engine 或添加本地媒体，然后点击 Apply selected。普通用户不需要安装 Node.js、Python，也不需要单独安装 .NET 运行时。

静态图片和普通视频可以直接加载。复杂的 Scene，则交给本机已经安装的 Wallpaper Engine 原生渲染，再通过本机 GPU 链路显示到 Codex。

不同壁纸的构图、亮度和色调并不一样，所以软件还提供自动配色、焦点、背景和面板透明度、可读性遮罩，以及可以保存和绑定的视觉预设。

动态背景在 Codex 隐藏时可以暂停或降频。Watchdog 会检查画面是否真的持续显示，Doctor 则提供经过隐私脱敏的诊断信息。

它不会修改 WindowsApps、app.asar、官方签名、聊天内容或登录数据。画面只在本机处理，不使用时也可以一键恢复 Codex 原始背景。

目前的公开 Beta 支持 Windows 11 x64，以及官方 x64 OpenAI Codex Store 或 MSIX 桌面包。普通图片和视频不需要 Wallpaper Engine；只有使用它的动态 Scene 或大型视频时才需要。

项目已经开源。如果你也不想每天面对一成不变的工作区，可以来试试 Codex Wallpaper Skin。觉得有用的话，欢迎到 GitHub 给项目一个 Star，也欢迎告诉我你最想支持哪一种壁纸。
