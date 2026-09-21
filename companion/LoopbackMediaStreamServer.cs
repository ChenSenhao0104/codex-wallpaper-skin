using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace CodexWallpaperSkin;

internal enum LoopbackMediaPacketKind : byte
{
    Jpeg = 1,
    H264AnnexB = 2,
    H264Configuration = 3
}

internal sealed record LoopbackMediaStreamMetrics(
    long Queued,
    long Sent,
    long Presented,
    long Dropped,
    long DecodeErrors,
    long Connections,
    long LastPresentedSequence);

/// <summary>
/// A capability-scoped, loopback-only binary media channel. CDP installs the
/// receiver once; steady-state frame bytes never travel through Runtime.evaluate
/// or Base64. The one-slot queue keeps latency bounded by dropping obsolete work.
/// </summary>
internal sealed class LoopbackMediaStreamServer : IAsyncDisposable
{
    private const int MaximumHandshakeBytes = 16 * 1024;
    private const int MaximumControlBytes = 4 * 1024;
    private const int MaximumPacketBytes = 8 * 1024 * 1024;
    private static readonly byte[] PacketMagic = "CWS4"u8.ToArray();
    private static readonly byte[] WebSocketMagic = Encoding.ASCII.GetBytes("258EAFA5-E914-47DA-95CA-C5AB0DC85B11");

    private readonly TcpListener _listener;
    private readonly string _capability;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<MediaPacket> _packets = Channel.CreateBounded<MediaPacket>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly ConcurrentDictionary<long, TaskCompletionSource<string>> _presentationWaiters = new();
    private readonly TaskCompletionSource _firstConnection = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _acceptTask;
    private long _sequence;
    private long _queued;
    private long _sent;
    private long _presented;
    private long _dropped;
    private long _decodeErrors;
    private long _connections;
    private long _lastPresentedSequence;
    private int _disposed;

    private sealed record MediaPacket(
        long Sequence,
        long TimestampMicroseconds,
        LoopbackMediaPacketKind Kind,
        byte[] Payload,
        TaskCompletionSource<string>? Presentation);

    private LoopbackMediaStreamServer(TcpListener listener, string capability)
    {
        _listener = listener;
        _capability = capability;
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Endpoint = new Uri($"ws://127.0.0.1:{port}/cws/{capability}");
        _acceptTask = Task.Run(() => AcceptLoopAsync(_lifetime.Token));
    }

    public Uri Endpoint { get; }

    public static LoopbackMediaStreamServer Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        return new LoopbackMediaStreamServer(listener, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
    }

    public LoopbackMediaStreamMetrics GetMetrics() => new(
        Interlocked.Read(ref _queued),
        Interlocked.Read(ref _sent),
        Interlocked.Read(ref _presented),
        Interlocked.Read(ref _dropped),
        Interlocked.Read(ref _decodeErrors),
        Interlocked.Read(ref _connections),
        Interlocked.Read(ref _lastPresentedSequence));

