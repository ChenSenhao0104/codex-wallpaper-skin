using System.Text.Json;

namespace CodexWallpaperSkin;

public sealed class CdpInjectionService : IAsyncDisposable
{
    private const int UploadChunkSize = 64 * 1024;
    private const int MaximumCleanupPages = 16;
    private static readonly TimeSpan MaximumApplyDuration = TimeSpan.FromMinutes(5);
    /// <summary>How often the supervisor re-evaluates a running capture stream.</summary>
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromMilliseconds(1_500);
    private static readonly JsonSerializerOptions PaletteJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private CdpClient? _client;
    private WallpaperEngineCaptureSession? _captureSession;
    private string? _captureToken;
    private CancellationTokenSource? _captureRecoveryLifetime;
    private Task? _captureRecoverySupervisor;

    public bool IsConnected => _client?.IsConnected == true;
    public bool HasActiveCapture => _captureSession?.IsRunning == true;
    public Task ActiveCaptureCompletion => _captureSession?.Completion ?? Task.CompletedTask;

    /// <summary>Current product-facing backend state (specification section 5).</summary>
    public WallpaperBackendStatus BackendStatus { get; private set; } = WallpaperBackendStatus.None;

    /// <summary>One concise sentence explaining the current backend state.</summary>
    public string BackendStatusDetail { get; private set; } = string.Empty;

    /// <summary>Raised whenever the backend state or its explanation changes.</summary>
    public event EventHandler? BackendStatusChanged;

    private void SetBackendStatus(WallpaperBackendStatus status, string detail)
    {
        if (BackendStatus == status && BackendStatusDetail == detail)
        {
            return;
        }
        BackendStatus = status;
        BackendStatusDetail = detail;
        try
        {
            BackendStatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // A status listener must never break rendering or recovery.
        }
    }

    /// <summary>Human-readable capture health for Doctor and local measurements.</summary>
    public string? CaptureMetricsSummary => _captureSession?.MetricsSummary;

    /// <summary>What the transported frames cost, or null when no stream is active.</summary>
    public CaptureTransportMetrics? TransportMetrics => _captureSession?.TransportMetrics;

    /// <summary>The most recently presented native frame, for local inspection.</summary>
    public byte[]? LatestCaptureFrame => _captureSession?.LastAcceptedFrame;

    /// <summary>
    /// Gate 8 check: after Restore, the private render window this session owned
    /// must be gone. Null when no session was ever started.
    /// </summary>
    public bool? OwnedCaptureWindowAlive => _captureSession?.IsWindowAlive;

    /// <summary>
    /// The private render window's title, read before Restore so the window can be
    /// checked afterwards. Null when no native session ran.
    /// </summary>
    public string? OwnedCaptureWindowName => _captureSession?.WindowName;

    /// <summary>Structured capture health, or null when no native stream is active.</summary>
    public CaptureHealth? CaptureHealthSnapshot => _captureSession?.Snapshot();

    /// <summary>
    /// Measures the input channel latency: inject a synthetic pointer move into
    /// Codex and time how long the companion takes to observe it. This is the
    /// injection-to-observation path, which is what the companion controls; the
    /// renderer's own reaction is not measurable from here.
    /// </summary>
    internal async Task<IReadOnlyList<double>> MeasureInputLatencyAsync(
        int samples,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(samples);
        var client = RequireClient();
        if (_captureToken is null)
        {
            throw new InvalidOperationException("Native capture is not active, so input latency cannot be measured.");
        }
        var results = new List<double>(samples);
        for (var index = 0; index < samples; index++)
        {
            var targetX = 0.2 + 0.6 * (index % 5 / 4.0);
            var targetY = 0.3 + 0.4 * (index % 3 / 2.0);
            var x = targetX.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
            var y = targetY.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            await client.EvaluateAsync(
                "window.dispatchEvent(new PointerEvent('pointermove', "
                + $"{{ clientX: innerWidth * {x}, clientY: innerHeight * {y} }}))",
                cancellationToken);
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(500);
            var observed = false;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pointer = await ReadCapturePointerAsync(cancellationToken);
                if (pointer is not null
                    && Math.Abs(pointer.X - targetX) < 0.01
                    && Math.Abs(pointer.Y - targetY) < 0.01)
                {
                    observed = true;
                    break;
                }
                await Task.Delay(2, cancellationToken);
            }
            if (observed)
            {
                results.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            await Task.Delay(30, cancellationToken);
        }
        return results;
    }
    public CdpTarget? Target => _client?.Target;

    public async Task<CdpTarget> ConnectAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        await StopCaptureAsync();
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var operationToken = timeout.Token;
        var endpointUri = CdpEndpoint.Normalize(endpoint);
        CdpProcessIdentity.EnsureOfficialCodexOwnsPort(endpointUri.Port);
        var targets = await CdpDiscovery.GetTargetsAsync(endpoint, operationToken);
        var pages = CdpDiscovery.GetCodexPages(targets);
        if (pages.Count == 0)
        {
            throw new InvalidOperationException("CDP exposes no Codex app:// page.");
        }
        if (pages.Count > MaximumCleanupPages)
        {
            throw new InvalidOperationException($"CDP exposes too many app pages ({pages.Count}); the safety limit is {MaximumCleanupPages}.");
        }

