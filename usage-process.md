# Codex Wallpaper Skin 具体操作流程

本文面向普通用户，说明如何安装、连接 Codex、加载 Wallpaper Engine 壁纸、调整效果以及恢复原始背景。

## 一分钟流程

1. 运行 `CodexWallpaperSkin-Setup-vX.Y.Z-win-x64.exe` 完成当前用户安装；也可解压便携 ZIP 后直接运行。
2. 点击 **Start / reconnect Codex**；检测、受控启动和重新连接均自动完成。
3. 如果 Codex 已普通启动，请先手动关闭 Codex，再点击 **Start / reconnect Codex**；软件不会自动关闭或重启 Codex。
4. 点击 **Scan Wallpaper Engine**，选择壁纸，检查参数后点击 **Apply selected**。
5. 不再使用时点击 **Restore Codex background**；若还要关闭 CDP 端口，请完全退出该 Codex 进程。

## 1. 使用前提

- Windows 11 x64。
- 官方 x64 `OpenAI.Codex` Store/MSIX 桌面版。
- 如需使用 Wallpaper Engine 项目，必须已经在 Steam 中安装 Wallpaper Engine，并等待订阅壁纸下载完成。
- 当前版本不支持非打包版、改名副本、ARM64 或 x86 Codex。
- 社区发布的 EXE 可能没有代码签名并触发 SmartScreen。只运行你信任的 Release，不要全局关闭 Windows 安全功能。

## 2. 安装 GUI 软件

推荐下载安装器 `CodexWallpaperSkin-Setup-vX.Y.Z-win-x64.exe` 和旁边的 `.sha256` 文件。安装器只写入当前用户的 `%LOCALAPPDATA%\Programs\Codex Wallpaper Skin`，无需管理员权限，并创建开始菜单入口；桌面快捷方式为可选项。

1. 对下载的安装器计算校验值：

   ```powershell
   Get-FileHash "D:\Downloads\CodexWallpaperSkin-Setup-vX.Y.Z-win-x64.exe" -Algorithm SHA256
   ```

2. 将结果与 `.sha256` 文件中的值比较。
3. 运行安装器，完成后从开始菜单打开 **Codex Wallpaper Skin**。

发布目录中的 `release-manifest.json` 还会列出安装器和便携 ZIP 的文件大小、SHA-256、源码提交与数字签名状态。它用于审计和自动化校验，不能替代可信下载来源或有效数字签名。

如需免安装使用，可改为下载便携 ZIP，完整解压到可信目录后运行 `CodexWallpaperSkin.exe`。安装版与便携版都是自包含程序，普通使用不需要安装 Python、PyYAML、Node.js 或 .NET。

### 升级、回滚与卸载

- **升级**：先从托盘选择 **Remove wallpaper and exit** 退出控制器，再运行新版安装器。安装器只替换程序文件，保留 `%LOCALAPPDATA%\CodexWallpaperSkin` 中的壁纸库、预设、设置和日志，不会关闭 Codex。
- **回滚**：同样先退出控制器，再运行你保留的旧版安装器。程序状态带版本保护；如果旧版提示状态格式较新，它会停止写入而不会破坏文件，此时重新安装新版即可。发布前必须对相邻版本执行一次升级与回滚测试。
- **卸载**：在 Windows“已安装的应用”中卸载。默认建议保留壁纸库、预设和设置，方便以后重装；选择彻底删除时，只删除 `%LOCALAPPDATA%\CodexWallpaperSkin`，不会删除本地壁纸、Wallpaper Engine 项目或任何 Codex 数据。
- 若控制器仍在运行，安装器或卸载器会要求先从托盘退出，不会强制结束控制器，也不会结束 Codex。

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

### 3.2 自动启动或连接 Codex

Codex 必须带有仅监听 `127.0.0.1` 的 CDP 参数，调节器才能添加背景层。

