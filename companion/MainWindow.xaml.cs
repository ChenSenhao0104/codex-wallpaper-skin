using Microsoft.Win32;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexWallpaperSkin;

public partial class MainWindow : Window
{
    private const string StaleStartupWarning = "Windows sign-in restore points to another copy of this controller. Turn the sign-in restore option on to update it to this executable.";
    private readonly ObservableCollection<WallpaperEntry> _wallpapers = [];
    private readonly ObservableCollection<VisualPresetProfile> _visualPresets = [];
    private readonly ICollectionView _wallpaperView;
    private readonly Dictionary<string, WallpaperPersonalization> _libraryPersonalizations;
    private readonly CdpInjectionService _injection = new();
    private readonly DispatcherTimer _settingsTimer;
    private AppState _state;
    private bool _loading = true;
    private bool _busy;
    private bool _closeRequested;
    private CancellationTokenSource? _operationCancellation;
    private bool _saveFailureShown;
    private string? _stateWarning;
    private bool _startupChangeGuard;
    private string? _streamRecoveryWallpaperId;
    private int _streamRecoveryAttempts;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _allowExit;
    private bool _trayTipShown;
    private bool _exitSequenceRunning;

    private sealed record WallpaperTypeFilterOption(string Label, WallpaperKind? Kind);
    private sealed record WallpaperCollectionFilterOption(string Label, string? Collection, bool Ungrouped = false);
    private sealed record WallpaperFitOption(string Label, WallpaperFit Value);

    public MainWindow()
    {
        InitializeComponent();
        _state = StateStore.Load();
        UiLanguage.Set(_state.UiLanguage);
        UiLanguage.Apply(this);
        UpdateLanguageButton();
        _libraryPersonalizations = WallpaperLibraryStore.Load();
        _settingsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _settingsTimer.Tick += SettingsTimer_Tick;

        _wallpaperView = CollectionViewSource.GetDefaultView(_wallpapers);
        _wallpaperView.Filter = WallpaperMatchesActiveFilters;
        WallpaperList.ItemsSource = _wallpaperView;
        LoadLocalizedTypeFilter();
        LoadLocalizedFitOptions(_state.Settings.Fit);
        SceneFpsCombo.ItemsSource = new[] { 30, 60 };
        foreach (var profile in _state.VisualPresets) _visualPresets.Add(profile);
        VisualPresetCombo.ItemsSource = _visualPresets;
        VisualPresetCombo.SelectedItem = _visualPresets.FirstOrDefault(item =>
            item.Id.Equals(_state.SelectedVisualPresetId, StringComparison.OrdinalIgnoreCase)) ?? _visualPresets[0];
        EndpointTextBox.Text = _state.CdpBaseUrl;
        AumidTextBox.Text = _state.Aumid ?? string.Empty;
        foreach (var savedItem in _state.Wallpapers)
        {
            var item = savedItem;
            if (savedItem.Source.Equals("Wallpaper Engine", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(savedItem.ProjectPath))
            {
                try
                {
                    item = WallpaperCatalog.ParseProject(savedItem.ProjectPath);
                }
                catch { }
            }
            WallpaperLibraryStore.Apply(item, _libraryPersonalizations);
            _wallpapers.Add(item);
        }
        RefreshCollectionFilter();
        RefreshWallpaperView();
        LoadSettings(_state.Settings);
        AutoRestoreCheck.IsChecked = _state.AutoRestoreOnLaunch;
        var startupRegistrationRepaired = false;
        try
        {
            var startupStatus = StartupRegistration.GetStatus();
            if (startupStatus == StartupRegistrationStatus.StaleExecutable
                && StartupRegistration.TryRepairOwnedRegistration())
            {
                startupStatus = StartupRegistrationStatus.CurrentExecutable;
                startupRegistrationRepaired = true;
            }
            StartupRestoreCheck.IsChecked = startupStatus == StartupRegistrationStatus.CurrentExecutable;
            if (startupStatus == StartupRegistrationStatus.StaleExecutable)
            {
                _stateWarning = StaleStartupWarning;
            }
        }
        catch
        {
            StartupRestoreCheck.IsChecked = false;
        }
        var selected = _wallpapers.FirstOrDefault(item => item.Id == _state.SelectedWallpaperId);
        if (selected is not null)
        {
            WallpaperList.SelectedItem = selected;
        }
        _loading = false;
        UpdateSettingLabels();
        if (!string.IsNullOrWhiteSpace(StateStore.LastLoadWarning))
        {
            _stateWarning = string.IsNullOrWhiteSpace(_stateWarning)
                ? StateStore.LastLoadWarning
                : _stateWarning + Environment.NewLine + StateStore.LastLoadWarning;
        }
        if (!string.IsNullOrWhiteSpace(WallpaperLibraryStore.LastLoadWarning))
        {
            _stateWarning = string.IsNullOrWhiteSpace(_stateWarning)
                ? WallpaperLibraryStore.LastLoadWarning
                : _stateWarning + Environment.NewLine + WallpaperLibraryStore.LastLoadWarning;
        }
        if (!string.IsNullOrWhiteSpace(_stateWarning))
        {
            SetStatus("Startup/state recovery needs attention.");
        }
        else if (startupRegistrationRepaired)
        {
            SetStatus("Windows sign-in restore was updated from the older controller to this version.");
        }
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        Loaded += MainWindow_Loaded;
        InitializeTrayIcon();
    }

    private void Language_Click(object sender, RoutedEventArgs e)
    {
        UiLanguage.Set(UiLanguage.IsChinese ? "en-US" : "zh-CN");
        _state.UiLanguage = UiLanguage.Code;
        UiLanguage.Apply(this);
        UpdateLanguageButton();
        LoadLocalizedTypeFilter();
        LoadLocalizedFitOptions(ReadSettings().Fit);
        RefreshCollectionFilter();
        RefreshWallpaperView();
        InitializeTrayIcon();
        SaveState();
        SetStatus(UiLanguage.Text("Language changed to English."));
    }

    private void UpdateLanguageButton()
    {
        LanguageButton.Content = UiLanguage.IsChinese ? "English" : "中文";
        LanguageButton.ToolTip = UiLanguage.IsChinese
            ? "Switch interface to English"
            : "将界面切换为中文";
    }

    private void LoadLocalizedTypeFilter()
    {
        var selectedKind = (WallpaperTypeFilter.SelectedItem as WallpaperTypeFilterOption)?.Kind;
        WallpaperTypeFilter.ItemsSource = new[]
        {
            new WallpaperTypeFilterOption(UiLanguage.Text("All types"), null),
            new WallpaperTypeFilterOption(UiLanguage.Text("Scenes"), WallpaperKind.Scene),
            new WallpaperTypeFilterOption(UiLanguage.Text("Videos"), WallpaperKind.Video),
            new WallpaperTypeFilterOption(UiLanguage.Text("Images"), WallpaperKind.Image),
            new WallpaperTypeFilterOption(UiLanguage.Text("Web"), WallpaperKind.Web),
            new WallpaperTypeFilterOption(UiLanguage.Text("Applications"), WallpaperKind.Application),
            new WallpaperTypeFilterOption(UiLanguage.Text("Unknown"), WallpaperKind.Unknown)
        };
        WallpaperTypeFilter.SelectedItem = WallpaperTypeFilter.Items
            .Cast<WallpaperTypeFilterOption>()
            .First(item => item.Kind == selectedKind);
    }

    private void LoadLocalizedFitOptions(WallpaperFit selected)
    {
        FitCombo.ItemsSource = Enum.GetValues<WallpaperFit>()
            .Select(value => new WallpaperFitOption(value switch
            {
                WallpaperFit.Cover => UiLanguage.IsChinese ? "覆盖（填满窗口）" : "Cover",
                WallpaperFit.Contain => UiLanguage.IsChinese ? "完整显示" : "Contain",
                WallpaperFit.Fill => UiLanguage.IsChinese ? "拉伸填满" : "Fill",
                WallpaperFit.None => UiLanguage.IsChinese ? "原始尺寸" : "Original size",
                WallpaperFit.ScaleDown => UiLanguage.IsChinese ? "仅缩小" : "Scale down",
                _ => value.ToString()
            }, value))
            .ToArray();
        FitCombo.SelectedItem = FitCombo.Items.Cast<WallpaperFitOption>()
            .First(item => item.Value == selected);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        if (!_state.AutoRestoreOnLaunch || AutoRestoreService.ResolveLastWallpaper(_state) is null)
        {
            return;
        }
        await RunBusyAsync(async cancellationToken =>
        {
            _state.Wallpapers = _wallpapers.ToList();
            UploadProgress.Value = 0;
            UploadProgress.Visibility = Visibility.Visible;
            SetStatus("Restoring the last applied wallpaper…");
            var progress = new Progress<double>(value => UploadProgress.Value = value * 100);
            AutoRestoreResult restored;
            try
            {
                restored = await AutoRestoreService.RestoreAsync(
                    _state, _injection, activateIfNeeded: true, progress, cancellationToken);
            }
            catch (CodexAlreadyRunningWithoutCdpException)
            {
                UploadProgress.Visibility = Visibility.Collapsed;
                var remembered = AutoRestoreService.ResolveLastWallpaper(_state);
                if (remembered is not null) RememberPendingApply(remembered);
                return;
            }
            UploadProgress.Visibility = Visibility.Collapsed;
            EndpointTextBox.Text = _state.CdpBaseUrl;
            AumidTextBox.Text = _state.Aumid ?? string.Empty;
            ConnectButton.Content = UiLanguage.Text("Reconnect Codex");
            var listed = _wallpapers.FirstOrDefault(item => item.Id.Equals(restored.Wallpaper.Id, StringComparison.OrdinalIgnoreCase));
            if (listed is not null)
            {
                WallpaperList.SelectedItem = listed;
            }
            CompleteSuccessfulApply(
                listed ?? restored.Wallpaper,
                _state.Settings,
                restored.ApplyResult,
                "Recovered");
        });
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async cancellationToken =>
        {
            var endpoint = EndpointTextBox.Text.Trim();
            if (!CdpEndpoint.IsLoopbackHttp(endpoint))
            {
                throw new InvalidOperationException("Use a loopback endpoint such as http://127.0.0.1:9222. Remote CDP endpoints are intentionally blocked.");
            }
            _state.CdpBaseUrl = endpoint;
            SetStatus("Connecting to Codex. If it is closed, the verified local wallpaper channel will be started automatically…");
            CdpConnectionResult connection;
            try
            {
                connection = await AutoRestoreService.ConnectOrActivateAsync(
                    _state, _injection, activateIfNeeded: true, cancellationToken);
            }
            catch (CodexAlreadyRunningWithoutCdpException)
            {
                SetManualReconnectStatus();
                return;
            }
            SaveState();
            EndpointTextBox.Text = _state.CdpBaseUrl;
            AumidTextBox.Text = _state.Aumid ?? string.Empty;
            ConnectButton.Content = UiLanguage.Text("Reconnect Codex");
            if (_state.AutoRestoreOnLaunch)
            {
                _state.Wallpapers = _wallpapers.ToList();
                var remembered = AutoRestoreService.ResolveLastWallpaper(_state);
                if (remembered?.CanApply == true)
                {
                    UploadProgress.Value = 0;
                    UploadProgress.Visibility = Visibility.Visible;
                    SetStatus(UiLanguage.IsChinese
                        ? $"已连接，正在恢复 {remembered.DisplayTitle}…"
                        : $"Connected. Restoring {remembered.DisplayTitle}…");
                    var progress = new Progress<double>(value => UploadProgress.Value = value * 100);
                    var result = await _injection.ApplyAsync(remembered, _state.Settings, progress, cancellationToken);
                    UploadProgress.Visibility = Visibility.Collapsed;
                    CompleteSuccessfulApply(remembered, _state.Settings, result, "Recovered");
                    return;
                }
            }
            SetStatus(UiLanguage.IsChinese
                ? connection.ActivatedCodex
                    ? $"Codex 已通过验证的壁纸通道启动并连接：{connection.Target.Title}。"
                    : $"已连接：{connection.Target.Title} — {connection.Target.Url}"
                : connection.ActivatedCodex
                    ? $"Codex was started with the verified wallpaper channel and connected: {connection.Target.Title}."
                    : $"Connected: {connection.Target.Title} — {connection.Target.Url}");
        });
    }