        Exception? lastFailure = null;
        foreach (var page in pages)
        {
            var candidate = new CdpClient();
            try
            {
                await candidate.ConnectAsync(endpoint, page.Id, operationToken);
                var marker = await candidate.EvaluateAsync(NativeSurfaceProbeScript, operationToken);
                if (!ReadBoolean(marker))
                {
                    throw new InvalidOperationException("The page does not expose the expected native Codex surface marker.");
                }
                _client = candidate;
                return candidate.Target!;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await candidate.DisposeAsync();
                throw;
            }
            catch (OperationCanceledException)
            {
                await candidate.DisposeAsync();
                throw new TimeoutException("Finding a verified Codex app page exceeded the 20-second safety timeout.");
            }
            catch (Exception exception)
            {
                lastFailure = exception;
                await candidate.DisposeAsync();
            }
        }
        throw new InvalidOperationException(
            "No exposed Codex app:// page passed the native surface check.",
            lastFailure);
    }

    public async Task<WallpaperApplyResult> ApplyAsync(
        WallpaperEntry wallpaper,
        WallpaperSettings settings,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var client = RequireClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MaximumApplyDuration);
        var operationToken = timeout.Token;
        var path = wallpaper.EffectivePath;
        if (!wallpaper.CanApply || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("The selected wallpaper media is unavailable.", path);
        }

        settings.Normalize();
        await StopCaptureAsync();
        await client.EvaluateAsync(BootstrapScript, operationToken);
        Exception? nativeCaptureFailure = null;
        try
        {
            if (wallpaper.Kind == WallpaperKind.Scene)
            {
                if (WallpaperEngineCaptureSession.CanUse(wallpaper))
                {
                    try
                    {
                        var native = await StartNativeCaptureAsync(
                            client, wallpaper, settings, progress, operationToken);
                        SetBackendStatus(
                            WallpaperBackendStatus.NativeDynamic,
                            "Wallpaper Engine is rendering this scene at full fidelity.");
                        return native with
                        {
                            Mode = "wallpaper-engine-capture",
                            Warning = "Rendered by Wallpaper Engine for full Scene fidelity and pointer interaction. "
                                + "Keep this controller running while the animated wallpaper is active."
                        };
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        nativeCaptureFailure = exception;
                    }
                }

                // The built-in safe renderer and the Workshop preview are explicitly
                // limited compatibility fallbacks, never a silent substitute for
                // native quality.
                var fallback = await ApplySceneFallbackAsync(
                    client, wallpaper, settings, progress, operationToken, nativeCaptureFailure);
                if (fallback.Result is not null)
                {
                    SetBackendStatus(
                        BackendStatuses.FromApplyMode(fallback.Result.Mode),
                        nativeCaptureFailure is null
                            ? "Wallpaper Engine rendering was not requested for this scene."
                            : "Native capture was unavailable, so a labeled fallback is active. "
                                + LimitMessage(nativeCaptureFailure.Message));
                    return fallback.Result;
                }

                SetBackendStatus(
                    WallpaperBackendStatus.Unsupported,
                    "No live Scene backend and no preview fallback is available for this project.");
                throw new InvalidOperationException(
                    "No live Scene backend is available for this project and it has no validated preview fallback. "
                    + LimitMessage(nativeCaptureFailure?.Message
                        ?? fallback.Failure?.Message
                        ?? "the safe renderer and the Workshop preview were both unavailable"));
            }

            await using var stream = WallpaperCatalog.OpenValidatedMediaFile(wallpaper);
            var direct = await UploadAsync(
                client, stream, path, wallpaper.MediaMode, settings, null,
                progress, operationToken);
            SetBackendStatus(WallpaperBackendStatus.DirectMedia,
                wallpaper.MediaMode == "video" ? "Direct video playback." : "Direct image playback.");
            return direct;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Applying the wallpaper timed out before it could finish.", exception);
        }
    }

    private static async Task<WallpaperApplyResult> UploadAsync(
        CdpClient client,
        Stream stream,
        string path,
        string mediaMode,
        WallpaperSettings settings,
        SceneBrowserOptions? sceneOptions,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid().ToString("N");
        var mime = WallpaperCatalog.MimeTypeFor(path);
        await client.EvaluateAsync(
            $"window.__codexWallpaperSkinBeginUpload({Js(token)}, {Js(mime)});",
            cancellationToken);
        try
        {
            var fileLength = stream.Length;
            long uploaded = 0;
            var buffer = new byte[UploadChunkSize];
            while (uploaded < fileLength)
            {
                var remaining = fileLength - uploaded;
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0)
                {
                    throw new EndOfStreamException("The wallpaper media changed or ended during upload.");
                }
                var base64 = Convert.ToBase64String(buffer, 0, read);
                await client.EvaluateAsync(
                    $"window.__codexWallpaperSkinPushChunk({Js(token)}, {Js(base64)});",
                    cancellationToken);
                uploaded += read;
                progress?.Report(fileLength == 0 ? 1 : (double)uploaded / fileLength);
            }
            if (stream.ReadByte() != -1)
            {
                throw new InvalidDataException("The wallpaper media changed size during upload.");
            }

            var settingsJson = JsonSerializer.Serialize(ToBrowserSettings(settings));
            var optionsJson = sceneOptions is null ? "null" : JsonSerializer.Serialize(sceneOptions);
            var result = await client.EvaluateAsync(
                $"window.__codexWallpaperSkinFinishUpload({Js(token)}, {Js(mediaMode)}, {settingsJson}, {optionsJson});",
                cancellationToken);
            progress?.Report(1);
            return ReadApplyResult(result, mediaMode);
        }
        catch
        {
            try
            {
                await client.EvaluateAsync(
                    $"window.__codexWallpaperSkinAbortUpload && window.__codexWallpaperSkinAbortUpload({Js(token)});",
                    CancellationToken.None);
            }
            catch
            {
                // Preserve the original upload failure. The existing background stays active.
            }
            throw;
        }
    }

    public async Task UpdateSettingsAsync(WallpaperSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Normalize();
        var json = JsonSerializer.Serialize(ToBrowserSettings(settings));
        var result = await RequireClient().EvaluateAsync(
            $"(() => {{ const helper = window.__codexWallpaperSkinSetSettings; if (typeof helper !== 'function') return false; helper({json}); return true; }})()",
            cancellationToken);
        if (!ReadBoolean(result))
        {
            throw new InvalidOperationException("The wallpaper runtime is no longer connected; reconnect and apply the wallpaper again.");
        }
        if (_captureSession is not null)
        {
            await _captureSession.UpdateSettingsAsync(settings, cancellationToken);
        }
    }

    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        await StopCaptureAsync();
        _ = await CleanupClientAsync(RequireClient(), cancellationToken);
        SetBackendStatus(WallpaperBackendStatus.None, string.Empty);
    }

    public async Task<int> CleanupAllAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        await StopCaptureAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var operationToken = timeout.Token;
        var endpointUri = CdpEndpoint.Normalize(endpoint);
        CdpProcessIdentity.EnsureOfficialCodexOwnsPort(endpointUri.Port);
        var targets = await CdpDiscovery.GetTargetsAsync(endpoint, operationToken);
        var pages = targets
            .Where(target => target.Type.Equals("page", StringComparison.OrdinalIgnoreCase)
                && target.Url.StartsWith("app://", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (pages.Length == 0)
        {
            throw new InvalidOperationException("CDP exposes no Codex app:// page to restore.");
        }
        if (pages.Length > MaximumCleanupPages)
        {
            throw new InvalidOperationException(
                $"CDP exposes {pages.Length} Codex app pages; refusing to process more than {MaximumCleanupPages} in one restore.");
        }

        var failures = new List<string>();
        var cleaned = 0;
        foreach (var page in pages)
        {
            operationToken.ThrowIfCancellationRequested();
            try
            {
                await using var client = new CdpClient();
                await client.ConnectAsync(endpoint, page.Id, operationToken);
                if (await CleanupClientAsync(client, operationToken))
                {
                    cleaned++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Restoring Codex pages timed out before it could finish.");
            }
            catch (Exception exception)
            {
                failures.Add($"{page.Id}: {exception.Message}");
            }
        }
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Restored {cleaned} of {pages.Length} Codex pages; {failures.Count} failed: {string.Join(" | ", failures)}");
        }
        SetBackendStatus(WallpaperBackendStatus.None, string.Empty);
        return cleaned;
    }

    private static async Task<bool> CleanupClientAsync(CdpClient client, CancellationToken cancellationToken)
    {
        var artifactProbe = await client.EvaluateAsync(ArtifactProbeScript, cancellationToken);
        var hadArtifacts = ReadBoolean(artifactProbe);
        await client.EvaluateAsync(CleanupScript, cancellationToken);
        var verification = await client.EvaluateAsync(CleanupVerificationScript, cancellationToken);
        if (!ReadBoolean(verification))
        {
            throw new InvalidOperationException(
                "The renderer did not confirm complete removal of the wallpaper runtime and its helper functions.");
        }
        if (hadArtifacts)
        {
            var nativeSurface = await client.EvaluateAsync(NativeSurfaceProbeScript, cancellationToken);
            if (!ReadBoolean(nativeSurface))
            {
                throw new InvalidOperationException(
                    "The wallpaper runtime was removed, but the renderer did not confirm an intact native Codex theme surface.");
            }
        }
        return hadArtifacts;
    }

    private CdpClient RequireClient() =>
        _client is { IsConnected: true }
            ? _client
            : throw new InvalidOperationException("Connect to the Codex CDP endpoint first.");

    private sealed record SceneFallbackAttempt(WallpaperApplyResult? Result, Exception? Failure);

    /// <summary>
    /// Starts native Wallpaper Engine capture and its recovery supervisor.
    /// Throws when the native path cannot start, so the caller can fall back to
    /// an explicitly labeled renderer.
    /// </summary>
    private async Task<WallpaperApplyResult> StartNativeCaptureAsync(
        CdpClient client,
        WallpaperEntry wallpaper,
        WallpaperSettings settings,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var viewport = await GetViewportAsync(client, cancellationToken);
        var session = await WallpaperEngineCaptureSession.StartAsync(
            wallpaper, settings, viewport.Width, viewport.Height, cancellationToken);
        try
        {
            await using var initialFrame = new MemoryStream(session.InitialFrame, writable: false);
            var initial = await UploadAsync(
                client, initialFrame, "wallpaper-engine-capture.jpg", "image", settings, null,
                progress, cancellationToken);
            var token = Guid.NewGuid().ToString("N");
            var captureStarted = await client.EvaluateAsync(
                $"window.__codexWallpaperSkinBeginCapturedStream({Js(token)})", cancellationToken);
            if (!ReadBoolean(captureStarted))
            {
                throw new InvalidOperationException("Codex rejected the native capture stream lease.");
            }
            _captureToken = token;
            Interlocked.Exchange(ref _captureSession, session);
            session.StartStreaming(PublishCapturedFrameAsync, ReadCapturePointerAsync);
            StartCaptureSupervisor(wallpaper, settings);
            return initial;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Applies the labeled fallbacks in order: the safe renderer when the package
    /// is inside its documented limits, then the validated Workshop preview.
    /// Returns a null result plus the reason when neither is possible.
    /// </summary>
    private static async Task<SceneFallbackAttempt> ApplySceneFallbackAsync(
        CdpClient client,
        WallpaperEntry wallpaper,
        WallpaperSettings settings,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        Exception? nativeFailure)
    {
        var scenePackagePath = WallpaperCatalog.ResolveScenePackagePath(wallpaper);
        Exception? sceneFailure;
        if (scenePackagePath is not null && ScenePackageValidator.TryValidate(scenePackagePath, out _))
        {
            try
            {
                await using var sceneStream = ScenePackageValidator.OpenValidated(scenePackagePath);
                var sceneOptions = SceneRuntimeAssets.Load(wallpaper);
                var limited = await UploadAsync(
                    client, sceneStream, scenePackagePath, "scene", settings, sceneOptions,
                    progress, cancellationToken);
                return new SceneFallbackAttempt(nativeFailure is null ? limited : limited with
                {
                    Warning = "Wallpaper Engine high-fidelity rendering was unavailable; the limited built-in Scene renderer was used. "
                        + LimitMessage(nativeFailure.Message)
                }, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                sceneFailure = exception;
            }
        }
        else
        {
            sceneFailure = new InvalidDataException(scenePackagePath is null
                ? "This Scene project keeps its content outside scene.pkg, which the built-in safe renderer cannot read."
                : "This scene.pkg is outside the built-in safe renderer's version or size limits.");
        }

        if (string.IsNullOrWhiteSpace(wallpaper.PreviewPath))
        {
            return new SceneFallbackAttempt(null, sceneFailure);
        }
        try
        {
            progress?.Report(0);
            await using var previewStream = WallpaperCatalog.OpenValidatedPreviewFile(wallpaper);
            var fallback = await UploadAsync(
                client, previewStream, wallpaper.PreviewPath!, "image", settings, null,
                progress, cancellationToken);
            var fallbackMode = Path.GetExtension(wallpaper.PreviewPath).Equals(".gif", StringComparison.OrdinalIgnoreCase)
                ? "animated-preview"
                : "static-preview";
            return new SceneFallbackAttempt(fallback with
            {
                Mode = fallbackMode,
                Warning = "The live Scene backends were unavailable, so a validated Workshop preview is shown. "
                    + LimitMessage(nativeFailure?.Message ?? sceneFailure.Message)
            }, sceneFailure);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new SceneFallbackAttempt(null, exception);
        }
    }

    /// <summary>
    /// Watches the active capture stream. A degraded stream is reported as
    /// "native dynamic, reduced frame rate"; a stopped stream is restarted with
    /// bounded backoff, and if that keeps failing the last good frame is replaced
    /// by a clearly labeled fallback instead of a stale hidden session.
    /// </summary>
    private void StartCaptureSupervisor(WallpaperEntry wallpaper, WallpaperSettings settings)
    {
        var previous = Interlocked.Exchange(ref _captureRecoveryLifetime, new CancellationTokenSource());
        previous?.Cancel();
        var lifetime = _captureRecoveryLifetime!;
        var supervisor = Task.Run(() => SuperviseCaptureAsync(wallpaper, settings, lifetime.Token));
        Interlocked.Exchange(ref _captureRecoverySupervisor, supervisor);
    }

    private async Task SuperviseCaptureAsync(
        WallpaperEntry wallpaper,
        WallpaperSettings settings,
        CancellationToken lifetime)
    {
        var consecutiveFailures = 0;
        var lastReason = "the capture stream stopped";
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var session = Volatile.Read(ref _captureSession);
                if (session is null)
                {
                    return;
                }

                // Report live health while the stream runs, so a stream that cannot
                // keep up is named instead of silently degrading.
                while (!lifetime.IsCancellationRequested && session.IsRunning)
                {
                    var status = CaptureRecoveryPolicy.Classify(session.Snapshot());
                    if (status != BackendStatus)
                    {
                        SetBackendStatus(status, status == WallpaperBackendStatus.NativeDynamicReducedFrameRate
                            ? "Wallpaper Engine is rendering this scene below its target frame rate."
                            : "Wallpaper Engine is rendering this scene at full fidelity.");
                    }
                    await Task.Delay(HealthPollInterval, lifetime);
                }
                if (lifetime.IsCancellationRequested)
                {
                    return;
                }
                if (!ReferenceEquals(Volatile.Read(ref _captureSession), session))
                {
                    // A newer apply owns the stream now.
                    return;
                }

                var health = session.Snapshot();
                if (!CaptureRecoveryPolicy.HasFailed(health))
                {
                    // A clean stop (Restore, another apply, controller shutdown)
                    // deliberately leaves the last presented frame in place.
                    return;
                }
                if (session.FailureReason.Length > 0)
                {
                    lastReason = session.FailureReason;
                }
                consecutiveFailures++;
                if (!CaptureRecoveryPolicy.ShouldRetry(consecutiveFailures))
                {
                    break;
                }

                SetBackendStatus(
                    WallpaperBackendStatus.NativeDynamicReducedFrameRate,
                    $"Native capture stopped ({lastReason}). Retrying ({consecutiveFailures}/{CaptureRecoveryPolicy.MaximumRecoveryAttempts})…");
                await Task.Delay(CaptureRecoveryPolicy.BackoffForAttempt(consecutiveFailures), lifetime);
                if (lifetime.IsCancellationRequested)
                {
                    return;
                }
                try
                {
                    await StartNativeCaptureAsync(RequireClient(), wallpaper, settings, null, lifetime);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    lastReason = exception.Message;
                }
            }

            await ApplyCaptureExhaustedAsync(wallpaper, settings, lastReason, lifetime);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Recovery is best effort; the last presented frame remains visible.
        }
    }

    private async Task ApplyCaptureExhaustedAsync(
        WallpaperEntry wallpaper,
        WallpaperSettings settings,
        string reason,
        CancellationToken lifetime)
    {
        // Close the private render window first: no stale hidden session is left
        // behind after the native backend gives up.
        await CloseCaptureWindowAsync();
        var concise = LimitMessage(reason);
        SetBackendStatus(
            WallpaperBackendStatus.CaptureFailed,
            $"Native capture failed after {CaptureRecoveryPolicy.MaximumRecoveryAttempts} attempts ({concise}). A labeled fallback was applied.");
        try
        {
            var fallback = await ApplySceneFallbackAsync(
                RequireClient(), wallpaper, settings, null, lifetime,
                new TimeoutException("Native capture failed repeatedly: " + concise));
            if (fallback.Result is not null)
            {
                SetBackendStatus(
                    BackendStatuses.FromApplyMode(fallback.Result.Mode),
                    $"Native capture failed ({concise}); {BackendStatuses.Describe(BackendStatuses.FromApplyMode(fallback.Result.Mode))} is active instead.");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // The labeled fallback is best effort; the failure status stands.
        }
    }

    private static async Task<(int Width, int Height)> GetViewportAsync(
        CdpClient client,
        CancellationToken cancellationToken)
    {
        var evaluation = await client.EvaluateAsync(
            "({ width: Math.round(innerWidth * (devicePixelRatio || 1)), height: Math.round(innerHeight * (devicePixelRatio || 1)) })",
            cancellationToken);
        try
        {
            var value = evaluation.GetProperty("result").GetProperty("value");
            return (
                Math.Clamp(value.GetProperty("width").GetInt32(), 960, 3840),
                Math.Clamp(value.GetProperty("height").GetInt32(), 600, 2160));
        }
        catch
        {
            return (1600, 1000);
        }
    }

    private async Task PublishCapturedFrameAsync(
        byte[] frame,
        CancellationToken cancellationToken)
    {
        var client = RequireClient();
        var encoded = Convert.ToBase64String(frame);
        var token = _captureToken
            ?? throw new InvalidOperationException("The native capture stream lease is no longer active.");
        var evaluation = await client.EvaluateAsync(
            $"window.__codexWallpaperSkinSetCapturedFrame({Js(token)}, {Js(encoded)})",
            cancellationToken);
        var value = evaluation.GetProperty("result").GetProperty("value");
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Another controller replaced the native capture stream.");
        }
    }

    /// <summary>
    /// Reads normalized pointer state on its own CDP call so input forwarding is
    /// never gated on a successfully captured or accepted frame.
    /// </summary>
    internal async Task<CapturedPointer?> ReadCapturePointerAsync(CancellationToken cancellationToken)
    {
        var client = RequireClient();
        var token = _captureToken;
        if (token is null)
        {
            return null;
        }
        try
        {
            var evaluation = await client.EvaluateAsync(
                $"window.__codexWallpaperSkinReadCapturePointer({Js(token)})",
                cancellationToken);
            var value = evaluation.GetProperty("result").GetProperty("value");
            if (value.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            return new CapturedPointer(
                Math.Clamp(value.GetProperty("x").GetDouble(), 0, 1),
                Math.Clamp(value.GetProperty("y").GetDouble(), 0, 1),
                value.TryGetProperty("down", out var down) && down.GetBoolean(),
                value.TryGetProperty("hidden", out var hidden) && hidden.GetBoolean(),
                ReadCaptureEvents(value),
                value.TryGetProperty("overflow", out var overflow) && overflow.ValueKind == JsonValueKind.True);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the ordered discrete input transitions exactly as Codex recorded
    /// them; unknown kinds and buttons fail closed instead of being guessed.
    /// </summary>
    private static IReadOnlyList<CapturedInputEvent> ReadCaptureEvents(JsonElement value)
    {
        if (!value.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var result = new List<CapturedInputEvent>();
        foreach (var element in events.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("kind", out var kindValue)
                || kindValue.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            var kind = kindValue.GetString() switch
            {
                "down" => CapturedInputKind.Down,
                "up" => CapturedInputKind.Up,
                "wheel" => CapturedInputKind.Wheel,
                "leave" => CapturedInputKind.Leave,
                _ => (CapturedInputKind?)null
            };
            if (kind is null)
            {
                continue;
            }
            var button = element.TryGetProperty("button", out var buttonValue) && buttonValue.ValueKind == JsonValueKind.String
                ? buttonValue.GetString() switch
                {
                    "right" => CapturedMouseButton.Right,
                    "middle" => CapturedMouseButton.Middle,
                    _ => CapturedMouseButton.Left
                }
                : CapturedMouseButton.Left;
            var deltaY = element.TryGetProperty("deltaY", out var deltaValue) && deltaValue.ValueKind == JsonValueKind.Number
                ? deltaValue.GetDouble()
                : 0;
            var deltaMode = element.TryGetProperty("deltaMode", out var modeValue) && modeValue.ValueKind == JsonValueKind.Number
                ? modeValue.GetInt32()
                : 0;
            result.Add(new CapturedInputEvent(
                kind.Value,
                button,
                deltaY,
                deltaMode,
                ReadCoordinate(element, "x"),
                ReadCoordinate(element, "y"),
                element.TryGetProperty("cancelled", out var cancelled) && cancelled.ValueKind == JsonValueKind.True));
        }
        return result;
    }

    private static double ReadCoordinate(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? Math.Clamp(value.GetDouble(), 0, 1)
            : 0.5;

    /// <summary>
    /// Stops native capture completely: cancels the recovery supervisor, closes
    /// the private render window and releases the stream lease. Used by Restore,
    /// reconnect and controller shutdown so nothing stale is left running.
    /// </summary>
    private async Task StopCaptureAsync()
    {
        var lifetime = Interlocked.Exchange(ref _captureRecoveryLifetime, null);
        var supervisor = Interlocked.Exchange(ref _captureRecoverySupervisor, null);
        lifetime?.Cancel();
        await CloseCaptureWindowAsync();
        if (supervisor is not null)
        {
            try
            {
                await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // A supervisor that will not settle must not block restore.
            }
        }
        lifetime?.Dispose();
    }

    /// <summary>Closes the private Wallpaper Engine window without touching the supervisor.</summary>
    private async Task CloseCaptureWindowAsync()
    {
        var capture = Interlocked.Exchange(ref _captureSession, null);
        _captureToken = null;
        if (capture is not null)
        {
            await capture.DisposeAsync();
        }
    }

    private static object ToBrowserSettings(WallpaperSettings value) => new
    {
        fit = FitToCss(value.Fit),
        focusX = value.FocusX,
        focusY = value.FocusY,
        opacity = value.Opacity,
        overlay = value.BlackOverlay,
        autoPalette = value.AutoPalette,
        paletteStrength = value.PaletteStrength,
        panelOpacity = value.PanelOpacity,
        tintInterfaceText = value.TintInterfaceText,
        blur = value.Blur,
        brightness = value.Brightness,
        contrast = value.Contrast,
        saturation = value.Saturation,
        rate = value.PlaybackRate,
        muted = value.Muted,
        pauseWhenHidden = value.PauseWhenHidden,
        sceneFrameRate = value.SceneFrameRate,
        sceneResolutionScale = value.SceneResolutionScale
    };

    private static WallpaperApplyResult ReadApplyResult(JsonElement evaluation, string fallbackMode)
    {
        var mode = fallbackMode;
        string? warning = null;
        try
        {
            var value = evaluation.GetProperty("result").GetProperty("value");
            if (value.TryGetProperty("mode", out var modeValue) && modeValue.ValueKind == JsonValueKind.String)
            {
                mode = modeValue.GetString() ?? fallbackMode;
            }
            if (value.TryGetProperty("warning", out var warningValue) && warningValue.ValueKind == JsonValueKind.String)
            {
                warning = LimitMessage(warningValue.GetString());
            }
        }
        catch
        {
            // The media is already committed; retain the conservative caller-provided mode.
        }
        return new WallpaperApplyResult(ReadPalette(evaluation), mode, warning);
    }

    private static PaletteResult? ReadPalette(JsonElement evaluation)
    {
        try
        {
            var value = evaluation.GetProperty("result").GetProperty("value");
            if (!value.TryGetProperty("palette", out var palette) || palette.ValueKind == JsonValueKind.Null)
            {
                return null;
            }
            return palette.Deserialize<PaletteResult>(PaletteJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static bool ReadBoolean(JsonElement evaluation)
    {
        try
        {
            return evaluation.GetProperty("result").GetProperty("value").GetBoolean();
        }
        catch
        {
            return false;
        }
    }

    internal static string FitToCss(WallpaperFit fit) => fit switch
    {
        WallpaperFit.Contain => "contain",
        WallpaperFit.Fill => "fill",
        WallpaperFit.None => "none",
        WallpaperFit.ScaleDown => "scale-down",
        _ => "cover"
    };

    private static string LimitMessage(string? message)
    {
        var normalized = string.Join(' ', (message ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 420 ? normalized : normalized[..420];
    }

    private static string Js(string value) => JsonSerializer.Serialize(value);

    internal const string ArtifactProbeScript = """
        (() => {
          const root = document.documentElement;
          return typeof window.__codexWallpaperSkin !== 'undefined'
            || typeof window.__codexWallpaperSkinBeginUpload !== 'undefined'
            || typeof window.__codexWallpaperSkinPushChunk !== 'undefined'
            || typeof window.__codexWallpaperSkinAbortUpload !== 'undefined'
            || typeof window.__codexWallpaperSkinFinishUpload !== 'undefined'
            || typeof window.__codexWallpaperSkinSetSettings !== 'undefined'
            || typeof window.__codexWallpaperSkinBeginCapturedStream !== 'undefined'
            || typeof window.__codexWallpaperSkinSetCapturedFrame !== 'undefined'
            || typeof window.__codexWallpaperSkinReadCapturePointer !== 'undefined'
            || typeof window.__codexWallpaperSkinCleanup !== 'undefined'
            || typeof window.__cwsCreateSceneWallpaper !== 'undefined'
            || typeof window.__cwsWeSceneLibrary !== 'undefined'
            || !!document.getElementById('codex-wallpaper-skin-host')
            || !!document.getElementById('codex-wallpaper-skin-style')
            || root.classList.contains('cws-active')
            || root.classList.contains('cws-palette')
            || root.classList.contains('cws-tint-text')
            || [...root.style].some(name => name.startsWith('--cws-'))
            || document.querySelectorAll('[data-cws-surface], .cws-media, .cws-overlay').length > 0;
        })()
        """;

    internal const string NativeSurfaceProbeScript = """
        (() => {
          if (!location.href.startsWith('app://') || !document.body || document.body.children.length === 0) return false;
          const root = document.documentElement;
          const active = window.__codexWallpaperSkin;
          const injectedMarker = active && !active.disposed
            && active.host?.id === 'codex-wallpaper-skin-host' && active.host?.isConnected
            && active.style?.id === 'codex-wallpaper-skin-style' && active.style?.isConnected
            && root.classList.contains('cws-active')
            ? String(active.nativeSurface || '').trim()
            : '';
          const nativeStyle = getComputedStyle(document.documentElement);
          const marker = injectedMarker || (nativeStyle.getPropertyValue('--app-color-background-surface').trim()
            || nativeStyle.getPropertyValue('--color-surface').trim());
          const lowered = marker.toLowerCase();
          if (!marker || lowered === 'transparent' || lowered === 'initial' || lowered === 'unset' || lowered === 'inherit') return false;
          return typeof CSS === 'undefined' || typeof CSS.supports !== 'function' || CSS.supports('color', marker);
        })()
        """;

    internal const string CleanupScript = """
        (() => {
          if (!location.href.startsWith('app://') || !document.body) {
            throw new Error('Refusing to clean up: the target is no longer a Codex app:// page.');
          }
          let result = 'nothing-to-clean';
          const capturedState = window.__codexWallpaperSkin;
          if (typeof window.__codexWallpaperSkinCleanup === 'function') {
            try { result = window.__codexWallpaperSkinCleanup(); } catch (_) { result = 'fallback-cleaned'; }
          }
          const state = window.__codexWallpaperSkin || capturedState;
          if (state) {
            try { state.disposed = true; } catch (_) {}
            try { state.observer && state.observer.disconnect(); } catch (_) {}
            try { state.rafId && cancelAnimationFrame(state.rafId); } catch (_) {}
            try { state.visibilityHandler && document.removeEventListener('visibilitychange', state.visibilityHandler); } catch (_) {}
            try { state.captureInputHandlers && window.removeEventListener('pointermove', state.captureInputHandlers.move, true); } catch (_) {}
            try { state.captureInputHandlers && window.removeEventListener('pointerdown', state.captureInputHandlers.down, true); } catch (_) {}
            try { state.captureInputHandlers && window.removeEventListener('pointerup', state.captureInputHandlers.up, true); } catch (_) {}
            try { state.captureInputHandlers && window.removeEventListener('pointercancel', state.captureInputHandlers.cancel, true); } catch (_) {}
            try { state.captureInputHandlers && window.removeEventListener('pointerleave', state.captureInputHandlers.leave, true); } catch (_) {}
            try { state.captureInputHandlers && document.removeEventListener('pointerleave', state.captureInputHandlers.leave, true); } catch (_) {}
            try { state.captureInputHandlers && window.removeEventListener('wheel', state.captureInputHandlers.wheel, true); } catch (_) {}
            try { state.pendingCancel && state.pendingCancel(); } catch (_) {}
            try { state.pendingMedia && state.pendingMedia.pause && state.pendingMedia.pause(); } catch (_) {}
            try { state.pendingSceneController && state.pendingSceneController.dispose && state.pendingSceneController.dispose(); } catch (_) {}
            try { state.pendingMedia && state.pendingMedia.remove(); } catch (_) {}
            try { state.pendingUrl && URL.revokeObjectURL(state.pendingUrl); } catch (_) {}
            try { state.media && state.media.pause && state.media.pause(); } catch (_) {}
            try { state.sceneController && state.sceneController.dispose && state.sceneController.dispose(); } catch (_) {}
            try { state.media && state.media.remove(); } catch (_) {}
            try { state.assetUrl && URL.revokeObjectURL(state.assetUrl); } catch (_) {}
            try { state.uploads && state.uploads.forEach(upload => { if (upload?.expiry) clearTimeout(upload.expiry); if (upload?.parts) upload.parts.length = 0; }); } catch (_) {}
            try { state.uploads && state.uploads.clear(); } catch (_) {}
            try { (state.touched || []).forEach(x => {
              if (!x.element) return;
              if (x.value) x.element.style.setProperty('background-color', x.value, x.priority || '');
              else x.element.style.removeProperty('background-color');
            }); } catch (_) {}
            try { state.host && state.host.remove(); } catch (_) {}
            try { state.style && state.style.remove(); } catch (_) {}
          }
          const root = document.documentElement;
          try { document.getElementById('codex-wallpaper-skin-host')?.remove(); } catch (_) {}
          try { document.getElementById('codex-wallpaper-skin-style')?.remove(); } catch (_) {}
          try { document.querySelectorAll('.cws-media, .cws-overlay').forEach(element => element.remove()); } catch (_) {}
          root.classList.remove('cws-active', 'cws-palette', 'cws-tint-text');
          [...root.style].filter(name => name.startsWith('--cws-')).forEach(name => root.style.removeProperty(name));
          document.querySelectorAll('[data-cws-surface]').forEach(x => x.removeAttribute('data-cws-surface'));
          delete window.__codexWallpaperSkin;
          delete window.__codexWallpaperSkinBeginUpload;
          delete window.__codexWallpaperSkinPushChunk;
          delete window.__codexWallpaperSkinAbortUpload;
          delete window.__codexWallpaperSkinFinishUpload;
          delete window.__codexWallpaperSkinSetSettings;
          delete window.__codexWallpaperSkinBeginCapturedStream;
          delete window.__codexWallpaperSkinSetCapturedFrame;
          delete window.__codexWallpaperSkinReadCapturePointer;
          delete window.__codexWallpaperSkinCleanup;
          delete window.__cwsCreateSceneWallpaper;
          delete window.__cwsWeSceneLibrary;
          return result === 'nothing-to-clean' ? result : 'cleaned';
        })()
        """;

    internal const string CleanupVerificationScript = """
        (() => {
          const root = document.documentElement;
          return location.href.startsWith('app://')
            && !!document.body
            && typeof window.__codexWallpaperSkin === 'undefined'
            && !document.getElementById('codex-wallpaper-skin-host')
            && !document.getElementById('codex-wallpaper-skin-style')
            && !root.classList.contains('cws-active')
            && !root.classList.contains('cws-palette')
            && !root.classList.contains('cws-tint-text')
            && ![...root.style].some(name => name.startsWith('--cws-'))
            && typeof window.__codexWallpaperSkinBeginUpload === 'undefined'
            && typeof window.__codexWallpaperSkinPushChunk === 'undefined'
            && typeof window.__codexWallpaperSkinAbortUpload === 'undefined'
            && typeof window.__codexWallpaperSkinFinishUpload === 'undefined'
            && typeof window.__codexWallpaperSkinSetSettings === 'undefined'
            && typeof window.__codexWallpaperSkinBeginCapturedStream === 'undefined'
            && typeof window.__codexWallpaperSkinSetCapturedFrame === 'undefined'
            && typeof window.__codexWallpaperSkinReadCapturePointer === 'undefined'
            && typeof window.__codexWallpaperSkinCleanup === 'undefined'
            && typeof window.__cwsCreateSceneWallpaper === 'undefined'
            && typeof window.__cwsWeSceneLibrary === 'undefined'
            && document.querySelectorAll('[data-cws-surface], .cws-media, .cws-overlay').length === 0;
        })()
        """;

    internal static string BootstrapScript { get; } = SceneRuntimeSource.Script + "\n" + BootstrapCoreScript;

    internal const string BootstrapCoreScript = """
        (() => {
          const root = document.documentElement;
          const discardSceneRuntime = () => {
            try { delete window.__cwsCreateSceneWallpaper; } catch (_) {}
            try { delete window.__cwsWeSceneLibrary; } catch (_) {}
          };
          if (!location.href.startsWith('app://') || !document.body || document.body.children.length === 0) {
            discardSceneRuntime();
            throw new Error('Refusing to inject: this is not a ready Codex app:// page.');
          }
          const existing = window.__codexWallpaperSkin;
          const existingHealthy = existing && existing.version === 12 && !existing.disposed
            && existing.host?.isConnected && existing.style?.isConnected && existing.overlay?.isConnected
            && document.getElementById('codex-wallpaper-skin-host') === existing.host
            && document.getElementById('codex-wallpaper-skin-style') === existing.style
            && existing.host.parentNode === document.body && existing.overlay.parentNode === existing.host
            && existing.style.parentNode === (document.head || root)
            && existing.uploads instanceof Map && existing.marked instanceof Set
            && (!existing.media || (existing.media.isConnected && existing.media.parentNode === existing.host))
            && typeof existing.styleText === 'string' && existing.style.textContent === existing.styleText
            && typeof window.__cwsCreateSceneWallpaper === 'function'
            && window.__cwsWeSceneLibrary?.version === 'we-scene@6b503a36b952f91dbab5e6f378f632f87baf05cc+cws.11'
            && window.__cwsCreateSceneWallpaper.version === 'cws-scene-host-2'
            && existing.helpers
            && typeof window.__codexWallpaperSkinBeginUpload === 'function'
            && window.__codexWallpaperSkinBeginUpload === existing.helpers.beginUpload
            && typeof window.__codexWallpaperSkinPushChunk === 'function'
            && window.__codexWallpaperSkinPushChunk === existing.helpers.pushChunk
            && typeof window.__codexWallpaperSkinAbortUpload === 'function'
            && window.__codexWallpaperSkinAbortUpload === existing.helpers.abortUpload
            && typeof window.__codexWallpaperSkinFinishUpload === 'function'
            && window.__codexWallpaperSkinFinishUpload === existing.helpers.finishUpload
            && typeof window.__codexWallpaperSkinSetSettings === 'function'
            && window.__codexWallpaperSkinSetSettings === existing.helpers.setSettings
            && typeof window.__codexWallpaperSkinBeginCapturedStream === 'function'
            && window.__codexWallpaperSkinBeginCapturedStream === existing.helpers.beginCapturedStream
            && typeof window.__codexWallpaperSkinSetCapturedFrame === 'function'
            && window.__codexWallpaperSkinSetCapturedFrame === existing.helpers.setCapturedFrame
            && typeof window.__codexWallpaperSkinReadCapturePointer === 'function'
            && window.__codexWallpaperSkinReadCapturePointer === existing.helpers.readCapturePointer
            && typeof window.__codexWallpaperSkinCleanup === 'function'
            && window.__codexWallpaperSkinCleanup === existing.helpers.cleanup;
          if (existingHealthy) return 'ready';

          const old = existing;
          if (typeof window.__codexWallpaperSkinCleanup === 'function') {
            try { window.__codexWallpaperSkinCleanup(); } catch (_) {}
          }
          if (old) {
            try { old.disposed = true; } catch (_) {}
            try { old.observer && old.observer.disconnect(); } catch (_) {}
            try { old.rafId && cancelAnimationFrame(old.rafId); } catch (_) {}
            try { old.visibilityHandler && document.removeEventListener('visibilitychange', old.visibilityHandler); } catch (_) {}
            try { old.captureInputHandlers && window.removeEventListener('pointermove', old.captureInputHandlers.move, true); } catch (_) {}
            try { old.captureInputHandlers && window.removeEventListener('pointerdown', old.captureInputHandlers.down, true); } catch (_) {}
            try { old.captureInputHandlers && window.removeEventListener('pointerup', old.captureInputHandlers.up, true); } catch (_) {}
            try { old.captureInputHandlers && window.removeEventListener('pointercancel', old.captureInputHandlers.cancel, true); } catch (_) {}
            try { old.captureInputHandlers && window.removeEventListener('pointerleave', old.captureInputHandlers.leave, true); } catch (_) {}
            try { old.captureInputHandlers && document.removeEventListener('pointerleave', old.captureInputHandlers.leave, true); } catch (_) {}
            try { old.captureInputHandlers && window.removeEventListener('wheel', old.captureInputHandlers.wheel, true); } catch (_) {}
            try { old.pendingCancel && old.pendingCancel(); } catch (_) {}
            try { old.pendingMedia && old.pendingMedia.pause && old.pendingMedia.pause(); } catch (_) {}
            try { old.pendingSceneController && old.pendingSceneController.dispose && old.pendingSceneController.dispose(); } catch (_) {}
            try { old.pendingMedia && old.pendingMedia.remove(); } catch (_) {}
            try { old.pendingUrl && URL.revokeObjectURL(old.pendingUrl); } catch (_) {}
            try { old.media && old.media.pause && old.media.pause(); } catch (_) {}
            try { old.sceneController && old.sceneController.dispose && old.sceneController.dispose(); } catch (_) {}
            try { old.media && old.media.remove(); } catch (_) {}
            try { old.assetUrl && URL.revokeObjectURL(old.assetUrl); } catch (_) {}
            try { old.uploads && old.uploads.forEach(upload => { if (upload?.expiry) clearTimeout(upload.expiry); if (upload?.parts) upload.parts.length = 0; }); } catch (_) {}
            try { old.uploads && old.uploads.clear(); } catch (_) {}
            try { (old.touched || []).forEach(x => {
              if (!x.element) return;
              if (x.value) x.element.style.setProperty('background-color', x.value, x.priority || '');
              else x.element.style.removeProperty('background-color');
            }); } catch (_) {}
            try { old.host && old.host.remove(); } catch (_) {}
            try { old.style && old.style.remove(); } catch (_) {}
          }
          try { document.getElementById('codex-wallpaper-skin-host')?.remove(); } catch (_) {}
          try { document.getElementById('codex-wallpaper-skin-style')?.remove(); } catch (_) {}
          try { document.querySelectorAll('.cws-media, .cws-overlay').forEach(element => element.remove()); } catch (_) {}
          document.querySelectorAll('[data-cws-surface]').forEach(x => x.removeAttribute('data-cws-surface'));
          root.classList.remove('cws-active', 'cws-palette', 'cws-tint-text');
          [...root.style].filter(name => name.startsWith('--cws-')).forEach(name => root.style.removeProperty(name));
          delete window.__codexWallpaperSkin;
          delete window.__codexWallpaperSkinBeginUpload;
          delete window.__codexWallpaperSkinPushChunk;
          delete window.__codexWallpaperSkinAbortUpload;
          delete window.__codexWallpaperSkinFinishUpload;
          delete window.__codexWallpaperSkinSetSettings;
          delete window.__codexWallpaperSkinBeginCapturedStream;
          delete window.__codexWallpaperSkinSetCapturedFrame;
          delete window.__codexWallpaperSkinReadCapturePointer;
          delete window.__codexWallpaperSkinCleanup;

          const nativeStyle = getComputedStyle(root);
          const nativeSurface = nativeStyle.getPropertyValue('--app-color-background-surface').trim()
            || nativeStyle.getPropertyValue('--color-surface').trim();
          if (!nativeSurface) {
            discardSceneRuntime();
            throw new Error('Refusing to inject: the expected Codex surface theme marker is missing.');
          }

          const style = document.createElement('style');
          style.id = 'codex-wallpaper-skin-style';
          const styleText = `
            html.cws-active, html.cws-active body { background: transparent !important; }
            html.cws-active body { isolation: isolate !important; }
            #codex-wallpaper-skin-host { position: fixed; inset: 0; overflow: hidden; z-index: -1; pointer-events: none !important; user-select: none !important; }
            #codex-wallpaper-skin-host > .cws-media { position: absolute; inset: 0; display: block; width: 100%; height: 100%; max-width: none; max-height: none; pointer-events: none !important; }
            #codex-wallpaper-skin-host > .cws-overlay { position: absolute; inset: 0; pointer-events: none !important; }
            html.cws-active:not(.cws-palette) {
              --app-color-background-surface: transparent !important;
              --color-surface: transparent !important;
            }
            html.cws-active.cws-palette [data-cws-surface="root"] { background-color: rgba(var(--cws-surface-rgb), var(--cws-root-alpha)) !important; }
            html.cws-active.cws-palette [data-cws-surface="panel"] { background-color: rgba(var(--cws-surface-rgb), var(--cws-panel-alpha)) !important; }
            html.cws-active:not(.cws-palette) [data-cws-surface="root"] { background-color: color-mix(in srgb, var(--cws-native-surface) calc(var(--cws-root-alpha) * 100%), transparent) !important; }
            html.cws-active:not(.cws-palette) [data-cws-surface="panel"] { background-color: color-mix(in srgb, var(--cws-native-surface) calc(var(--cws-panel-alpha) * 100%), transparent) !important; }
            html.cws-palette {
              --app-color-background-surface: transparent !important;
              --app-color-background-surface-under: transparent !important;
              --app-color-background-elevated-primary: rgba(var(--cws-surface-rgb), var(--cws-elevated-alpha)) !important;
              --app-color-background-elevated-secondary: rgba(var(--cws-surface-rgb), var(--cws-panel-alpha)) !important;
              --app-color-background-button-primary: rgb(var(--cws-accent-rgb)) !important;
              --app-color-background-button-primary-hover: rgb(var(--cws-accent-hover-rgb)) !important;
              --app-color-background-accent: rgba(var(--cws-accent-rgb), .20) !important;
              --app-color-background-accent-hover: rgba(var(--cws-accent-rgb), .28) !important;
              --app-color-background-accent-active: rgba(var(--cws-accent-rgb), .36) !important;
              --app-color-border: rgba(var(--cws-border-rgb), .48) !important;
              --app-color-border-light: rgba(var(--cws-border-rgb), .28) !important;
              --app-color-border-heavy: rgba(var(--cws-border-rgb), .70) !important;
              --app-color-border-focus: rgb(var(--cws-accent-rgb)) !important;
              --app-color-icon-accent: rgb(var(--cws-accent-rgb)) !important;
              --app-color-text-accent: rgb(var(--cws-accent-rgb)) !important;
              --app-color-text-on-accent: rgb(var(--cws-accent-text-rgb)) !important;
              --color-surface: transparent !important;
              --color-surface-secondary: rgba(var(--cws-surface-rgb), var(--cws-under-alpha)) !important;
              --color-surface-tertiary: transparent !important;
              --color-surface-elevated: rgba(var(--cws-surface-rgb), var(--cws-elevated-alpha)) !important;
              --color-border: rgba(var(--cws-border-rgb), .48) !important;
              --color-border-primary: rgb(var(--cws-accent-rgb)) !important;
              --color-border-strong: rgba(var(--cws-border-rgb), .72) !important;
              --color-border-subtle: rgba(var(--cws-border-rgb), .28) !important;
              --color-icon-accent: rgb(var(--cws-accent-rgb)) !important;
              --color-text-accent: rgb(var(--cws-accent-rgb)) !important;
              --color-text-on-accent: rgb(var(--cws-accent-text-rgb)) !important;
              accent-color: rgb(var(--cws-accent-rgb));
            }
            html.cws-palette.cws-tint-text {
              --app-color-text-foreground: rgb(var(--cws-text-rgb)) !important;
              --app-color-text-foreground-secondary: rgb(var(--cws-muted-rgb)) !important;
              --app-color-text-foreground-tertiary: rgba(var(--cws-muted-rgb), .78) !important;
              --app-color-text-secondary: rgb(var(--cws-muted-rgb)) !important;
              --color-text: rgb(var(--cws-text-rgb)) !important;
              --color-text-foreground: rgb(var(--cws-text-rgb)) !important;
              --color-text-primary: rgb(var(--cws-text-rgb)) !important;
              --color-text-secondary: rgb(var(--cws-muted-rgb)) !important;
              --color-text-tertiary: rgba(var(--cws-muted-rgb), .78) !important;
              --color-text-prose: rgb(var(--cws-text-rgb)) !important;
            }
            html.cws-palette ::selection { background: rgba(var(--cws-accent-rgb), .38) !important; }
            html.cws-palette :focus-visible { outline-color: rgb(var(--cws-accent-rgb)) !important; }
          `;
          style.textContent = styleText;
          (document.head || root).appendChild(style);

          const host = document.createElement('div');
          host.id = 'codex-wallpaper-skin-host';
          host.setAttribute('aria-hidden', 'true');
          const overlay = document.createElement('div');
          overlay.className = 'cws-overlay';
          host.appendChild(overlay);
          document.body.appendChild(host);

          const state = window.__codexWallpaperSkin = {
            version: 12, disposed: false, style, host, overlay, media: null, assetUrl: null,
            sceneController: null, pendingSceneController: null,
            pendingMedia: null, pendingUrl: null, pendingCancel: null,
            uploads: new Map(), marked: new Set(), settings: null, rawPalette: null,
            palette: null, observer: null, rafId: 0, visibilityHandler: null, nativeSurface,
            capturePointer: { x: .5, y: .5, down: false, buttons: { left: false, middle: false, right: false } },
            captureInputHandlers: null, captureInputEvents: [], captureInputOverflow: false,
            captureFrameBusy: false, captureToken: null,
            captureFrameCount: 0, captureRejectedCount: 0, captureLastError: '',
            styleText, helpers: null
          };

          const clamp = (value, min, max) => Math.min(max, Math.max(min, Number(value)));
          const mix = (a, b, amount) => a.map((value, index) => Math.round(value + (b[index] - value) * amount));
          const rgbText = value => value.map(x => Math.round(x)).join(', ');
          const hex = value => '#' + value.map(x => Math.round(x).toString(16).padStart(2, '0')).join('');
          const linear = value => { const channel = value / 255; return channel <= .04045 ? channel / 12.92 : Math.pow((channel + .055) / 1.055, 2.4); };
          const luminance = value => .2126 * linear(value[0]) + .7152 * linear(value[1]) + .0722 * linear(value[2]);
          const contrast = (a, b) => { const first = luminance(a), second = luminance(b); return (Math.max(first, second) + .05) / (Math.min(first, second) + .05); };
          const saturation = value => { const max = Math.max(...value), min = Math.min(...value); return max === 0 ? 0 : (max - min) / max; };
          const vivid = value => {
            const gray = (value[0] + value[1] + value[2]) / 3;
            const boosted = value.map(channel => clamp(gray + (channel - gray) * 1.7, 0, 255));
            const light = luminance(boosted);
            if (light < .16) return mix(boosted, [255, 255, 255], .28);
            if (light > .72) return mix(boosted, [0, 0, 0], .24);
            return boosted.map(Math.round);
          };

          const buildPalette = raw => {
            if (!raw || !state.settings) return null;
            const strength = clamp(state.settings.paletteStrength, 0, 1);
            const lightTheme = raw.averageLuminance > .58;
            const neutralSurface = lightTheme ? [244, 246, 249] : [20, 23, 29];
            const tintedSurface = mix(raw.dominant, lightTheme ? [255, 255, 255] : [0, 0, 0], lightTheme ? .80 : .70);
            const surface = mix(neutralSurface, tintedSurface, strength);
            const accent = mix([47, 111, 235], vivid(raw.accent), strength);
            const accentHover = mix(accent, lightTheme ? [0, 0, 0] : [255, 255, 255], .12);
            const white = [248, 250, 253], black = [14, 17, 22];
            let text = contrast(surface, white) >= contrast(surface, black) ? white : black;
            const gentlyTintedText = mix(text, raw.dominant, .07 * strength);
            if (contrast(surface, gentlyTintedText) >= 4.5) text = gentlyTintedText;
            const accentText = contrast(accent, white) >= contrast(accent, black) ? white : black;
            const muted = mix(text, surface, .34);
            const border = mix(accent, text, .35);
            return {
              dominant: hex(raw.dominant), surface: hex(surface), accent: hex(accent), text: hex(text),
              accentText: hex(accentText), textContrast: Math.round(contrast(surface, text) * 10) / 10,
              values: { surface, accent, accentHover, text, accentText, muted, border }
            };
          };

          const samplePalette = media => {
            try {
              const width = media.videoWidth || media.naturalWidth || media.width || 0;
              const height = media.videoHeight || media.naturalHeight || media.height || 0;
              if (!width || !height) return null;
              const canvas = document.createElement('canvas');
              canvas.width = 32; canvas.height = 32;
              const context = canvas.getContext('2d', { willReadFrequently: true });
              context.drawImage(media, 0, 0, 32, 32);
              const pixels = context.getImageData(0, 0, 32, 32).data;
              const buckets = new Map();
              let total = 0, luminanceSum = 0;
              for (let index = 0; index < pixels.length; index += 4) {
                if (pixels[index + 3] < 128) continue;
                const color = [pixels[index], pixels[index + 1], pixels[index + 2]];
                const key = `${color[0] >> 4}:${color[1] >> 4}:${color[2] >> 4}`;
                let bucket = buckets.get(key);
                if (!bucket) { bucket = { count: 0, sums: [0, 0, 0] }; buckets.set(key, bucket); }
                bucket.count++;
                bucket.sums[0] += color[0]; bucket.sums[1] += color[1]; bucket.sums[2] += color[2];
                total++; luminanceSum += luminance(color);
              }
              if (!total || buckets.size === 0) return null;
              const choices = [...buckets.values()].map(bucket => ({ count: bucket.count, color: bucket.sums.map(value => value / bucket.count) }));
              const dominant = choices.slice().sort((a, b) => b.count - a.count)[0].color;
              const score = choice => choice.count * (.12 + Math.pow(saturation(choice.color), 2) * 4) * (.45 + 1 - Math.abs(luminance(choice.color) - .5));
              const accentChoice = choices.filter(choice => choice.count / total >= .01).sort((a, b) => score(b) - score(a))[0];
              return { dominant: dominant.map(Math.round), accent: (accentChoice ? accentChoice.color : dominant).map(Math.round), averageLuminance: luminanceSum / total };
            } catch (_) { return null; }
          };

          const applyPalette = () => {
            const palette = state.settings?.autoPalette ? buildPalette(state.rawPalette) : null;
            state.palette = palette;
            root.classList.toggle('cws-palette', !!palette);
            root.classList.toggle('cws-tint-text', !!palette && !!state.settings?.tintInterfaceText);
            if (!palette) {
              ['--cws-surface-rgb', '--cws-accent-rgb', '--cws-accent-hover-rgb', '--cws-text-rgb',
                '--cws-accent-text-rgb', '--cws-muted-rgb', '--cws-border-rgb']
                .forEach(name => root.style.removeProperty(name));
              return null;
            }
            root.style.setProperty('--cws-surface-rgb', rgbText(palette.values.surface));
            root.style.setProperty('--cws-accent-rgb', rgbText(palette.values.accent));
            root.style.setProperty('--cws-accent-hover-rgb', rgbText(palette.values.accentHover));
            root.style.setProperty('--cws-text-rgb', rgbText(palette.values.text));
            root.style.setProperty('--cws-accent-text-rgb', rgbText(palette.values.accentText));
            root.style.setProperty('--cws-muted-rgb', rgbText(palette.values.muted));
            root.style.setProperty('--cws-border-rgb', rgbText(palette.values.border));
            return { dominant: palette.dominant, surface: palette.surface, accent: palette.accent, text: palette.text, accentText: palette.accentText, textContrast: palette.textContrast };
          };

          const clearSurfaceMarkers = () => {
            state.marked.forEach(element => { try { element.removeAttribute('data-cws-surface'); } catch (_) {} });
            state.marked.clear();
          };
          const scanSurfaces = () => {
            if (state.disposed || !root.classList.contains('cws-active')) return;
            clearSurfaceMarkers();
            const width = Math.max(root.clientWidth, 1), height = Math.max(root.clientHeight, 1), viewportArea = width * height;
            const candidates = new Set([...document.body.children, ...document.querySelectorAll('main, aside, nav, [role="dialog"], [role="region"]')]);
            for (const element of candidates) {
              if (!(element instanceof HTMLElement) || element === host || host.contains(element)) continue;
              if (element.matches('pre, code, kbd, samp, input, textarea, [contenteditable="true"], [role="textbox"]')) continue;
              const rect = element.getBoundingClientRect();
              const ratio = rect.width * rect.height / viewportArea;
              const semanticPanel = element.matches('aside, nav, [role="dialog"], [role="region"]');
              if (ratio < (semanticPanel ? .06 : .32)) continue;
              if (getComputedStyle(element).pointerEvents === 'none') continue;
              const marker = ratio >= .60 ? 'root' : 'panel';
              const markedAncestor = element.parentElement?.closest('[data-cws-surface]');
              if (markedAncestor?.getAttribute('data-cws-surface') === marker) continue;
              element.setAttribute('data-cws-surface', marker);
              state.marked.add(element);
            }
          };
          const scheduleSurfaceScan = () => {
            if (state.disposed || state.rafId) return;
            state.rafId = requestAnimationFrame(() => { state.rafId = 0; scanSurfaces(); });
          };

          const waitForMedia = (media, isVideo) => new Promise((resolve, reject) => {
            let settled = false;
            const finish = callback => value => {
              if (settled) return;
              settled = true; clearTimeout(timeout);
              media.removeEventListener(isVideo ? 'loadeddata' : 'load', ready);
              media.removeEventListener('error', failed); callback(value);
            };
            const ready = finish(resolve);
            const failed = finish(() => reject(new Error('Chromium could not decode the selected wallpaper.')));
            state.pendingCancel = () => failed();
            const timeout = setTimeout(() => failed(), 12000);
            media.addEventListener(isVideo ? 'loadeddata' : 'load', ready, { once: true });
            media.addEventListener('error', failed, { once: true });
            if ((isVideo && media.readyState >= 2) || (!isVideo && media.complete && media.naturalWidth > 0)) ready();
          });

          const discardUpload = token => {
            const upload = state.uploads.get(token);
            if (!upload) return false;
            if (upload.expiry) clearTimeout(upload.expiry);
            if (upload.parts) upload.parts.length = 0;
            state.uploads.delete(token);
            return true;
          };
          const discardAllUploads = () => {
            [...state.uploads.keys()].forEach(discardUpload);
          };

          window.__codexWallpaperSkinBeginUpload = (token, mime) => {
            if (typeof token !== 'string' || !/^[A-Za-z0-9_-]{1,64}$/.test(token)) throw new Error('Invalid upload token');
            const allowedMimes = new Set(['image/png', 'image/jpeg', 'image/webp', 'image/gif', 'video/mp4', 'video/webm', 'application/x-wallpaper-engine-scene']);
            if (!allowedMimes.has(mime)) throw new Error('Unsupported upload MIME type');
            discardAllUploads();
            const upload = {
              mime, parts: [], byteLength: 0,
              maximumBytes: mime.startsWith('video/')
                ? 256 * 1024 * 1024
                : mime === 'application/x-wallpaper-engine-scene'
                  ? 128 * 1024 * 1024
                  : 32 * 1024 * 1024,
              expiry: 0
            };
            upload.expiry = setTimeout(() => discardUpload(token), 5 * 60 * 1000);
            state.uploads.set(token, upload); return true;
          };
          window.__codexWallpaperSkinPushChunk = (token, encoded) => {
            const upload = state.uploads.get(token);
            if (!upload) throw new Error('Unknown upload token');
            if (typeof encoded !== 'string' || encoded.length > 96 * 1024) {
              discardUpload(token); throw new Error('Upload chunk exceeded its encoded size limit');
            }
            const binary = atob(encoded), bytes = new Uint8Array(binary.length);
            if (binary.length > 64 * 1024 || upload.byteLength + binary.length > upload.maximumBytes) {
              discardUpload(token); throw new Error('Wallpaper upload exceeded its byte limit');
            }
            for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
            upload.parts.push(bytes); upload.byteLength += bytes.length; return upload.parts.length;
          };
          window.__codexWallpaperSkinAbortUpload = token => discardUpload(token);
          window.__codexWallpaperSkinSetSettings = settings => {
            state.settings = settings;
            const media = state.media;
            if (media) {
              media.style.objectFit = settings.fit || 'cover';
              media.style.objectPosition = `${settings.focusX}% ${settings.focusY}%`;
              media.style.opacity = String(settings.opacity);
              const brightness = clamp(settings.brightness ?? 1, .5, 1.5);
              const contrastValue = clamp(settings.contrast ?? 1, .5, 1.5);
              const saturationValue = clamp(settings.saturation ?? 1, 0, 2);
              const blurValue = clamp(settings.blur || 0, 0, 30);
              media.style.filter = brightness === 1 && contrastValue === 1 && saturationValue === 1 && blurValue === 0
                ? 'none'
                : `brightness(${brightness}) contrast(${contrastValue}) saturate(${saturationValue}) blur(${blurValue}px)`;
              media.style.transform = settings.blur > 0 ? `scale(${1 + settings.blur / 300})` : 'none';
              if (media.tagName === 'VIDEO') {
                media.playbackRate = settings.rate;
                media.muted = !!settings.muted;
                if (settings.pauseWhenHidden && document.hidden) media.pause();
                else media.play().catch(() => {});
              }
            }
            try { state.sceneController && state.sceneController.update && state.sceneController.update(settings); } catch (_) {}
            state.overlay.style.background = `rgba(0,0,0,${settings.overlay})`;
            const panelAlpha = clamp(settings.panelOpacity, .20, .95);
            root.style.setProperty('--cws-native-surface', state.nativeSurface);
            root.style.setProperty('--cws-panel-alpha', String(panelAlpha));
            root.style.setProperty('--cws-under-alpha', String(Math.min(.18, panelAlpha * .25)));
            root.style.setProperty('--cws-elevated-alpha', String(Math.min(.98, panelAlpha + .12)));
            // Never place an automatic full-workspace veil over the wallpaper.
            // Users who want global dimming can opt into the explicit overlay control.
            root.style.setProperty('--cws-root-alpha', '0');
            const palette = applyPalette(); scheduleSurfaceScan(); return palette;
          };
          const capturePointerState = () => ({
            x: state.capturePointer.x,
            y: state.capturePointer.y,
            down: state.capturePointer.down,
            hidden: !!document.hidden
          });
          // Only the dedicated input channel drains the queue. A frame publish
          // returns position state alone, so it can never consume an input event
          // that the controller has not forwarded yet.
          const drainCaptureInput = () => {
            const snapshot = capturePointerState();
            // Ordered discrete input since the previous read. Movement is carried
            // as state above, so it coalesces naturally while button and wheel
            // ordering is preserved exactly as it happened.
            snapshot.events = state.captureInputEvents.splice(0, state.captureInputEvents.length);
            snapshot.overflow = state.captureInputOverflow;
            state.captureInputOverflow = false;
            return snapshot;
          };
          const enqueueCaptureInput = event => {
            if (state.captureInputEvents.length >= 256) {
              // Never reorder or silently drop a button/wheel transition; report
              // the overflow so the controller can resynchronize instead.
              state.captureInputOverflow = true;
              return;
            }
            state.captureInputEvents.push(event);
          };
          const installCaptureInputHandlers = () => {
            if (state.captureInputHandlers) return;
            const update = event => {
              state.capturePointer.x = clamp(event.clientX / Math.max(1, innerWidth), 0, 1);
              state.capturePointer.y = clamp(event.clientY / Math.max(1, innerHeight), 0, 1);
            };
            const buttonName = button => button === 2 ? 'right' : button === 1 ? 'middle' : 'left';
            const down = event => {
              update(event);
              state.capturePointer.buttons[buttonName(event.button)] = true;
              state.capturePointer.down = true;
              enqueueCaptureInput({ kind: 'down', button: buttonName(event.button), x: state.capturePointer.x, y: state.capturePointer.y });
            };
            const up = event => {
              update(event);
              state.capturePointer.buttons[buttonName(event.button)] = false;
              state.capturePointer.down = state.capturePointer.buttons.left
                || state.capturePointer.buttons.middle || state.capturePointer.buttons.right;
              enqueueCaptureInput({ kind: 'up', button: buttonName(event.button), x: state.capturePointer.x, y: state.capturePointer.y });
            };
            const cancel = event => {
              update(event);
              for (const name of ['left', 'middle', 'right']) {
                if (!state.capturePointer.buttons[name]) continue;
                state.capturePointer.buttons[name] = false;
                enqueueCaptureInput({ kind: 'up', button: name, x: state.capturePointer.x, y: state.capturePointer.y, cancelled: true });
              }
              state.capturePointer.down = false;
              enqueueCaptureInput({ kind: 'leave', x: state.capturePointer.x, y: state.capturePointer.y });
            };
            const wheel = event => {
              update(event);
              enqueueCaptureInput({
                kind: 'wheel',
                deltaY: Number.isFinite(event.deltaY) ? event.deltaY : 0,
                deltaMode: Number.isFinite(event.deltaMode) ? event.deltaMode : 0,
                x: state.capturePointer.x,
                y: state.capturePointer.y
              });
            };
            const leave = () => {
              if (!state.capturePointer.down && state.capturePointer.buttons.left === false
                && state.capturePointer.buttons.middle === false && state.capturePointer.buttons.right === false) {
                enqueueCaptureInput({ kind: 'leave', x: state.capturePointer.x, y: state.capturePointer.y });
                return;
              }
              for (const name of ['left', 'middle', 'right']) {
                if (!state.capturePointer.buttons[name]) continue;
                state.capturePointer.buttons[name] = false;
                enqueueCaptureInput({ kind: 'up', button: name, x: state.capturePointer.x, y: state.capturePointer.y, cancelled: true });
              }
              state.capturePointer.down = false;
              enqueueCaptureInput({ kind: 'leave', x: state.capturePointer.x, y: state.capturePointer.y });
            };
            state.captureInputHandlers = { move: update, down, up, cancel, wheel, leave };
            window.addEventListener('pointermove', update, { passive: true, capture: true });
            window.addEventListener('pointerdown', down, { passive: true, capture: true });
            window.addEventListener('pointerup', up, { passive: true, capture: true });
            window.addEventListener('pointercancel', cancel, { passive: true, capture: true });
            window.addEventListener('pointerleave', leave, { passive: true, capture: true });
            document.addEventListener('pointerleave', leave, { passive: true, capture: true });
            window.addEventListener('wheel', wheel, { passive: true, capture: true });
          };
          window.__codexWallpaperSkinBeginCapturedStream = token => {
            if (typeof token !== 'string' || !/^[A-Za-z0-9_-]{16,64}$/.test(token)) return false;
            state.captureToken = token;
            state.captureFrameBusy = false;
            state.captureFrameCount = 0;
            state.captureRejectedCount = 0;
            state.captureLastError = '';
            state.captureInputEvents = [];
            state.captureInputOverflow = false;
            // Input tracking starts with the lease, so interaction is never gated
            // on the first successfully captured frame.
            installCaptureInputHandlers();
            return true;
          };
          // Independent input channel: input delivery never waits for a frame.
          window.__codexWallpaperSkinReadCapturePointer = token => {
            if (token !== state.captureToken) return false;
            if (state.disposed || window.__codexWallpaperSkin !== state) return false;
            return drainCaptureInput();
          };
          window.__codexWallpaperSkinSetCapturedFrame = (token, encoded) => {
            if (token !== state.captureToken) return false;
            if (typeof encoded !== 'string' || encoded.length === 0 || encoded.length > 3 * 1024 * 1024) {
              throw new Error('Captured frame exceeded its encoded size limit.');
            }
            if (state.disposed || window.__codexWallpaperSkin !== state) {
              throw new Error('The wallpaper runtime is no longer active.');
            }
            const media = state.media;
            if (!(media instanceof HTMLImageElement) || !media.isConnected || media.parentNode !== state.host) {
              throw new Error('The native capture target is unavailable.');
            }
            installCaptureInputHandlers();
            if (state.captureFrameBusy) {
              // Coalesce while a decode is in flight; ordering of the frames that
              // do arrive is preserved.
              state.captureRejectedCount++;
              state.captureLastError = 'a frame was already decoding';
              return capturePointerState();
            }
            // Atomic presentation: decode into a back buffer and swap it into the
            // visible layer only after it decodes successfully. A blank, partial
            // or undecodable frame therefore never replaces the last known-good
            // image and never flashes.
            state.captureFrameBusy = true;
            const candidate = new Image();
            const release = () => {
              state.captureFrameBusy = false;
              candidate.onload = null;
              candidate.onerror = null;
            };
            candidate.onload = () => {
              try {
                const intact = !state.disposed && window.__codexWallpaperSkin === state
                  && state.media === media && media.isConnected && media.parentNode === state.host;
                if (intact) {
                  media.src = candidate.src;
                  state.captureFrameCount++;
                } else {
                  state.captureRejectedCount++;
                  state.captureLastError = 'the wallpaper was replaced while the frame decoded';
                }
              } finally {
                release();
              }
            };
            candidate.onerror = () => {
              state.captureRejectedCount++;
              state.captureLastError = 'the captured frame could not be decoded';
              release();
            };
            candidate.src = `data:image/jpeg;base64,${encoded}`;
            return capturePointerState();
          };
          window.__codexWallpaperSkinFinishUpload = async (token, mediaKind, settings, sceneOptions) => {
            const upload = state.uploads.get(token);
            if (!upload) throw new Error('Unknown upload token');
            const isVideo = mediaKind === 'video';
            const isScene = mediaKind === 'scene';
            if (!['image', 'video', 'scene'].includes(mediaKind)
                || isVideo !== upload.mime.startsWith('video/')
                || isScene !== (upload.mime === 'application/x-wallpaper-engine-scene')) {
              discardUpload(token); throw new Error('Wallpaper media kind did not match its MIME type');
            }
            if (upload.expiry) clearTimeout(upload.expiry);
            state.uploads.delete(token);
            const parts = upload.parts;
            const byteLength = upload.byteLength;
            let blob = null, candidateUrl = null, media = null, sceneController = null, paletteSource = null;
            try {
              if (isScene) {
                const bytes = new Uint8Array(byteLength);
                let offset = 0;
                for (const part of parts) { bytes.set(part, offset); offset += part.length; }
                parts.length = 0;
                if (typeof window.__cwsCreateSceneWallpaper !== 'function') throw new Error('The embedded 2D scene renderer is unavailable.');
                sceneController = await window.__cwsCreateSceneWallpaper(bytes, { ...(sceneOptions || {}), settings });
                media = sceneController?.element || null;
                paletteSource = sceneController?.paletteSource || media;
                if (!(media instanceof HTMLElement)) throw new Error('The 2D scene renderer returned no visual element.');
                state.pendingSceneController = sceneController;
              } else {
                blob = new Blob(parts, { type: upload.mime });
                parts.length = 0;
                candidateUrl = URL.createObjectURL(blob);
                media = document.createElement(isVideo ? 'video' : 'img');
                if (isVideo) { media.autoplay = false; media.loop = true; media.playsInline = true; media.controls = false; media.muted = true; }
                state.pendingUrl = candidateUrl;
                media.src = candidateUrl;
                await waitForMedia(media, isVideo);
                paletteSource = media;
              }
              media.className = 'cws-media'; media.draggable = false;
              state.pendingMedia = media;
              const runtimeIntact = () => !state.disposed && window.__codexWallpaperSkin === state
                && state.host?.isConnected && state.host.parentNode === document.body
                && state.overlay?.isConnected && state.overlay.parentNode === state.host
                && state.style?.isConnected && state.style.parentNode === (document.head || root)
                && state.style.textContent === state.styleText
                && typeof window.__cwsCreateSceneWallpaper === 'function'
                && window.__cwsWeSceneLibrary?.version === 'we-scene@6b503a36b952f91dbab5e6f378f632f87baf05cc+cws.11'
                && window.__cwsCreateSceneWallpaper.version === 'cws-scene-host-2'
                && state.helpers
                && window.__codexWallpaperSkinFinishUpload === state.helpers.finishUpload
                && window.__codexWallpaperSkinSetSettings === state.helpers.setSettings
                && window.__codexWallpaperSkinBeginCapturedStream === state.helpers.beginCapturedStream
                && window.__codexWallpaperSkinSetCapturedFrame === state.helpers.setCapturedFrame
                && window.__codexWallpaperSkinReadCapturePointer === state.helpers.readCapturePointer
                && window.__codexWallpaperSkinCleanup === state.helpers.cleanup;
              if (!runtimeIntact()) {
                throw new Error('The wallpaper runtime was restored or replaced while media was decoding.');
              }
              let rawPalette = samplePalette(paletteSource || media);
              if (!rawPalette && settings?.autoPalette) {
                // Chromium can report loadeddata before the first video frame is
                // paintable to a canvas. Start muted decode and retry briefly so
                // a temporary miss cannot force this and later wallpapers into
                // a dark fallback.
                if (isVideo) {
                  try { media.muted = true; await media.play(); } catch (_) {}
                }
                for (const delay of [60, 180]) {
                  await new Promise(resolve => setTimeout(resolve, delay));
                  if (!runtimeIntact()) {
                    throw new Error('The wallpaper runtime was restored or replaced while palette sampling was pending.');
                  }
                  rawPalette = samplePalette(paletteSource || media);
                  if (rawPalette) break;
                }
                if (isVideo) { try { media.pause(); } catch (_) {} }
              }
              state.pendingCancel = null; state.pendingMedia = null; state.pendingUrl = null; state.pendingSceneController = null;
              const previousMedia = state.media, previousUrl = state.assetUrl, previousSceneController = state.sceneController;
              state.captureToken = null; state.captureFrameBusy = false;
              state.media = media; state.assetUrl = candidateUrl; state.sceneController = sceneController; state.rawPalette = rawPalette;
              host.insertBefore(media, state.overlay); root.classList.add('cws-active');
              window.__codexWallpaperSkinSetSettings(settings);
              if (previousSceneController) { try { previousSceneController.dispose && previousSceneController.dispose(); } catch (_) {} }
              if (previousMedia) { try { previousMedia.pause && previousMedia.pause(); } catch (_) {} previousMedia.remove(); }
              if (previousUrl) URL.revokeObjectURL(previousUrl);
              if (!state.visibilityHandler) {
                state.visibilityHandler = () => {
                  if (!state.media || !state.settings) return;
                  try { state.sceneController && state.sceneController.update && state.sceneController.update(state.settings); } catch (_) {}
                  if (state.media.tagName === 'VIDEO') {
                    if (state.settings.pauseWhenHidden && document.hidden) state.media.pause(); else state.media.play().catch(() => {});
                  }
                };
                document.addEventListener('visibilitychange', state.visibilityHandler);
              }
              if (!state.observer) {
                const semanticSelector = 'main, aside, nav, [role="dialog"], [role="region"]';
                state.observer = new MutationObserver(records => {
                  const runtimeIntact = state.host?.isConnected && state.host.parentNode === document.body
                    && state.overlay?.isConnected && state.overlay.parentNode === state.host
                    && state.style?.isConnected && state.style.parentNode === (document.head || root)
                    && state.style.textContent === state.styleText
                    && typeof window.__cwsCreateSceneWallpaper === 'function'
                    && window.__cwsWeSceneLibrary?.version
                    && (!state.media || (state.media.isConnected && state.media.parentNode === state.host))
                    && state.helpers
                    && window.__codexWallpaperSkinBeginUpload === state.helpers.beginUpload
                    && window.__codexWallpaperSkinPushChunk === state.helpers.pushChunk
                    && window.__codexWallpaperSkinAbortUpload === state.helpers.abortUpload
                    && window.__codexWallpaperSkinFinishUpload === state.helpers.finishUpload
                    && window.__codexWallpaperSkinSetSettings === state.helpers.setSettings
                    && window.__codexWallpaperSkinBeginCapturedStream === state.helpers.beginCapturedStream
                    && window.__codexWallpaperSkinSetCapturedFrame === state.helpers.setCapturedFrame
                    && window.__codexWallpaperSkinReadCapturePointer === state.helpers.readCapturePointer
                    && window.__codexWallpaperSkinCleanup === state.helpers.cleanup;
                  if (!runtimeIntact) {
                    if (window.__codexWallpaperSkin === state && typeof window.__codexWallpaperSkinCleanup === 'function') {
                      try { window.__codexWallpaperSkinCleanup(); } catch (_) {}
                    } else {
                      state.disposed = true;
                      try { state.observer.disconnect(); } catch (_) {}
                      try { state.visibilityHandler && document.removeEventListener('visibilitychange', state.visibilityHandler); } catch (_) {}
                      try { state.captureInputHandlers && window.removeEventListener('pointermove', state.captureInputHandlers.move, true); } catch (_) {}
                      try { state.captureInputHandlers && window.removeEventListener('pointerdown', state.captureInputHandlers.down, true); } catch (_) {}
                      try { state.captureInputHandlers && window.removeEventListener('pointerup', state.captureInputHandlers.up, true); } catch (_) {}
                      try { state.captureInputHandlers && window.removeEventListener('pointercancel', state.captureInputHandlers.cancel, true); } catch (_) {}
                      try { state.captureInputHandlers && window.removeEventListener('pointerleave', state.captureInputHandlers.leave, true); } catch (_) {}
                      try { state.captureInputHandlers && document.removeEventListener('pointerleave', state.captureInputHandlers.leave, true); } catch (_) {}
                      try { state.captureInputHandlers && window.removeEventListener('wheel', state.captureInputHandlers.wheel, true); } catch (_) {}
                      try { state.pendingCancel && state.pendingCancel(); } catch (_) {}
                      try { state.pendingMedia && state.pendingMedia.pause && state.pendingMedia.pause(); } catch (_) {}
                      try { state.pendingSceneController && state.pendingSceneController.dispose && state.pendingSceneController.dispose(); } catch (_) {}
                      try { state.pendingMedia && state.pendingMedia.remove(); } catch (_) {}
                      try { state.pendingUrl && URL.revokeObjectURL(state.pendingUrl); } catch (_) {}
                      try { state.media && state.media.pause && state.media.pause(); } catch (_) {}
                      try { state.sceneController && state.sceneController.dispose && state.sceneController.dispose(); } catch (_) {}
                      try { state.media && state.media.remove(); } catch (_) {}
                      try { state.assetUrl && URL.revokeObjectURL(state.assetUrl); } catch (_) {}
                      try { state.uploads && state.uploads.forEach(upload => { if (upload?.expiry) clearTimeout(upload.expiry); if (upload?.parts) upload.parts.length = 0; }); } catch (_) {}
                      try { state.uploads && state.uploads.clear(); } catch (_) {}
                      try { state.host && state.host.remove(); } catch (_) {}
                      try { state.style && state.style.remove(); } catch (_) {}
                    }
                    return;
                  }
                  const relevant = records.some(record => record.target === document.body || [...record.addedNodes].some(node =>
                    node instanceof HTMLElement && (node.matches(semanticSelector) || node.querySelector?.(semanticSelector))));
                  if (relevant) scheduleSurfaceScan();
                });
                state.observer.observe(document.body, { childList: true, subtree: true });
                if (document.head) state.observer.observe(document.head, { childList: true });
                state.observer.observe(root, { childList: true });
                state.observer.observe(style, { childList: true, characterData: true, subtree: true });
              }
              scanSurfaces();
              return { byteLength, type: upload.mime, mode: sceneController?.mode || mediaKind, warning: sceneController?.warning || null, palette: state.palette ? {
                dominant: state.palette.dominant, surface: state.palette.surface, accent: state.palette.accent,
                text: state.palette.text, accentText: state.palette.accentText, textContrast: state.palette.textContrast
              } : null };
            } catch (error) {
              if (state.pendingMedia === media) state.pendingMedia = null;
              if (state.pendingUrl === candidateUrl) state.pendingUrl = null;
              if (state.pendingSceneController === sceneController) state.pendingSceneController = null;
              state.pendingCancel = null;
              try { sceneController && sceneController.dispose && sceneController.dispose(); } catch (_) {}
              try { media.pause && media.pause(); } catch (_) {}
              try { media && media.remove(); } catch (_) {}
              if (candidateUrl) URL.revokeObjectURL(candidateUrl);
              throw error;
            }
          };

          window.__codexWallpaperSkinCleanup = () => {
            const current = state;
            const ownsGlobals = !window.__codexWallpaperSkin || window.__codexWallpaperSkin === state;
            current.disposed = true;
            try { current.observer && current.observer.disconnect(); } catch (_) {}
            try { current.rafId && cancelAnimationFrame(current.rafId); } catch (_) {}
            try { current.visibilityHandler && document.removeEventListener('visibilitychange', current.visibilityHandler); } catch (_) {}
            try { current.captureInputHandlers && window.removeEventListener('pointermove', current.captureInputHandlers.move, true); } catch (_) {}
            try { current.captureInputHandlers && window.removeEventListener('pointerdown', current.captureInputHandlers.down, true); } catch (_) {}
            try { current.captureInputHandlers && window.removeEventListener('pointerup', current.captureInputHandlers.up, true); } catch (_) {}
            try { current.captureInputHandlers && window.removeEventListener('pointercancel', current.captureInputHandlers.cancel, true); } catch (_) {}
            try { current.captureInputHandlers && window.removeEventListener('pointerleave', current.captureInputHandlers.leave, true); } catch (_) {}
            try { current.captureInputHandlers && document.removeEventListener('pointerleave', current.captureInputHandlers.leave, true); } catch (_) {}
            try { current.captureInputHandlers && window.removeEventListener('wheel', current.captureInputHandlers.wheel, true); } catch (_) {}
            try { current.pendingCancel && current.pendingCancel(); } catch (_) {}
            try { current.pendingMedia && current.pendingMedia.pause && current.pendingMedia.pause(); } catch (_) {}
            try { current.pendingSceneController && current.pendingSceneController.dispose && current.pendingSceneController.dispose(); } catch (_) {}
            try { current.pendingMedia && current.pendingMedia.remove(); } catch (_) {}
            try { current.pendingUrl && URL.revokeObjectURL(current.pendingUrl); } catch (_) {}
            try { current.media && current.media.pause && current.media.pause(); } catch (_) {}
            try { current.sceneController && current.sceneController.dispose && current.sceneController.dispose(); } catch (_) {}
            try { current.media && current.media.remove(); } catch (_) {}
            try { current.assetUrl && URL.revokeObjectURL(current.assetUrl); } catch (_) {}
            try { discardAllUploads(); } catch (_) { current.uploads.clear(); }
            if (ownsGlobals) {
              clearSurfaceMarkers();
              root.classList.remove('cws-active', 'cws-palette', 'cws-tint-text');
              [...root.style].filter(name => name.startsWith('--cws-')).forEach(name => root.style.removeProperty(name));
            }
            try { current.host && current.host.remove(); } catch (_) {}
            try { current.style && current.style.remove(); } catch (_) {}
            if (ownsGlobals) {
              delete window.__codexWallpaperSkin;
              delete window.__codexWallpaperSkinBeginUpload;
              delete window.__codexWallpaperSkinPushChunk;
              delete window.__codexWallpaperSkinAbortUpload;
              delete window.__codexWallpaperSkinFinishUpload;
              delete window.__codexWallpaperSkinSetSettings;
              delete window.__codexWallpaperSkinBeginCapturedStream;
              delete window.__codexWallpaperSkinSetCapturedFrame;
              delete window.__codexWallpaperSkinReadCapturePointer;
              delete window.__codexWallpaperSkinCleanup;
            }
            return 'cleaned';
          };
          state.helpers = {
            beginUpload: window.__codexWallpaperSkinBeginUpload,
            pushChunk: window.__codexWallpaperSkinPushChunk,
            abortUpload: window.__codexWallpaperSkinAbortUpload,
            finishUpload: window.__codexWallpaperSkinFinishUpload,
            setSettings: window.__codexWallpaperSkinSetSettings,
            beginCapturedStream: window.__codexWallpaperSkinBeginCapturedStream,
            setCapturedFrame: window.__codexWallpaperSkinSetCapturedFrame,
            readCapturePointer: window.__codexWallpaperSkinReadCapturePointer,
            cleanup: window.__codexWallpaperSkinCleanup
          };
          return 'ready';
        })()
        """;

    public async ValueTask DisposeAsync()
    {
        await StopCaptureAsync();
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
    }
}