1. 点击 **Start / reconnect Codex**。调节器只接受官方 `OpenAI.Codex` 包身份，并会自动连接或用回环 CDP 启动它。
2. 如果普通 Codex 已经运行，调节器会立即识别，不会等待 30 秒、强制退出或干扰当前任务。
3. 若普通 Codex 已运行，调节器会明确提示“请手动关闭 Codex，然后点击 Start / reconnect Codex”。已选壁纸会保留，但不启动自动关闭、超时等待或自动重开流程。
4. 普通使用不需要手动输入端口或 AUMID；这些项目仅保留在折叠的 **Advanced connection settings** 中用于诊断。

Windows 无法把 Chromium 调试参数动态补到已经运行的进程，因此真正的即时安全注入在这种状态下不可行。为避免卡顿和误操作，软件不再请求关闭 Codex；用户手动关闭后，统一通过 **Start / reconnect Codex** 重新打开。Windows 登录自动恢复仍为可选项。

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
   | `[WE LIVE SCENE]` | 优先由 Wallpaper Engine 原生渲染并桥接动画；当前稳定 H.264 路径不转发鼠标互动。 |
   | `[ANIMATED PREVIEW]` | Scene/Web 的包或原始媒体不可用时，使用经过验证的 GIF 预览。 |
   | `[STATIC FALLBACK]` | 使用经过验证的静态预览。 |
   | 不可应用 | Application 或无法安全验证的项目会被拒绝。 |

4. 选择项目后，在右侧检查参数。
5. 点击 **Apply selected**，等待进度完成和成功提示。

Image/Video 项目直接使用已经下载到本机的素材。Scene 项目会启动 Wallpaper Engine 的私有离屏窗口并保持后台运行；Puppet Warp、粒子、作者脚本和音频响应仍由官方引擎处理，但当前稳定 H.264 路径不会把 Codex 鼠标输入转发给壁纸。原生后端不可用时，底部状态栏会明确说明已使用受限内置渲染器、包内纹理或预览回退。

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
- 点击 **Assign preset to wallpaper** 可把当前预设绑定到所选壁纸。以后应用或恢复该壁纸时会先自动载入对应预设；删除预设时相关绑定会安全清除。

### 性能档位

- **Power saver / 省电**：30 FPS、60% Scene 比例、隐藏时限速。
- **Balanced / 均衡**：30 FPS、85% Scene 比例、隐藏时限速。
- **High quality / 高画质**：60 FPS、100% Scene 比例、隐藏时限速。
- 修改单独的质量参数后会显示为 **Custom / 自定义**，不会锁住任何控件。

60 FPS 模式会提高本地 H.264 码率预算，并按时间戳配合屏幕刷新呈现画面。由于 Codex 当前只允许通过 CDP 发送压缩视频批次，画质仍不可能与 Wallpaper Engine 直接桌面合成做到逐像素一致；如果 Doctor 显示实际编码或显示 FPS 明显低于目标，可用其中的平均捕获/编码耗时判断瓶颈，而不必只凭肉眼猜测。

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
| **Scene quality target** | 控制 Scene 采集目标；实际帧率不会超过 Wallpaper Engine 的全局限制。 | 省电时选 30。 |
| **Live scene render scale** | 按 Codex 视口的 50%–100% 渲染 Scene。 | 降低可明显减少 GPU 和显存占用。 |

点击 **Original color / clarity** 会把背景不透明度、黑色遮罩、亮度、对比度、饱和度和模糊恢复为中性值，不会关闭自动取色或改变面板透明度。

每个滑杆右侧的当前数字都可以点击。弹窗只接受范围内的纯数字（动画速度按 `0.25`–`2.00` 倍输入），确定后滑杆、保存状态和已连接的 Codex 会同步更新。

