namespace CodexWallpaperSkin;

public static class DiagnosticsService
{
    public static Task<DiagnosticReport> RunAsync(
        AppState state,
        CancellationToken cancellationToken = default) =>
        RunAsync(state, injection: null, cancellationToken: cancellationToken);

    public static async Task<DiagnosticReport> RunAsync(
        AppState state,
        CdpInjectionService? injection,
        CancellationToken cancellationToken = default)
    {
        var report = new DiagnosticReport
        {
            AppVersion = typeof(DiagnosticsService).Assembly.GetName().Version?.ToString() ?? "unknown",
            StateFileExists = File.Exists(StateStore.StatePath),
            LogFileExists = File.Exists(AppLog.LogPath),
            CdpEndpoint = state.CdpBaseUrl,
            CdpEndpointIsLoopback = CdpEndpoint.IsLoopbackHttp(state.CdpBaseUrl),
            RequestedSceneFrameRate = state.Settings.SceneFrameRate >= 60 ? 60 : 30
        };

        var selected = state.Wallpapers.FirstOrDefault(item => item.Id == state.SelectedWallpaperId);
        report.SavedWallpaperSource = selected?.Source;
        report.SavedWallpaperKind = selected?.Kind.ToString();
        report.SavedWallpaperExists = selected?.EffectivePath is { } selectedPath && File.Exists(selectedPath);
        if (selected?.IsWallpaperEngineScene == true)
        {
            report.WallpaperEngineFrameRateLimit = WallpaperEngineCaptureSession.GetConfiguredFrameRateLimit(selected);
        }

        var encoderProbe = MediaFoundationH264Probe.Run();
        report.HardwareH264Encoders = encoderProbe.Encoders
            .Where(candidate => candidate.Hardware)
            .Select(candidate => candidate.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        report.HardwareH264Available = report.HardwareH264Encoders.Count > 0;
            report.HardwareH264ProbeError = DiagnosticPrivacy.RedactText(encoderProbe.Error);
        if (!report.HardwareH264Available)
        {
            report.Notes.Add(encoderProbe.Error is null
                ? "No Windows hardware H.264 encoder was detected. Dynamic Scenes will use a clearly labeled compatibility backend."
                : "The Windows hardware H.264 encoder probe failed: " + encoderProbe.Error);
        }
        if (report.WallpaperEngineFrameRateLimit is int engineFps
            && engineFps < report.RequestedSceneFrameRate)
        {
            report.Notes.Add(
                $"Wallpaper Engine is configured for {engineFps} FPS, below the controller's {report.RequestedSceneFrameRate} FPS target. "
                + "The controller will report the source limit and will not fabricate duplicate frames.");
        }

        if (report.CdpEndpointIsLoopback)
        {
            try
            {
                var endpoint = CdpEndpoint.Normalize(state.CdpBaseUrl);
                CdpProcessIdentity.EnsureOfficialCodexOwnsPort(endpoint.Port);
                report.Targets = (await CdpDiscovery.GetTargetsAsync(state.CdpBaseUrl, cancellationToken))
                    .Select(SanitizeTarget)
                    .ToList();
                report.CdpReachable = true;
                if (report.Targets.Count == 0)
                {
                    report.Notes.Add("CDP answered, but no page targets were exposed.");
                }
            }
            catch (Exception exception)
            {
                report.CdpError = DiagnosticPrivacy.RedactText(exception.Message);
                report.Notes.Add("CDP is not reachable. This tool never restarts Codex; start/activate it with a loopback remote-debugging port, then retry.");
            }
        }
        else
        {
            report.CdpError = "Saved endpoint is not an HTTP loopback URL.";
        }

        var candidates = await AppActivation.FindCodexCandidatesAsync(cancellationToken);
        report.AumidCandidates = candidates.Select(candidate => candidate.ToString()).ToList();
        if (candidates.Count == 0)
        {
            report.Notes.Add("No Codex AUMID was detected via Get-StartApps. Paste an AUMID manually or start Codex yourself with the CDP flag.");
        }
        else if (candidates.Any(candidate => candidate.Name.Contains("enumeration unavailable", StringComparison.OrdinalIgnoreCase)))
        {
            report.Notes.Add("Windows did not enumerate Codex through Get-StartApps, so the companion supplied its fixed official package identity. Activation will still fail closed if that package is not installed.");
        }
        if (state.Settings.Blur > 0)
        {
            report.Notes.Add("Blur is enabled. Set it to 0 for the lowest GPU cost.");
        }
        await PopulateStreamWatchdogAsync(report, state, injection, cancellationToken);
        report.Notes = report.Notes
            .Select(note => DiagnosticPrivacy.RedactText(note) ?? string.Empty)
            .ToList();
        return report;
    }

    private static async Task PopulateStreamWatchdogAsync(
        DiagnosticReport report,
        AppState state,
        CdpInjectionService? injection,
        CancellationToken cancellationToken)
    {
        var target = report.StreamWatchdog;
        target.LiveControllerAttached = injection is not null;
        if (injection is null) return;

        var runtime = injection.GetStreamWatchdogSnapshot();
        target.ActiveCapture = runtime.ActiveCapture;
        target.RecoveryCount = runtime.RecoveryCount;
        target.LastRecoveryKind = runtime.LastRecoveryKind;
        target.LastRecoveryAt = runtime.LastRecoveryAt;
        target.CurrentHealth = runtime.ActiveCapture ? "Probe unavailable" : "Inactive";
        if (!runtime.ActiveCapture || !injection.IsConnected) return;

        try
        {
            var diagnostics = await injection.GetActiveStreamDiagnosticsAsync(cancellationToken);
            var health = StreamHealthPolicy.Evaluate(
                diagnostics, state.Settings.PauseWhenHidden, DateTimeOffset.UtcNow);
            target.CurrentHealth = health.Kind.ToString();
            target.LastPresentationAt = diagnostics.LastPresentation;
            target.PageHidden = diagnostics.PageHidden;
            target.Mode = diagnostics.Mode;
            target.ReceivedFrames = diagnostics.Received;
            target.PresentedFrames = diagnostics.Presented;
            target.DroppedFrames = diagnostics.Dropped;
            target.DecodeErrors = diagnostics.DecodeErrors;
            target.CapturedFrames = diagnostics.Native?.CapturedFrames;
            target.EncodedFrames = diagnostics.Native?.EncodedFrames;
            target.TransportErrorPresent = !string.IsNullOrWhiteSpace(diagnostics.TransportError);
        }
        catch (Exception exception)
        {
            // Exception types are enough to route support. Messages can contain
            // local paths and are already available in the private rotating log.
            target.ProbeErrorType = exception.GetType().Name;
        }
    }

    internal static CdpTarget SanitizeTarget(CdpTarget target) =>
        DiagnosticPrivacy.SanitizeTarget(target);
}
