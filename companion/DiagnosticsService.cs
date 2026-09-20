namespace CodexWallpaperSkin;

public static class DiagnosticsService
{
    public static async Task<DiagnosticReport> RunAsync(AppState state, CancellationToken cancellationToken = default)
    {
        var report = new DiagnosticReport
        {
            StateFileExists = File.Exists(StateStore.StatePath),
            CdpEndpoint = state.CdpBaseUrl,
            CdpEndpointIsLoopback = CdpEndpoint.IsLoopbackHttp(state.CdpBaseUrl)
        };

        var selected = state.Wallpapers.FirstOrDefault(item => item.Id == state.SelectedWallpaperId);
        report.SavedWallpaper = selected?.EffectivePath;
        report.SavedWallpaperExists = selected?.EffectivePath is { } selectedPath && File.Exists(selectedPath);

        if (report.CdpEndpointIsLoopback)
        {
            try
            {
                var endpoint = CdpEndpoint.Normalize(state.CdpBaseUrl);
                CdpProcessIdentity.EnsureOfficialCodexOwnsPort(endpoint.Port);
                report.Targets = (await CdpDiscovery.GetTargetsAsync(state.CdpBaseUrl, cancellationToken)).ToList();
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
        report.GpuStreamEnabled = state.Settings.GpuStreamEnabled;
        if (!state.Settings.GpuStreamEnabled)
        {
            report.Notes.Add(
                "The GPU media path is disabled in settings, so Wallpaper Engine scenes use the reduced-frame-rate compatibility backend.");
        }
        else
        {
            report.Notes.Add(
                $"Wallpaper Engine scenes request {GpuStreamStatusLabel.Describe(GpuStreamStatus.GpuDynamic60)} "
                + "and fall back to a labeled 30 FPS mode or the reduced-frame-rate compatibility backend. "
                + "No listening socket is created: media is carried by the existing CDP session.");
        }
        return report;
    }
}
