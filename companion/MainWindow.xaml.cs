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

    private sealed record WallpaperTypeFilterOption(string Label, WallpaperKind? Kind);
    private sealed record WallpaperCollectionFilterOption(string Label, string? Collection, bool Ungrouped = false);

    public MainWindow()
    {
        InitializeComponent();
        _state = StateStore.Load();
        _libraryPersonalizations = WallpaperLibraryStore.Load();
        _settingsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _settingsTimer.Tick += SettingsTimer_Tick;

        _wallpaperView = CollectionViewSource.GetDefaultView(_wallpapers);
        _wallpaperView.Filter = WallpaperMatchesActiveFilters;
        WallpaperList.ItemsSource = _wallpaperView;
        WallpaperTypeFilter.ItemsSource = new[]
        {
            new WallpaperTypeFilterOption("All types", null),
            new WallpaperTypeFilterOption("Scenes", WallpaperKind.Scene),
            new WallpaperTypeFilterOption("Videos", WallpaperKind.Video),
            new WallpaperTypeFilterOption("Images", WallpaperKind.Image),
            new WallpaperTypeFilterOption("Web", WallpaperKind.Web),
            new WallpaperTypeFilterOption("Applications", WallpaperKind.Application),
            new WallpaperTypeFilterOption("Unknown", WallpaperKind.Unknown)
        };
        WallpaperTypeFilter.SelectedIndex = 0;
        FitCombo.ItemsSource = Enum.GetValues<WallpaperFit>();
        SceneFpsCombo.ItemsSource = new[] { 10, 15 };
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
        try
        {
            var startupStatus = StartupRegistration.GetStatus();
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
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        Loaded += MainWindow_Loaded;
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
                if (remembered is not null) QueueDeferredRestore(remembered);
                return;
            }
            UploadProgress.Visibility = Visibility.Collapsed;
            EndpointTextBox.Text = _state.CdpBaseUrl;
            AumidTextBox.Text = _state.Aumid ?? string.Empty;
            ConnectButton.Content = "Reconnect";
            var listed = _wallpapers.FirstOrDefault(item => item.Id.Equals(restored.Wallpaper.Id, StringComparison.OrdinalIgnoreCase));
            if (listed is not null)
            {
                WallpaperList.SelectedItem = listed;
            }
            SaveState();
            SetStatus($"Restored {restored.Wallpaper.DisplayTitle} from the previous session."
                + (restored.ActivatedCodex ? " Codex was started with its verified local CDP endpoint." : string.Empty));
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
                if (_state.PendingActivation && AutoRestoreService.ResolveLastWallpaper(_state) is not null)
                {
                    try { DeferredRestoreLauncher.EnsureRunning(); } catch { }
                    SetQueuedStatus();
                }
                else
                {
                    SetStatus("Codex is open without the wallpaper channel. Its Chromium process can enable this channel only at startup. Your current task was left untouched; select a wallpaper and click Apply to queue it, or close Codex normally and click Connect again.");
                }
                return;
            }
            SaveState();
            EndpointTextBox.Text = _state.CdpBaseUrl;
            AumidTextBox.Text = _state.Aumid ?? string.Empty;
            ConnectButton.Content = "Reconnect";
            if (_state.AutoRestoreOnLaunch)
            {
                _state.Wallpapers = _wallpapers.ToList();
                var remembered = AutoRestoreService.ResolveLastWallpaper(_state);
                if (remembered?.CanApply == true)
                {
                    UploadProgress.Value = 0;
                    UploadProgress.Visibility = Visibility.Visible;
                    SetStatus($"Connected. Restoring {remembered.DisplayTitle}…");
                    var progress = new Progress<double>(value => UploadProgress.Value = value * 100);
                    await _injection.ApplyAsync(remembered, _state.Settings, progress, cancellationToken);
                    UploadProgress.Visibility = Visibility.Collapsed;
                    SetStatus($"Connected and restored {remembered.DisplayTitle} from the previous session.");
                    return;
                }
            }
            SetStatus(connection.ActivatedCodex
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
            SetStatus(report.CdpReachable
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
                    QueueDeferredRestore(queued);
                }
                else
                {
                    SetStatus("Codex is already open without its local wallpaper channel. Select a wallpaper and click Apply; it will be queued without interrupting the current task.");
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
            RefreshCollectionFilter();
            RefreshWallpaperView();
            SaveState();
            var direct = found.Count(item => item.Support == WallpaperSupport.Direct);
            var liveScene = found.Count(item => item.Support == WallpaperSupport.LiveScene);
            var animatedPreview = found.Count(item => item.Support == WallpaperSupport.AnimatedPreview);
            var fallback = found.Count(item => item.Support == WallpaperSupport.StaticPreview);
            var rejected = found.Count(item => item.Support == WallpaperSupport.Rejected);
            SetStatus($"Wallpaper Engine scan: {direct} direct, {liveScene} live 2D scene, {animatedPreview} animated preview, {fallback} static preview, {rejected} rejected.");
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
                    ConnectButton.Content = "Reconnect";
                }
                catch (CodexAlreadyRunningWithoutCdpException)
                {
                    UploadProgress.Visibility = Visibility.Collapsed;
                    QueueDeferredRestore(selected);
                    return;
                }
            }
            else
            {
                SetStatus($"Uploading {Path.GetFileName(selected.EffectivePath)} to the Codex renderer…");
                applyResult = await _injection.ApplyAsync(selected, settings, progress, cancellationToken);
            }
            UploadProgress.Visibility = Visibility.Collapsed;
            _state.LastAppliedWallpaperId = selected.Id;
            _state.PendingWallpaperId = null;
            _state.PendingActivation = false;
            SaveState();
            var paletteStatus = settings.AutoPalette
                ? applyResult.Palette is null
                    ? " Palette sampling was unavailable, so the neutral fallback remains active."
                    : $" Palette: surface {applyResult.Palette.Surface}, accent {applyResult.Palette.Accent}, text contrast {applyResult.Palette.TextContrast:0.0}:1."
                : " Automatic palette is off.";
            var modeStatus = applyResult.Mode switch
            {
                "live-scene" => " Live 2D scene rendering is active.",
                "scene-partial" => " Live 2D scene rendering is active with unsupported layers omitted.",
                "scene-static" => " The renderer used the full-resolution scene texture fallback.",
                "wallpaper-engine-capture" => " Wallpaper Engine native rendering and pointer forwarding are active through a reduced-frame-rate capture stream.",
                "animated-preview" => " The low-resolution animated Workshop preview is active.",
                "static-preview" => " The static Workshop preview fallback is active.",
                "video" => " Direct video playback is active.",
                _ => " Direct image playback is active."
            };
            var warningStatus = string.IsNullOrWhiteSpace(applyResult.Warning) ? string.Empty : " Note: " + applyResult.Warning;
            SetStatus($"Applied {selected.DisplayTitle}.{modeStatus}{paletteStatus}{warningStatus} No Codex file was changed; Restore removes the whole layer.");
            if (applyResult.Mode == "wallpaper-engine-capture")
            {
                _ = MonitorCaptureAsync(selected.Id, selected.DisplayTitle, _injection.ActiveCaptureCompletion);
            }
        });
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
            await Dispatcher.InvokeAsync(() =>
            {
                if (!_closeRequested
                    && !_injection.HasActiveCapture
                    && ReferenceEquals(_injection.ActiveCaptureCompletion, completion)
                    && string.Equals(_state.LastAppliedWallpaperId, wallpaperId, StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus($"The live stream for {title} stopped after repeated renderer or connection failures. The last good frame was kept; click Apply selected to restart it.");
                }
            });
        }
        catch
        {
            // Apply/switch/close owns user-facing error reporting. This monitor
            // exists only to surface a stream that ended after Apply succeeded.
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
                SetStatus("Cancelled the queued wallpaper restore. No running Codex process was changed.");
                return;
            }
            var cleanedPages = await _injection.CleanupAllAsync(_state.CdpBaseUrl, cancellationToken);
            _state.LastAppliedWallpaperId = null;
            _state.PendingWallpaperId = null;
            _state.PendingActivation = false;
            SaveState();
            SetStatus($"Restored the original Codex background on {cleanedPages} app page(s). Temporary layers, style changes and Blob URLs were removed.");
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
            Fit = FitCombo.SelectedItem is WallpaperFit fit ? fit : WallpaperFit.Cover,
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
            SceneFrameRate = SceneFpsCombo.SelectedItem is int frameRate ? frameRate : 15,
            SceneResolutionScale = SceneScaleSlider.Value / 100
        }.Normalize();
    }

    private void LoadSettings(WallpaperSettings settings)
    {
        FitCombo.SelectedItem = settings.Fit;
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
        catch (CodexAlreadyRunningWithoutCdpException exception)
        {
            UploadProgress.Visibility = Visibility.Collapsed;
            if (!_closeRequested)
            {
                if (_state.PendingActivation && AutoRestoreService.ResolveLastWallpaper(_state) is not null)
                {
                    try { DeferredRestoreLauncher.EnsureRunning(); } catch { }
                    SetQueuedStatus();
                }
                else
                {
                    SetStatus(exception.Message);
                }
            }
        }
        catch (Exception exception)
        {
            UploadProgress.Visibility = Visibility.Collapsed;
            if (!_closeRequested)
            {
                ShowError(exception.Message);
            }
        }
        finally
        {
            _operationCancellation = null;
            _busy = false;
            RootGrid.IsEnabled = true;
            if (_closeRequested)
            {
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
        }
    }

    private void QueueDeferredRestore(WallpaperEntry wallpaper)
    {
        _state.Wallpapers = _wallpapers.ToList();
        _state.SelectedWallpaperId = wallpaper.Id;
        _state.PendingWallpaperId = wallpaper.Id;
        _state.PendingActivation = true;
        SaveState();
        DeferredRestoreLauncher.EnsureRunning();
        SetQueuedStatus();
    }

    private void SetQueuedStatus()
    {
        SetStatus("Queued — the wallpaper is not applied yet. Codex is currently running without its startup-only wallpaper channel, so the current task was left untouched. The controller will retry after Codex closes normally; click Restore Codex background to cancel the queue.");
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
        WallpaperLibrarySummary.Text = visible == _wallpapers.Count
            ? $"{visible:N0} wallpaper{(visible == 1 ? string.Empty : "s")}"
            : $"{visible:N0} of {_wallpapers.Count:N0}";
    }

    private void RefreshCollectionFilter()
    {
        var previous = WallpaperCollectionFilter.SelectedItem as WallpaperCollectionFilterOption;
        var options = new List<WallpaperCollectionFilterOption>
        {
            new("All collections", null),
            new("Ungrouped", null, Ungrouped: true)
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
        StatusText.Text = string.IsNullOrWhiteSpace(_stateWarning)
            ? text
            : text + Environment.NewLine + "⚠ " + _stateWarning;
    }

    private void ShowError(string message)
    {
        SetStatus("Error: " + message);
        MessageBox.Show(this, message, "Codex Wallpaper Skin", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static bool IsWpfPreviewImage(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif";
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy)
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
        if (_injection.HasActiveCapture && !string.IsNullOrWhiteSpace(_state.LastAppliedWallpaperId))
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
            await _injection.DisposeAsync();
        }
        catch
        {
            // Closing the controller intentionally leaves the renderer layer in place.
            // High-fidelity Scene playback is handed to the hidden restore worker above.
        }
    }
}
