namespace CodexWallpaperSkin;

public static class DiagnosticsService
{
    public static async Task<DiagnosticReport> RunAsync(
        AppState state,
        CancellationToken cancellationToken = default,
        string? captureMetrics = null,
        string? backendStatus = null)
    {
        var environment = new WindowsConnectionEnvironment();
        var report = new DiagnosticReport
        {
            StateFileExists = File.Exists(StateStore.StatePath),
            CdpEndpoint = state.CdpBaseUrl,
            CdpEndpointIsLoopback = CdpEndpoint.IsLoopbackHttp(state.CdpBaseUrl)
        };

        var selected = state.Wallpapers.FirstOrDefault(item => item.Id == state.SelectedWallpaperId);
        report.SavedWallpaper = selected?.EffectivePath;
        report.SavedWallpaperExists = selected?.EffectivePath is { } selectedPath && File.Exists(selectedPath);

        EndpointProbe? probe = null;
        if (report.CdpEndpointIsLoopback)
        {
            try
            {
                probe = await environment.ProbeAsync(state.CdpBaseUrl, cancellationToken);
                report.CdpReachable = probe.CodexPageAvailable;
                if (report.CdpReachable)
                {
                    report.Targets = (await CdpDiscovery.GetTargetsAsync(state.CdpBaseUrl, cancellationToken)).ToList();
                    if (report.Targets.Count == 0)
                    {
                        report.Notes.Add("CDP answered, but no page targets were exposed.");
                    }
                }
                else if (probe.PortHasListener && !probe.ListenerVerifiedAsCodex)
                {
                    report.CdpError = "The loopback port is owned by a listener that is not the official Codex package.";
                    report.Notes.Add("Another program holds this port. Connect moves to a fresh loopback port instead of attaching to it.");
                }
                else if (probe.PortHasListener)
                {
                    report.CdpError = "The official Codex process owns the loopback port but has not exposed a page target yet.";
                }
                else
                {
                    report.CdpError = "No Windows listener owns the configured loopback port.";
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                report.CdpError = exception.Message;
            }
        }
        else
        {
            report.CdpError = "Saved endpoint is not an HTTP loopback URL.";
        }

        var queued = AutoRestoreService.ResolveLastWallpaper(state);
        report.WallpaperQueued = WallpaperQueue.HasQueued(state);
        report.QueueSummary = WallpaperQueue.Describe(state, queued);
        var classified = ConnectionRecovery.Classify(new ConnectionProbe(
            report.CdpEndpointIsLoopback,
            probe?.PortHasListener ?? false,
            probe?.ListenerVerifiedAsCodex ?? false,
            probe?.CodexPageAvailable ?? false,
            probe?.OfficialCodexProcessCount ?? 0,
            report.WallpaperQueued,
            state.PendingLastFailure));
        report.ConnectionState = ConnectionRecovery.Badge(classified);
        report.Notes.Add(ConnectionRecovery.Guidance(classified));

        var startup = StartupRegistration.GetState();
        report.StartupRegistration = startup.Status.ToString();
        report.StartupRegistrationExecutable = startup.Registered?.Executable;
        if (startup.NeedsRepair)
        {
            report.Notes.Add(StartupRegistration.Describe(startup));
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
        if (!string.IsNullOrWhiteSpace(captureMetrics))
        {
            report.Notes.Add("Native capture: " + captureMetrics);
        }
        if (!string.IsNullOrWhiteSpace(backendStatus))
        {
            report.Notes.Add("Backend: " + backendStatus);
        }
        report.Notes.Add("Capture backend: " + CaptureBackends.Describe(CaptureBackends.Availability));
        return report;
    }
}
