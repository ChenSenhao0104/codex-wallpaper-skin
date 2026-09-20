using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CodexWallpaperSkin;

/// <summary>
/// Serves captured frames to the Codex page over a loopback MJPEG stream. The
/// steady state then costs one socket write per frame instead of a CDP round
/// trip plus a Base64 expansion, and the browser decodes the frames itself.
///
/// Security posture: bound to 127.0.0.1 only, every request must carry a
/// per-session random token, only two paths are served, the request line is
/// length-capped, and at most a handful of clients are accepted. Frames are
/// latest-wins, so a stalled reader can never grow a backlog or block capture.
/// </summary>
public sealed class FrameStreamServer : IAsyncDisposable
{
    private const int MaximumClients = 4;
    private const int MaximumRequestBytes = 2048;
    private const int PumpIntervalMilliseconds = 4;

    /// <summary>A 1x1 GIF used only to prove the origin is reachable from the page.</summary>
    private static readonly byte[] ProbeImage = Convert.FromBase64String(
        "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

    private static readonly byte[] Crlf = "\r\n"u8.ToArray();

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _clients = [];
    private readonly object _clientLock = new();
    private byte[]? _pendingFrame;
    private Task? _acceptLoop;
    private int _connectedClients;
    private int _captureFrames;
    private int _streamedFrames;
    private bool _disposed;

    public string Token { get; } = Guid.NewGuid().ToString("N");

    public string Boundary { get; } = "cwsframe" + Guid.NewGuid().ToString("N");

    public int Port { get; private set; }

    public string StreamUrl => $"http://127.0.0.1:{Port}/stream?t={Token}";

    public string ProbeUrl => $"http://127.0.0.1:{Port}/probe?t={Token}";

    /// <summary>Frames handed to the server by the capture loop.</summary>
    public int CaptureFrames => Volatile.Read(ref _captureFrames);

    /// <summary>Frames actually written to at least one reader.</summary>
    public int StreamedFrames => Volatile.Read(ref _streamedFrames);

    public int ConnectedClients => Volatile.Read(ref _connectedClients);

    public bool IsLoopbackOnly => ((IPEndPoint)_listener.LocalEndpoint).Address.Equals(IPAddress.Loopback);

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FrameStreamServer));
        if (_acceptLoop is not null) throw new InvalidOperationException("The frame stream server is already running.");
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_lifetime.Token));
    }

    /// <summary>Latest-wins: an unread frame is replaced, never queued.</summary>
    public void Publish(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Volatile.Write(ref _pendingFrame, frame);
        Interlocked.Increment(ref _captureFrames);
    }

    public Task PublishAsync(byte[] frame, CancellationToken cancellationToken)
    {
        Publish(frame);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            if (Volatile.Read(ref _connectedClients) >= MaximumClients)
            {
                client.Dispose();
                continue;
            }
            var task = Task.Run(() => ServeClientAsync(client, cancellationToken), CancellationToken.None);
            lock (_clientLock)
            {
                _clients.RemoveAll(existing => existing.IsCompleted);
                _clients.Add(task);
            }
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _connectedClients);
        try
        {
            using (client)
            {
                client.NoDelay = true;
                using var stream = client.GetStream();
                var target = await ReadRequestTargetAsync(stream, cancellationToken);
                if (target is null)
                {
                    return;
                }
                if (!IsAuthorized(target))
                {
                    // No detail on failure: an unauthorized probe learns nothing.
                    await WriteStatusAsync(stream, "401 Unauthorized", cancellationToken);
                    return;
                }
                if (target.StartsWith("/probe", StringComparison.Ordinal))
                {
                    await WriteBytesAsync(
                        stream,
                        Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Type: image/gif\r\nContent-Length: "
                            + ProbeImage.Length
                            + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"),
                        cancellationToken);
                    await WriteBytesAsync(stream, ProbeImage, cancellationToken);
                    return;
                }
                if (!target.StartsWith("/stream", StringComparison.Ordinal))
                {
                    await WriteStatusAsync(stream, "404 Not Found", cancellationToken);
                    return;
                }

                await WriteBytesAsync(
                    stream,
                    Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\n"
                        + $"Content-Type: multipart/x-mixed-replace; boundary={Boundary}\r\n"
                        + "Cache-Control: no-store, no-cache, must-revalidate\r\n"
                        + "Pragma: no-cache\r\nConnection: close\r\n\r\n"),
                    cancellationToken);
                await PumpFramesAsync(stream, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            Interlocked.Decrement(ref _connectedClients);
        }
    }

    private async Task PumpFramesAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[]? lastWritten = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = Volatile.Read(ref _pendingFrame);
            if (frame is null || ReferenceEquals(frame, lastWritten))
            {
                await Task.Delay(PumpIntervalMilliseconds, cancellationToken);
                continue;
            }
            lastWritten = frame;
            var header = Encoding.ASCII.GetBytes(
                $"--{Boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n");
            await WriteBytesAsync(stream, header, cancellationToken);
            await WriteBytesAsync(stream, frame, cancellationToken);
            await WriteBytesAsync(stream, Crlf, cancellationToken);
            Interlocked.Increment(ref _streamedFrames);
        }
    }

    private static async Task WriteBytesAsync(NetworkStream stream, byte[] payload, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task WriteStatusAsync(NetworkStream stream, string status, CancellationToken cancellationToken)
    {
        await WriteBytesAsync(
            stream,
            Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"),
            cancellationToken);
    }

    /// <summary>Reads the request line and drains the headers, bounded in size.</summary>
    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaximumRequestBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1), cancellationToken);
            if (read == 0)
            {
                return null;
            }
            length += read;
            if (length >= 4
                && buffer[length - 4] == (byte)'\r' && buffer[length - 3] == (byte)'\n'
                && buffer[length - 2] == (byte)'\r' && buffer[length - 1] == (byte)'\n')
            {
                break;
            }
        }
        var text = Encoding.ASCII.GetString(buffer, 0, length);
        var lineEnd = text.IndexOf('\r');
        var requestLine = lineEnd < 0 ? text : text[..lineEnd];
        return requestLine.Split(' ') is [var method, var target, ..]
            && method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            ? target
            : null;
    }

    private bool IsAuthorized(string target)
    {
        var separator = target.IndexOf("?t=", StringComparison.Ordinal);
        if (separator < 0)
        {
            return false;
        }
        var token = target[(separator + 3)..];
        var ampersand = token.IndexOf('&');
        if (ampersand >= 0)
        {
            token = token[..ampersand];
        }
        return token.Length == Token.Length
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(token),
                Encoding.ASCII.GetBytes(Token));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _lifetime.CancelAsync();
        try
        {
            _listener.Stop();
        }
        catch
        {
            // The listener may already be closed.
        }
        Task[] clients;
        lock (_clientLock)
        {
            clients = [.. _clients];
        }
        if (_acceptLoop is not null)
        {
            clients = [.. clients, _acceptLoop];
        }
        try
        {
            await Task.WhenAll(clients).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // A client that will not settle must not delay capture shutdown.
        }
        _lifetime.Dispose();
    }
}
