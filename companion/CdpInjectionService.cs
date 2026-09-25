using System.Text.Json;
using System.Threading.Channels;

namespace CodexWallpaperSkin;

public sealed record BrowserMediaCapabilityReport(
    bool SecureContext,
    bool LoopbackWebSocket,
    bool VideoDecoder,
    bool H264DecoderConfiguration,
    bool VideoFrame,
    bool CreateImageBitmap,
    string UserAgent,
    string? Error);

public sealed record ActiveStreamDiagnostics(
    long Received,
    long Presented,
    long Dropped,
    long DecodeErrors,
    long Queued,
    long Batches,
    long EncodedBytes,
    int DecodeQueueSize,
    long RecoveryCount,
    string StreamIdentity,
    DateTimeOffset? LastPresentation,
    bool PageHidden,
    string Mode,
    string? TransportError,
    NativeStreamMetrics? Native);

public sealed record StreamWatchdogRuntimeSnapshot(
    bool ActiveCapture,
    long RecoveryCount,
    string? LastRecoveryKind,
    DateTimeOffset? LastRecoveryAt);

public sealed class CdpInjectionService : IAsyncDisposable
{
    private const int UploadChunkSize = 64 * 1024;
    private const int MaximumCleanupPages = 16;
    private static readonly TimeSpan MaximumApplyDuration = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions PaletteJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private CdpClient? _client;
    private string? _endpoint;
    private CaptureLease? _captureLease;
    private readonly SemaphoreSlim _transitionLock = new(1, 1);
    private long _recoveryCount;
    private string? _lastRecoveryKind;
    private long _lastRecoveryUnixMilliseconds = -1;

