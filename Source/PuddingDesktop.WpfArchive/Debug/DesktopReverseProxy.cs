using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using PuddingDesktop.Diagnostics;

namespace PuddingDesktop.Debug;

/// <summary>
/// Loopback reverse proxy for Desktop debug mode: one entry origin on
/// http://127.0.0.1:{ProxyPort} that routes backend-owned prefixes
/// (see <see cref="ProxyRoutePlanner"/>) to the source-built Core and
/// everything else to the pnpm frontend dev server. HTTP bodies and SSE
/// streams are relayed chunk-by-chunk; WebSocket upgrades (frontend HMR)
/// are relayed as a full-duplex message bridge. Ported from the proven
/// dev-up.py Python proxy semantics.
/// </summary>
public sealed class DesktopReverseProxy : IAsyncDisposable
{
    private const int StreamBufferSize = 64 * 1024;
    private static readonly TimeSpan WebSocketConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WebSocketKeepAlive = TimeSpan.FromSeconds(30);

    private readonly Uri _backendBaseAddress;
    private readonly Uri _frontendBaseAddress;
    private readonly string _listenHost;
    private readonly int _port;
    private readonly HttpListener _listener = new();
    private readonly HttpClient _httpClient;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;
    private int _disposeState;

    public DesktopReverseProxy(Uri backendBaseAddress, Uri frontendBaseAddress, int port)
        : this(backendBaseAddress, frontendBaseAddress, "127.0.0.1", port)
    {
    }

