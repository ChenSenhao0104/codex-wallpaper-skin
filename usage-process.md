# Codex Wallpaper Skin 具体操作流程

本文面向普通用户，说明如何安装、连接 Codex、加载 Wallpaper Engine 壁纸、调整效果以及恢复原始背景。

## 一分钟流程

1. 解压 `CodexWallpaperSkin-win-x64.zip`，运行 `CodexWallpaperSkin.exe`。
2. 点击 **Detect app** 检测官方 Codex。
3. 点击 **Activate with CDP**，等待 Codex 打开，然后点击 **Connect**。
4. 如果 Codex 已普通启动且当前不能退出，直接选择壁纸并点击 **Apply selected**；软件会排队等待你日后自然关闭，不会中断任务。
5. 点击 **Scan Wallpaper Engine**，选择 `[IMAGE]`、`[VIDEO]`、`[WE NATIVE VIDEO]` 或 `[WE LIVE SCENE]` 壁纸。
6. 检查自动配色、模糊、静音和隐藏暂停等参数，点击 **Apply selected**。
7. 不再使用时点击 **Restore Codex background**；若还要关闭 CDP 端口，请完全退出该 Codex 进程。

## 1. 使用前提

- Windows 11 x64。
- 官方 x64 `OpenAI.Codex` Store/MSIX 桌面版。
- 如需使用 Wallpaper Engine 项目，必须已经在 Steam 中安装 Wallpaper Engine，并等待订阅壁纸下载完成。
- 当前版本不支持非打包版、改名副本、ARM64 或 x86 Codex。
- 社区发布的 EXE 可能没有代码签名并触发 SmartScreen。只运行你信任的 Release，不要全局关闭 Windows 安全功能。

## 2. 安装 GUI 软件

1. 下载 `CodexWallpaperSkin-win-x64.zip` 和旁边的 `.sha256` 文件。
2. 对下载的 ZIP 计算校验值：

   ```powershell
   Get-FileHash "D:\Downloads\CodexWallpaperSkin-win-x64.zip" -Algorithm SHA256
   ```

3. 将结果与 `.sha256` 文件中的值比较。
4. 把 ZIP 完整解压到你信任的目录。
5. 双击 `CodexWallpaperSkin.exe`。

便携版是自包含程序，普通使用不需要安装 Python、PyYAML、Node.js 或 .NET。

## 3. 首次连接 Codex

### 3.1 启动调节器

运行 `CodexWallpaperSkin.exe`。源码用户也可以在项目目录运行：

```powershell
pwsh -NoProfile -File .\scripts\launch.ps1
```

