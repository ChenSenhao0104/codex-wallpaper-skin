using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace CodexWallpaperSkin;

public static class UiLanguage
{
    private static readonly IReadOnlyDictionary<string, string> EnglishToChinese =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Codex connection"] = "Codex 连接",
            ["One button handles normal startup and reconnection."] = "一个按钮即可启动或重新连接 Codex。",
            ["Start / reconnect Codex"] = "启动 / 重新连接 Codex",
            ["Reconnect Codex"] = "重新连接 Codex",
            ["Doctor"] = "诊断",
            ["Advanced connection settings"] = "高级连接设置",
            ["Activate endpoint"] = "启用端点",
            ["Detect app"] = "检测应用",
            ["Wallpapers"] = "壁纸",
            ["Add local"] = "添加本地壁纸",
            ["Scan Wallpaper Engine"] = "扫描 Wallpaper Engine",
            ["Search"] = "搜索",
            ["Type filter"] = "类型筛选",
            ["Personal collection"] = "个人分类",
            ["Rename"] = "重命名",
            ["Set collection"] = "设置分类",
            ["Select a wallpaper"] = "请选择壁纸",
            ["Background settings"] = "背景设置",
            ["Original color / clarity"] = "原始色彩 / 清晰度",
            ["Fit"] = "适配方式",
            ["Show the complete wallpaper (may add margins)"] = "显示完整壁纸（可能出现留白）",
            ["Focus X"] = "水平焦点",
            ["Focus Y"] = "垂直焦点",
            ["Background opacity"] = "背景不透明度",
            ["Black readability overlay"] = "黑色可读性遮罩",
            ["Brightness"] = "亮度",
            ["Contrast"] = "对比度",
            ["Saturation"] = "饱和度",
            ["Automatically coordinate surfaces, accents and text"] = "自动协调界面、强调色和文字",
            ["Palette strength"] = "配色强度",
            ["Panel opacity"] = "面板不透明度",
            ["Coordinate inherited interface text (code colors stay intact)"] = "协调界面文字（保留代码颜色）",
            ["Blur (0 = lowest GPU cost)"] = "模糊（0 = GPU 占用最低）",
            ["Blur"] = "模糊",
            ["Animation playback speed"] = "动画播放速度",
            ["Reset playback speed to 1.00×"] = "恢复为 1.00× 速度",
            ["Mute video / Wallpaper Engine scene"] = "静音视频 / Wallpaper Engine 场景",
            ["Pause video / throttle scene while Codex is hidden"] = "Codex 隐藏时暂停视频 / 限制场景",
            ["Scene quality target (native capture is safely capped)"] = "场景质量目标（原生捕获会安全限制）",
            ["Live scene render scale"] = "动态场景渲染比例",
            ["Visual preset"] = "视觉预设",
            ["Apply preset"] = "应用预设",
            ["Manage presets…"] = "管理预设…",
            ["Apply selected"] = "应用所选壁纸",
            ["Restore Codex background"] = "恢复 Codex 背景",
            ["Automatically reapply the last wallpaper when this controller opens"] = "控制器打开时自动重新应用上次壁纸",
            ["Remember the last wallpaper across Windows restarts (starts Codex if needed)"] = "Windows 重启后仍记住上次壁纸（必要时启动 Codex）",
            ["All types"] = "所有类型",
            ["Scenes"] = "场景",
            ["Videos"] = "视频",
            ["Images"] = "图片",
            ["Web"] = "网页",
            ["Applications"] = "应用程序",
            ["Unknown"] = "未知",
            ["All collections"] = "所有分类",
            ["Ungrouped"] = "未分类",
            ["Open adjustment window"] = "打开调节窗口",
            ["Keep wallpaper running and hide this icon"] = "保持壁纸运行并隐藏此图标",
            ["Remove wallpaper and exit"] = "移除壁纸并退出",
            ["Remove wallpaper and exit?"] = "要移除壁纸并退出吗？",
            ["The controller will make one quick cleanup attempt, then exit even if Codex is already closed or unavailable."] = "控制器将快速尝试一次清理，即使 Codex 已关闭或无法连接也会立即退出。",
            ["Cancel"] = "取消",
            ["OK"] = "确定",
            ["Save"] = "保存",
            ["Close"] = "关闭",
            ["Copy"] = "复制",
            ["Manage visual presets"] = "管理视觉预设",
            ["Create named presets for wallpapers with different brightness and colors. Applying a preset never prevents later manual adjustment."] = "为不同亮度和色彩的壁纸创建命名预设。应用预设后仍可继续手动调整。",
            ["New"] = "新建",
            ["Delete"] = "删除",
            ["Preset name"] = "预设名称",
            ["Scene capture scale"] = "场景捕获比例",
            ["Restore built-in values"] = "恢复内置数值",
            ["Save preset library"] = "保存预设库",
            ["Enter exact value"] = "输入精确数值",
            ["Exact value"] = "精确数值",
            ["Read-only diagnostic report"] = "只读诊断报告",
            ["Codex Wallpaper Skin Doctor"] = "Codex Wallpaper Skin 诊断",
            ["At most 32 visual presets can be saved."] = "最多可保存 32 个视觉预设。",
            ["Keep at least one visual preset."] = "请至少保留一个视觉预设。",
            ["Delete visual preset"] = "删除视觉预设",
            ["Preset name must contain 1 to 64 characters."] = "预设名称必须包含 1 到 64 个字符。",
            ["Preset names must be unique."] = "预设名称不能重复。",
            ["Enter a valid number."] = "请输入有效数字。",
            ["Allowed range:"] = "允许范围：",
            ["Language changed to English."] = "已切换为中文。",
            ["Codex is already open without the wallpaper channel. Please close Codex manually, then click Start / reconnect Codex. The controller will never close Codex for you."] = "Codex 已在未启用壁纸通道的状态下打开。请手动关闭 Codex，然后点击“启动 / 重新连接 Codex”。控制器绝不会替你关闭 Codex。",
            ["No wallpaper was removed because Codex is already closed or its wallpaper channel is unavailable. The controller will exit now."] = "Codex 已关闭或壁纸通道不可用，因此未执行页面清理。控制器现在将直接退出。",
            ["Ready. Connect to a loopback CDP endpoint to apply a background."] = "已就绪。连接本机 CDP 端点后即可应用背景。",
            ["Performance: v0.4 targets 60 FPS with a 30 FPS fallback using Windows hardware H.264 and a persistent browser decoder. The last valid frame remains visible during brief capture or decode stalls. JPEG/CDP is retained only as an explicit compatibility backend."] = "性能：v0.4 使用 Windows 硬件 H.264 和持久浏览器解码器，目标为 60 FPS，并以 30 FPS 作为后备。捕获或解码短暂停顿时会保留最后一帧。JPEG/CDP 仅作为显式兼容后端。",
            ["Automatically connects to Codex or starts it with the local wallpaper channel."] = "自动连接 Codex，或使用本地壁纸通道启动它。",
            ["Change only the name shown in this app"] = "仅修改本软件中显示的名称",
            ["Organize this wallpaper in a personal collection"] = "将此壁纸归入个人分类",
            ["Search the current custom name, original name, or collection"] = "搜索自定义名称、原始名称或分类",
            ["Filter by wallpaper type"] = "按壁纸类型筛选",
            ["Show all wallpapers or one personal collection"] = "显示全部壁纸或某个人分类",
            ["Click to enter an exact value"] = "点击输入精确数值",
            ["Choose one of your named visual presets."] = "选择一个已命名的视觉预设。",
            ["Applies the selected preset. Every value remains editable afterward."] = "应用所选预设，之后所有数值仍可编辑。",
            ["Create, name, edit, delete, or restore visual presets."] = "创建、命名、编辑、删除或恢复视觉预设。",
            ["Adds or removes a per-user Windows startup entry. No administrator permission is required."] = "添加或移除当前用户的 Windows 启动项，无需管理员权限。",
            ["Adjusts ordinary videos and Wallpaper Engine Scene playback from 0.25× to 2.00×."] = "将普通视频和 Wallpaper Engine 场景的播放速度调整为 0.25× 到 2.00×。",
            ["50% is centered. Extended values allow intentional panning beyond the normal CSS edge range."] = "50% 为居中。扩展数值允许超出普通 CSS 边界进行平移。",
            ["Sets media opacity, overlay, brightness, contrast, saturation and blur to neutral values."] = "将媒体不透明度、遮罩、亮度、对比度、饱和度和模糊恢复为中性值。",
            ["Uses Contain and centers the source. A different aspect ratio cannot both fill the whole Codex window and show every edge."] = "使用完整显示并居中图源。宽高比不同时，无法同时填满整个 Codex 窗口并保留所有边缘。",
            ["Loopback only, e.g. http://127.0.0.1:9222"] = "仅限本机回环，例如 http://127.0.0.1:9222",
            ["Official Codex package identity"] = "Codex 官方应用包标识",
            ["Codex Wallpaper Skin is still running"] = "Codex Wallpaper Skin 仍在运行",
            ["The adjustment window is hidden, while the animated wallpaper continues. Double-click the tray icon to reopen it."] = "调节窗口已隐藏，动态壁纸会继续播放。双击托盘图标可重新打开。",
            ["Startup/state recovery needs attention."] = "启动或状态恢复需要注意。",
            ["Windows sign-in restore was updated from the older controller to this version."] = "Windows 登录恢复已从旧控制器更新到当前版本。",
            ["Restoring the last applied wallpaper…"] = "正在恢复上次应用的壁纸…",
            ["Connecting to Codex. If it is closed, the verified local wallpaper channel will be started automatically…"] = "正在连接 Codex。如果 Codex 已关闭，将自动使用已验证的本地壁纸通道启动…",
            ["Looking for Codex in the Windows Start app registry…"] = "正在 Windows 开始应用列表中查找 Codex…",
            ["Wallpaper Engine scan cancelled."] = "已取消 Wallpaper Engine 扫描。",
            ["Scanning Wallpaper Engine project.json files…"] = "正在扫描 Wallpaper Engine project.json 文件…",
            ["Preparing Codex and the selected wallpaper…"] = "正在准备 Codex 和所选壁纸…",
            ["Cancelled the queued wallpaper restore. No running Codex process was changed."] = "已取消待应用的壁纸，未改动正在运行的 Codex。",
            ["The last successfully applied wallpaper will be restored when this controller opens."] = "此控制器打开时将恢复上次成功应用的壁纸。",
            ["Automatic restore when the controller opens is disabled."] = "已关闭控制器打开时的自动恢复。",
            ["Windows sign-in restore enabled. It will restore immediately when possible, or wait without interrupting an already-open Codex task."] = "已启用 Windows 登录恢复。可行时会立即恢复；Codex 已打开时不会中断当前任务。",
            ["Windows sign-in restore disabled."] = "已关闭 Windows 登录恢复。",
            ["Original media color/clarity restored. Interface palette and panel opacity were left unchanged."] = "已恢复媒体的原始色彩和清晰度，界面配色和面板不透明度保持不变。",
            ["Complete-wallpaper fit selected. Every edge is preserved; margins may appear when the wallpaper and Codex window use different aspect ratios."] = "已选择完整壁纸适配。所有边缘都会保留；壁纸与 Codex 窗口宽高比不同时可能出现留白。",
            ["Operation cancelled."] = "操作已取消。",
            ["Cancelling the active operation before closing…"] = "正在取消当前操作后关闭…",
            ["The current in-memory adjustment remains active."] = "当前内存中的调整仍在生效。",
            ["Codex did not answer the local connection request in time. Wait a moment, then click Start / reconnect Codex."] = "Codex 未及时响应本地连接请求。请稍候，然后点击“启动 / 重新连接 Codex”。",
            ["The previous Codex wallpaper channel is no longer running. Click Start / reconnect Codex to create a fresh connection."] = "之前的 Codex 壁纸通道已不再运行。请点击“启动 / 重新连接 Codex”创建新连接。"
        };

    private static readonly IReadOnlyDictionary<string, string> ChineseToEnglish =
        EnglishToChinese
            .GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal);

    public static bool IsChinese { get; private set; }

    public static string DefaultCode =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? "zh-CN"
            : "en-US";

    public static string Code => IsChinese ? "zh-CN" : "en-US";

    public static void Set(string? code) =>
        IsChinese = code?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true;

    public static string Text(string value)
    {
        if (IsChinese)
        {
            return EnglishToChinese.TryGetValue(value, out var translated) ? translated : value;
        }
        return ChineseToEnglish.TryGetValue(value, out var english) ? english : value;
    }

    public static string Format(string englishFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Text(englishFormat), args);

    public static void Apply(Window window)
    {
        window.Title = Text(window.Title);
        ApplyElement(window);
    }

    private static void ApplyElement(DependencyObject parent)
    {
        if (parent is TextBlock textBlock && !string.IsNullOrWhiteSpace(textBlock.Text))
            textBlock.Text = Text(textBlock.Text);
        if (parent is ContentControl contentControl && contentControl.Content is string content)
            contentControl.Content = Text(content);
        if (parent is HeaderedContentControl headered && headered.Header is string header)
            headered.Header = Text(header);
        if (parent is FrameworkElement element && element.ToolTip is string toolTip)
            element.ToolTip = Text(toolTip);

        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
            ApplyElement(child);
    }
}