    public bool IsConnected => _client?.IsConnected == true;
    public bool HasActiveCapture => _captureLease?.Session.IsRunning == true;
    public Task ActiveCaptureCompletion => _captureLease?.Session.Completion ?? Task.CompletedTask;
    public CdpTarget? Target => _client?.Target;
    public void RecordRecoveryAttempt(LiveStreamHealthKind kind = LiveStreamHealthKind.StreamEnded)
    {
        Interlocked.Increment(ref _recoveryCount);
        Volatile.Write(ref _lastRecoveryKind, kind.ToString());
        Interlocked.Exchange(ref _lastRecoveryUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public StreamWatchdogRuntimeSnapshot GetStreamWatchdogSnapshot()
    {
        var milliseconds = Interlocked.Read(ref _lastRecoveryUnixMilliseconds);
        return new StreamWatchdogRuntimeSnapshot(
            HasActiveCapture,
            Interlocked.Read(ref _recoveryCount),
            Volatile.Read(ref _lastRecoveryKind),
            milliseconds >= 0 ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null);
    }

    public async Task<bool> CheckConnectionAsync(CancellationToken cancellationToken = default)
    {
        var client = _client;
        var endpoint = _endpoint;
        if (client is not { IsConnected: true } || client.Target is null || string.IsNullOrWhiteSpace(endpoint))
            return false;
        try
        {
            var targets = await CdpDiscovery.GetTargetsAsync(endpoint, cancellationToken);
            if (!CdpDiscovery.GetConnectableCodexPages(targets)
                .Any(target => target.Id.Equals(client.Target.Id, StringComparison.Ordinal)))
                return false;
            return ReadBoolean(await client.EvaluateAsync(NativeSurfaceProbeScript, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private sealed record CaptureLease(
        WallpaperEngineCaptureSession Session,
        string Token,
        LoopbackMediaStreamServer? MediaStream,
        H264BatchPublisher? H264Publisher);

    private sealed class H264BatchPublisher : IAsyncDisposable
    {
        private const int MaximumBatchFrames = 6;
        private const int MaximumBatchBytes = 2 * 1024 * 1024;
        private readonly CdpClient _client;
        private readonly string _token;
        private readonly TimeSpan _coalescingDelay;
        private readonly Channel<H264EncodedFrame> _frames = Channel.CreateBounded<H264EncodedFrame>(
            new BoundedChannelOptions(24)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource _firstPresentation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _worker;
        private long _queued;
        private long _batches;
        private long _bytes;
        private string? _lastError;

        public H264BatchPublisher(CdpClient client, string token, int targetFrameRate)
        {
            _client = client;
            _token = token;
            // Keep enough frames together to amortize CDP JSON overhead without
            // delivering 60 FPS video in visibly uneven 50 ms bursts.
            _coalescingDelay = TimeSpan.FromMilliseconds(targetFrameRate >= 60 ? 24 : 40);
            _worker = Task.Run(() => RunAsync(_lifetime.Token));
        }

        public (long Queued, long Batches, long Bytes) Metrics =>
            (Interlocked.Read(ref _queued), Interlocked.Read(ref _batches), Interlocked.Read(ref _bytes));
        public string? LastError => Volatile.Read(ref _lastError);

        public async Task PublishAsync(H264EncodedFrame frame, CancellationToken cancellationToken)
        {
            await _frames.Writer.WriteAsync(frame, cancellationToken);
            Interlocked.Increment(ref _queued);
        }

        public async Task WaitForFirstPresentationAsync(CancellationToken cancellationToken) =>
            await _firstPresentation.Task.WaitAsync(cancellationToken);

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (await _frames.Reader.WaitToReadAsync(cancellationToken))
                {
                    var batch = new List<H264EncodedFrame>(MaximumBatchFrames);
                    var byteLength = 0;
                    if (_frames.Reader.TryRead(out var first))
                    {
                        batch.Add(first);
                        byteLength += first.Data.Length;
                    }
                    await Task.Delay(_coalescingDelay, cancellationToken);
                    while (batch.Count < MaximumBatchFrames
                        && byteLength < MaximumBatchBytes
                        && _frames.Reader.TryRead(out var frame))
                    {
                        batch.Add(frame);
                        byteLength += frame.Data.Length;
                    }
                    if (batch.Count == 0) continue;
                    var payload = JsonSerializer.Serialize(batch.Select(frame => new
                    {
                        timestamp = frame.TimestampMicroseconds,
                        keyFrame = frame.KeyFrame,
                        data = Convert.ToBase64String(frame.Data)
                    }));
                    var evaluation = await _client.EvaluateAsync(
                        $"window.__codexWallpaperSkinPushH264Batch({Js(_token)}, {payload})",
                        cancellationToken);
                    var status = ReadString(evaluation);
                    if (status.Equals("stale", StringComparison.Ordinal))
                        throw new InvalidOperationException("Another controller replaced the H.264 stream.");
                    if (!status.Equals("accepted", StringComparison.Ordinal)
                        && !status.Equals("presented", StringComparison.Ordinal))
                        throw new IOException($"Codex rejected an H.264 batch ({status}).");
                    Interlocked.Increment(ref _batches);
                    Interlocked.Add(ref _bytes, byteLength);
                    if (status.Equals("presented", StringComparison.Ordinal))
                        _firstPresentation.TrySetResult();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _firstPresentation.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _lastError, LimitMessage(exception.Message));
                _frames.Writer.TryComplete(exception);
                _firstPresentation.TrySetException(exception);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _frames.Writer.TryComplete();
            _lifetime.Cancel();
            try { await _worker; } catch { }
            _lifetime.Dispose();
        }
    }

    public async Task<CdpTarget> ConnectAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        await StopCaptureAsync(cancellationToken);
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
        IReadOnlyList<CdpTarget> targets;
        try
        {
            targets = await CdpDiscovery.GetTargetsAsync(endpoint, operationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Finding a verified Codex app page exceeded the 20-second safety timeout.", exception);
        }
        var pages = CdpDiscovery.GetConnectableCodexPages(targets);
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

                // Codex can publish its main target before the first navigation
                // and React shell have settled. Re-check both target ownership
                // and the native surface after a short stability window so the
                // caller never receives a connection that is already obsolete.
                await Task.Delay(TimeSpan.FromMilliseconds(750), operationToken);
                var stableTargets = await CdpDiscovery.GetTargetsAsync(endpoint, operationToken);
                var stablePage = CdpDiscovery.GetConnectableCodexPages(stableTargets)
                    .FirstOrDefault(target => target.Id.Equals(page.Id, StringComparison.Ordinal));
                if (stablePage is null)
                {
                    throw new InvalidOperationException("The Codex main page changed while the wallpaper channel was connecting.");
                }
                var stableMarker = await candidate.EvaluateAsync(NativeSurfaceProbeScript, operationToken);
                if (!ReadBoolean(stableMarker))
                {
                    throw new InvalidOperationException("The Codex main page was not stable after startup.");
                }
                _client = candidate;
                _endpoint = endpointUri.AbsoluteUri.TrimEnd('/');
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
        await _transitionLock.WaitAsync(cancellationToken);
        try
        {
            return await ApplyCoreAsync(wallpaper, settings, progress, cancellationToken);
        }
        finally
        {
            _transitionLock.Release();
        }
    }

    private async Task<WallpaperApplyResult> ApplyCoreAsync(
        WallpaperEntry wallpaper,
        WallpaperSettings settings,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MaximumApplyDuration);
        var operationToken = timeout.Token;
        var path = wallpaper.EffectivePath;
        if (!wallpaper.CanApply)
        {
            throw new FileNotFoundException("The selected wallpaper media is unavailable.", path);
        }

        settings.Normalize();
        var client = await EnsureStableClientAsync(operationToken);
        await StopCaptureAsync(operationToken);
        await client.EvaluateAsync(BootstrapScript, operationToken);
        Exception? nativeCaptureFailure = null;
        try
        {
            if (wallpaper.UsesWallpaperEngineCapture)
            {
                if (WallpaperEngineCaptureSession.CanUse(wallpaper))
                {
                    WallpaperEngineCaptureSession? session = null;
                    LoopbackMediaStreamServer? mediaStream = null;
                    H264BatchPublisher? h264Publisher = null;
                    try
                    {
                        var viewport = await GetViewportAsync(client, operationToken);
                        session = await WallpaperEngineCaptureSession.StartAsync(
                            wallpaper, settings, viewport.Width, viewport.Height, operationToken);
                        await using var initialFrame = new MemoryStream(session.InitialFrame, writable: false);
                        var initial = await UploadAsync(
                            client, initialFrame, "wallpaper-engine-capture.jpg", "image", settings, null,
                            progress, operationToken);
                        var captureToken = Guid.NewGuid().ToString("N");
                        Exception? h264Failure = null;
                        if (session.CanUseHardwareH264)
                        {
                            try
                            {
                                var h264Started = await client.EvaluateAsync(
                                    $"window.__codexWallpaperSkinBeginH264Stream({Js(captureToken)}, {session.CaptureWidth & ~1}, {session.CaptureHeight & ~1})",
                                    operationToken);
                                if (ReadString(h264Started).Equals("ready", StringComparison.Ordinal))
                                {
                                    h264Publisher = new H264BatchPublisher(
                                        client, captureToken, settings.SceneFrameRate);
                                    var h264Lease = new CaptureLease(session, captureToken, null, h264Publisher);
                                    _captureLease = h264Lease;
                                    session.StartH264Streaming(
                                        (frame, token) => h264Publisher.PublishAsync(frame, token));
                                    using var firstFrameTimeout = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
                                    firstFrameTimeout.CancelAfter(TimeSpan.FromSeconds(12));
                                    await h264Publisher.WaitForFirstPresentationAsync(firstFrameTimeout.Token);
                                    return initial with
                                    {
                                        Mode = "wallpaper-engine-h264",
                                        Warning = "Rendered by Wallpaper Engine, encoded by the Windows hardware H.264 encoder, and decoded by Codex WebCodecs on one persistent surface. "
                                            + "Compressed frames are coalesced into bounded control batches because Codex blocks additional local media ports. "
                                            + (session.ConfiguredWallpaperEngineFrameRateLimit is int engineFps && engineFps < settings.SceneFrameRate
                                                ? $"Wallpaper Engine currently limits rendering to {engineFps} FPS, below this app's {settings.SceneFrameRate} FPS target. "
                                                : string.Empty)
                                            + "Closing the adjustment window keeps the controller running in the notification area."
                                    };
                                }
                            }
                            catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception exception)
                            {
                                h264Failure = exception;
                                ClearCaptureLease(session);
                                await session.DisposeAsync();
                                if (h264Publisher is not null)
                                {
                                    await h264Publisher.DisposeAsync();
                                    h264Publisher = null;
                                }
                                // A failed encoder/decoder must not strand the
                                // user on a static first frame. Recreate the WE
                                // render lease before entering compatibility.
                                session = await WallpaperEngineCaptureSession.StartAsync(
                                    wallpaper, settings, viewport.Width, viewport.Height, operationToken);
                                captureToken = Guid.NewGuid().ToString("N");
                            }
                        }
                        mediaStream = LoopbackMediaStreamServer.Start();
                        var captureStarted = await client.EvaluateAsync(
                            $"window.__codexWallpaperSkinBeginLoopbackStream({Js(captureToken)}, {Js(mediaStream.Endpoint.AbsoluteUri)})",
                            operationToken);
                        var loopbackStatus = ReadString(captureStarted);
                        if (loopbackStatus.Equals("ready", StringComparison.Ordinal))
                        {
                            var loopbackLease = new CaptureLease(session, captureToken, mediaStream, null);
                            _captureLease = loopbackLease;
                            await mediaStream.WaitForConnectionAsync(operationToken);
                            // Do not report Apply as successful until the binary
                            // receiver has decoded and drawn this stream's first
                            // frame on the persistent compositor surface.
                            await mediaStream.PublishAsync(
                                LoopbackMediaPacketKind.Jpeg,
                                session.InitialFrame,
                                waitForPresentation: true,
                                operationToken);
                            session.StartStreaming(
                                (frame, token) => PublishLoopbackFrameAsync(loopbackLease, frame, token),
                                token => ReadCapturedPointerAsync(loopbackLease, token));
                            return initial with
                            {
                                Mode = "wallpaper-engine-loopback-jpeg",
                                Warning = session.UsesWindowsGraphicsCapture
                                    ? "Rendered by Wallpaper Engine and carried over a bounded local binary stream. "
                                        + "JPEG encoding remains active as the explicit compatibility codec while the GPU video codec is unavailable. "
                                        + "Closing the adjustment window keeps the controller running in the notification area."
                                    : "Rendered by Wallpaper Engine with the compatibility capture path because Windows Graphics Capture was unavailable. "
                                        + "Closing the adjustment window keeps the controller running in the notification area."
                            };
                        }
                        await mediaStream.DisposeAsync();
                        mediaStream = null;
                        var compatibilityStarted = await client.EvaluateAsync(
                            $"window.__codexWallpaperSkinBeginCapturedStream({Js(captureToken)})",
                            operationToken);
                        if (!ReadBoolean(compatibilityStarted))
                        {
                            throw new InvalidOperationException("Codex rejected the native capture compatibility stream lease.");
                        }
                        var lease = new CaptureLease(session, captureToken, null, null);
                        _captureLease = lease;
                        await PublishCapturedFrameAsync(lease, session.InitialFrame, operationToken);
                        session.StartStreaming(
                            (frame, token) => PublishCapturedFrameAsync(lease, frame, token),
                            token => ReadCapturedPointerAsync(lease, token));
                        return initial with
                        {
                            Mode = "wallpaper-engine-capture",
                            Warning = session.UsesWindowsGraphicsCapture
                                ? "Rendered by Wallpaper Engine through the reduced-frame-rate JPEG/CDP compatibility backend because Codex blocked the isolated local media channel. "
                                    + (h264Failure is null ? string.Empty : "Hardware video startup failed safely: " + LimitMessage(h264Failure.Message) + " ")
                                    + "Closing the adjustment window keeps the controller running in the notification area."
                                : "Rendered by Wallpaper Engine with the compatibility capture path because Windows Graphics Capture was unavailable. "
                                    + "Closing the adjustment window keeps the controller running in the notification area."
                        };
                    }
                    catch (OperationCanceledException)
                    {
                        if (session is not null) ClearCaptureLease(session);
                        if (session is not null) await session.DisposeAsync();
                        if (mediaStream is not null) await mediaStream.DisposeAsync();
                        if (h264Publisher is not null) await h264Publisher.DisposeAsync();
                        throw;
                    }
                    catch (Exception exception)
                    {
                        if (session is not null)
                        {
                            ClearCaptureLease(session);
                        }
                        if (session is not null) await session.DisposeAsync();
                        if (mediaStream is not null) await mediaStream.DisposeAsync();
                        if (h264Publisher is not null) await h264Publisher.DisposeAsync();
                        nativeCaptureFailure = exception;
                    }
                }
                if (wallpaper.IsScene)
                {
                    try
                    {
                        await using var sceneStream = WallpaperCatalog.OpenValidatedMediaFile(wallpaper);
                        var sceneOptions = SceneRuntimeAssets.Load(wallpaper);
                        var limited = await UploadAsync(
                            client, sceneStream, path!, wallpaper.MediaMode, settings, sceneOptions,
                            progress, operationToken);
                        return nativeCaptureFailure is null ? limited : limited with
                        {
                            Warning = "Wallpaper Engine high-fidelity rendering was unavailable; the limited built-in Scene renderer was used. "
                                + LimitMessage(nativeCaptureFailure.Message)
                        };
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception sceneError)
                    {
                        nativeCaptureFailure ??= sceneError;
                    }
                }
                if (!string.IsNullOrWhiteSpace(wallpaper.PreviewPath))
                {
                    progress?.Report(0);
                    await using var previewStream = WallpaperCatalog.OpenValidatedPreviewFile(wallpaper);
                    var fallback = await UploadAsync(
                        client, previewStream, wallpaper.PreviewPath!, "image", settings, null,
                        progress, operationToken);
                    var fallbackMode = Path.GetExtension(wallpaper.PreviewPath).Equals(".gif", StringComparison.OrdinalIgnoreCase)
                        ? "animated-preview"
                        : "static-preview";
                    return fallback with
                    {
                        Mode = fallbackMode,
                        Warning = "Native Wallpaper Engine rendering was unavailable; a low-resolution Workshop preview was used. "
                            + LimitMessage(nativeCaptureFailure?.Message ?? "No compatible native renderer was available.")
                    };
                }
                throw new InvalidOperationException(
                    "Native Wallpaper Engine rendering failed and this wallpaper has no safe fallback.",
                    nativeCaptureFailure);
            }

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new FileNotFoundException("The selected wallpaper media is unavailable.", path);
            }
            await using var stream = WallpaperCatalog.OpenValidatedMediaFile(wallpaper);
            return await UploadAsync(
                client, stream, path, wallpaper.MediaMode, settings, null,
                progress, operationToken);
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
        var lease = _captureLease;
        if (lease is not null)
        {
            await lease.Session.UpdateSettingsAsync(settings, cancellationToken);
        }
    }

    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        await StopCaptureAsync(cancellationToken);
        _ = await CleanupClientAsync(RequireClient(), cancellationToken);
    }

    public async Task<int> CleanupAllAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        await StopCaptureAsync(cancellationToken);
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

    private async Task<CdpClient> EnsureStableClientAsync(CancellationToken cancellationToken)
    {
        var endpoint = _endpoint
            ?? throw new InvalidOperationException("Connect to the Codex CDP endpoint first.");
        var client = _client;
        if (client is { IsConnected: true } && client.Target is not null)
        {
            try
            {
                var targets = await CdpDiscovery.GetTargetsAsync(endpoint, cancellationToken);
                var targetStillPrimary = CdpDiscovery.GetConnectableCodexPages(targets)
                    .Any(target => target.Id.Equals(client.Target.Id, StringComparison.Ordinal));
                if (targetStillPrimary)
                {
                    var marker = await client.EvaluateAsync(NativeSurfaceProbeScript, cancellationToken);
                    if (ReadBoolean(marker))
                    {
                        return client;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A socket can remain Open while its renderer has navigated or
                // been replaced. Reconnect below instead of asking the user to
                // click the same button repeatedly.
            }
        }

        await ConnectAsync(endpoint, cancellationToken);
        return RequireClient();
    }

    public async Task<BrowserMediaCapabilityReport> ProbeBrowserMediaAsync(
        CancellationToken cancellationToken = default)
    {
        var client = RequireClient();
        await using var mediaServer = LoopbackMediaStreamServer.Start();
        var script = $$"""
            (async () => {
              const result = {
                secureContext: !!window.isSecureContext,
                loopbackWebSocket: false,
                videoDecoder: typeof VideoDecoder === 'function',
                h264DecoderConfiguration: false,
                videoFrame: typeof VideoFrame === 'function',
                createImageBitmap: typeof window.createImageBitmap === 'function',
                userAgent: String(navigator.userAgent || '').slice(0, 300),
                error: null
              };
              let policyViolation = null;
              const onPolicyViolation = event => {
                policyViolation = `${event.violatedDirective || 'csp'} blocked ${event.blockedURI || 'resource'}`;
              };
              window.addEventListener('securitypolicyviolation', onPolicyViolation);
              try {
                if (result.videoDecoder) {
                  const support = await VideoDecoder.isConfigSupported({
                    codec: 'avc1.42E01E', codedWidth: 1920, codedHeight: 1080,
                    hardwareAcceleration: 'prefer-hardware', optimizeForLatency: true
                  });
                  result.h264DecoderConfiguration = !!support?.supported;
                }
                result.loopbackWebSocket = await new Promise(resolve => {
                  const socket = new WebSocket({{Js(mediaServer.Endpoint.AbsoluteUri)}});
                  const timer = setTimeout(() => { try { socket.close(); } catch (_) {} resolve(false); }, 4000);
                  socket.onopen = () => { clearTimeout(timer); try { socket.close(1000, 'probe'); } catch (_) {} resolve(true); };
                  socket.onerror = () => { clearTimeout(timer); resolve(false); };
                });
              } catch (error) {
                result.error = String(error?.message || error || 'probe-failed').slice(0, 300);
              } finally {
                await new Promise(resolve => setTimeout(resolve, 50));
                window.removeEventListener('securitypolicyviolation', onPolicyViolation);
                if (!result.loopbackWebSocket && !result.error && policyViolation) result.error = policyViolation.slice(0, 300);
              }
              return result;
            })()
            """;
        var evaluation = await client.EvaluateAsync(script, cancellationToken);
        try
        {
            var value = evaluation.GetProperty("result").GetProperty("value");
            var report = new BrowserMediaCapabilityReport(
                value.GetProperty("secureContext").GetBoolean(),
                value.GetProperty("loopbackWebSocket").GetBoolean(),
                value.GetProperty("videoDecoder").GetBoolean(),
                value.GetProperty("h264DecoderConfiguration").GetBoolean(),
                value.GetProperty("videoFrame").GetBoolean(),
                value.GetProperty("createImageBitmap").GetBoolean(),
                value.GetProperty("userAgent").GetString() ?? string.Empty,
                value.GetProperty("error").ValueKind == JsonValueKind.String
                    ? value.GetProperty("error").GetString()
                    : null);
            if (!report.LoopbackWebSocket && string.IsNullOrWhiteSpace(report.Error))
            {
                var connections = mediaServer.GetMetrics().Connections;
                report = report with
                {
                    Error = connections == 0
                        ? "The Codex page did not reach the loopback listener."
                        : "The loopback WebSocket handshake did not complete."
                };
            }
            return report;
        }
        catch (Exception exception)
        {
            throw new InvalidDataException("Codex returned an invalid browser media capability report.", exception);
        }
    }

    public async Task<ActiveStreamDiagnostics> GetActiveStreamDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        var evaluation = await RequireClient().EvaluateAsync(
            """
            (() => {
              const s = window.__codexWallpaperSkin;
              const d = s?.captureDiagnostics;
              if (!s || s.disposed || !d) return null;
              return {
                received: Number(d.received || 0), presented: Number(d.presented || 0),
                dropped: Number(d.dropped || 0), decodeErrors: Number(d.decodeErrors || 0),
                streamIdentity: String(s.captureToken || ''),
                lastPresentation: d.lastPresentation || null,
                pageHidden: document.hidden === true,
                decodeQueueSize: Number(s.h264Decoder?.decodeQueueSize || 0),
                mode: s.h264Decoder ? 'h264-webcodecs' : (s.captureSocket ? 'loopback' : 'jpeg-cdp')
              };
            })()
            """,
            cancellationToken);
        var value = evaluation.GetProperty("result").GetProperty("value");
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("No active native wallpaper stream is available.");
        var publisher = _captureLease?.H264Publisher?.Metrics ?? default;
        return new ActiveStreamDiagnostics(
            value.GetProperty("received").GetInt64(),
            value.GetProperty("presented").GetInt64(),
            value.GetProperty("dropped").GetInt64(),
            value.GetProperty("decodeErrors").GetInt64(),
            publisher.Queued,
            publisher.Batches,
            publisher.Bytes,
            value.GetProperty("decodeQueueSize").GetInt32(),
            Interlocked.Read(ref _recoveryCount),
            value.GetProperty("streamIdentity").GetString() ?? string.Empty,
            value.GetProperty("lastPresentation").ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(value.GetProperty("lastPresentation").GetString(), out var lastPresentation)
                    ? lastPresentation
                    : null,
            value.GetProperty("pageHidden").GetBoolean(),
            value.GetProperty("mode").GetString() ?? "unknown",
            _captureLease?.H264Publisher?.LastError,
            _captureLease?.Session.StreamMetrics);
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
                Math.Clamp(value.GetProperty("width").GetInt32(), 640, 4096),
                Math.Clamp(value.GetProperty("height").GetInt32(), 400, 4096));
        }
        catch
        {
            return (1600, 1000);
        }
    }

    private async Task PublishCapturedFrameAsync(
        CaptureLease lease,
        byte[] frame,
        CancellationToken cancellationToken)
    {
        EnsureCurrentCapture(lease);
        var client = RequireClient();
        var encoded = Convert.ToBase64String(frame);
        var evaluation = await client.EvaluateAsync(
            $"window.__codexWallpaperSkinSetCapturedFrame({Js(lease.Token)}, {Js(encoded)})",
            cancellationToken);
        var presentation = ReadString(evaluation);
        if (presentation.Equals("stale", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Another controller replaced the native capture stream.");
        }
        if (!presentation.Equals("presented", StringComparison.Ordinal))
        {
            throw new IOException($"Codex did not present the captured frame ({presentation}).");
        }
    }

    private async Task PublishLoopbackFrameAsync(
        CaptureLease lease,
        byte[] frame,
        CancellationToken cancellationToken)
    {
        EnsureCurrentCapture(lease);
        var mediaStream = lease.MediaStream
            ?? throw new InvalidOperationException("The local media stream is no longer active.");
        await mediaStream.PublishAsync(
            LoopbackMediaPacketKind.Jpeg,
            frame,
            waitForPresentation: false,
            cancellationToken);
    }

    private async Task<CapturedPointer?> ReadCapturedPointerAsync(
        CaptureLease lease,
        CancellationToken cancellationToken)
    {
        EnsureCurrentCapture(lease);
        var client = RequireClient();
        var evaluation = await client.EvaluateAsync(
            $"window.__codexWallpaperSkinGetCapturedPointer({Js(lease.Token)})",
            cancellationToken);
        try
        {
            var value = evaluation.GetProperty("result").GetProperty("value");
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Another controller replaced the native capture stream.");
            }
            return new CapturedPointer(
                Math.Clamp(value.GetProperty("x").GetDouble(), 0, 1),
                Math.Clamp(value.GetProperty("y").GetDouble(), 0, 1),
                Math.Clamp(value.GetProperty("buttons").GetInt32(), 0, 7),
                Math.Clamp(value.GetProperty("wheel").GetInt32(), -1200, 1200),
                value.GetProperty("hidden").GetBoolean(),
                value.GetProperty("inside").GetBoolean());
        }
        catch (InvalidOperationException) { throw; }
        catch
        {
            return null;
        }
    }

    private async Task StopCaptureAsync(CancellationToken cancellationToken = default)
    {
        var lease = Interlocked.Exchange(ref _captureLease, null);
        if (lease is not null)
        {
            // The caller may stop waiting, but the detached local cleanup must
            // still release every renderer/stream resource in the background.
            await DisposeCaptureLeaseAsync(lease).WaitAsync(cancellationToken);
            var client = _client;
            if (client is { IsConnected: true })
            {
                try
                {
                    await client.EvaluateAsync(
                        $"(() => {{ const s = window.__codexWallpaperSkin; if (!s || s.captureToken !== {Js(lease.Token)}) return false; s.captureToken = null; s.captureFrameBusy = false; s.capturePendingPacket = null; if (s.captureSocket) {{ try {{ s.captureSocket.close(1000, 'replaced'); }} catch (_) {{}} s.captureSocket = null; }} if (s.h264Decoder) {{ try {{ s.h264Decoder.close(); }} catch (_) {{}} s.h264Decoder = null; s.h264Generation = (s.h264Generation || 0) + 1; }} if (s.captureStaging) {{ try {{ s.captureStaging.src = ''; }} catch (_) {{}} s.captureStaging = null; }} return true; }})()",
                        cancellationToken);
                }
                catch
                {
                    // The next bootstrap/upload validates ownership again. A
                    // disconnected page does not need a best-effort invalidation.
                }
            }
        }
    }

    private static async Task DisposeCaptureLeaseAsync(CaptureLease lease)
    {
        try { await lease.Session.DisposeAsync(); } catch { }
        if (lease.MediaStream is not null)
        {
            try { await lease.MediaStream.DisposeAsync(); } catch { }
        }
        if (lease.H264Publisher is not null)
        {
            try { await lease.H264Publisher.DisposeAsync(); } catch { }
        }
    }

    private void EnsureCurrentCapture(CaptureLease lease)
    {
        if (!ReferenceEquals(Volatile.Read(ref _captureLease), lease))
        {
            throw new InvalidOperationException("The native capture stream lease is no longer active.");
        }
    }

    private void ClearCaptureLease(WallpaperEngineCaptureSession session)
    {
        var failedLease = Volatile.Read(ref _captureLease);
        if (failedLease is not null && ReferenceEquals(failedLease.Session, session))
        {
            Interlocked.CompareExchange(ref _captureLease, null, failedLease);
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

    private static string ReadString(JsonElement evaluation)
    {
        try
        {
            return evaluation.GetProperty("result").GetProperty("value").GetString() ?? "invalid";
        }
        catch
        {
            return "invalid";
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
            || typeof window.__codexWallpaperSkinBeginH264Stream !== 'undefined'
            || typeof window.__codexWallpaperSkinPushH264Batch !== 'undefined'
            || typeof window.__codexWallpaperSkinBeginLoopbackStream !== 'undefined'
            || typeof window.__codexWallpaperSkinSetCapturedFrame !== 'undefined'
            || typeof window.__codexWallpaperSkinGetCapturedPointer !== 'undefined'
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
            try { state.captureSocket && state.captureSocket.close(1000, 'cleanup'); } catch (_) {}
            try { state.h264Decoder && state.h264Decoder.close(); } catch (_) {}
            try { state.observer && state.observer.disconnect(); } catch (_) {}
            try { state.rafId && cancelAnimationFrame(state.rafId); } catch (_) {}
            try { state.visibilityHandler && document.removeEventListener('visibilitychange', state.visibilityHandler); } catch (_) {}
            try { state.capturePointerHandlers && window.removeEventListener('pointermove', state.capturePointerHandlers.move, true); } catch (_) {}
            try { state.capturePointerHandlers && window.removeEventListener('pointerenter', state.capturePointerHandlers.enter, true); } catch (_) {}
            try { state.capturePointerHandlers && window.removeEventListener('pointerdown', state.capturePointerHandlers.down, true); } catch (_) {}
            try { state.capturePointerHandlers && window.removeEventListener('pointerup', state.capturePointerHandlers.up, true); } catch (_) {}
            try { state.capturePointerHandlers && window.removeEventListener('pointercancel', state.capturePointerHandlers.cancel, true); } catch (_) {}
            try { state.capturePointerHandlers && window.removeEventListener('pointerleave', state.capturePointerHandlers.leave, true); } catch (_) {}
            try { state.capturePointerHandlers && window.removeEventListener('blur', state.capturePointerHandlers.blur, true); } catch (_) {}
            try { state.capturePointerHandlers && window.removeEventListener('wheel', state.capturePointerHandlers.wheel, true); } catch (_) {}
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
          delete window.__codexWallpaperSkinBeginH264Stream;
          delete window.__codexWallpaperSkinPushH264Batch;
          delete window.__codexWallpaperSkinBeginLoopbackStream;
          delete window.__codexWallpaperSkinSetCapturedFrame;
          delete window.__codexWallpaperSkinGetCapturedPointer;
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
            && typeof window.__codexWallpaperSkinBeginH264Stream === 'undefined'
            && typeof window.__codexWallpaperSkinPushH264Batch === 'undefined'
            && typeof window.__codexWallpaperSkinBeginLoopbackStream === 'undefined'
            && typeof window.__codexWallpaperSkinSetCapturedFrame === 'undefined'
            && typeof window.__codexWallpaperSkinGetCapturedPointer === 'undefined'
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
          const existingHealthy = existing && existing.version === 18 && !existing.disposed
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
            && typeof window.__codexWallpaperSkinBeginH264Stream === 'function'
            && window.__codexWallpaperSkinBeginH264Stream === existing.helpers.beginH264Stream
            && typeof window.__codexWallpaperSkinPushH264Batch === 'function'
            && window.__codexWallpaperSkinPushH264Batch === existing.helpers.pushH264Batch
            && typeof window.__codexWallpaperSkinBeginLoopbackStream === 'function'
            && window.__codexWallpaperSkinBeginLoopbackStream === existing.helpers.beginLoopbackStream
            && typeof window.__codexWallpaperSkinSetCapturedFrame === 'function'
            && window.__codexWallpaperSkinSetCapturedFrame === existing.helpers.setCapturedFrame
            && typeof window.__codexWallpaperSkinGetCapturedPointer === 'function'
            && window.__codexWallpaperSkinGetCapturedPointer === existing.helpers.getCapturedPointer
            && typeof window.__codexWallpaperSkinCleanup === 'function'
            && window.__codexWallpaperSkinCleanup === existing.helpers.cleanup;
          if (existingHealthy) return 'ready';

          const old = existing;
          if (typeof window.__codexWallpaperSkinCleanup === 'function') {
            try { window.__codexWallpaperSkinCleanup(); } catch (_) {}
          }
          if (old) {
            try { old.disposed = true; } catch (_) {}
            try { old.captureSocket && old.captureSocket.close(1000, 'replaced'); } catch (_) {}
            try { old.h264Decoder && old.h264Decoder.close(); } catch (_) {}
            try { old.observer && old.observer.disconnect(); } catch (_) {}
            try { old.rafId && cancelAnimationFrame(old.rafId); } catch (_) {}
            try { old.visibilityHandler && document.removeEventListener('visibilitychange', old.visibilityHandler); } catch (_) {}
            try { old.capturePointerHandlers && window.removeEventListener('pointermove', old.capturePointerHandlers.move, true); } catch (_) {}
            try { old.capturePointerHandlers && window.removeEventListener('pointerenter', old.capturePointerHandlers.enter, true); } catch (_) {}
            try { old.capturePointerHandlers && window.removeEventListener('pointerdown', old.capturePointerHandlers.down, true); } catch (_) {}
            try { old.capturePointerHandlers && window.removeEventListener('pointerup', old.capturePointerHandlers.up, true); } catch (_) {}
            try { old.capturePointerHandlers && window.removeEventListener('pointercancel', old.capturePointerHandlers.cancel, true); } catch (_) {}
            try { old.capturePointerHandlers && window.removeEventListener('pointerleave', old.capturePointerHandlers.leave, true); } catch (_) {}
            try { old.capturePointerHandlers && window.removeEventListener('blur', old.capturePointerHandlers.blur, true); } catch (_) {}
            try { old.capturePointerHandlers && window.removeEventListener('wheel', old.capturePointerHandlers.wheel, true); } catch (_) {}
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
          delete window.__codexWallpaperSkinBeginH264Stream;
          delete window.__codexWallpaperSkinPushH264Batch;
          delete window.__codexWallpaperSkinBeginLoopbackStream;
          delete window.__codexWallpaperSkinSetCapturedFrame;
          delete window.__codexWallpaperSkinGetCapturedPointer;
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
            version: 18, disposed: false, style, host, overlay, media: null, assetUrl: null,
            sceneController: null, pendingSceneController: null,
            pendingMedia: null, pendingUrl: null, pendingCancel: null,
            uploads: new Map(), marked: new Set(), settings: null, rawPalette: null,
            palette: null, observer: null, rafId: 0, visibilityHandler: null, nativeSurface,
            capturePointer: { x: .5, y: .5, buttons: 0, wheel: 0, inside: false }, capturePointerHandlers: null,
            captureFrameBusy: false, captureStaging: null, captureToken: null,
            captureSocket: null, capturePendingPacket: null,
            h264Decoder: null, h264Generation: 0, h264Presented: 0, h264DecodeErrors: 0,
            h264Frames: [], h264PresentationRaf: 0, h264BaseTimestamp: null, h264BaseNow: 0,
            h264LastTimestamp: null,
            captureDiagnostics: { received: 0, presented: 0, dropped: 0, decodeErrors: 0, lastSequence: 0, lastPresentation: null },
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
          const ensureCapturePointerHandlers = () => {
            if (state.capturePointerHandlers) return;
            const update = event => {
              state.capturePointer.x = clamp(event.clientX / Math.max(1, innerWidth), 0, 1);
              state.capturePointer.y = clamp(event.clientY / Math.max(1, innerHeight), 0, 1);
              state.capturePointer.inside = true;
            };
            const enter = event => { update(event); state.capturePointer.inside = true; };
            const down = event => { update(event); state.capturePointer.buttons = event.buttons & 7; };
            const up = event => { update(event); state.capturePointer.buttons = event.buttons & 7; };
            const cancel = event => { update(event); state.capturePointer.buttons = 0; state.capturePointer.inside = false; };
            const leave = event => { update(event); state.capturePointer.buttons = 0; state.capturePointer.inside = false; };
            const blur = () => { state.capturePointer.buttons = 0; state.capturePointer.inside = false; };
            const wheel = event => {
              update(event);
              state.capturePointer.wheel = Math.round(clamp(
                state.capturePointer.wheel - event.deltaY, -1200, 1200));
            };
            state.capturePointerHandlers = { move: update, enter, down, up, cancel, leave, blur, wheel };
            window.addEventListener('pointermove', update, { passive: true, capture: true });
            window.addEventListener('pointerenter', enter, { passive: true, capture: true });
            window.addEventListener('pointerdown', down, { passive: true, capture: true });
            window.addEventListener('pointerup', up, { passive: true, capture: true });
            window.addEventListener('pointercancel', cancel, { passive: true, capture: true });
            window.addEventListener('pointerleave', leave, { passive: true, capture: true });
            window.addEventListener('blur', blur, { passive: true, capture: true });
            window.addEventListener('wheel', wheel, { passive: true, capture: true });
          };
          const closeH264Decoder = () => {
            state.h264Generation++;
            if (state.h264PresentationRaf) {
              try { cancelAnimationFrame(state.h264PresentationRaf); } catch (_) {}
              state.h264PresentationRaf = 0;
            }
            try { state.h264Frames.splice(0).forEach(frame => frame.close()); } catch (_) { state.h264Frames = []; }
            state.h264BaseTimestamp = null;
            state.h264BaseNow = 0;
            state.h264LastTimestamp = null;
            const decoder = state.h264Decoder;
            state.h264Decoder = null;
            if (decoder) {
              try { decoder.close(); } catch (_) {}
            }
          };
          window.__codexWallpaperSkinBeginCapturedStream = token => {
            if (typeof token !== 'string' || !/^[A-Za-z0-9_-]{16,64}$/.test(token)) return false;
            closeH264Decoder();
            const source = state.media;
            if (source instanceof HTMLImageElement) {
              const width = Math.max(1, source.naturalWidth || source.width);
              const height = Math.max(1, source.naturalHeight || source.height);
              const canvas = document.createElement('canvas');
              canvas.width = width; canvas.height = height;
              canvas.className = source.className;
              canvas.style.cssText = source.style.cssText;
              const context = canvas.getContext('2d', { alpha: false });
              if (!context) return false;
              context.drawImage(source, 0, 0, width, height);
              host.insertBefore(canvas, state.overlay);
              state.media = canvas;
              source.remove();
            } else if (!(source instanceof HTMLCanvasElement)) {
              return false;
            }
            state.captureToken = token;
            state.captureFrameBusy = false;
            state.captureStaging = null;
            ensureCapturePointerHandlers();
            return true;
          };
          window.__codexWallpaperSkinBeginH264Stream = async (token, width, height) => {
            if (typeof VideoDecoder !== 'function' || typeof EncodedVideoChunk !== 'function') return 'unsupported';
            if (!window.__codexWallpaperSkinBeginCapturedStream(token)) return 'rejected';
            width = Math.floor(Number(width)); height = Math.floor(Number(height));
            if (width < 64 || height < 64 || width > 4096 || height > 4096 || width * height > 10000000) {
              return 'invalid-size';
            }
            const support = await VideoDecoder.isConfigSupported({
              codec: 'avc1.42E01E', codedWidth: width, codedHeight: height,
              hardwareAcceleration: 'prefer-hardware', optimizeForLatency: true
            });
            if (!support?.supported) return 'unsupported';
            const media = state.media;
            if (!(media instanceof HTMLCanvasElement) || !media.isConnected || media.parentNode !== state.host) {
              return 'canvas-unavailable';
            }
            closeH264Decoder();
            state.captureToken = token;
            const generation = state.h264Generation;
            state.h264Presented = 0;
            state.h264DecodeErrors = 0;
            state.captureDiagnostics = { received: 0, presented: 0, dropped: 0, decodeErrors: 0, lastSequence: 0, lastPresentation: null };
            const presentFrame = frame => {
              const frameWidth = Math.max(1, frame.displayWidth || frame.codedWidth);
              const frameHeight = Math.max(1, frame.displayHeight || frame.codedHeight);
              const context = media.getContext('2d', { alpha: false, desynchronized: true });
              if (!context) throw new Error('canvas-unavailable');
              if (media.width !== frameWidth || media.height !== frameHeight) {
                media.width = frameWidth; media.height = frameHeight;
              }
              context.drawImage(frame, 0, 0, frameWidth, frameHeight);
              state.h264Presented++;
              state.captureDiagnostics.presented++;
              state.captureDiagnostics.lastPresentation = new Date().toISOString();
            };
            const pumpPresentation = now => {
              state.h264PresentationRaf = 0;
              if (state.disposed || state.h264Decoder !== decoder || generation !== state.h264Generation
                  || token !== state.captureToken || state.media !== media) {
                try { state.h264Frames.splice(0).forEach(frame => frame.close()); } catch (_) {}
                return;
              }
              try {
                if (state.h264Frames.length > 0) {
                  const first = state.h264Frames[0];
                  if (state.h264BaseTimestamp === null) {
                    state.h264BaseTimestamp = first.timestamp;
                    state.h264BaseNow = now;
                  }
                  // If decoding or the renderer falls behind, keep the newest
                  // due frame and close older ones. Latency remains bounded.
                  let dueIndex = -1;
                  for (let index = 0; index < state.h264Frames.length; index++) {
                    const target = state.h264BaseNow
                      + Math.max(0, state.h264Frames[index].timestamp - state.h264BaseTimestamp) / 1000;
                    if (target <= now + 1) dueIndex = index; else break;
                  }
                  if (dueIndex >= 0) {
                    for (let index = 0; index < dueIndex; index++) {
                      try { state.h264Frames.shift().close(); } catch (_) {}
                      state.captureDiagnostics.dropped++;
                    }
                    const frame = state.h264Frames.shift();
                    try { presentFrame(frame); } finally { try { frame.close(); } catch (_) {} }
                  }
                }
              } catch (_) {
                state.h264DecodeErrors++;
                state.captureDiagnostics.decodeErrors++;
              }
              if (state.h264Frames.length > 0) {
                state.h264PresentationRaf = requestAnimationFrame(pumpPresentation);
              }
            };
            const decoder = new VideoDecoder({
              output: frame => {
                if (state.disposed || state.h264Decoder !== decoder || generation !== state.h264Generation
                    || token !== state.captureToken || state.media !== media) {
                  try { frame.close(); } catch (_) {}
                  return;
                }
                if (state.h264LastTimestamp !== null && frame.timestamp <= state.h264LastTimestamp) {
                  try { state.h264Frames.splice(0).forEach(queued => queued.close()); } catch (_) {}
                  state.h264BaseTimestamp = null;
                  state.h264BaseNow = 0;
                }
                state.h264LastTimestamp = frame.timestamp;
                while (state.h264Frames.length >= 8) {
                  try { state.h264Frames.shift().close(); } catch (_) {}
                  state.captureDiagnostics.dropped++;
                }
                state.h264Frames.push(frame);
                if (!state.h264PresentationRaf) {
                  state.h264PresentationRaf = requestAnimationFrame(pumpPresentation);
                }
              },
              error: () => {
                state.h264DecodeErrors++;
                state.captureDiagnostics.decodeErrors++;
              }
            });
            decoder.configure({
              codec: 'avc1.42E01E', codedWidth: width, codedHeight: height,
              hardwareAcceleration: 'prefer-hardware', optimizeForLatency: true
            });
            state.h264Decoder = decoder;
            return 'ready';
          };
          window.__codexWallpaperSkinPushH264Batch = async (token, frames) => {
            if (token !== state.captureToken) return 'stale';
            const decoder = state.h264Decoder;
            if (!decoder || decoder.state !== 'configured' || !Array.isArray(frames)
                || frames.length < 1 || frames.length > 16) return 'rejected';
            const presentedBefore = state.h264Presented;
            const waitForFirstPresentation = presentedBefore === 0;
            for (const frame of frames) {
              if (!frame || typeof frame.data !== 'string' || frame.data.length < 4
                  || frame.data.length > 2 * 1024 * 1024
                  || !Number.isSafeInteger(frame.timestamp) || frame.timestamp < 0) return 'invalid-frame';
              const binary = atob(frame.data);
              if (binary.length < 4 || binary.length > 1536 * 1024) return 'invalid-frame';
              const bytes = new Uint8Array(binary.length);
              for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
              decoder.decode(new EncodedVideoChunk({
                type: frame.keyFrame ? 'key' : 'delta',
                timestamp: frame.timestamp,
                data: bytes
              }));
              state.captureDiagnostics.received++;
            }
            // VideoDecoder.flush() makes the next chunk require another key
            // frame, so it must not be used as per-batch backpressure. Wait on
            // the output callback and decodeQueueSize instead, preserving one
            // continuous GOP across batches.
            const deadline = performance.now() + 2500;
            while (waitForFirstPresentation && state.h264Presented === 0 && performance.now() < deadline) {
              await new Promise(resolve => setTimeout(resolve, 4));
            }
            while (decoder.decodeQueueSize > 8 && performance.now() < deadline) {
              await new Promise(resolve => setTimeout(resolve, 4));
            }
            if (waitForFirstPresentation && state.h264Presented === 0) {
              state.h264DecodeErrors++;
              state.captureDiagnostics.decodeErrors++;
              return 'decode-error';
            }
            return state.h264Presented > presentedBefore ? 'presented' : 'accepted';
          };
          const closeCaptureSocket = reason => {
            const socket = state.captureSocket;
            state.captureSocket = null;
            state.capturePendingPacket = null;
            if (socket) {
              try { socket.close(1000, reason || 'replaced'); } catch (_) {}
            }
          };
          const sendCaptureControl = (socket, value) => {
            if (socket === state.captureSocket && socket.readyState === WebSocket.OPEN) {
              try { socket.send(value); } catch (_) {}
            }
          };
          const processLoopbackPacket = async (socket, packet) => {
            if (socket !== state.captureSocket || state.disposed || !(packet instanceof ArrayBuffer)) return;
            if (packet.byteLength < 25 || packet.byteLength > 8 * 1024 * 1024 + 24) {
              state.captureDiagnostics.decodeErrors++;
              return;
            }
            const bytes = new Uint8Array(packet);
            if (bytes[0] !== 67 || bytes[1] !== 87 || bytes[2] !== 83 || bytes[3] !== 52) {
              state.captureDiagnostics.decodeErrors++;
              return;
            }
            const view = new DataView(packet);
            const kind = bytes[4];
            const sequence = Number(view.getBigInt64(8, true));
            state.captureDiagnostics.received++;
            state.captureDiagnostics.lastSequence = sequence;
            if (kind !== 1) {
              state.captureDiagnostics.decodeErrors++;
              sendCaptureControl(socket, `decode-error:${sequence}:unsupported-packet`);
              return;
            }
            const media = state.media;
            if (!(media instanceof HTMLCanvasElement) || !media.isConnected || media.parentNode !== state.host) {
              sendCaptureControl(socket, `decode-error:${sequence}:canvas-unavailable`);
              return;
            }
            state.captureFrameBusy = true;
            try {
              const blob = new Blob([packet.slice(24)], { type: 'image/jpeg' });
              const bitmap = await createImageBitmap(blob);
              try {
                if (socket !== state.captureSocket || state.disposed || state.media !== media) {
                  state.captureDiagnostics.dropped++;
                  return;
                }
                const context = media.getContext('2d', { alpha: false, desynchronized: true });
                if (!context) throw new Error('canvas-unavailable');
                if (media.width !== bitmap.width || media.height !== bitmap.height) {
                  media.width = bitmap.width; media.height = bitmap.height;
                }
                context.drawImage(bitmap, 0, 0, bitmap.width, bitmap.height);
                state.captureDiagnostics.presented++;
                state.captureDiagnostics.lastPresentation = new Date().toISOString();
                sendCaptureControl(socket, `presented:${sequence}`);
              } finally {
                try { bitmap.close(); } catch (_) {}
              }
            } catch (error) {
              state.captureDiagnostics.decodeErrors++;
              const reason = String(error?.message || error || 'decode-error').replace(/[:\r\n]/g, '-').slice(0, 80);
              sendCaptureControl(socket, `decode-error:${sequence}:${reason}`);
            } finally {
              state.captureFrameBusy = false;
              const pending = state.capturePendingPacket;
              state.capturePendingPacket = null;
              if (pending && socket === state.captureSocket) {
                queueMicrotask(() => processLoopbackPacket(socket, pending));
              }
            }
          };
          window.__codexWallpaperSkinBeginLoopbackStream = async (token, endpoint) => {
            if (!window.__codexWallpaperSkinBeginCapturedStream(token)) return 'rejected';
            if (typeof endpoint !== 'string'
                || !/^ws:\/\/127\.0\.0\.1:\d{1,5}\/cws\/[a-f0-9]{64}$/.test(endpoint)) {
              return 'invalid-endpoint';
            }
            closeCaptureSocket('replaced');
            state.captureDiagnostics = { received: 0, presented: 0, dropped: 0, decodeErrors: 0, lastSequence: 0, lastPresentation: null };
            const socket = new WebSocket(endpoint);
            socket.binaryType = 'arraybuffer';
            state.captureSocket = socket;
            socket.onmessage = event => {
              if (socket !== state.captureSocket || !(event.data instanceof ArrayBuffer)) return;
              if (state.captureFrameBusy) {
                if (state.capturePendingPacket) state.captureDiagnostics.dropped++;
                state.capturePendingPacket = event.data;
                return;
              }
              processLoopbackPacket(socket, event.data);
            };
            socket.onclose = () => {
              if (state.captureSocket === socket) state.captureSocket = null;
            };
            return await new Promise(resolve => {
              let settled = false;
              const finish = value => {
                if (settled) return;
                settled = true;
                clearTimeout(watchdog);
                resolve(value);
              };
              const watchdog = setTimeout(() => {
                try { socket.close(); } catch (_) {}
                if (state.captureSocket === socket) state.captureSocket = null;
                finish('connect-timeout');
              }, 5000);
              socket.onopen = () => finish('ready');
              socket.onerror = () => {
                try { socket.close(); } catch (_) {}
                if (state.captureSocket === socket) state.captureSocket = null;
                finish('connect-error');
              };
            });
          };
          window.__codexWallpaperSkinSetCapturedFrame = (token, encoded) => {
            if (token !== state.captureToken) return 'stale';
            if (typeof encoded !== 'string' || encoded.length === 0 || encoded.length > 3 * 1024 * 1024) {
              throw new Error('Captured frame exceeded its encoded size limit.');
            }
            if (state.disposed || window.__codexWallpaperSkin !== state) {
              throw new Error('The wallpaper runtime is no longer active.');
            }
            const media = state.media;
            if (!(media instanceof HTMLCanvasElement) || !media.isConnected || media.parentNode !== state.host) {
              throw new Error('The native capture target is unavailable.');
            }
            if (state.captureFrameBusy) return 'busy';
            state.captureFrameBusy = true;
            const nextSource = `data:image/jpeg;base64,${encoded}`;
            const staging = document.createElement('img');
            staging.decoding = 'async';
            state.captureStaging = staging;
            return new Promise(resolve => {
              let settled = false;
              const finish = (status, draw) => {
                if (settled) return;
                settled = true;
                clearTimeout(watchdog);
                if (draw && !state.disposed && token === state.captureToken && state.media === media) {
                  // Keep one persistent compositor surface. A frame is
                  // acknowledged only after decode and the actual canvas draw,
                  // so the controller cannot report a switch that the user has
                  // not seen.
                  const width = Math.max(1, staging.naturalWidth);
                  const height = Math.max(1, staging.naturalHeight);
                  const context = media.getContext('2d', { alpha: false });
                  if (context) {
                    if (media.width !== width || media.height !== height) {
                      media.width = width; media.height = height;
                    }
                    context.drawImage(staging, 0, 0, width, height);
                  } else {
                    status = 'canvas-unavailable';
                  }
                } else if (draw) {
                  status = 'stale';
                }
                if (state.captureStaging === staging) state.captureStaging = null;
                if (token === state.captureToken) state.captureFrameBusy = false;
                resolve(status);
              };
              const watchdog = setTimeout(() => {
                try { staging.src = ''; } catch (_) {}
                finish('decode-timeout', false);
              }, 3000);
              staging.onload = () => finish('presented', true);
              staging.onerror = () => finish('decode-error', false);
              staging.src = nextSource;
            });
          };
          window.__codexWallpaperSkinGetCapturedPointer = token => {
            if (token !== state.captureToken || state.disposed || window.__codexWallpaperSkin !== state) return false;
            ensureCapturePointerHandlers();
            const wheelDelta = state.capturePointer.wheel;
            state.capturePointer.wheel = 0;
            return {
              x: state.capturePointer.x,
              y: state.capturePointer.y,
              buttons: state.capturePointer.buttons,
              wheel: wheelDelta,
              hidden: !!document.hidden,
              inside: !!state.capturePointer.inside
            };
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
                && window.__codexWallpaperSkinBeginLoopbackStream === state.helpers.beginLoopbackStream
                && window.__codexWallpaperSkinSetCapturedFrame === state.helpers.setCapturedFrame
                && window.__codexWallpaperSkinGetCapturedPointer === state.helpers.getCapturedPointer
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
              closeCaptureSocket('media-replaced');
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
                    && window.__codexWallpaperSkinBeginH264Stream === state.helpers.beginH264Stream
                    && window.__codexWallpaperSkinPushH264Batch === state.helpers.pushH264Batch
                    && window.__codexWallpaperSkinBeginLoopbackStream === state.helpers.beginLoopbackStream
                    && window.__codexWallpaperSkinSetCapturedFrame === state.helpers.setCapturedFrame
                    && window.__codexWallpaperSkinGetCapturedPointer === state.helpers.getCapturedPointer
                    && window.__codexWallpaperSkinCleanup === state.helpers.cleanup;
                  if (!runtimeIntact) {
                    if (window.__codexWallpaperSkin === state && typeof window.__codexWallpaperSkinCleanup === 'function') {
                      try { window.__codexWallpaperSkinCleanup(); } catch (_) {}
                    } else {
                      state.disposed = true;
                      try { closeCaptureSocket('runtime-invalid'); } catch (_) {}
                      try { closeH264Decoder(); } catch (_) {}
                      try { state.observer.disconnect(); } catch (_) {}
                      try { state.visibilityHandler && document.removeEventListener('visibilitychange', state.visibilityHandler); } catch (_) {}
                      try { state.capturePointerHandlers && window.removeEventListener('pointermove', state.capturePointerHandlers.move, true); } catch (_) {}
                      try { state.capturePointerHandlers && window.removeEventListener('pointerenter', state.capturePointerHandlers.enter, true); } catch (_) {}
                      try { state.capturePointerHandlers && window.removeEventListener('pointerdown', state.capturePointerHandlers.down, true); } catch (_) {}
                      try { state.capturePointerHandlers && window.removeEventListener('pointerup', state.capturePointerHandlers.up, true); } catch (_) {}
                      try { state.capturePointerHandlers && window.removeEventListener('pointercancel', state.capturePointerHandlers.cancel, true); } catch (_) {}
                      try { state.capturePointerHandlers && window.removeEventListener('pointerleave', state.capturePointerHandlers.leave, true); } catch (_) {}
                      try { state.capturePointerHandlers && window.removeEventListener('blur', state.capturePointerHandlers.blur, true); } catch (_) {}
                      try { state.capturePointerHandlers && window.removeEventListener('wheel', state.capturePointerHandlers.wheel, true); } catch (_) {}
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
            try { closeCaptureSocket('cleanup'); } catch (_) {}
            try { closeH264Decoder(); } catch (_) {}
            try { current.observer && current.observer.disconnect(); } catch (_) {}
            try { current.rafId && cancelAnimationFrame(current.rafId); } catch (_) {}
            try { current.visibilityHandler && document.removeEventListener('visibilitychange', current.visibilityHandler); } catch (_) {}
            try { current.capturePointerHandlers && window.removeEventListener('pointermove', current.capturePointerHandlers.move, true); } catch (_) {}
            try { current.capturePointerHandlers && window.removeEventListener('pointerenter', current.capturePointerHandlers.enter, true); } catch (_) {}
            try { current.capturePointerHandlers && window.removeEventListener('pointerdown', current.capturePointerHandlers.down, true); } catch (_) {}
            try { current.capturePointerHandlers && window.removeEventListener('pointerup', current.capturePointerHandlers.up, true); } catch (_) {}
            try { current.capturePointerHandlers && window.removeEventListener('pointercancel', current.capturePointerHandlers.cancel, true); } catch (_) {}
            try { current.capturePointerHandlers && window.removeEventListener('pointerleave', current.capturePointerHandlers.leave, true); } catch (_) {}
            try { current.capturePointerHandlers && window.removeEventListener('blur', current.capturePointerHandlers.blur, true); } catch (_) {}
            try { current.capturePointerHandlers && window.removeEventListener('wheel', current.capturePointerHandlers.wheel, true); } catch (_) {}
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
              delete window.__codexWallpaperSkinBeginH264Stream;
              delete window.__codexWallpaperSkinPushH264Batch;
              delete window.__codexWallpaperSkinBeginLoopbackStream;
              delete window.__codexWallpaperSkinSetCapturedFrame;
              delete window.__codexWallpaperSkinGetCapturedPointer;
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
            beginH264Stream: window.__codexWallpaperSkinBeginH264Stream,
            pushH264Batch: window.__codexWallpaperSkinPushH264Batch,
            beginLoopbackStream: window.__codexWallpaperSkinBeginLoopbackStream,
            setCapturedFrame: window.__codexWallpaperSkinSetCapturedFrame,
            getCapturedPointer: window.__codexWallpaperSkinGetCapturedPointer,
            cleanup: window.__codexWallpaperSkinCleanup
          };
          return 'ready';
        })()
        """;

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { await StopCaptureAsync(timeout.Token); } catch { }
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
        _endpoint = null;
    }
}
