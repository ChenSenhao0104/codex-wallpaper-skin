using Microsoft.Win32;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexWallpaperSkin;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<WallpaperEntry> _wallpapers = [];
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

    public MainWindow()
    {
        InitializeComponent();
        _state = StateStore.Load();
        _settingsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _settingsTimer.Tick += SettingsTimer_Tick;

        WallpaperList.ItemsSource = _wallpapers;
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
                try { item = WallpaperCatalog.ParseProject(savedItem.ProjectPath); } catch { }
            }
            _wallpapers.Add(item);
        }
        LoadSettings(_state.Settings);
        AutoRestoreCheck.IsChecked = _state.AutoRestoreOnLaunch;
        try
        {
            StartupRestoreCheck.IsChecked = StartupRegistration.IsEnabled();
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
            _stateWarning = StateStore.LastLoadWarning;
            SetStatus("State recovery needs attention.");
        }
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        if (!_state.AutoRestoreOnLaunch || string.IsNullOrWhiteSpace(_state.LastAppliedWallpaperId))
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
            SetStatus($"Restored {restored.Wallpaper.Title} from the previous session."
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
            SetStatus("Connecting to Codex CDP…");
            var target = await _injection.ConnectAsync(endpoint, cancellationToken);
            _state.CdpBaseUrl = endpoint;
            SaveState();
            ConnectButton.Content = "Reconnect";
            if (_state.AutoRestoreOnLaunch && !string.IsNullOrWhiteSpace(_state.LastAppliedWallpaperId))
            {
                _state.Wallpapers = _wallpapers.ToList();
                var remembered = AutoRestoreService.ResolveLastWallpaper(_state);
                if (remembered?.CanApply == true)
                {
                    UploadProgress.Value = 0;
                    UploadProgress.Visibility = Visibility.Visible;
                    SetStatus($"Connected. Restoring {remembered.Title}…");
                    var progress = new Progress<double>(value => UploadProgress.Value = value * 100);
                    await _injection.ApplyAsync(remembered, _state.Settings, progress, cancellationToken);
                    UploadProgress.Visibility = Visibility.Collapsed;
                    SetStatus($"Connected and restored {remembered.Title} from the previous session.");
                    return;
                }
            }
            SetStatus($"Connected: {target.Title} — {target.Url}");
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
                Upsert(entry);
                last = entry;
            }
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
            foreach (var existing in _wallpapers.Where(item => item.Source == "Wallpaper Engine").ToArray())
            {
                _wallpapers.Remove(existing);
            }
            foreach (var entry in found)
            {
                Upsert(entry);
            }
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
        PreviewImage.Source = null;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        if (selected is null)
        {
            WallpaperDetails.Text = string.Empty;
            PreviewPlaceholder.Text = "Select a wallpaper";
            return;
        }

        _state.SelectedWallpaperId = selected.Id;
        WallpaperDetails.Text = $"{selected.Source} · {selected.Kind} · {selected.Note}";
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
                "wallpaper-engine-capture" => " Wallpaper Engine high-fidelity rendering and pointer interaction are active.",
                "animated-preview" => " The low-resolution animated Workshop preview is active.",
                "static-preview" => " The static Workshop preview fallback is active.",
                "video" => " Direct video playback is active.",
                _ => " Direct image playback is active."
            };
            var warningStatus = string.IsNullOrWhiteSpace(applyResult.Warning) ? string.Empty : " Note: " + applyResult.Warning;
            SetStatus($"Applied {selected.Title}.{modeStatus}{paletteStatus}{warningStatus} No Codex file was changed; Restore removes the whole layer.");
        });
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
                try { DeferredRestoreLauncher.EnsureRunning(); } catch { }
                SetStatus(exception.Message);
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
        SetStatus("Codex is already running without the wallpaper channel. The selected wallpaper is queued; the current task will not be interrupted, and it will be restored automatically after Codex is next closed normally.");
    }

    private void Upsert(WallpaperEntry entry)
    {
        var existing = _wallpapers.FirstOrDefault(item => item.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
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
