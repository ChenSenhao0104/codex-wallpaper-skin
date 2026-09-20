namespace CodexWallpaperSkin;

/// <summary>
/// Production <see cref="IConnectionEnvironment"/>: verified loopback ownership,
/// bounded CDP discovery, and Windows activation. It never terminates a running
/// Codex process and never adopts a listener that is not the official package.
/// </summary>
public sealed class WindowsConnectionEnvironment : IConnectionEnvironment
{
    public WindowsConnectionEnvironment(TimeSpan? readinessTimeout = null, TimeSpan? readinessPollInterval = null)
    {
        ReadinessTimeout = readinessTimeout ?? TimeSpan.FromSeconds(30);
        ReadinessPollInterval = readinessPollInterval ?? TimeSpan.FromMilliseconds(500);
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public TimeSpan ReadinessTimeout { get; }

    public TimeSpan ReadinessPollInterval { get; }

    public async Task<EndpointProbe> ProbeAsync(string endpoint, CancellationToken cancellationToken)
    {
        if (!CdpEndpoint.IsLoopbackHttp(endpoint))
        {
            return new EndpointProbe(false, false, false, false, 0);
        }

        var port = CdpEndpoint.Normalize(endpoint).Port;
        var hasListener = !CdpEndpoint.IsAvailableForActivation(endpoint);
        var verified = false;
        var pageAvailable = false;
        var processCount = 0;

        if (hasListener)
        {
            try
            {
                CdpProcessIdentity.EnsureOfficialCodexOwnsPort(port);
                verified = true;
            }
            catch
            {
                verified = false;
            }

            if (verified)
            {
                try
                {
                    var targets = await CdpDiscovery.GetTargetsAsync(endpoint, cancellationToken);
                    CdpDiscovery.SelectCodexPage(targets);
                    pageAvailable = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    pageAvailable = false;
                }
            }
        }

        if (!hasListener)
        {
            // Only needed to distinguish "Codex is closed" from "Codex is open
            // without CDP"; skipped when a verified listener already answers.
            processCount = CdpProcessIdentity.FindRunningOfficialCodexProcessIds().Count;
        }

        return new EndpointProbe(true, hasListener, verified, pageAvailable, processCount);
    }

    public string CreateUnusedEndpoint() => CdpEndpoint.CreateUnusedLoopbackUrl();

    public Task ActivateAsync(string aumid, string endpoint, CancellationToken cancellationToken) =>
        AppActivation.ActivateWithCdpAsync(aumid, endpoint, cancellationToken);

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}
