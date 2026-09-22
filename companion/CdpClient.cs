using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodexWallpaperSkin;

public static class CdpEndpoint
{
    public static string CreateUnusedLoopbackUrl()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return $"http://127.0.0.1:{port}";
        }
        finally
        {
            listener.Stop();
        }
    }

    public static bool IsAvailableForActivation(string value)
    {
        var uri = Normalize(value);
        var listener = new TcpListener(IPAddress.Loopback, uri.Port);
        try
        {
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener.Stop();
        }
    }

    public static bool IsLoopbackHttp(string? value)
    {
        const string prefix = "http://127.0.0.1:";
        if (string.IsNullOrEmpty(value) || !value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }
        var withoutSlash = value.EndsWith("/", StringComparison.Ordinal) ? value[..^1] : value;
        var portText = withoutSlash[prefix.Length..];
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is <= 0 or > 65535
            || portText != port.ToString(CultureInfo.InvariantCulture))
        {
            return false;
        }
        var canonical = prefix + portText;
        if (value != canonical && value != canonical + "/")
        {
            return false;
        }
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttp
            && uri.Host.Equals("127.0.0.1", StringComparison.Ordinal)
            && uri.Port == port
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && uri.AbsolutePath == "/";
    }

    public static Uri Normalize(string value)
    {
        if (!IsLoopbackHttp(value))
        {
            throw new ArgumentException("CDP endpoint must be an HTTP loopback URL, for example http://127.0.0.1:9222.", nameof(value));
        }
        var uri = new Uri(value, UriKind.Absolute);
        return new Uri($"http://127.0.0.1:{uri.Port}/", UriKind.Absolute);
    }
}

