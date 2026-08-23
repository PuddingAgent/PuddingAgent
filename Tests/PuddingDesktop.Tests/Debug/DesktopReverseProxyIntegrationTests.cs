using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using PuddingDesktop.Debug;

namespace PuddingDesktop.Tests.Debug;

/// <summary>
/// End-to-end tests against real loopback HttpListeners: backend/frontend
/// routing, header forwarding, status passthrough, SPA fallback, SSE
/// streaming, 502 on dead upstreams and WebSocket relay.
/// </summary>
public sealed class DesktopReverseProxyIntegrationTests : IDisposable
{
    private readonly List<FakeUpstream> _upstreams = new();
    private readonly List<DesktopReverseProxy> _proxies = new();
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(30) };

    [Fact]
    public async Task Get_BackendPath_RoutesToBackendWithForwardedHeaders()
    {
        var backend = StartUpstream(async ctx =>
        {
            if (ctx.Request.Url!.AbsolutePath == "/api/echo")
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = await reader.ReadToEndAsync();
                var json = $$"""
                    {"forwardedHost":"{{ctx.Request.Headers["X-Forwarded-Host"]}}",
                    "custom":"{{ctx.Request.Headers["X-Custom"]}}",
                    "body":"{{body}}"}
                    """;
                await WriteTextAsync(ctx.Response, json, "application/json");
            }
            else
            {
                await WriteTextAsync(ctx.Response, "backend-other", "text/plain");
            }
        });
        var frontend = StartUpstream(ctx =>
            WriteTextAsync(ctx.Response, "frontend-index", "text/plain"));
        var proxy = StartProxy(backend.Port, frontend.Port);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(proxy.BaseAddress, "/api/echo"));
        request.Headers.Add("X-Custom", "abc");
        request.Content = new StringContent("hello", Encoding.UTF8, "text/plain");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains($"127.0.0.1:{proxy.Port}", body); // X-Forwarded-Host carried the proxy origin
        Assert.Contains("abc", body);
        Assert.Contains("hello", body);
    }

    [Fact]
    public async Task Get_FrontendPath_RoutesToFrontend()
    {
        var backend = StartUpstream(ctx =>
            WriteTextAsync(ctx.Response, "backend", "text/plain"));
        var frontend = StartUpstream(ctx =>
            WriteTextAsync(
                ctx.Response,
                ctx.Request.Url!.AbsolutePath == "/admin/" ? "frontend-index" : $"frontend-other:{ctx.Request.Url.AbsolutePath}",
                "text/plain"));
        var proxy = StartProxy(backend.Port, frontend.Port);

        using var indexResponse = await _client.GetAsync(new Uri(proxy.BaseAddress, "/admin/"));
        Assert.Equal("frontend-index", await indexResponse.Content.ReadAsStringAsync());

        using var assetResponse = await _client.GetAsync(new Uri(proxy.BaseAddress, "/admin/app.css"));
        Assert.Equal("frontend-other:/admin/app.css", await assetResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_AdminDeepLinkWithoutExtension_RewritesToSpaIndex()
    {
        var backend = StartUpstream(ctx =>
            WriteTextAsync(ctx.Response, "backend", "text/plain"));
        var frontend = StartUpstream(ctx =>
            WriteTextAsync(
                ctx.Response,
                ctx.Request.Url!.AbsolutePath == "/admin/" ? "frontend-index" : $"frontend-other:{ctx.Request.Url.AbsolutePath}",
                "text/plain"));
        var proxy = StartProxy(backend.Port, frontend.Port);

        using var response = await _client.GetAsync(new Uri(proxy.BaseAddress, "/admin/user/login"));

        // The rewrite to /admin/ must reach the frontend, not the raw deep link.
        Assert.Equal("frontend-index", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_UpstreamNotFound_ForwardsStatusAndBody()
    {
        var backend = StartUpstream(async ctx =>
        {
            ctx.Response.StatusCode = 404;
            await WriteTextAsync(ctx.Response, "nope", "text/plain");
        });
        var frontend = StartUpstream(ctx =>
            WriteTextAsync(ctx.Response, "frontend", "text/plain"));
        var proxy = StartProxy(backend.Port, frontend.Port);

        using var response = await _client.GetAsync(new Uri(proxy.BaseAddress, "/api/missing"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("nope", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_DeadUpstream_ReturnsBadGateway()
    {
        var backend = StartUpstream(ctx =>
            WriteTextAsync(ctx.Response, "backend", "text/plain"));
        var deadPort = GetFreeLoopbackPort();
        var proxy = StartProxy(backend.Port, deadPort);

        using var response = await _client.GetAsync(new Uri(proxy.BaseAddress, "/"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Proxy error", body);
    }

    [Fact]
    public async Task Get_EventStream_DeliversFirstChunkBeforeUpstreamCloses()
    {
        var backend = StartUpstream(async ctx =>
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.SendChunked = true;
            await ctx.Response.OutputStream.WriteAsync("event: ping\ndata: 1\n\n"u8.ToArray());
            await ctx.Response.OutputStream.FlushAsync();
            await Task.Delay(TimeSpan.FromSeconds(3));
            await ctx.Response.OutputStream.WriteAsync("event: end\ndata: 2\n\n"u8.ToArray());
            await ctx.Response.OutputStream.FlushAsync();
            ctx.Response.Close();
        });
        var frontend = StartUpstream(ctx =>
            WriteTextAsync(ctx.Response, "frontend", "text/plain"));
        var proxy = StartProxy(backend.Port, frontend.Port);

        using var response = await _client.GetAsync(
            new Uri(proxy.BaseAddress, "/api/sessions/s1/events/stream"),
            HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        using var reader = new StreamReader(
            await response.Content.ReadAsStreamAsync(), Encoding.UTF8);
        // The upstream keeps the connection open for 3 more seconds; the first
        // line can only arrive within 1.5s if the proxy streams and flushes.
        var firstLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(1.5));
        Assert.Equal("event: ping", firstLine);
    }

    [Fact]
    public async Task WebSocket_RelayEchoesThroughProxy()
    {
        var backend = StartUpstream(ctx => WriteTextAsync(ctx.Response, "backend", "text/plain"));
        var frontend = StartUpstream(async ctx =>
        {
            if (!ctx.Request.IsWebSocketRequest)
            {
                await WriteTextAsync(ctx.Response, "frontend", "text/plain");
                return;
            }

            var webSocket = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
            var buffer = new byte[4096];
            while (true)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                await webSocket.SendAsync(
                    new ArraySegment<byte>(buffer, 0, result.Count),
                    result.MessageType,
                    endOfMessage: true,
                    CancellationToken.None);
            }
        });
        var proxy = StartProxy(backend.Port, frontend.Port);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{proxy.Port}/hmr"), CancellationToken.None);
        await client.SendAsync("ping"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[256];
        var receiveTask = client.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        var received = await receiveTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WebSocketMessageType.Text, received.MessageType);
        Assert.Equal("ping", Encoding.UTF8.GetString(buffer, 0, received.Count));
    }

    private FakeUpstream StartUpstream(Func<HttpListenerContext, Task> handler)
    {
        var upstream = FakeUpstream.Start(handler);
        _upstreams.Add(upstream);
        return upstream;
    }

    private DesktopReverseProxy StartProxy(int backendPort, int frontendPort)
    {
        var proxy = new DesktopReverseProxy(
            new Uri($"http://127.0.0.1:{backendPort}"),
            new Uri($"http://127.0.0.1:{frontendPort}"),
            GetFreeLoopbackPort());
        proxy.Start();
        _proxies.Add(proxy);
        return proxy;
    }

    private static async Task WriteTextAsync(HttpListenerResponse response, string text, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.OutputStream.Close();
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        _client.Dispose();
        foreach (var proxy in _proxies)
        {
            try { proxy.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch { }
        }

        foreach (var upstream in _upstreams)
            upstream.Dispose();
    }

    private sealed class FakeUpstream : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public HttpListener Listener { get; }
        public int Port { get; }
        public Uri BaseAddress => new($"http://127.0.0.1:{Port}");

        private FakeUpstream(HttpListener listener, int port)
        {
            Listener = listener;
            Port = port;
        }

        public static FakeUpstream Start(Func<HttpListenerContext, Task> handler)
        {
            var port = GetFreeLoopbackPort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            var upstream = new FakeUpstream(listener, port);
            _ = Task.Run(() => upstream.LoopAsync(handler));
            return upstream;
        }

        private async Task LoopAsync(Func<HttpListenerContext, Task> handler)
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await Listener.GetContextAsync();
                }
                catch (Exception) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    continue;
                }

                _ = Task.Run(() => handler(context));
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { Listener.Close(); }
            catch { }
            _cts.Dispose();
        }
    }
}
