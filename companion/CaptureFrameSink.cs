namespace CodexWallpaperSkin;

/// <summary>
/// Where a captured frame is delivered. The native path can present frames
/// either through CDP (one evaluate call per frame, Base64 encoded) or through
/// the loopback MJPEG stream, and it can switch between them at runtime when the
/// browser reports that the direct stream is unusable.
/// </summary>
public interface ICaptureFrameSink
{
    string Name { get; }

    Task PublishAsync(byte[] frame, CancellationToken cancellationToken);
}

/// <summary>The original transport: one CDP call carrying a Base64 JPEG.</summary>
public sealed class CdpFrameSink : ICaptureFrameSink
{
    private readonly Func<byte[], CancellationToken, Task> _publish;

    public CdpFrameSink(Func<byte[], CancellationToken, Task> publish)
    {
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
    }

    public string Name => "cdp";

    public Task PublishAsync(byte[] frame, CancellationToken cancellationToken) =>
        _publish(frame, cancellationToken);
}

/// <summary>
/// The direct transport: the frame is handed to the loopback MJPEG server and
/// the browser fetches it, so no CDP round trip or Base64 expansion is needed.
/// </summary>
public sealed class StreamFrameSink : ICaptureFrameSink
{
    private readonly FrameStreamServer _server;

    public StreamFrameSink(FrameStreamServer server)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
    }

    public string Name => "stream";

    public Task PublishAsync(byte[] frame, CancellationToken cancellationToken)
    {
        _server.Publish(frame);
        return Task.CompletedTask;
    }
}