    internal DesktopReverseProxy(
        Uri backendBaseAddress,
        Uri frontendBaseAddress,
        string listenHost,
        int port)
    {
        ArgumentNullException.ThrowIfNull(backendBaseAddress);
        ArgumentNullException.ThrowIfNull(frontendBaseAddress);
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        _backendBaseAddress = backendBaseAddress;
        _frontendBaseAddress = frontendBaseAddress;
        _listenHost = listenHost;
        _port = port;
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public Uri BaseAddress => new($"http://{_listenHost}:{_port}");

    public int Port => _port;

    /// <summary>Starts the listener and request loop. Throws on port/ACL failure.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        if (_loopTask is not null)
            return;

        _listener.Prefixes.Add($"http://{_listenHost}:{_port}/");
        _listener.Start();

        _loopTask = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); }
        catch (ObjectDisposedException) { }
        catch (HttpListenerException) { }

        if (_loopTask is not null)
        {
            try { await _loopTask; }
            catch (OperationCanceledException) { }
            catch (HttpListenerException) { }
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                DesktopDiagnosticLog.Write("DebugProxyLoop", ex);
                continue;
            }

            _ = HandleContextAsync(context, cancellationToken);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.IsWebSocketRequest)
                await HandleWebSocketAsync(context, cancellationToken);
            else
                await HandleHttpAsync(context, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException
            && cancellationToken.IsCancellationRequested)
        {
            // Desktop shutdown owns cancellation.
        }
        catch (Exception ex)
        {
            DesktopDiagnosticLog.Write("DebugProxyRequest", ex);
            TryAbort(context);
        }
    }

    private async Task HandleHttpAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var pathAndQuery = request.Url?.PathAndQuery ?? "/";
        var effectivePath = ProxyRoutePlanner.GetEffectivePath(request.HttpMethod, pathAndQuery);
        var upstreamBase = ProxyRoutePlanner.IsBackendPath(effectivePath)
            ? _backendBaseAddress
            : _frontendBaseAddress;
        var upstreamUri = new Uri(upstreamBase.AbsoluteUri.TrimEnd('/') + effectivePath, UriKind.Absolute);

        using var upstreamRequest = new HttpRequestMessage(
            new HttpMethod(request.HttpMethod),
            upstreamUri);
        CopyRequestHeaders(request, upstreamRequest);

        if (request.HasEntityBody)
        {
            var content = new StreamContent(request.InputStream, StreamBufferSize);
            if (request.ContentLength64 >= 0)
                content.Headers.ContentLength = request.ContentLength64;
            var contentType = request.Headers["Content-Type"];
            if (!string.IsNullOrEmpty(contentType))
                content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            upstreamRequest.Content = content;
        }

        HttpResponseMessage upstreamResponse;
        try
        {
            upstreamResponse = await _httpClient.SendAsync(
                upstreamRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex) when (IsUpstreamFailure(ex))
        {
            WriteBadGateway(context, upstreamUri, ex);
            return;
        }

        using (upstreamResponse)
        {
            try
            {
                await RelayResponseAsync(context, upstreamResponse, cancellationToken);
            }
            catch (Exception ex) when (IsClientDisconnect(ex))
            {
                // The browser went away (e.g. SSE page closed); dropping the
                // upstream response aborts the relay.
            }
        }
    }

    private static void CopyRequestHeaders(HttpListenerRequest request, HttpRequestMessage upstreamRequest)
    {
        foreach (var key in request.Headers.AllKeys)
        {
            if (key is null)
                continue;

            if (IsSkippedRequestHeader(key))
                continue;

            var values = request.Headers.GetValues(key);
            if (values is { Length: > 0 })
                upstreamRequest.Headers.TryAddWithoutValidation(key, values);
        }

        upstreamRequest.Headers.Remove("X-Forwarded-Host");
        upstreamRequest.Headers.TryAddWithoutValidation(
            "X-Forwarded-Host", string.IsNullOrEmpty(request.UserHostName) ? "" : request.UserHostName!);
        upstreamRequest.Headers.Remove("X-Forwarded-Proto");
        upstreamRequest.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "http");
    }

    private static bool IsSkippedRequestHeader(string key) =>
        key.Equals("Host", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
        || ProxyRoutePlanner.HopByHopHeaders.Contains(key, StringComparer.OrdinalIgnoreCase);

    private async Task RelayResponseAsync(
        HttpListenerContext context,
        HttpResponseMessage upstreamResponse,
        CancellationToken cancellationToken)
    {
        var response = context.Response;
        response.StatusCode = (int)upstreamResponse.StatusCode;
        if (!string.IsNullOrEmpty(upstreamResponse.ReasonPhrase))
            response.StatusDescription = upstreamResponse.ReasonPhrase;

        foreach (var header in upstreamResponse.Headers)
            AddResponseHeader(response, header.Key, string.Join(", ", header.Value));

        var contentLength = upstreamResponse.Content.Headers.ContentLength;
        foreach (var header in upstreamResponse.Content.Headers)
        {
            if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;
            AddResponseHeader(response, header.Key, string.Join(", ", header.Value));
        }

        if (context.Request.HttpMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            if (contentLength is >= 0)
                response.ContentLength64 = contentLength.Value;
            return;
        }

        if (contentLength is >= 0)
        {
            response.ContentLength64 = contentLength.Value;
        }
        else
        {
            // Unknown length (SSE, chunked upstreams): http.sys streams it out
            // chunked and per-write flushes keep server-sent events flowing.
            response.SendChunked = true;
        }

        var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[StreamBufferSize];
        int read;
        while ((read = await upstreamStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await response.OutputStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            await response.OutputStream.FlushAsync(cancellationToken);
        }
    }

    private static void AddResponseHeader(HttpListenerResponse response, string name, string value)
    {
        // Some headers (Date, Content-Length, Keep-Alive, ...) are managed by
        // http.sys itself; setting them through Headers throws.
        try
        {
            response.AddHeader(name, value);
        }
        catch (Exception)
        {
            // Managed or duplicate header — http.sys owns it.
        }
    }

    private static void WriteBadGateway(HttpListenerContext context, Uri upstreamUri, Exception cause)
    {
        try
        {
            var response = context.Response;
            var message = Encoding.UTF8.GetBytes(
                $"Proxy error for {upstreamUri}: {cause.Message}");
            response.StatusCode = 502;
            response.StatusDescription = "Bad Gateway";
            response.ContentType = "text/plain; charset=utf-8";
            response.ContentLength64 = message.Length;
            response.OutputStream.Write(message, 0, message.Length);
            response.OutputStream.Close();
        }
        catch (Exception)
        {
            // The client may already be gone.
        }
    }

    private async Task HandleWebSocketAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var pathAndQuery = context.Request.Url?.PathAndQuery ?? "/";
        var upstreamBase = ProxyRoutePlanner.IsBackendPath(pathAndQuery)
            ? _backendBaseAddress
            : _frontendBaseAddress;
        var upstreamUri = new Uri($"ws://{upstreamBase.Host}:{upstreamBase.Port}{pathAndQuery}");

        // .NET accepts a single subprotocol; take the first client offer so
        // single-protocol clients still see their protocol echoed.
        var offered = context.Request.Headers.GetValues("Sec-WebSocket-Protocol");
        var subProtocol = offered is { Length: > 0 }
            ? offered[0].Split(',')[0].Trim()
            : null;
        if (subProtocol?.Length == 0)
            subProtocol = null;

        var webSocketContext = await context.AcceptWebSocketAsync(
            subProtocol, StreamBufferSize, WebSocketKeepAlive);
        var serverWebSocket = webSocketContext.WebSocket;
        try
        {
            using var upstream = new ClientWebSocket();
            if (subProtocol is not null)
                upstream.Options.AddSubProtocol(subProtocol);
            upstream.Options.KeepAliveInterval = WebSocketKeepAlive;

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(WebSocketConnectTimeout);
            await upstream.ConnectAsync(upstreamUri, connectCts.Token);

            var downstreamPump = PumpAsync(serverWebSocket, upstream, cancellationToken);
            var upstreamPump = PumpAsync(upstream, serverWebSocket, cancellationToken);
            await Task.WhenAny(downstreamPump, upstreamPump);

            // The first finished pump means the bridge is over; abort both
            // sockets so the remaining pump cannot block on ReceiveAsync
            // forever waiting for traffic that will never arrive.
            serverWebSocket.Abort();
            upstream.Abort();
            await Task.WhenAll(downstreamPump, upstreamPump);
        }
        finally
        {
            serverWebSocket.Dispose();
        }
    }

    private static async Task PumpAsync(
        WebSocket source,
        WebSocket destination,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[StreamBufferSize];
        try
        {
            while (true)
            {
                int total = 0;
                WebSocketReceiveResult result;
                do
                {
                    result = await source.ReceiveAsync(
                        new ArraySegment<byte>(buffer), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await destination.CloseAsync(
                            result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                            result.CloseStatusDescription,
                            CancellationToken.None);
                        return;
                    }

                    total += result.Count;
                    if (result.Count > 0 && !result.EndOfMessage)
                        Array.Resize(ref buffer, Math.Max(buffer.Length * 2, total));
                } while (!result.EndOfMessage);

                await destination.SendAsync(
                    new ArraySegment<byte>(buffer, 0, total),
                    result.MessageType,
                    endOfMessage: true,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Desktop shutdown owns cancellation.
        }
        catch (Exception)
        {
            // Socket closed/aborted from either side: disposing both sockets in
            // the caller unwinds the opposite pump as well.
        }
    }

    private static bool IsUpstreamFailure(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or InvalidOperationException;

    private static bool IsClientDisconnect(Exception ex) =>
        ex is IOException or HttpListenerException or ObjectDisposedException;

    private static void TryAbort(HttpListenerContext context)
    {
        try { context.Response.Abort(); }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        await StopAsync();
        _httpClient.Dispose();
        _cts.Dispose();
    }
}