    public async Task WaitForConnectionAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await _firstConnection.Task.WaitAsync(timeout.Token);
    }

    public async Task<long> PublishAsync(
        LoopbackMediaPacketKind kind,
        byte[] payload,
        bool waitForPresentation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length is <= 0 or > MaximumPacketBytes)
        {
            throw new InvalidDataException("The local media packet exceeded its bounded size.");
        }

        var sequence = Interlocked.Increment(ref _sequence);
        TaskCompletionSource<string>? presentation = waitForPresentation
            ? new(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
        var packet = new MediaPacket(
            sequence,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000,
            kind,
            payload,
            presentation);
        if (presentation is not null)
        {
            _presentationWaiters[sequence] = presentation;
        }

        while (_packets.Reader.TryRead(out var obsolete))
        {
            DropPacket(obsolete, "superseded");
        }
        if (!_packets.Writer.TryWrite(packet))
        {
            DropPacket(packet, "queue-closed");
            throw new IOException("The local media channel is no longer accepting frames.");
        }
        Interlocked.Increment(ref _queued);

        if (presentation is not null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                var status = await presentation.Task.WaitAsync(timeout.Token);
                if (!status.Equals("presented", StringComparison.Ordinal))
                {
                    throw new IOException($"Codex rejected the local media frame ({status}).");
                }
            }
            finally
            {
                _presentationWaiters.TryRemove(sequence, out _);
            }
        }
        return sequence;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                using (client)
                {
                    client.NoDelay = true;
                    try
                    {
                        await HandleClientAsync(client, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                    }
                    catch
                    {
                        // The browser may reload or replace the stream. The
                        // listener remains available for the current token.
                    }
                }
            }
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _firstConnection.TrySetCanceled(cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var request = await ReadHttpHandshakeAsync(stream, cancellationToken);
        var expectedPath = "/cws/" + _capability;
        if (!request.Path.Equals(expectedPath, StringComparison.Ordinal)
            || !request.Headers.TryGetValue("Upgrade", out var upgrade)
            || !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase)
            || !request.Headers.TryGetValue("Connection", out var connection)
            || !connection.Split(',').Any(value => value.Trim().Equals("upgrade", StringComparison.OrdinalIgnoreCase))
            || !request.Headers.TryGetValue("Sec-WebSocket-Version", out var version)
            || !version.Equals("13", StringComparison.Ordinal)
            || !request.Headers.TryGetValue("Sec-WebSocket-Key", out var key)
            || !IsValidWebSocketKey(key))
        {
            await WriteHttpErrorAsync(stream, cancellationToken);
            return;
        }

        var accept = ComputeWebSocketAccept(key);
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n"
            + "Upgrade: websocket\r\n"
            + "Connection: Upgrade\r\n"
            + $"Sec-WebSocket-Accept: {accept}\r\n"
            + "Cache-Control: no-store\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken);
        Interlocked.Increment(ref _connections);
        _firstConnection.TrySetResult();

        using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var send = SendLoopAsync(stream, connectionLifetime.Token);
        var receive = ReceiveLoopAsync(stream, connectionLifetime.Token);
        await Task.WhenAny(send, receive);
        connectionLifetime.Cancel();
        try { await Task.WhenAll(send, receive); } catch (OperationCanceledException) { }
    }

    private async Task SendLoopAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        await foreach (var packet in _packets.Reader.ReadAllAsync(cancellationToken))
        {
            var body = new byte[24 + packet.Payload.Length];
            PacketMagic.CopyTo(body, 0);
            body[4] = (byte)packet.Kind;
            body[5] = 1; // Every compatibility JPEG is independently decodable.
            BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(8, 8), packet.Sequence);
            BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(16, 8), packet.TimestampMicroseconds);
            packet.Payload.CopyTo(body, 24);
            try
            {
                await WriteWebSocketFrameAsync(stream, 0x2, body, cancellationToken);
                Interlocked.Increment(ref _sent);
            }
            catch
            {
                if (!_packets.Writer.TryWrite(packet))
                {
                    DropPacket(packet, "connection-lost");
                }
                throw;
            }
        }
    }

    private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await ReadWebSocketFrameAsync(stream, cancellationToken);
            switch (frame.Opcode)
            {
                case 0x1:
                    HandleControl(Encoding.UTF8.GetString(frame.Payload));
                    break;
                case 0x8:
                    await WriteWebSocketFrameAsync(stream, 0x8, frame.Payload, cancellationToken);
                    return;
                case 0x9:
                    await WriteWebSocketFrameAsync(stream, 0xA, frame.Payload, cancellationToken);
                    break;
            }
        }
    }

    private void HandleControl(string control)
    {
        var parts = control.Split(':', 3, StringSplitOptions.None);
        if (parts.Length < 2 || !long.TryParse(parts[1], out var sequence)) return;
        var status = parts[0] switch
        {
            "presented" => "presented",
            "decode-error" => parts.Length == 3 ? "decode-error-" + Limit(parts[2]) : "decode-error",
            _ => null
        };
        if (status is null) return;
        if (status == "presented")
        {
            Interlocked.Increment(ref _presented);
            Interlocked.Exchange(ref _lastPresentedSequence, sequence);
        }
        else
        {
            Interlocked.Increment(ref _decodeErrors);
        }
        if (_presentationWaiters.TryRemove(sequence, out var waiter))
        {
            waiter.TrySetResult(status);
        }
    }

    private void DropPacket(MediaPacket packet, string reason)
    {
        Interlocked.Increment(ref _dropped);
        if (packet.Presentation is not null)
        {
            _presentationWaiters.TryRemove(packet.Sequence, out _);
            packet.Presentation.TrySetResult(reason);
        }
    }

    private static async Task<HttpHandshake> ReadHttpHandshakeAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[1024];
        while (bytes.Length < MaximumHandshakeBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) throw new EndOfStreamException("The local media handshake ended early.");
            bytes.Write(buffer, 0, read);
            var content = bytes.GetBuffer();
            var length = checked((int)bytes.Length);
            if (length >= 4
                && content[length - 4] == (byte)'\r'
                && content[length - 3] == (byte)'\n'
                && content[length - 2] == (byte)'\r'
                && content[length - 1] == (byte)'\n') break;
        }
        if (bytes.Length >= MaximumHandshakeBytes)
        {
            throw new InvalidDataException("The local media handshake exceeded its limit.");
        }
        var text = Encoding.ASCII.GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length));
        var lines = text.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3 || !requestLine[0].Equals("GET", StringComparison.Ordinal)
            || !requestLine[2].Equals("HTTP/1.1", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The local media handshake was not a WebSocket GET request.");
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) break;
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return new HttpHandshake(requestLine[1], headers);
    }

    private static async Task WriteHttpErrorAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 404 Not Found\r\nConnection: close\r\nCache-Control: no-store\r\nContent-Length: 0\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken);
    }

    private static bool IsValidWebSocketKey(string key)
    {
        try { return Convert.FromBase64String(key).Length == 16; }
        catch { return false; }
    }

    private static string ComputeWebSocketAccept(string key)
    {
        var keyBytes = Encoding.ASCII.GetBytes(key);
        var combined = new byte[keyBytes.Length + WebSocketMagic.Length];
        keyBytes.CopyTo(combined, 0);
        WebSocketMagic.CopyTo(combined, keyBytes.Length);
        return Convert.ToBase64String(SHA1.HashData(combined));
    }

    private static async Task WriteWebSocketFrameAsync(
        NetworkStream stream,
        byte opcode,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length > MaximumPacketBytes) throw new InvalidDataException("WebSocket payload exceeded its limit.");
        var header = new byte[10];
        header[0] = (byte)(0x80 | opcode);
        var headerLength = 2;
        if (payload.Length <= 125)
        {
            header[1] = (byte)payload.Length;
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            header[1] = 126;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), checked((ushort)payload.Length));
            headerLength = 4;
        }
        else
        {
            header[1] = 127;
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(2, 8), checked((ulong)payload.Length));
            headerLength = 10;
        }
        await stream.WriteAsync(header.AsMemory(0, headerLength), cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
    }

    private static async Task<WebSocketFrame> ReadWebSocketFrameAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var first = new byte[2];
        await ReadExactlyAsync(stream, first, cancellationToken);
        var final = (first[0] & 0x80) != 0;
        var opcode = (byte)(first[0] & 0x0F);
        var masked = (first[1] & 0x80) != 0;
        if (!final || !masked) throw new InvalidDataException("The browser sent an invalid local media frame.");
        ulong length = (uint)(first[1] & 0x7F);
        if (length == 126)
        {
            var extended = new byte[2];
            await ReadExactlyAsync(stream, extended, cancellationToken);
            length = BinaryPrimitives.ReadUInt16BigEndian(extended);
        }
        else if (length == 127)
        {
            var extended = new byte[8];
            await ReadExactlyAsync(stream, extended, cancellationToken);
            length = BinaryPrimitives.ReadUInt64BigEndian(extended);
        }
        if (length > MaximumControlBytes) throw new InvalidDataException("The browser control frame exceeded its limit.");
        var mask = new byte[4];
        await ReadExactlyAsync(stream, mask, cancellationToken);
        var payload = new byte[checked((int)length)];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        for (var index = 0; index < payload.Length; index++) payload[index] ^= mask[index & 3];
        return new WebSocketFrame(opcode, payload);
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = await stream.ReadAsync(destination[read..], cancellationToken);
            if (count == 0) throw new EndOfStreamException("The local media connection closed.");
            read += count;
        }
    }

    private static string Limit(string value) => value.Length <= 96 ? value : value[..96];

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _packets.Writer.TryComplete();
        try { _listener.Stop(); } catch { }
        while (_packets.Reader.TryRead(out var packet)) DropPacket(packet, "disposed");
        foreach (var waiter in _presentationWaiters)
        {
            waiter.Value.TrySetCanceled(_lifetime.Token);
        }
        _presentationWaiters.Clear();
        try { await _acceptTask; } catch (OperationCanceledException) { }
        _lifetime.Dispose();
    }

    private sealed record HttpHandshake(string Path, Dictionary<string, string> Headers);
    private sealed record WebSocketFrame(byte Opcode, byte[] Payload);
}