public static class CdpDiscovery
{
    public static async Task<IReadOnlyList<CdpTarget>> GetTargetsAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        var root = CdpEndpoint.Normalize(baseUrl);
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false
        };
        // A freshly started Codex can expose the port before Chromium's local
        // discovery handlers are responsive. Three seconds proved too short on
        // busy Windows sign-ins and turned a healthy startup into a false
        // timeout. Keep this bounded, but give the local endpoint enough time
        // to answer while the outer connection loop remains in control.
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using (var versionResponse = await GetLocalAsync(http, new Uri(root, "json/version"), cancellationToken))
        {
            versionResponse.EnsureSuccessStatusCode();
            await using var versionStream = await versionResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var versionDocument = await JsonDocument.ParseAsync(versionStream, cancellationToken: cancellationToken);
            var browser = ReadString(versionDocument.RootElement, "Browser") ?? string.Empty;
            if (!browser.Contains("Chrome", StringComparison.OrdinalIgnoreCase)
                && !browser.Contains("Chromium", StringComparison.OrdinalIgnoreCase)
                && !browser.Contains("Electron", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The loopback endpoint is not a Chromium CDP browser.");
            }
        }
        using var response = await GetLocalAsync(http, new Uri(root, "json/list"), cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("CDP /json/list did not return an array.");
        }

        var targets = new List<CdpTarget>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var webSocketUrl = ReadString(element, "webSocketDebuggerUrl");
            if (string.IsNullOrWhiteSpace(webSocketUrl))
            {
                continue;
            }
            if (!Uri.TryCreate(webSocketUrl, UriKind.Absolute, out var webSocketUri)
                || webSocketUri.Scheme != "ws"
                || !webSocketUri.Host.Equals(root.Host, StringComparison.Ordinal)
                || webSocketUri.Port != root.Port
                || !string.IsNullOrEmpty(webSocketUri.UserInfo)
                || !string.IsNullOrEmpty(webSocketUri.Query)
                || !string.IsNullOrEmpty(webSocketUri.Fragment)
                || !webSocketUri.AbsolutePath.StartsWith("/devtools/page/", StringComparison.Ordinal)
                || webSocketUri.AbsolutePath.Length <= "/devtools/page/".Length)
            {
                continue;
            }

            targets.Add(new CdpTarget(
                ReadString(element, "id") ?? string.Empty,
                ReadString(element, "type") ?? string.Empty,
                ReadString(element, "title") ?? string.Empty,
                ReadString(element, "url") ?? string.Empty,
                webSocketUrl));
        }
        return targets;
    }

    private static async Task<HttpResponseMessage> GetLocalAsync(
        HttpClient http,
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            return await http.GetAsync(uri, HttpCompletionOption.ResponseContentRead, requestTimeout.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient timeouts are TaskCanceledException instances. They are
            // transient startup failures, not a user cancellation; translate
            // them so the outer bounded connection loop can retry.
            throw new TimeoutException("The local Codex discovery endpoint did not answer within 8 seconds.", exception);
        }
    }

    public static CdpTarget SelectCodexPage(IEnumerable<CdpTarget> targets)
    {
        var pages = GetConnectableCodexPages(targets);
        if (pages.Count == 0)
        {
            throw new InvalidOperationException("CDP is reachable, but it exposes no Codex app:// page. Refusing to inject another browser or Electron app.");
        }
        return pages[0];
    }

    internal static IReadOnlyList<CdpTarget> GetCodexPages(IEnumerable<CdpTarget> targets)
    {
        return targets
            .Where(target => string.Equals(target.Type, "page", StringComparison.OrdinalIgnoreCase)
                && target.Url.StartsWith("app://", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(IsPrimaryCodexPage)
            .ThenByDescending(target => ContainsCodex(target.Title) || ContainsCodex(target.Url))
            .ThenBy(target => target.Id, StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyList<CdpTarget> GetConnectableCodexPages(IEnumerable<CdpTarget> targets)
    {
        var pages = GetCodexPages(targets);
        var primaryPages = pages.Where(IsPrimaryCodexPage).ToArray();
        // Current Codex builds expose extra app:// pages for avatar overlays and
        // detached windows. They share theme variables with the real shell, so
        // a visual-surface probe alone can accept them and inject an invisible
        // wallpaper. When the normal shell is present, never fall through to
        // those auxiliary targets. Retain the fallback for older Codex builds.
        return primaryPages.Length > 0 ? primaryPages : pages;
    }

    internal static bool IsPrimaryCodexPage(CdpTarget target)
    {
        if (!target.Type.Equals("page", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(target.Url, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals("app", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = uri.AbsolutePath.Replace('\\', '/');
        if (!path.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var route = Uri.UnescapeDataString(uri.Query + uri.Fragment);
        return !route.Contains("avatar-overlay", StringComparison.OrdinalIgnoreCase)
            && !route.Contains("detached-window", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsCodex(string value) =>
        value.Contains("codex", StringComparison.OrdinalIgnoreCase)
        || value.Contains("openai", StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public sealed class CdpClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private long _requestId;

    public CdpTarget? Target { get; private set; }
    public bool IsConnected => _socket.State == WebSocketState.Open;
    internal bool IsProxyDisabled => _socket.Options.Proxy is null;

    public CdpClient()
    {
        // CDP is strictly local; never let system proxy settings reroute its WebSocket.
        _socket.Options.Proxy = null;
    }

    public async Task ConnectAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        await ConnectCoreAsync(baseUrl, targetId: null, cancellationToken);
    }

    internal async Task ConnectAsync(string baseUrl, string targetId, CancellationToken cancellationToken = default)
    {
        await ConnectCoreAsync(baseUrl, targetId, cancellationToken);
    }

    private async Task ConnectCoreAsync(string baseUrl, string? targetId, CancellationToken cancellationToken)
    {
        if (IsConnected)
        {
            return;
        }

        var endpoint = CdpEndpoint.Normalize(baseUrl);
        CdpProcessIdentity.EnsureOfficialCodexOwnsPort(endpoint.Port);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var targets = await CdpDiscovery.GetTargetsAsync(baseUrl, timeout.Token);
        Target = targetId is null
            ? CdpDiscovery.SelectCodexPage(targets)
            : targets.SingleOrDefault(target => target.Id.Equals(targetId, StringComparison.Ordinal)
                && target.Type.Equals("page", StringComparison.OrdinalIgnoreCase)
                && target.Url.StartsWith("app://", StringComparison.OrdinalIgnoreCase))
              ?? throw new InvalidOperationException("The selected Codex app:// page is no longer available.");
        await _socket.ConnectAsync(new Uri(Target.WebSocketDebuggerUrl), timeout.Token);
        // Close the local check/connect race: once the socket is established, the
        // listening port must still belong to the official Codex package process.
        CdpProcessIdentity.EnsureOfficialCodexOwnsPort(endpoint.Port);
    }

    public async Task<JsonElement> EvaluateAsync(string expression, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("CDP is not connected.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await _requestLock.WaitAsync(timeout.Token);
        try
        {
            var id = Interlocked.Increment(ref _requestId);
            var request = JsonSerializer.Serialize(new
            {
                id,
                method = "Runtime.evaluate",
                @params = new
                {
                    expression,
                    awaitPromise = true,
                    returnByValue = true,
                    userGesture = false
                }
            });
            var requestBytes = Encoding.UTF8.GetBytes(request);
            await _socket.SendAsync(requestBytes, WebSocketMessageType.Text, true, timeout.Token);

            while (true)
            {
                var responseText = await ReceiveMessageAsync(timeout.Token);
                using var document = JsonDocument.Parse(responseText);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt64() != id)
                {
                    continue;
                }
                if (root.TryGetProperty("error", out var protocolError))
                {
                    throw new InvalidOperationException("CDP protocol error: " + protocolError.GetRawText());
                }

                var result = root.GetProperty("result");
                if (result.TryGetProperty("exceptionDetails", out var exceptionDetails))
                {
                    throw new InvalidOperationException("Injected JavaScript failed: " + exceptionDetails.GetRawText());
                }
                return result.Clone();
            }
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task<string> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new IOException("The CDP WebSocket closed unexpectedly.");
            }
            output.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new InvalidDataException("CDP returned a non-text message.");
                }
                return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Codex Wallpaper Skin disconnect",
                    timeout.Token);
            }
        }
        catch
        {
            _socket.Abort();
        }
        _socket.Dispose();
        _requestLock.Dispose();
    }
}