如果系统只有内置 Windows PowerShell 5.1，可在检查脚本后使用：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\launch.ps1
```

该执行策略参数只影响当前 PowerShell 进程，不要修改全局执行策略。

### 3.2 检测官方应用

点击 **Detect app**。调节器只接受官方 `OpenAI.Codex` 包身份，不会连接同名的其他程序。

### 3.3 用回环 CDP 启动 Codex

Codex 必须带有仅监听 `127.0.0.1` 的 CDP 参数，调节器才能添加背景层。

1. 点击 **Activate with CDP**。如果 Codex 尚未运行，调节器会用回环 CDP 启动它。
2. 如果普通 Codex 已经运行，调节器会立即识别，不会等待 30 秒、强制退出或干扰当前任务。
3. 此时选择壁纸并点击 **Apply selected** 即可加入等待队列。需要立即生效时，先保存或暂停正在进行的工作，再点击 **Restart Codex normally and apply now**；软件只请求 Codex 正常关闭，绝不强制结束进程，随后自动重新打开并应用壁纸。
4. 如果不希望现在重启，可继续使用 Codex；日后自然关闭后，后台等待器会自动完成下一次受控启动与恢复。
5. 等待官方 Codex 窗口出现。
6. 点击 **Connect**。

Windows 无法把 Chromium 调试参数动态补到已经运行的进程，因此真正的即时安全注入在这种状态下不可行。软件提供“用户确认的正常重启”和“不中断任务的等待队列”两条路径；如果 Codex 未在 45 秒内正常退出，软件会停止即时重启，不会强制终止，并继续保留等待队列。建议启用 **Restore at Windows sign-in**，让后台组件在登录后先准备控制通道并在需要时启动 Codex，从源头减少启动顺序问题。

## 4. 应用 Wallpaper Engine 壁纸

1. 在 Wallpaper Engine 中订阅或选择喜欢的壁纸，等待本地文件下载完成。
2. 回到 Codex Wallpaper Skin，点击 **Scan Wallpaper Engine**。
   扫描会以 Steam 当前“已订阅且已下载”的清单为准；取消订阅后遗留在磁盘上的旧目录会从列表移除，**Add local** 加入的项目不受影响。
3. 在左侧列表找到对应项目并查看类型标签：

   | 标签 | 行为 |
   |---|---|
   | `[IMAGE]` | 将项目的本地图片独立加载到 Codex。 |
   | `[VIDEO]` | 将本地 MP4/WebM 独立加载并循环播放。 |
   | `[WE NATIVE VIDEO]` | 由 Wallpaper Engine 播放大型视频，再经 WGC/硬件 H.264 链路显示，避免把整个文件上传到 Codex。 |
   | `[WE LIVE SCENE]` | 优先由 Wallpaper Engine 原生渲染，并桥接动画和鼠标交互。 |
   | `[ANIMATED PREVIEW]` | Scene/Web 的包或原始媒体不可用时，使用经过验证的 GIF 预览。 |
   | `[STATIC FALLBACK]` | 使用经过验证的静态预览。 |
   | 不可应用 | Application 或无法安全验证的项目会被拒绝。 |

4. 选择项目后，在右侧检查参数。
5. 点击 **Apply selected**，等待进度完成和成功提示。

Image/Video 项目直接使用已经下载到本机的素材。Scene 项目会启动 Wallpaper Engine 的私有离屏窗口并保持后台运行；Codex 的鼠标移动和按压会映射给原壁纸，所以水波、视差、Puppet Warp、粒子、脚本和音频响应由官方引擎原样处理。原生后端不可用时，底部状态栏会明确说明已使用受限内置渲染器、包内纹理或预览回退。

### 使用普通本地文件

点击 **Add local**，可选择：

- 图片：PNG、JPEG、WebP、GIF；
- 视频：MP4、WebM。

选择后同样点击 **Apply selected**。

Video 项目在 256 MiB 安全上限内会直接使用原始 MP4/WebM；更大的 Wallpaper Engine 视频会改走 `[WE NATIVE VIDEO]` 原生窗口捕获路径。只有该路径也不可用时才会按可用预览安全降级。扫描过旧版本目录后，请用当前版本重新点击 **Scan Wallpaper Engine**，让已有条目重新识别。

### 一键预设与自定义预设

- **Visual preset** 下拉框可选择不同命名方案；首次使用时提供软件内置的 **Brighter high-clarity**。
- **Manage presets…** 可新建、命名、修改或删除多组方案（最多 32 组），每组可保存亮度、对比度、饱和度、面板透明度、遮罩、模糊和 Scene 捕获比例。
- 选好方案后点击 **Apply preset**。若当前已连接 Codex，可立即看到效果；若未连接，状态栏会明确提示尚未应用。
- 套用预设后，右侧每一项仍可继续手动微调。手动微调只影响当前参数，不会反向覆盖已保存的方案。

### 整理、重命名和查找壁纸

- 在 **Search** 中输入自定义名称、原始名称或收藏夹名称，可立即缩小列表。
- **All types** 下拉框可只显示 Scene、Video、Image、Web 等技术类型；不需要筛选时保持默认即可。
- 选择壁纸后点击 **Rename** 可设置本软件中的显示名称；这不会改动 Steam 工坊项目或本地文件。以后优先按新名称显示和搜索，也仍可用原始名称找回。
- 点击 **Set collection** 可建立任意个人收藏夹，例如“治愈”“动漫”“风景”或“工作”；收藏夹下拉框可只查看其中一组。列表中的收藏夹前缀用于快速辨认。
- “Restore original name”和“Remove from collection”可分别撤销重命名和移出收藏夹。

个人名称和收藏夹保存在独立的本地 `library.json` 中。重新扫描 Wallpaper Engine 不会清除它们，也不会修改或复制任何工坊素材。

## 5. 参数怎么调

新建状态默认启用自动配色、文字协调、视频静音和隐藏暂停，模糊为 `0`。后续启动会恢复上次保存的值，因此每次应用前都应检查当前控件。

| 参数 | 作用 | 建议 |
|---|---|---|
| **Fit** | 控制壁纸覆盖、完整显示或拉伸方式。 | 通常使用 `Cover`。 |
| **Focus X / Y** | 调整裁切焦点；`50%` 为中心，范围扩展至 `-100%～200%`，也可点击数字精确输入。 | 人物或主体被裁掉时再调；超出 `0%～100%` 可能露出边缘，应结合 Fit 使用。 |
| **Background opacity** | 调整背景可见程度。 | 文字不清晰时适当降低。 |
| **Black readability overlay** | 添加黑色可读性遮罩。 | 亮色壁纸可适当提高。 |
| **Brightness / Contrast / Saturation** | 独立调节画面亮度、对比度和饱和度。 | 保持 `100%` 可得到原始色彩。 |
| **Automatically coordinate surfaces, accents and text** | 从壁纸提取一次配色并协调界面。 | 默认开启。 |
| **Palette strength** | 控制壁纸配色对界面的影响强度。 | 颜色太浓时降低。 |
| **Panel opacity** | 调整 Codex 面板的不透明度。 | 内容难读时提高。 |
| **Coordinate inherited interface text** | 协调继承的界面文字颜色。 | 默认开启；代码、终端、警告和状态色不改。 |
| **Blur** | 对背景增加模糊。 | `0` 最省 GPU。 |
| **Animation playback speed** | 调整视频和 Scene 动画速度。 | 通常保持 `100%`。 |
| **Mute video / Wallpaper Engine scene** | 静音视频和高保真 Scene。 | 默认开启。 |
| **Pause video / throttle scene while Codex is hidden** | Codex 隐藏时暂停视频，并显著降低原生 Scene 的采集频率。 | 默认开启，可降低后台消耗。 |
| **Scene quality target** | 控制 Scene 采集目标；高保真传输为稳定与负载安全最高限制在 15 FPS。 | 省电时选 15。 |
| **Live scene render scale** | 按 Codex 视口的 50%–100% 渲染 Scene。 | 降低可明显减少 GPU 和显存占用。 |

点击 **Original color / clarity** 会把背景不透明度、黑色遮罩、亮度、对比度、饱和度和模糊恢复为中性值，不会关闭自动取色或改变面板透明度。

每个滑杆右侧的当前数字都可以点击。弹窗只接受范围内的纯数字（动画速度按 `0.25`–`2.00` 倍输入），确定后滑杆、保存状态和已连接的 Codex 会同步更新。

**Automatically reapply the last wallpaper when this controller opens** 默认开启，只记住最后一次成功 Apply 的项目；应用失败不会覆盖记忆。**Restore at Windows sign-in** 是可选的当前用户启动项。若 Codex 已经在没有 CDP 参数的情况下运行，软件保存待应用项目并在后台等待用户自然退出，不需要再次手工打开调节器。

自动取色只在图片、视频首帧或 Scene 首次渲染画面上进行一次 32×32 采样，不会逐帧持续取色。

## 6. 低负担推荐配置

最低负担方案是静态图片。如果使用视频或 Scene，建议：

- 1080p、24/30 FPS；
- **Blur** 设为 `0`；
- 开启 **Mute video**；
- 开启 **Pause video / throttle scene while Codex is hidden**；
- Scene 选择 15 FPS 目标和较低渲染比例；
- 不需要时恢复原始背景。

本项目不会转码，也不会根据电池、CPU 或 GPU 使用率自动切换预设。Image/Video 由 Codex 独立解码；高保真 Scene 会由 Wallpaper Engine 创建一份专用渲染窗口，因此桌面端同时播放壁纸时会产生额外渲染负载。

## 7. 恢复原始背景

在调节器中点击 **Restore Codex background**。它会移除本项目添加的背景节点、样式、监听器和页面 Blob URL，并验证清理是否完成。

也可以在源码目录运行：

```powershell
pwsh -NoProfile -File .\scripts\reset.ps1
```

普通恢复：

- 不会删除保存的壁纸列表和参数；
- 会清除“最后一次应用”的记忆，避免下次启动又自动加回背景；
- 不会关闭 Codex；
- 不会关闭 CDP 监听端口；
- 不会修改 Codex 安装文件。

要关闭 CDP 端口，请保存未发送内容后完全退出以 CDP 参数启动的 Codex。不要把 `-PurgeLocalState` 用于普通恢复；该参数仅适合明确要删除本项目本地预设和恢复文件时使用。

## 8. 常见问题

### Connect 失败或显示 CDP 不可达

确认普通 Codex 已完全退出，再点击 **Activate with CDP**，等待窗口打开后点击 **Connect**。不要把地址改成 `localhost`、局域网 IP 或远程地址；本项目只接受规范的 `http://127.0.0.1:<端口>`。

