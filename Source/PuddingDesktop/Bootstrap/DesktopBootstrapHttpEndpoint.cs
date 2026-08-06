using System.Net;
using System.Text;
using System.Text.Json;
using PuddingDesktop.Configuration;
using PuddingDesktop.Diagnostics;

namespace PuddingDesktop.Bootstrap;

/// <summary>
/// Loopback-only HTTP control endpoint for the guided bootstrap.
/// Listens on http://127.0.0.1:{HttpPort}/ (default 8199 — deliberately away
/// from the Core HTTP port 8080). Routes:
///   POST /desktop/bootstrap/start       — token-checked manual rebuild-restart → 202 / 401 / 409
///   POST /desktop/bootstrap/core/stop   — atomic stop Core → 200 / 401 / 409
///   POST /desktop/bootstrap/build       — atomic build only → 200 / 401 / 409 (core_running)
///   POST /desktop/bootstrap/core/start  — atomic start Core → 200 / 401 / 409
///   GET  /desktop/bootstrap/status      — {"busy":bool,"coreState":str,"lastResult":&lt;result file content or null&gt;}
///   anything else                       → 404
/// Every response is application/json; charset=utf-8. Listener failures (port
/// in use, URL ACL, http.sys) are logged via DesktopDiagnosticLog and never
/// crash Desktop — bootstrap stays signal-file / UI only.
/// </summary>
public sealed class DesktopBootstrapHttpEndpoint : IAsyncDisposable
{
    private const int MaxBodyChars = 64 * 1024;
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web);
    private const string StartPath = "/desktop/bootstrap/start";
    private const string StatusPath = "/desktop/bootstrap/status";
    private const string CoreStopPath = "/desktop/bootstrap/core/stop";
    private const string BuildPath = "/desktop/bootstrap/build";
    private const string CoreStartPath = "/desktop/bootstrap/core/start";

    private readonly DesktopBootstrapSignalService _signalService;
    private readonly IDesktopControlTokenService _tokenService;
    private readonly Func<string> _coreStateProvider;
    private readonly string _dataRoot;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private HttpListener? _listener;
    private Task? _loopTask;

    public DesktopBootstrapHttpEndpoint(
        DesktopBootstrapSignalService signalService,
        IDesktopControlTokenService tokenService,
        string dataRoot,
        int port,
        Func<string> coreStateProvider)
    {
        ArgumentNullException.ThrowIfNull(signalService);
        ArgumentNullException.ThrowIfNull(tokenService);
        ArgumentNullException.ThrowIfNull(coreStateProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        _signalService = signalService;
        _tokenService = tokenService;
        _coreStateProvider = coreStateProvider;
        _dataRoot = dataRoot;
        _port = port;
    }

    /// <summary>Starts the loopback listener and the request loop. Idempotent.</summary>
    public void Start(CancellationToken cancellationToken)
    {
        if (_listener is not null)
            return;

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{_port}/");

        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            // Port in use / URL ACL missing / http.sys failure: log it and keep
            // Desktop running — bootstrap stays file-signal / UI-only.
            DesktopDiagnosticLog.Write("BootstrapHttpStart", ex);
            return;
        }

        _listener = listener;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        _loopTask = Task.Run(() => LoopAsync(listener, linked.Token), CancellationToken.None);
    }

    private async Task LoopAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
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
                DesktopDiagnosticLog.Write("BootstrapHttpLoop", ex);
                continue;
            }

            _ = HandleContextAsync(context, cancellationToken);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";

            if (string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(path, StartPath, StringComparison.OrdinalIgnoreCase))
                    await HandleStartAsync(context, cancellationToken);
                else if (string.Equals(path, CoreStopPath, StringComparison.OrdinalIgnoreCase))
                    await HandleCoreStopAsync(context, cancellationToken);
                else if (string.Equals(path, BuildPath, StringComparison.OrdinalIgnoreCase))
                    await HandleBuildAsync(context, cancellationToken);
                else if (string.Equals(path, CoreStartPath, StringComparison.OrdinalIgnoreCase))
                    await HandleCoreStartAsync(context, cancellationToken);
                else
                    await WriteJsonAsync(context, 404, """{"error":"not_found"}""");
            }
            else if (string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
                && string.Equals(path, StatusPath, StringComparison.OrdinalIgnoreCase))
            {
                await HandleStatusAsync(context, cancellationToken);
            }
            else
            {
                await WriteJsonAsync(context, 404, """{"error":"not_found"}""");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            DesktopDiagnosticLog.Write("BootstrapHttpRequest", ex);
            try { await WriteJsonAsync(context, 500, """{"error":"internal_error"}"""); }
            catch { }
        }
    }

    private async Task HandleStartAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(context.Request, cancellationToken);
        DesktopBootstrapHttpRequestParser.TryParseStartBody(
            body, out _, out var requestedBy, out var yolo);

        if (!await CheckTokenAsync(context, body, cancellationToken))
        {
            await WriteJsonAsync(context, 401, """{"error":"unauthorized"}""");
            return;
        }

        if (_signalService.IsBusy)
        {
            await WriteJsonAsync(context, 409, """{"error":"busy"}""");
            return;
        }

        // Accepted: run the rebuild-restart in the background; the signal
        // service still writes the result to rebuild.signal.result.json.
        _ = RunInBackgroundAsync(requestedBy, yolo);
        await WriteJsonAsync(
            context,
            202,
            JsonSerializer.Serialize(new { accepted = true, resultPath = _signalService.ResultPath }));
    }

    /// <summary>
    /// Shared token check for all POST routes: the X-Control-Token header and
    /// the JSON body token are the union of accepted sources (identical to the
    /// original /start validation).
    /// </summary>
    private async Task<bool> CheckTokenAsync(
        HttpListenerContext context, string body, CancellationToken cancellationToken)
    {
        DesktopBootstrapHttpRequestParser.TryParseStartBody(body, out var bodyToken, out _, out _);

        var headerToken = context.Request.Headers["X-Control-Token"];
        var expectedToken = await _tokenService.GetOrCreateAsync(_dataRoot, cancellationToken);

        var headerValid = DesktopBootstrapSignalParser.IsTokenValid(headerToken, expectedToken);
        var bodyValid = DesktopBootstrapSignalParser.IsTokenValid(bodyToken, expectedToken);
        return headerValid || bodyValid;
    }

    /// <summary>
    /// POST /desktop/bootstrap/core/stop — synchronously stops Core and waits
    /// for the fully-stopped state. Atomic operations may take a while; the
    /// synchronous wait is intentional.
    /// </summary>
    private async Task HandleCoreStopAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(context.Request, cancellationToken);
        if (!await CheckTokenAsync(context, body, cancellationToken))
        {
            await WriteJsonAsync(context, 401, """{"error":"unauthorized"}""");
            return;
        }

        if (_signalService.IsBusy)
        {
            await WriteJsonAsync(context, 409, """{"error":"busy"}""");
            return;
        }

        DesktopBootstrapResult result;
        try
        {
            result = await _signalService.StopCoreAtomicAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            await WriteJsonAsync(context, 409, """{"error":"busy"}""");
            return;
        }

        await WriteJsonAsync(
            context, 200, JsonSerializer.Serialize(result, ResponseJsonOptions), cancellationToken);
    }

    /// <summary>
    /// POST /desktop/bootstrap/build — synchronously runs only the dotnet build.
    /// 409 {"error":"core_running"} when Core is still running.
    /// </summary>
    private async Task HandleBuildAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(context.Request, cancellationToken);
        if (!await CheckTokenAsync(context, body, cancellationToken))
        {
            await WriteJsonAsync(context, 401, """{"error":"unauthorized"}""");
            return;
        }

        if (_signalService.IsBusy)
        {
            await WriteJsonAsync(context, 409, """{"error":"busy"}""");
            return;
        }

        DesktopBootstrapResult result;
        try
        {
            result = await _signalService.BuildOnlyAtomicAsync(cancellationToken);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("bootstrap already running", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(context, 409, """{"error":"busy"}""");
            return;
        }
        catch (InvalidOperationException)
        {
            await WriteJsonAsync(context, 409, """{"error":"core_running"}""");
            return;
        }

        await WriteJsonAsync(
            context, 200, JsonSerializer.Serialize(result, ResponseJsonOptions), cancellationToken);
    }

    /// <summary>
    /// POST /desktop/bootstrap/core/start — synchronously starts Core.
    /// </summary>
    private async Task HandleCoreStartAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(context.Request, cancellationToken);
        if (!await CheckTokenAsync(context, body, cancellationToken))
        {
            await WriteJsonAsync(context, 401, """{"error":"unauthorized"}""");
            return;
        }

        if (_signalService.IsBusy)
        {
            await WriteJsonAsync(context, 409, """{"error":"busy"}""");
            return;
        }

        DesktopBootstrapResult result;
        try
        {
            result = await _signalService.StartCoreAtomicAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            await WriteJsonAsync(context, 409, """{"error":"busy"}""");
            return;
        }

        await WriteJsonAsync(
            context, 200, JsonSerializer.Serialize(result, ResponseJsonOptions), cancellationToken);
    }

    private async Task RunInBackgroundAsync(string? requestedBy, bool yolo)
    {
        try
        {
            await _signalService.TriggerRebuildRestartAsync(requestedBy, yolo, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Includes the busy race: another trigger won the Interlocked slot
            // between IsBusy and this call. Logged; the winner writes the result.
            DesktopDiagnosticLog.Write("BootstrapHttpTrigger", ex);
        }
    }

    private async Task HandleStatusAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var payload = new
        {
            busy = _signalService.IsBusy,
            coreState = _coreStateProvider(),
            lastResult = ReadLastResult(),
        };
        await WriteJsonAsync(context, 200, JsonSerializer.Serialize(payload), cancellationToken);
    }

    /// <summary>Reads the result file and returns it as raw JSON (or null when absent/unreadable).</summary>
    private object? ReadLastResult()
    {
        try
        {
            if (!File.Exists(_signalService.ResultPath))
                return null;

            var json = File.ReadAllText(_signalService.ResultPath);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            // JsonElement is serialized inline as raw JSON inside the payload.
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasEntityBody)
            return string.Empty;

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        var buffer = new char[MaxBodyChars];
        var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
        return new string(buffer, 0, read);
    }

    private static async Task WriteJsonAsync(
        HttpListenerContext context, int statusCode, string json, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
        context.Response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        var listener = _listener;
        _listener = null;
        if (listener is not null)
        {
            try { listener.Close(); }
            catch { }
        }

        if (_loopTask is not null)
        {
            try { await _loopTask; }
            catch { }
            _loopTask = null;
        }

        _cts.Dispose();
    }
}

/// <summary>
/// Pure, side-effect-free parsing for the bootstrap HTTP endpoint (request body).
/// Kept static so it can be unit tested when a PuddingDesktop test project exists.
/// </summary>
internal static class DesktopBootstrapHttpRequestParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Parses the POST /desktop/bootstrap/start JSON body
    /// {"token":"...","requestedBy":"...","yolo":true}.
    /// Returns false when the body is empty or not valid JSON.
    /// </summary>
    public static bool TryParseStartBody(string? body, out string? token, out string? requestedBy, out bool yolo)
    {
        token = null;
        requestedBy = null;
        yolo = false;

        if (string.IsNullOrWhiteSpace(body))
            return false;

        try
        {
            var payload = JsonSerializer.Deserialize<StartRequestBody>(body, JsonOptions);
            if (payload is null)
                return false;

            token = payload.Token;
            requestedBy = payload.RequestedBy;
            yolo = payload.Yolo;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record StartRequestBody
    {
        public string? Token { get; init; }
        public string? RequestedBy { get; init; }
        public bool Yolo { get; init; }
    }
}