    private async void Doctor_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async cancellationToken =>
        {
            SyncConnectionState();
            var report = await DiagnosticsService.RunAsync(_state, cancellationToken);
            var text = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            var dialog = new DiagnosticWindow(text) { Owner = this };
            dialog.ShowDialog();
            SetStatus(UiLanguage.IsChinese
                ? report.CdpReachable
                    ? $"诊断：CDP 可访问，找到 {report.Targets.Count} 个目标。"
                    : "诊断：CDP 不可访问。请查看报告中的安全启动建议。"
                : report.CdpReachable
                    ? $"Doctor: CDP reachable with {report.Targets.Count} target(s)."
                    : "Doctor: CDP not reachable. See the report for safe startup guidance.");
        });
    }

    private async void DetectAumid_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async cancellationToken =>
        {
            SetStatus("Looking for Codex in the Windows Start app registry…");
            var candidates = await AppActivation.FindCodexCandidatesAsync(cancellationToken);
            if (candidates.Count == 0)
            {
                SetStatus("No Codex AUMID detected. Run Get-StartApps in PowerShell and paste its Codex AppID, or start Codex manually with --remote-debugging-port=9222.");
                return;
            }
            var chosen = candidates.FirstOrDefault(item => item.Name.Contains("Codex", StringComparison.OrdinalIgnoreCase)) ?? candidates[0];
            AumidTextBox.Text = chosen.AppId;
            _state.Aumid = chosen.AppId;
            SaveState();
            SetStatus(candidates.Count == 1
                ? $"Detected {chosen.Name}: {chosen.AppId}"
                : $"Detected {candidates.Count} candidates; selected {chosen.Name}. Verify the AUMID before activation.");
        });
    }

    private async void ActivateWithCdp_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async cancellationToken =>
        {
            SyncConnectionState();
            var runningCodex = CdpProcessIdentity.FindRunningOfficialCodexProcessIds();
            if (runningCodex.Count > 0 && !CdpEndpoint.IsAvailableForActivation(_state.CdpBaseUrl))
            {
                var queued = WallpaperList.SelectedItem as WallpaperEntry
                    ?? AutoRestoreService.ResolveLastWallpaper(_state);
                if (queued?.CanApply == true)
                {
                    _state.Settings = ReadSettings();
                    RememberPendingApply(queued);
                }
                else
                {
                    SetManualReconnectStatus();
                }
                return;
            }
            if (!CdpEndpoint.IsAvailableForActivation(_state.CdpBaseUrl))
            {
                var officialCodexOwnsPort = false;
                try
                {
                    var endpoint = CdpEndpoint.Normalize(_state.CdpBaseUrl);
                    CdpProcessIdentity.EnsureOfficialCodexOwnsPort(endpoint.Port);
                    officialCodexOwnsPort = true;
                }
                catch
                {
                    _state.CdpBaseUrl = CdpEndpoint.CreateUnusedLoopbackUrl();
                    EndpointTextBox.Text = _state.CdpBaseUrl;
                    SaveState();
                    SetStatus($"The previous port was occupied by an unverified process. Selected an unused loopback endpoint: {_state.CdpBaseUrl}");
                }

                if (officialCodexOwnsPort)
                {
                    try
                    {
                        var targets = await CdpDiscovery.GetTargetsAsync(_state.CdpBaseUrl, cancellationToken);
                        var target = CdpDiscovery.SelectCodexPage(targets);
                        SetStatus($"Codex CDP is already available for {target.Title}. Click Connect instead of activating another endpoint.");
                        return;
                    }
                    catch (Exception exception)
                    {
                        SetStatus($"The official Codex process owns this loopback port, but CDP is not ready yet ({exception.Message}). Keep this endpoint, wait a moment, then click Connect.");
                        return;
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            // WPF controls are Dispatcher-bound. Capture their values on the UI
            // thread and pass only immutable strings to the background worker.
            var activationAumid = AumidTextBox.Text.Trim();
            var activationEndpoint = _state.CdpBaseUrl;
            var result = await AppActivation.ActivateWithCdpAsync(
                activationAumid,
                activationEndpoint,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _state.Aumid = activationAumid;
            SaveState();
            SetStatus($"Activation requested (PID {result.ProcessId}). {result.Message} Wait for Codex to open, then click Connect.");
        });
    }

    private void AddLocal_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add a local wallpaper",
            Filter = "Supported wallpapers|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.mp4;*.webm|Images|*.png;*.jpg;*.jpeg;*.webp;*.gif|Videos|*.mp4;*.webm",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            WallpaperEntry? last = null;
            foreach (var file in dialog.FileNames)
            {
                var entry = WallpaperCatalog.CreateLocal(file);
                WallpaperLibraryStore.Apply(entry, _libraryPersonalizations);
                Upsert(entry);
                last = entry;
            }
            RefreshCollectionFilter();
            RefreshWallpaperView();
            SaveState();
            if (last is not null)
            {
                WallpaperList.SelectedItem = _wallpapers.First(item => item.Id == last.Id);
            }
            SetStatus($"Added {dialog.FileNames.Length} local wallpaper(s). Files stay in their original locations.");
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private async void ScanWallpaperEngine_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async cancellationToken =>
        {
            var roots = WallpaperCatalog.DiscoverWorkshopRoots().ToList();
            if (!string.IsNullOrWhiteSpace(_state.WallpaperEngineRoot) && Directory.Exists(_state.WallpaperEngineRoot))
            {
                roots.Insert(0, _state.WallpaperEngineRoot);
            }
            roots = roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (roots.Count == 0)
            {
                var folderDialog = new OpenFolderDialog
                {
                    Title = "Choose Steam library, 431960 workshop folder, or a wallpaper project folder",
                    Multiselect = false
                };
                if (folderDialog.ShowDialog(this) != true)
                {
                    SetStatus("Wallpaper Engine scan cancelled.");
                    return;
                }
                roots.Add(WallpaperCatalog.NormalizeWorkshopRoot(folderDialog.FolderName));
            }

            SetStatus("Scanning Wallpaper Engine project.json files…");
            var found = await Task.Run(
                () => WallpaperCatalog.ScanWorkshopRoots(roots, cancellationToken).ToArray(),
                cancellationToken);
            _state.WallpaperEngineRoot = roots[0];
            var previousEntries = _wallpapers
                .Where(item => item.Source.Equals("Wallpaper Engine", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            var foundIds = found.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = found.Count(item => !previousEntries.ContainsKey(item.Id));
            var removed = previousEntries.Keys.Count(id => !foundIds.Contains(id));
            var updated = found.Length - added;
            foreach (var existing in previousEntries.Values)
            {
                _wallpapers.Remove(existing);
            }
            foreach (var entry in found)
            {
                if (previousEntries.TryGetValue(entry.Id, out var previous))
                {
                    WallpaperLibrary.CopyPersonalization(previous, entry);
                }
                WallpaperLibraryStore.Apply(entry, _libraryPersonalizations);
                Upsert(entry);
            }
            var allIds = _wallpapers.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (_state.SelectedWallpaperId is not null && !allIds.Contains(_state.SelectedWallpaperId))
                _state.SelectedWallpaperId = null;
            if (_state.LastAppliedWallpaperId is not null && !allIds.Contains(_state.LastAppliedWallpaperId))
                _state.LastAppliedWallpaperId = null;
            if (_state.PendingWallpaperId is not null && !allIds.Contains(_state.PendingWallpaperId))
            {
                _state.PendingWallpaperId = null;
                _state.PendingActivation = false;
            }
            RefreshCollectionFilter();
            RefreshWallpaperView();
            SaveState();
            var nativeVideo = found.Count(item => item.Kind == WallpaperKind.Video && item.PreferNativeCapture);
            var direct = found.Count(item => item.Support == WallpaperSupport.Direct && !item.PreferNativeCapture);
            var liveScene = found.Count(item => item.Support == WallpaperSupport.LiveScene);
            var animatedPreview = found.Count(item => item.Support == WallpaperSupport.AnimatedPreview);
            var fallback = found.Count(item => item.Support == WallpaperSupport.StaticPreview);
            var rejected = found.Count(item => item.Support == WallpaperSupport.Rejected);
            SetStatus($"Wallpaper Engine scan synchronized current downloaded subscriptions: {added} added, {updated} updated, {removed} removed. "
                + $"Available modes: {direct} direct, {nativeVideo} native large-video, {liveScene} native/live Scene, "
                + $"{animatedPreview} animated preview, {fallback} static preview, {rejected} rejected.");
        });
    }

    private void WallpaperList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = WallpaperList.SelectedItem as WallpaperEntry;
        ApplyButton.IsEnabled = selected?.CanApply == true;
        RenameWallpaperButton.IsEnabled = selected is not null;
        SetCollectionButton.IsEnabled = selected is not null;
        PreviewImage.Source = null;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        if (selected is null)
        {
            WallpaperDetails.Text = string.Empty;
            PreviewPlaceholder.Text = "Select a wallpaper";
            return;
        }

        _state.SelectedWallpaperId = selected.Id;
        UpdateWallpaperDetails(selected);
        var path = selected.IsScene ? selected.PreviewPath : selected.EffectivePath;
        if (path is not null && IsWpfPreviewImage(path))
        {
            try
            {
                WallpaperCatalog.ValidateMediaFile(path, isVideo: false);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 960;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                PreviewImage.Source = image;
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
                if (selected.IsScene)
                {
                    WallpaperDetails.Text += " Controller preview: Workshop thumbnail only; Apply uses scene.pkg and its original textures.";
                }
            }
            catch
            {
                PreviewPlaceholder.Text = "Preview unavailable here; Chromium may still support this image.";
            }
        }
        else
        {
            PreviewPlaceholder.Text = selected.IsScene
                ? "Live 2D scene selected. The full-resolution scene is rendered inside Codex after Apply."
                : selected.IsVideo
                ? "Video selected. It will loop in Codex after Apply."
                : selected.Support == WallpaperSupport.Rejected
                    ? selected.Note
                    : "WebP/browser preview will appear after Apply.";
        }
        if (!_loading)
        {
            SaveState();
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (WallpaperList.SelectedItem is not WallpaperEntry selected)
        {
            return;
        }
        await RunBusyAsync(async cancellationToken =>
        {
            if (!selected.CanApply)
            {
                throw new InvalidOperationException(selected.Note);
            }

            _settingsTimer.Stop();
            var settings = ReadSettings();
            _state.Settings = settings;
            SaveState();
            UploadProgress.Value = 0;
            UploadProgress.Visibility = Visibility.Visible;
            var progress = new Progress<double>(value => UploadProgress.Value = value * 100);
            WallpaperApplyResult applyResult;
            if (!_injection.IsConnected)
            {
                _state.Wallpapers = _wallpapers.ToList();
                _state.PendingWallpaperId = selected.Id;
                _state.PendingActivation = true;
                SaveState();
                SetStatus("Preparing Codex and the selected wallpaper…");
                try
                {
                    var restored = await AutoRestoreService.RestoreAsync(
                        _state, _injection, activateIfNeeded: true, progress, cancellationToken);
                    applyResult = restored.ApplyResult;
                    EndpointTextBox.Text = _state.CdpBaseUrl;
                    ConnectButton.Content = UiLanguage.Text("Reconnect Codex");
                }
                catch (CodexAlreadyRunningWithoutCdpException)
                {
                    UploadProgress.Visibility = Visibility.Collapsed;
                    RememberPendingApply(selected);
                    return;
                }
            }
            else
            {
                SetStatus(UiLanguage.IsChinese
                    ? $"正在将 {Path.GetFileName(selected.EffectivePath)} 传送到 Codex 渲染器…"
                    : $"Uploading {Path.GetFileName(selected.EffectivePath)} to the Codex renderer…");
                applyResult = await _injection.ApplyAsync(selected, settings, progress, cancellationToken);
            }
            UploadProgress.Visibility = Visibility.Collapsed;
            CompleteSuccessfulApply(selected, settings, applyResult, "Applied");
        });
    }

    private void CompleteSuccessfulApply(
        WallpaperEntry selected,
        WallpaperSettings settings,
        WallpaperApplyResult applyResult,
        string action)
    {
        if (!action.Equals("Recovered", StringComparison.Ordinal))
        {
            _streamRecoveryWallpaperId = selected.Id;
            _streamRecoveryAttempts = 0;
        }
        _state.LastAppliedWallpaperId = selected.Id;
        _state.PendingWallpaperId = null;
        _state.PendingActivation = false;
        SaveState();
        var paletteStatus = settings.AutoPalette
            ? applyResult.Palette is null
                ? UiLanguage.IsChinese ? " 无法取样配色，已保持中性后备方案。" : " Palette sampling was unavailable, so the neutral fallback remains active."
                : UiLanguage.IsChinese
                    ? $" 配色：表面 {applyResult.Palette.Surface}，强调色 {applyResult.Palette.Accent}，文字对比度 {applyResult.Palette.TextContrast:0.0}:1。"
                    : $" Palette: surface {applyResult.Palette.Surface}, accent {applyResult.Palette.Accent}, text contrast {applyResult.Palette.TextContrast:0.0}:1."
            : UiLanguage.IsChinese ? " 自动配色已关闭。" : " Automatic palette is off.";
        var modeStatus = UiLanguage.IsChinese
            ? applyResult.Mode switch
            {
                "live-scene" => " 动态 2D 场景渲染已启用。",
                "scene-partial" => " 动态 2D 场景渲染已启用，不支持的图层已省略。",
                "scene-static" => " 渲染器已使用全分辨率场景纹理后备。",
                "wallpaper-engine-h264" => " Wallpaper Engine 原生渲染已通过 Windows 硬件 H.264 和 Codex WebCodecs 启用。",
                "wallpaper-engine-loopback-jpeg" => " Wallpaper Engine 原生渲染已通过本地二进制兼容流启用。",
                "wallpaper-engine-capture" => " Wallpaper Engine 原生渲染和指针转发已通过低帧率捕获流启用。",
                "animated-preview" => " 低分辨率动态工坊预览已启用。",
                "static-preview" => " 静态工坊预览后备已启用。",
                "video" => " 直接视频播放已启用。",
                _ => " 直接图片显示已启用。"
            }
            : applyResult.Mode switch
            {
                "live-scene" => " Live 2D scene rendering is active.",
                "scene-partial" => " Live 2D scene rendering is active with unsupported layers omitted.",
                "scene-static" => " The renderer used the full-resolution scene texture fallback.",
                "wallpaper-engine-h264" => " Wallpaper Engine native rendering is active through Windows hardware H.264 and Codex WebCodecs.",
                "wallpaper-engine-loopback-jpeg" => " Wallpaper Engine native rendering is active through the local binary compatibility stream.",
                "wallpaper-engine-capture" => " Wallpaper Engine native rendering and pointer forwarding are active through a reduced-frame-rate capture stream.",
                "animated-preview" => " The low-resolution animated Workshop preview is active.",
                "static-preview" => " The static Workshop preview fallback is active.",
                "video" => " Direct video playback is active.",
                _ => " Direct image playback is active."
            };
        var warningStatus = string.IsNullOrWhiteSpace(applyResult.Warning)
            ? string.Empty
            : (UiLanguage.IsChinese ? " 注意：" : " Note: ") + applyResult.Warning;
        var actionStatus = UiLanguage.IsChinese
            ? action.Equals("Recovered", StringComparison.Ordinal) ? "已恢复" : "已应用"
            : action;
        SetStatus(UiLanguage.IsChinese
            ? $"{actionStatus} {selected.DisplayTitle}。{modeStatus}{paletteStatus}{warningStatus} 未修改任何 Codex 文件；Restore 可移除整个壁纸层。"
            : $"{actionStatus} {selected.DisplayTitle}.{modeStatus}{paletteStatus}{warningStatus} No Codex file was changed; Restore removes the whole layer.");
        if (applyResult.Mode is "wallpaper-engine-h264" or "wallpaper-engine-loopback-jpeg" or "wallpaper-engine-capture")
        {
            _ = MonitorCaptureAsync(selected.Id, selected.DisplayTitle, _injection.ActiveCaptureCompletion);
        }
    }

    private async Task MonitorCaptureAsync(string wallpaperId, string title, Task completion)
    {
        try
        {
            await completion;
            // A normal switch finishes the old task immediately before the new
            // lease is installed. Give that transaction time to complete, then
            // warn only if this exact lease is still the current one.
            await Task.Delay(250);
            var shouldRecover = await Dispatcher.InvokeAsync(() =>
            {
                return !_closeRequested
                    && !_busy
                    && _injection.IsConnected
                    && !_injection.HasActiveCapture
                    && ReferenceEquals(_injection.ActiveCaptureCompletion, completion)
                    && string.Equals(_state.LastAppliedWallpaperId, wallpaperId, StringComparison.OrdinalIgnoreCase);
            });
            if (!shouldRecover) return;

            if (!string.Equals(_streamRecoveryWallpaperId, wallpaperId, StringComparison.OrdinalIgnoreCase))
            {
                _streamRecoveryWallpaperId = wallpaperId;
                _streamRecoveryAttempts = 0;
            }
            while (_streamRecoveryAttempts < 2)
            {
                var attempt = ++_streamRecoveryAttempts;
                _injection.RecordRecoveryAttempt();
                await Dispatcher.InvokeAsync(() =>
                    SetStatus($"Recovering the live stream for {title} (attempt {attempt} of 2). The last good frame remains visible…"));
                await Task.Delay(attempt == 1 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(3));
                var recoveryTask = await Dispatcher.InvokeAsync(() =>
                    RecoverStoppedCaptureAsync(
                        wallpaperId,
                        title,
                        attempt == 1 ? completion : null,
                        attempt));
                if (await recoveryTask) return;

                var canRetry = await Dispatcher.InvokeAsync(() =>
                    !_closeRequested
                    && !_busy
                    && _injection.IsConnected
                    && !_injection.HasActiveCapture
                    && string.Equals(_state.LastAppliedWallpaperId, wallpaperId, StringComparison.OrdinalIgnoreCase));
                if (!canRetry) return;
            }
            await Dispatcher.InvokeAsync(() =>
                SetStatus($"The live stream for {title} could not be restored after two bounded attempts. The last good frame was kept; click Apply selected to retry manually."));
        }
        catch
        {
            // Apply/switch/close owns user-facing error reporting. This monitor
            // exists only to surface a stream that ended after Apply succeeded.
        }
    }

    private async Task<bool> RecoverStoppedCaptureAsync(
        string wallpaperId,
        string title,
        Task? stoppedCompletion,
        int attempt)
    {
        if (_busy
            || _closeRequested
            || !_injection.IsConnected
            || _injection.HasActiveCapture
            || (stoppedCompletion is not null
                && !ReferenceEquals(_injection.ActiveCaptureCompletion, stoppedCompletion))
            || !string.Equals(_state.LastAppliedWallpaperId, wallpaperId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var wallpaper = _wallpapers.FirstOrDefault(item =>
            item.Id.Equals(wallpaperId, StringComparison.OrdinalIgnoreCase));
        if (wallpaper?.CanApply != true)
        {
            SetStatus($"The live stream for {title} stopped, and its source is no longer available. The last good frame was kept.");
            return false;
        }

        _busy = true;
        RootGrid.IsEnabled = false;
        using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        try
        {
            var result = await _injection.ApplyAsync(
                wallpaper,
                _state.Settings,
                cancellationToken: recoveryTimeout.Token);
            CompleteSuccessfulApply(wallpaper, _state.Settings, result, "Recovered");
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !_closeRequested)
        {
            SetStatus(
                $"Live-stream recovery {attempt} of 2 for {title} did not complete: {exception.Message} "
                + "The last good frame was kept; recovery remains bounded.");
            return false;
        }
        finally
        {
            _busy = false;
            RootGrid.IsEnabled = true;
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async cancellationToken =>
        {
            SyncConnectionState();
            if (_state.PendingActivation && !CdpEndpoint.IsAvailableForActivation(_state.CdpBaseUrl))
            {
                _state.LastAppliedWallpaperId = null;
                _state.PendingWallpaperId = null;
                _state.PendingActivation = false;
                SaveState();
                DeferredRestoreLauncher.RequestStop();
                SetStatus("Cancelled the queued wallpaper restore. No running Codex process was changed.");
                return;
            }
            var cleanedPages = await _injection.CleanupAllAsync(_state.CdpBaseUrl, cancellationToken);
            _state.LastAppliedWallpaperId = null;
            _state.PendingWallpaperId = null;
            _state.PendingActivation = false;
            SaveState();
            DeferredRestoreLauncher.RequestStop();
            SetStatus(UiLanguage.IsChinese
                ? $"已在 {cleanedPages} 个 Codex 应用页面上恢复原始背景，并移除临时图层、样式修改和 Blob URL。"
                : $"Restored the original Codex background on {cleanedPages} app page(s). Temporary layers, style changes and Blob URLs were removed.");
        });
    }

    private void NumericValue_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock { Tag: string tag }
            || tag.Split('|') is not [var sliderName, var label, var unit, var decimalsText]
            || FindName(sliderName) is not Slider slider
            || !slider.IsEnabled
            || !int.TryParse(decimalsText, out var decimals))
        {
            return;
        }

        var isRate = sliderName == nameof(RateSlider);
        var scale = isRate ? 100d : 1d;
        var dialog = new NumericInputDialog(
            label,
            slider.Value / scale,
            slider.Minimum / scale,
            slider.Maximum / scale,
            unit,
            decimals)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            slider.Value = dialog.Value * scale;
            UpdateSettingLabels();
        }
        e.Handled = true;
    }

    private void RestorePreferenceChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        _state.AutoRestoreOnLaunch = AutoRestoreCheck.IsChecked == true;
        SaveState();
        SetStatus(_state.AutoRestoreOnLaunch
            ? "The last successfully applied wallpaper will be restored when this controller opens."
            : "Automatic restore when the controller opens is disabled.");
    }

    private void StartupRestoreChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _startupChangeGuard)
        {
            return;
        }
        var enabled = StartupRestoreCheck.IsChecked == true;
        try
        {
            StartupRegistration.SetEnabled(enabled);
            if (enabled && !string.IsNullOrWhiteSpace(_stateWarning))
            {
                var remainingWarnings = _stateWarning
                    .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => !line.Equals(StaleStartupWarning, StringComparison.Ordinal))
                    .ToArray();
                _stateWarning = remainingWarnings.Length == 0
                    ? null
                    : string.Join(Environment.NewLine, remainingWarnings);
            }
            SetStatus(enabled
                ? "Windows sign-in restore enabled. It will restore immediately when possible, or wait without interrupting an already-open Codex task."
                : "Windows sign-in restore disabled.");
        }
        catch (Exception exception)
        {
            _startupChangeGuard = true;
            StartupRestoreCheck.IsChecked = !enabled;
            _startupChangeGuard = false;
            ShowError("Windows startup preference could not be changed: " + exception.Message);
        }
    }

    private void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded)
        {
            return;
        }
        UpdateSettingLabels();
        _state.Settings = ReadSettings();
        _settingsTimer.Stop();
        _settingsTimer.Start();
    }

    private async void SettingsTimer_Tick(object? sender, EventArgs e)
    {
        _settingsTimer.Stop();
        _state.Settings = ReadSettings();
        SaveState();
        if (_busy || !_injection.IsConnected)
        {
            return;
        }
        try
        {
            await _injection.UpdateSettingsAsync(_state.Settings);
        }
        catch (Exception exception)
        {
            SetStatus("Live settings update failed: " + exception.Message);
        }
    }

    private WallpaperSettings ReadSettings()
    {
        return new WallpaperSettings
        {
            Fit = FitCombo.SelectedItem is WallpaperFitOption fit ? fit.Value : WallpaperFit.Cover,
            FocusX = FocusXSlider.Value,
            FocusY = FocusYSlider.Value,
            Opacity = OpacitySlider.Value / 100,
            BlackOverlay = OverlaySlider.Value / 100,
            AutoPalette = AutoPaletteCheck.IsChecked == true,
            PaletteStrength = PaletteStrengthSlider.Value / 100,
            PanelOpacity = PanelOpacitySlider.Value / 100,
            TintInterfaceText = TintTextCheck.IsChecked == true,
            Blur = BlurSlider.Value,
            Brightness = BrightnessSlider.Value / 100,
            Contrast = ContrastSlider.Value / 100,
            Saturation = SaturationSlider.Value / 100,
            PlaybackRate = RateSlider.Value / 100,
            Muted = MutedCheck.IsChecked == true,
            PauseWhenHidden = PauseHiddenCheck.IsChecked == true,
            SceneFrameRate = SceneFpsCombo.SelectedItem is int frameRate ? frameRate : 60,
            SceneResolutionScale = SceneScaleSlider.Value / 100
        }.Normalize();
    }

    private void LoadSettings(WallpaperSettings settings)
    {
        FitCombo.SelectedItem = FitCombo.Items.Cast<WallpaperFitOption>()
            .FirstOrDefault(item => item.Value == settings.Fit);
        FocusXSlider.Value = settings.FocusX;
        FocusYSlider.Value = settings.FocusY;
        OpacitySlider.Value = settings.Opacity * 100;
        OverlaySlider.Value = settings.BlackOverlay * 100;
        AutoPaletteCheck.IsChecked = settings.AutoPalette;
        PaletteStrengthSlider.Value = settings.PaletteStrength * 100;
        PanelOpacitySlider.Value = settings.PanelOpacity * 100;
        TintTextCheck.IsChecked = settings.TintInterfaceText;
        BlurSlider.Value = settings.Blur;
        BrightnessSlider.Value = settings.Brightness * 100;
        ContrastSlider.Value = settings.Contrast * 100;
        SaturationSlider.Value = settings.Saturation * 100;
        RateSlider.Value = settings.PlaybackRate * 100;
        MutedCheck.IsChecked = settings.Muted;
        PauseHiddenCheck.IsChecked = settings.PauseWhenHidden;
        SceneFpsCombo.SelectedItem = settings.SceneFrameRate;
        SceneScaleSlider.Value = settings.SceneResolutionScale * 100;
    }

    private void UpdateSettingLabels()
    {
        FocusXValue.Text = $"{FocusXSlider.Value:0.##}%";
        FocusYValue.Text = $"{FocusYSlider.Value:0.##}%";
        OpacityValue.Text = $"{OpacitySlider.Value:0.##}%";
        OverlayValue.Text = $"{OverlaySlider.Value:0.##}%";
        BrightnessValue.Text = $"{BrightnessSlider.Value:0.##}%";
        ContrastValue.Text = $"{ContrastSlider.Value:0.##}%";
        SaturationValue.Text = $"{SaturationSlider.Value:0.##}%";
        PaletteStrengthValue.Text = $"{PaletteStrengthSlider.Value:0.##}%";
        PanelOpacityValue.Text = $"{PanelOpacitySlider.Value:0.##}%";
        var paletteEnabled = AutoPaletteCheck.IsChecked == true;
        PaletteStrengthSlider.IsEnabled = paletteEnabled;
        PanelOpacitySlider.IsEnabled = true;
        TintTextCheck.IsEnabled = paletteEnabled;
        BlurValue.Text = $"{BlurSlider.Value:0}px";
        RateValue.Text = $"{RateSlider.Value / 100:0.00}×";
        SceneScaleValue.Text = $"{SceneScaleSlider.Value:0}%";
    }

    private void OriginalFidelity_Click(object sender, RoutedEventArgs e)
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            OpacitySlider.Value = 100;
            OverlaySlider.Value = 0;
            BrightnessSlider.Value = 100;
            ContrastSlider.Value = 100;
            SaturationSlider.Value = 100;
            BlurSlider.Value = 0;
        }
        finally
        {
            _loading = wasLoading;
        }
        UpdateSettingLabels();
        _state.Settings = ReadSettings();
        _settingsTimer.Stop();
        _settingsTimer.Start();
        SetStatus("Original media color/clarity restored. Interface palette and panel opacity were left unchanged.");
    }

    private async void ApplyVisualPreset_Click(object sender, RoutedEventArgs e)
    {
        var profile = VisualPresetCombo.SelectedItem as VisualPresetProfile ?? _visualPresets.First();
        var preset = profile.Settings.Normalize();
        var wasLoading = _loading;
        _loading = true;
        try
        {
            OpacitySlider.Value = preset.Opacity * 100;
            OverlaySlider.Value = preset.BlackOverlay * 100;
            BrightnessSlider.Value = preset.Brightness * 100;
            ContrastSlider.Value = preset.Contrast * 100;
            SaturationSlider.Value = preset.Saturation * 100;
            BlurSlider.Value = preset.Blur;
            SceneScaleSlider.Value = preset.SceneResolutionScale * 100;
            PanelOpacitySlider.Value = preset.PanelOpacity * 100;
        }
        finally
        {
            _loading = wasLoading;
        }
        UpdateSettingLabels();
        _state.Settings = ReadSettings();
        _settingsTimer.Stop();
        SaveState();

        await RunBusyAsync(async cancellationToken =>
        {
            if (!_injection.IsConnected
                && CdpEndpoint.IsLoopbackHttp(_state.CdpBaseUrl)
                && !CdpEndpoint.IsAvailableForActivation(_state.CdpBaseUrl))
            {
                try
                {
                    await _injection.ConnectAsync(_state.CdpBaseUrl, cancellationToken);
                    ConnectButton.Content = UiLanguage.Text("Reconnect Codex");
                }
                catch
                {
                    // The preset remains saved and will be applied by the next
                    // successful Apply. Do not misreport it as live.
                }
            }

            if (_injection.IsConnected)
            {
                await _injection.UpdateSettingsAsync(_state.Settings, cancellationToken);
                SetStatus($"Visual preset ‘{profile.Name}’ applied to Codex now. The capture-scale value takes full effect on the next wallpaper Apply; every control remains editable.");
            }
            else
            {
                SetStatus($"Visual preset ‘{profile.Name}’ selected but not applied: this window is not connected to the current Codex wallpaper channel. Its values will be used on the next successful Apply.");
            }
        });
    }

    private void VisualPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || VisualPresetCombo.SelectedItem is not VisualPresetProfile profile) return;
        _state.SelectedVisualPresetId = profile.Id;
        SaveState();
        SetStatus($"Visual preset ‘{profile.Name}’ selected. Click Apply preset to use it.");
    }

    private void VisualPresetSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new VisualPresetDialog(_visualPresets, _state.SelectedVisualPresetId) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _visualPresets.Clear();
        foreach (var profile in dialog.Profiles.Select(item => item.Copy().Normalize()))
            _visualPresets.Add(profile);
        _state.VisualPresets = _visualPresets.ToList();
        _state.SelectedVisualPresetId = dialog.SelectedProfileId;
        VisualPresetCombo.SelectedItem = _visualPresets.FirstOrDefault(item =>
            item.Id.Equals(dialog.SelectedProfileId, StringComparison.OrdinalIgnoreCase)) ?? _visualPresets[0];
        SaveState();
        SetStatus($"Visual preset library saved ({_visualPresets.Count} presets). Select one and click Apply preset; every right-side control remains editable afterward.");
    }

    private void ShowCompleteWallpaper_Click(object sender, RoutedEventArgs e)
    {
        FitCombo.SelectedItem = FitCombo.Items.Cast<WallpaperFitOption>()
            .First(item => item.Value == WallpaperFit.Contain);
        FocusXSlider.Value = 50;
        FocusYSlider.Value = 50;
        UpdateSettingLabels();
        _state.Settings = ReadSettings();
        _settingsTimer.Stop();
        _settingsTimer.Start();
        SetStatus("Complete-wallpaper fit selected. Every edge is preserved; margins may appear when the wallpaper and Codex window use different aspect ratios.");
    }

    private void ResetPlaybackRate_Click(object sender, RoutedEventArgs e)
    {
        RateSlider.Value = 100;
        UpdateSettingLabels();
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> operation)
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        RootGrid.IsEnabled = false;
        using var operationCancellation = new CancellationTokenSource();
        _operationCancellation = operationCancellation;
        try
        {
            await operation(operationCancellation.Token);
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
            UploadProgress.Visibility = Visibility.Collapsed;
            if (!_closeRequested)
            {
                SetStatus("Operation cancelled.");
            }
        }
        catch (CodexAlreadyRunningWithoutCdpException)
        {
            UploadProgress.Visibility = Visibility.Collapsed;
            if (!_closeRequested)
            {
                SetManualReconnectStatus();
            }
        }
        catch (Exception exception)
        {
            UploadProgress.Visibility = Visibility.Collapsed;
            if (!_closeRequested)
            {
                ShowError(ToUserFacingError(exception));
            }
        }
        finally
        {
            _operationCancellation = null;
            _busy = false;
            if (!_exitSequenceRunning) RootGrid.IsEnabled = true;
            if (_closeRequested)
            {
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
        }
    }

    private void RememberPendingApply(WallpaperEntry wallpaper)
    {
        _state.Wallpapers = _wallpapers.ToList();
        _state.SelectedWallpaperId = wallpaper.Id;
        _state.PendingWallpaperId = wallpaper.Id;
        _state.PendingActivation = true;
        SaveState();
        DeferredRestoreLauncher.RequestStop();
        SetManualReconnectStatus();
    }

    private void SetManualReconnectStatus()
    {
        SetStatus(UiLanguage.Text("Codex is already open without the wallpaper channel. Please close Codex manually, then click Start / reconnect Codex. The controller will never close Codex for you."));
    }

    private void WallpaperSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading) RefreshWallpaperView();
    }

    private void WallpaperFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) RefreshWallpaperView();
    }

    private bool WallpaperMatchesActiveFilters(object item)
    {
        if (item is not WallpaperEntry wallpaper) return false;
        var typeFilter = WallpaperTypeFilter.SelectedItem as WallpaperTypeFilterOption;
        var collectionFilter = WallpaperCollectionFilter.SelectedItem as WallpaperCollectionFilterOption;
        return WallpaperLibrary.Matches(
            wallpaper,
            WallpaperSearchTextBox.Text,
            typeFilter?.Kind,
            collectionFilter?.Collection,
            collectionFilter?.Ungrouped == true);
    }

    private void RefreshWallpaperView()
    {
        _wallpaperView.Refresh();
        var visible = _wallpaperView.Cast<object>().Count();
        WallpaperLibrarySummary.Text = UiLanguage.IsChinese
            ? visible == _wallpapers.Count
                ? $"{visible:N0} 张壁纸"
                : $"已显示 {visible:N0} / {_wallpapers.Count:N0}"
            : visible == _wallpapers.Count
                ? $"{visible:N0} wallpaper{(visible == 1 ? string.Empty : "s")}"
                : $"{visible:N0} of {_wallpapers.Count:N0}";
    }

    private void RefreshCollectionFilter()
    {
        var previous = WallpaperCollectionFilter.SelectedItem as WallpaperCollectionFilterOption;
        var options = new List<WallpaperCollectionFilterOption>
        {
            new(UiLanguage.Text("All collections"), null),
            new(UiLanguage.Text("Ungrouped"), null, Ungrouped: true)
        };
        options.AddRange(_wallpapers
            .Select(item => item.Collection)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .Select(value => new WallpaperCollectionFilterOption(value!, value)));

        WallpaperCollectionFilter.ItemsSource = options;
        WallpaperCollectionFilter.SelectedItem = previous is null
            ? options[0]
            : options.FirstOrDefault(option => option.Ungrouped == previous.Ungrouped
                && string.Equals(option.Collection, previous.Collection, StringComparison.CurrentCultureIgnoreCase))
                ?? options[0];
    }

    private void RenameWallpaper_Click(object sender, RoutedEventArgs e)
    {
        if (WallpaperList.SelectedItem is not WallpaperEntry selected) return;
        var dialog = new TextInputDialog(
            this,
            "Rename wallpaper",
            "Choose the name shown and searched in this app. The Steam Workshop project and local file are not renamed.",
            selected.DisplayTitle,
            WallpaperLibrary.MaximumCustomTitleLength,
            "Restore original name");
        if (dialog.ShowDialog() != true) return;

        var previousCustomTitle = selected.CustomTitle;
        try
        {
            selected.CustomTitle = dialog.UseSecondaryAction
                ? null
                : WallpaperLibrary.NormalizeCustomTitle(dialog.Result ?? string.Empty);
            if (string.Equals(selected.CustomTitle, selected.Title, StringComparison.CurrentCulture))
            {
                selected.CustomTitle = null;
            }
            SavePersonalization(selected);
            RefreshWallpaperView();
            if (_wallpaperView.Contains(selected))
            {
                WallpaperList.SelectedItem = selected;
                UpdateWallpaperDetails(selected);
            }
            SaveState();
            SetStatus(dialog.UseSecondaryAction
                ? $"Restored the original name: {selected.Title}."
                : $"Wallpaper renamed to {selected.DisplayTitle}. This app only; source files were not changed.");
        }
        catch (Exception exception)
        {
            selected.CustomTitle = previousCustomTitle;
            WallpaperLibraryStore.Update(selected, _libraryPersonalizations);
            RefreshWallpaperView();
            if (_wallpaperView.Contains(selected))
            {
                WallpaperList.SelectedItem = selected;
                UpdateWallpaperDetails(selected);
            }
            ShowError(exception.Message);
        }
    }

    private void SetCollection_Click(object sender, RoutedEventArgs e)
    {
        if (WallpaperList.SelectedItem is not WallpaperEntry selected) return;
        var dialog = new TextInputDialog(
            this,
            "Set wallpaper collection",
            "Enter a personal collection such as Relaxing, Anime, Landscape, or Work. Collections are stored only in this app.",
            selected.Collection ?? string.Empty,
            WallpaperLibrary.MaximumCollectionLength,
            "Remove from collection");
        if (dialog.ShowDialog() != true) return;

        var previousCollection = selected.Collection;
        try
        {
            selected.Collection = dialog.UseSecondaryAction
                ? null
                : WallpaperLibrary.NormalizeCollection(dialog.Result);
            SavePersonalization(selected);
            RefreshCollectionFilter();
            RefreshWallpaperView();
            if (_wallpaperView.Contains(selected))
            {
                WallpaperList.SelectedItem = selected;
                UpdateWallpaperDetails(selected);
            }
            SaveState();
            SetStatus(string.IsNullOrWhiteSpace(selected.Collection)
                ? $"Removed {selected.DisplayTitle} from its collection."
                : $"Added {selected.DisplayTitle} to the {selected.Collection} collection.");
        }
        catch (Exception exception)
        {
            selected.Collection = previousCollection;
            WallpaperLibraryStore.Update(selected, _libraryPersonalizations);
            RefreshCollectionFilter();
            RefreshWallpaperView();
            if (_wallpaperView.Contains(selected))
            {
                WallpaperList.SelectedItem = selected;
                UpdateWallpaperDetails(selected);
            }
            ShowError(exception.Message);
        }
    }

    private void UpdateWallpaperDetails(WallpaperEntry selected)
    {
        var collection = string.IsNullOrWhiteSpace(selected.Collection)
            ? "Ungrouped"
            : selected.Collection;
        var originalName = string.IsNullOrWhiteSpace(selected.CustomTitle)
            ? string.Empty
            : $" · Original name: {selected.Title}";
        WallpaperDetails.Text = $"{selected.Source} · {selected.Kind} · Collection: {collection}{originalName} · {selected.Note}";
    }

    private void SavePersonalization(WallpaperEntry selected)
    {
        WallpaperLibraryStore.Update(selected, _libraryPersonalizations);
        WallpaperLibraryStore.Save(_libraryPersonalizations);
    }

    private void Upsert(WallpaperEntry entry)
    {
        var existing = _wallpapers.FirstOrDefault(item => item.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            WallpaperLibrary.CopyPersonalization(existing, entry);
            var index = _wallpapers.IndexOf(existing);
            _wallpapers[index] = entry;
        }
        else
        {
            _wallpapers.Add(entry);
        }
    }

    private void SyncConnectionState()
    {
        var endpoint = EndpointTextBox.Text.Trim();
        if (!CdpEndpoint.IsLoopbackHttp(endpoint))
        {
            throw new InvalidOperationException("CDP endpoint must be a loopback URL.");
        }
        _state.CdpBaseUrl = endpoint;
        _state.Aumid = string.IsNullOrWhiteSpace(AumidTextBox.Text) ? null : AumidTextBox.Text.Trim();
        SaveState();
    }

    private bool SaveState()
    {
        _state.Wallpapers = _wallpapers.ToList();
        try
        {
            StateStore.Save(_state);
            _saveFailureShown = false;
            _stateWarning = null;
            return true;
        }
        catch (Exception exception)
        {
            _stateWarning = "State is not being saved: " + exception.Message;
            SetStatus("The current in-memory adjustment remains active.");
            if (!_saveFailureShown)
            {
                _saveFailureShown = true;
                MessageBox.Show(
                    this,
                    "The adjustment is still active in memory, but settings could not be saved.\n\n" + exception.Message,
                    "Codex Wallpaper Skin",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            return false;
        }
    }

    private void SetStatus(string text)
    {
        text = UiLanguage.Text(text);
        StatusText.Text = string.IsNullOrWhiteSpace(_stateWarning)
            ? text
            : text + Environment.NewLine + "⚠ " + _stateWarning;
    }

    private void ShowError(string message)
    {
        SetStatus("Error: " + message);
        MessageBox.Show(this, message, "Codex Wallpaper Skin", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static string ToUserFacingError(Exception exception)
    {
        var message = exception.Message;
        if (message.Contains("HttpClient.Timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("configured timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "Codex did not answer the local connection request in time. Wait a moment, then click Start / reconnect Codex.";
        }
        if (message.StartsWith("No Windows listener process owns CDP port", StringComparison.OrdinalIgnoreCase))
        {
            return "The previous Codex wallpaper channel is no longer running. Click Start / reconnect Codex to create a fresh connection.";
        }
        return message;
    }

    private static bool IsWpfPreviewImage(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif";
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowExit)
        {
            e.Cancel = true;
            _settingsTimer.Stop();
            SaveState();
            Hide();
            ShowInTaskbar = false;
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = true;
                if (!_trayTipShown)
                {
                    _trayTipShown = true;
                    _trayIcon.ShowBalloonTip(
                        4000,
                        UiLanguage.Text("Codex Wallpaper Skin is still running"),
                        UiLanguage.Text("The adjustment window is hidden, while the animated wallpaper continues. Double-click the tray icon to reopen it."),
                        System.Windows.Forms.ToolTipIcon.Info);
                }
            }
            return;
        }
        if (_busy && !_exitSequenceRunning)
        {
            e.Cancel = true;
            if (!_closeRequested)
            {
                _closeRequested = true;
                SetStatus("Cancelling the active operation before closing…");
                _operationCancellation?.Cancel();
            }
            return;
        }
        _settingsTimer.Stop();
        _saveFailureShown = true;
        if (!SaveState())
        {
            var choice = MessageBox.Show(
                this,
                "Settings could not be saved. Exit anyway and lose changes made in this session?",
                "Unsaved Codex Wallpaper Skin settings",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (choice != MessageBoxResult.Yes)
            {
                _closeRequested = false;
                _saveFailureShown = false;
                e.Cancel = true;
            }
        }
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        if (!_exitSequenceRunning
            && _injection.HasActiveCapture
            && !string.IsNullOrWhiteSpace(_state.LastAppliedWallpaperId))
        {
            try
            {
                _state.Wallpapers = _wallpapers.ToList();
                _state.PendingWallpaperId = _state.LastAppliedWallpaperId;
                _state.PendingActivation = true;
                StateStore.Save(_state);
                DeferredRestoreLauncher.EnsureRunning();
            }
            catch
            {
                // The current frame remains in Codex if a background handoff
                // cannot be scheduled; shutdown still closes the private WE window.
            }
        }
        try
        {
            var disposal = _injection.DisposeAsync().AsTask();
            await disposal.WaitAsync(TimeSpan.FromSeconds(_exitSequenceRunning ? 2 : 8));
        }
        catch
        {
            // Closing the controller intentionally leaves the renderer layer in place.
            // High-fidelity Scene playback is handed to the hidden restore worker above.
        }
        finally
        {
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            Application.Current.Shutdown();
        }
    }

    internal void PrepareForSystemShutdown()
    {
        _allowExit = true;
        _closeRequested = true;
    }

    private void InitializeTrayIcon()
    {
        var wasVisible = _trayIcon?.Visible == true;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var openItem = new System.Windows.Forms.ToolStripMenuItem(UiLanguage.Text("Open adjustment window"));
        openItem.Click += (_, _) => Dispatcher.BeginInvoke(ShowFromTray);
        var backgroundItem = new System.Windows.Forms.ToolStripMenuItem(UiLanguage.Text("Keep wallpaper running and hide this icon"));
        backgroundItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(KeepRunningWithoutTray));
        var exitItem = new System.Windows.Forms.ToolStripMenuItem(UiLanguage.Text("Remove wallpaper and exit"));
        exitItem.Click += (_, _) => Dispatcher.BeginInvoke(new Action(RestoreAndExitFromTray));
        menu.Items.Add(openItem);
        menu.Items.Add(backgroundItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "Codex Wallpaper Skin",
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = wasVisible
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowFromTray);
    }

    private void ShowFromTray()
    {
        if (_allowExit || _closeRequested) return;
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (_trayIcon is not null) _trayIcon.Visible = false;
    }

    internal void ShowFromExternalActivation() => ShowFromTray();

    private void KeepRunningWithoutTray()
    {
        if (_allowExit || _closeRequested) return;
        try
        {
            // This explicit command mirrors Wallpaper Engine's remembered
            // background behavior: keep the current owner alive now and start
            // the hidden restore worker again at the next Windows sign-in.
            StartupRegistration.SetEnabled(true);
            _startupChangeGuard = true;
            StartupRestoreCheck.IsChecked = true;
            _startupChangeGuard = false;
            _state.AutoRestoreOnLaunch = true;
            SaveState();
            Hide();
            ShowInTaskbar = false;
            if (_trayIcon is not null) _trayIcon.Visible = false;
        }
        catch (Exception exception)
        {
            _startupChangeGuard = false;
            ShowError("Background persistence could not be enabled: " + exception.Message);
        }
    }

    private async void RestoreAndExitFromTray()
    {
        if (_allowExit || _exitSequenceRunning) return;
        ShowFromTray();
        var choice = MessageBox.Show(
            this,
            UiLanguage.Text("The controller will make one quick cleanup attempt, then exit even if Codex is already closed or unavailable."),
            UiLanguage.Text("Remove wallpaper and exit?"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (choice != MessageBoxResult.Yes) return;

        _exitSequenceRunning = true;
        RootGrid.IsEnabled = false;
        _operationCancellation?.Cancel();

        string? cleanupWarning = null;
        try
        {
            var endpoint = EndpointTextBox.Text.Trim();
            if (_busy)
            {
                cleanupWarning = "The previous operation did not stop promptly, so remote page cleanup was skipped. Local renderer resources will still be released.";
            }
            else if (CdpEndpoint.IsLoopbackHttp(endpoint))
            {
                _state.CdpBaseUrl = endpoint;
                // A free port means Codex has already closed, so there is no
                // page process left to clean. Otherwise keep exit cleanup short.
                if (!CdpEndpoint.IsAvailableForActivation(endpoint))
                {
                    using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _injection.CleanupAllAsync(endpoint, cleanupTimeout.Token);
                }
                else
                {
                    cleanupWarning = UiLanguage.Text("No wallpaper was removed because Codex is already closed or its wallpaper channel is unavailable. The controller will exit now.");
                }
            }
        }
        catch (Exception exception)
        {
            cleanupWarning = "Codex was unavailable for page cleanup; local renderer resources will still be released ("
                + exception.Message + ").";
        }
        finally
        {
            _state.LastAppliedWallpaperId = null;
            _state.PendingWallpaperId = null;
            _state.PendingActivation = false;
            _saveFailureShown = true;
            SaveState();
            DeferredRestoreLauncher.RequestStop();
            if (!string.IsNullOrWhiteSpace(cleanupWarning))
            {
                SetStatus(cleanupWarning + " The controller is exiting now.");
            }
            _allowExit = true;
            _closeRequested = true;
            Close();
        }
    }
}