### 提示端口监听者不是官方 OpenAI.Codex 包

`0.1.1` 会把安装在非系统盘（例如 `E:\WindowsApps`）的官方 Codex 错误识别成非官方程序；此问题从 `0.1.2` 起已修复。请完全退出旧版调节器，使用当前 `0.3.0` 或更新版本的 `CodexWallpaperSkin.exe`，然后重新点击 **Connect**。新版会核对 Windows 返回的官方包身份和实际可执行文件身份，不依赖 Codex 必须安装在 C 盘。如果仍显示这条提示，不要绕过校验；运行下方只读诊断并提交脱敏后的结果。

### 扫描不到 Wallpaper Engine

确认 Wallpaper Engine 已通过 Steam 安装，订阅项目已经下载完成。点击 **Scan Wallpaper Engine** 重新扫描。也可以先使用 **Add local** 验证普通图片或视频。

### 点击 Apply 后提示 `Injected JavaScript failed` 或 `... is not a function`

这是 `0.2.0` 注入脚本拼接边界的缺陷，会同时影响图片、视频和 Scene；已在 `0.2.1` 修复。请完全关闭旧版 `CodexWallpaperSkin.exe`，换用 `0.2.1` 或更新版本后重新连接。若新版仍出现注入错误，请先点击 **Restore Codex background**，再运行 `--doctor --json` 并提交脱敏后的完整错误文本。

