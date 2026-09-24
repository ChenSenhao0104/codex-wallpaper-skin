namespace CodexWallpaperSkin;

public static class DiagnosticsService
{
    public static async Task<DiagnosticReport> RunAsync(AppState state, CancellationToken cancellationToken = default)
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
        report.SavedWallpaper = selected?.EffectivePath;
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
        report.HardwareH264ProbeError = encoderProbe.Error;
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
                report.CdpError = exception.Message;
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
        return report;
    }

    internal static CdpTarget SanitizeTarget(CdpTarget target) => target with
    {
        // Codex page titles can contain the current task title. It is not
        // needed for support and must not be copied into a package the user
        // may share.
        Title = CdpDiscovery.IsPrimaryCodexPage(target)
            ? "Codex main window"
            : target.Url.StartsWith("app://", StringComparison.OrdinalIgnoreCase)
                ? "Codex auxiliary window"
                : target.Type
    };
}