**Automatically reapply the last wallpaper when this controller opens** 默认开启，只记住最后一次成功 Apply 的项目；应用失败不会覆盖记忆。Windows 登录恢复是可选的当前用户启动项。若 Codex 已经在没有 CDP 参数的情况下运行，软件会保存待应用项目，但不会关闭、等待关闭或重新打开 Codex；请手动关闭 Codex，再点击 **Start / reconnect Codex**。

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

## 6.1 诊断包

点击顶部 **Doctor**，可以看到 CDP、编码器和动态流看门狗的只读状态。使用 GUI 打开时，报告还会显示当前健康分类、捕获/编码/接收/显示计数与实际 FPS、捕获分辨率、目标码率、平均捕获/编码耗时、自动恢复次数，以及最近一次恢复原因和时间；命令行独立运行 Doctor 时没有附着实时控制器，因此这部分会明确显示不可用。报告使用可分享隐私模式：用户名、本地绝对路径、页面标识符、会话 URL 参数和可能带有任务内容的 Codex 窗口标题都会被替换。

再点击 **Export diagnostic package…** 可导出 ZIP。诊断包包含该只读报告和经过相同路径脱敏的滚动控制器日志，不包含壁纸媒体、完整 `state.json`、认证信息、对话内容或 Codex 任务标题。发送给他人前仍建议自行检查内容。

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

### Start / reconnect Codex 失败或显示 CDP 不可达

点击 **Start / reconnect Codex**。如果 Codex 已普通启动，软件会提示你手动关闭 Codex；关闭后再次点击同一个按钮。软件不会排队自动关闭或自动重启 Codex。只有排障时才展开高级设置，不要把地址改成 `localhost`、局域网 IP 或远程地址。

### 提示端口监听者不是官方 OpenAI.Codex 包

`0.1.1` 会把安装在非系统盘（例如 `E:\WindowsApps`）的官方 Codex 错误识别成非官方程序；此问题从 `0.1.2` 起已修复。请完全退出旧版调节器，使用当前版本的 `CodexWallpaperSkin.exe`，然后重新点击 **Start / reconnect Codex**。新版会核对 Windows 返回的官方包身份和实际可执行文件身份，不依赖 Codex 必须安装在 C 盘。如果仍显示这条提示，不要绕过校验；运行下方只读诊断并提交脱敏后的结果。

### 扫描不到 Wallpaper Engine

确认 Wallpaper Engine 已通过 Steam 安装，订阅项目已经下载完成。点击 **Scan Wallpaper Engine** 重新扫描。也可以先使用 **Add local** 验证普通图片或视频。

### 点击 Apply 后提示 `Injected JavaScript failed` 或 `... is not a function`

这是 `0.2.0` 注入脚本拼接边界的缺陷，会同时影响图片、视频和 Scene；已在 `0.2.1` 修复。请完全关闭旧版 `CodexWallpaperSkin.exe`，换用 `0.2.1` 或更新版本后重新连接。若新版仍出现注入错误，请先点击 **Restore Codex background**，再运行 `--doctor --json` 并提交脱敏后的完整错误文本。

### 壁纸明显变暗，或换回亮色壁纸后界面仍然发灰

`0.2.1` 可能把多个嵌套的全屏容器重复当作半透明面板，深色层叠加后会遮暗壁纸；自动取色暂时失败时还可能保留旧取色状态，重连也可能误选辅助页面。上述问题已在 `0.2.2` 修复。请使用当前 `0.3.0` 或更新版本重新连接并应用壁纸。

### 更新后 Scene 仍保持旧版的放大、模糊或错位

旧版尝试在浏览器中近似解释 `scene.pkg`，无法完整实现 Wallpaper Engine 的反馈缓冲、Puppet Warp、作者脚本、粒子等语义，因此“画面在动”也可能只是错误图层。当前版本对 Scene 改用 Wallpaper Engine 官方离屏渲染，但稳定 H.264 路径不转发 Codex 鼠标坐标。请使用当前版本重新扫描并点击 **Apply selected**。

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