### 壁纸明显变暗，或换回亮色壁纸后界面仍然发灰

`0.2.1` 可能把多个嵌套的全屏容器重复当作半透明面板，深色层叠加后会遮暗壁纸；自动取色暂时失败时还可能保留旧取色状态，重连也可能误选辅助页面。上述问题已在 `0.2.2` 修复。请使用当前 `0.3.0` 或更新版本重新连接并应用壁纸。

### 更新后 Scene 仍保持旧版的放大、模糊、错位或无鼠标水波

旧版尝试在浏览器中近似解释 `scene.pkg`，无法完整实现 Wallpaper Engine 的反馈缓冲、Puppet Warp、作者脚本、粒子等语义，因此“画面在动”也可能只是错误图层。`0.3.0` 对 Scene 改用 Wallpaper Engine 官方离屏渲染，并转发 Codex 鼠标坐标；玛奇玛、Pastel 与 Saki 三份报告素材已按此路径验证。请用 `0.3.0` 重新扫描并 Apply。

### 项目显示 WE LIVE SCENE，但应用后提示部分兼容或静态回退

`WE LIVE SCENE` 表示项目通过路径校验并可交给本机 Wallpaper Engine。请确认 Wallpaper Engine 安装完整且未被安全软件阻止；软件会按用户现有 32/64 位运行架构发送官方 `playInWindow` 命令。原生路径失败时才会明确降级，且不会再声称受限内置渲染等同于原壁纸。

### Application 壁纸无法应用

这是安全限制。Application 项目包含可执行内容，本项目永不启动它。

### 应用后卡顿或耗电增加

先把 **Blur** 调为 `0` 并开启隐藏暂停；再换成较低分辨率、较低帧率的视频，或改用静态图片。自动取色本身不是持续负载。

### Codex 更新后无法应用

先使用 **Restore Codex background**。项目在无法验证官方页面结构时会拒绝注入，不要通过放宽进程、来源或页面校验来强行兼容。可运行只读诊断并在项目仓库提交脱敏后的兼容性报告。

便携目录：

```powershell
.\CodexWallpaperSkin.exe --doctor --json
```

源码目录：

```powershell
pwsh -NoProfile -File .\scripts\doctor.ps1 -Json
```

## 9. 隐私与安全边界

- 媒体只通过 `127.0.0.1` 进入 Codex 渲染器内存，不发送到互联网或局域网服务。
- 项目不会读取聊天、登录状态、Cookie、API Key 或浏览器存储。
- 项目不会修改 `WindowsApps`、`app.asar`、官方签名或 Codex 配置文件。
- CDP 端口虽然只监听回环地址，但同一 Windows 用户会话中的其他进程仍可能访问它；只在可信环境中启用。
- 项目不附带或重新分发 Wallpaper Engine 素材。用户需要拥有 Wallpaper Engine，并遵守壁纸作者许可。
