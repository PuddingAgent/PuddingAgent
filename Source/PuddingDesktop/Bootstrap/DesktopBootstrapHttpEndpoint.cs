using System.Net;
using System.Text;
using System.Text.Json;
using PuddingDesktop.Configuration;
using PuddingDesktop.Debug;
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
///   POST /desktop/bootstrap/core/restart — restart Core without replacing artifacts
///   POST /desktop/bootstrap/core/deploy-restart — load prebuilt Core artifacts, verify, restart
///   POST /desktop/bootstrap/frontend/build-deploy — build and hot-deploy Admin frontend
///   POST /desktop/bootstrap/frontend/load — load an already-built frontend dist
///   GET  /desktop/bootstrap/diagnostics — token-checked bounded runtime/log snapshot
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
    internal const string CoreRestartPath = "/desktop/bootstrap/core/restart";
    internal const string CoreDeployRestartPath = "/desktop/bootstrap/core/deploy-restart";
    internal const string FrontendBuildDeployPath = "/desktop/bootstrap/frontend/build-deploy";
    internal const string FrontendLoadPath = "/desktop/bootstrap/frontend/load";
    internal const string DiagnosticsPath = "/desktop/bootstrap/diagnostics";

    private readonly DesktopBootstrapSignalService _signalService;
    private readonly IDesktopControlTokenService _tokenService;
    private readonly Func<string> _coreStateProvider;
    private readonly Func<CancellationToken, Task<FrontendDeployResult>> _frontendBuildDeploy;
    private readonly Func<string, string?, CancellationToken, Task<FrontendDeployResult>> _frontendLoad;
    private readonly Func<DesktopControlDiagnosticsSnapshot> _diagnosticsProvider;
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
        Func<string> coreStateProvider,
        Func<CancellationToken, Task<FrontendDeployResult>> frontendBuildDeploy,
        Func<string, string?, CancellationToken, Task<FrontendDeployResult>> frontendLoad,
        Func<DesktopControlDiagnosticsSnapshot> diagnosticsProvider)
    {
        ArgumentNullException.ThrowIfNull(signalService);
        ArgumentNullException.ThrowIfNull(tokenService);
        ArgumentNullException.ThrowIfNull(coreStateProvider);
        ArgumentNullException.ThrowIfNull(frontendBuildDeploy);
        ArgumentNullException.ThrowIfNull(frontendLoad);
        ArgumentNullException.ThrowIfNull(diagnosticsProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        _signalService = signalService;
        _tokenService = tokenService;
        _coreStateProvider = coreStateProvider;
        _frontendBuildDeploy = frontendBuildDeploy;
        _frontendLoad = frontendLoad;
        _diagnosticsProvider = diagnosticsProvider;
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
                else if (string.Equals(path, CoreRestartPath, StringComparison.OrdinalIgnoreCase))
                    await HandleCoreRestartAsync(context, cancellationToken);
                else if (string.Equals(path, CoreDeployRestartPath, StringComparison.OrdinalIgnoreCase))
                    await HandleCoreDeployRestartAsync(context, cancellationToken);
                else if (string.Equals(path, FrontendBuildDeployPath, StringComparison.OrdinalIgnoreCase))
                    await HandleFrontendBuildDeployAsync(context, cancellationToken);
                else if (string.Equals(path, FrontendLoadPath, StringComparison.OrdinalIgnoreCase))
                    await HandleFrontendLoadAsync(context, cancellationToken);
                else
                    await WriteJsonAsync(context, 404, """{"error":"not_found"}""");
            }
            else if (string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
                && string.Equals(path, StatusPath, StringComparison.OrdinalIgnoreCase))
            {
                await HandleStatusAsync(context, cancellationToken);
            }
            else if (string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
                && string.Equals(path, DiagnosticsPath, StringComparison.OrdinalIgnoreCase))
            {
                await HandleDiagnosticsAsync(context, cancellationToken);
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
            body,
            out _,
            out var requestedBy,
            out var yolo,
            out var deploymentMode,
            out var artifactDirectory,
            out var artifactAssemblySha256);

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
        if (DesktopBootstrapSignalParser.NormalizeDeploymentMode(deploymentMode) is null)
        {
            await WriteJsonAsync(context, 400, """{"error":"unsupported_deployment_mode"}""");
            return;
        }

        _ = RunInBackgroundAsync(
            requestedBy,
            yolo,
            deploymentMode,
            artifactDirectory,
            artifactAssemblySha256);
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
        DesktopBootstrapHttpRequestParser.TryParseStartBody(
            body, out var bodyToken, out _, out _, out _, out _, out _);

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

    private Task HandleCoreRestartAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
        => HandleCoreDeployOperationAsync(
            context,
            DesktopBootstrapSignalParser.RestartOnlyMode,
            cancellationToken);

    private Task HandleCoreDeployRestartAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
        => HandleCoreDeployOperationAsync(
            context,
            DesktopBootstrapSignalParser.PrebuiltArtifactMode,
            cancellationToken);

    private async Task HandleCoreDeployOperationAsync(
        HttpListenerContext context,
        string deploymentMode,
        CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(context.Request, cancellationToken);
        if (!await CheckTokenAsync(context, body, cancellationToken))
        {
            await WriteJsonAsync(context, 401, """{"error":"unauthorized"}""");
            return;
        }

        DesktopBootstrapHttpRequestParser.TryParseStartBody(
            body,
            out _,
            out var requestedBy,
            out var yolo,
            out _,
            out var artifactDirectory,
            out var artifactAssemblySha256);

        if (_signalService.IsBusy)
        {
            await WriteJsonAsync(context, 409, """{"error":"busy"}""");
            return;
        }

        try
        {
            var result = await _signalService.TriggerRebuildRestartAsync(
                requestedBy,
                yolo,
                deploymentMode,
                deploymentMode == DesktopBootstrapSignalParser.PrebuiltArtifactMode
                    ? artifactDirectory
                    : null,
                deploymentMode == DesktopBootstrapSignalParser.PrebuiltArtifactMode
                    ? artifactAssemblySha256
                    : null,
                cancellationToken);
            await WriteJsonAsync(
                context,
                result.Success ? 200 : 422,
                JsonSerializer.Serialize(result, ResponseJsonOptions),
                cancellationToken);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("bootstrap already running", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(context, 409, """{"error":"busy"}""");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    /// <summary>
    /// Builds the Admin frontend and replaces only the running Core's
    /// wwwroot/admin static subtree. Core stays running.
    /// </summary>
    private Task HandleFrontendBuildDeployAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
        => HandleFrontendOperationAsync(context, loadPrebuilt: false, cancellationToken);

    /// <summary>Loads a caller-built dist directory without invoking pnpm.</summary>
    private Task HandleFrontendLoadAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
        => HandleFrontendOperationAsync(context, loadPrebuilt: true, cancellationToken);

    private async Task HandleFrontendOperationAsync(
        HttpListenerContext context,
        bool loadPrebuilt,
        CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(context.Request, cancellationToken);
        if (!await CheckTokenAsync(context, body, cancellationToken))
        {
            await WriteJsonAsync(context, 401, """{"error":"unauthorized"}""");
            return;
        }

        DesktopBootstrapHttpRequestParser.TryParseFrontendBody(
            body,
            out _,
            out var artifactDirectory,
            out var artifactIndexSha256);
        if (loadPrebuilt && string.IsNullOrWhiteSpace(artifactDirectory))
        {
            await WriteJsonAsync(context, 400, """{"error":"artifact_directory_required"}""");
            return;
        }

        try
        {
            var result = loadPrebuilt
                ? await _frontendLoad(
                    artifactDirectory!,
                    artifactIndexSha256,
                    cancellationToken)
                : await _frontendBuildDeploy(cancellationToken);
            await WriteJsonAsync(
                context,
                200,
                JsonSerializer.Serialize(
                    new
                    {
                        success = true,
                        result.TargetAdminDirectory,
                        result.CopiedFileCount,
                        result.RanInstall,
                        result.BuiltFromSource,
                        result.IndexSha256,
                    },
                    ResponseJsonOptions),
                cancellationToken);
        }
        catch (FrontendDeployInProgressException)
        {
            await WriteJsonAsync(context, 409, """{"error":"frontend_deploy_busy"}""");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            DesktopDiagnosticLog.Write("BootstrapHttpFrontendDeploy", ex);
            await WriteJsonAsync(
                context,
                500,
                JsonSerializer.Serialize(
                    new { error = "frontend_deploy_failed", message = ex.Message },
                    ResponseJsonOptions),
                cancellationToken);
        }
    }

    private async Task RunInBackgroundAsync(
        string? requestedBy,
        bool yolo,
        string? deploymentMode,
        string? artifactDirectory,
        string? artifactAssemblySha256)
    {
        try
        {
            await _signalService.TriggerRebuildRestartAsync(
                requestedBy,
                yolo,
                deploymentMode,
                artifactDirectory,
                artifactAssemblySha256,
                CancellationToken.None);
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

    private async Task HandleDiagnosticsAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        if (!await CheckTokenAsync(context, string.Empty, cancellationToken))
        {
            await WriteJsonAsync(context, 401, """{"error":"unauthorized"}""");
            return;
        }

        var payload = new
        {
            diagnostics = _diagnosticsProvider(),
            lastDeploymentResult = ReadLastResult(),
        };
        await WriteJsonAsync(
            context,
            200,
            JsonSerializer.Serialize(payload, ResponseJsonOptions),
            cancellationToken);
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

/// <summary>Bounded, token-protected Desktop/Core diagnostic snapshot for local automation.</summary>
public sealed record DesktopControlDiagnosticsSnapshot
{
    public required DateTimeOffset CapturedAt { get; init; }
    public required string DesktopVersion { get; init; }
    public required string DesktopState { get; init; }
    public required string CoreState { get; init; }
    public int? CoreProcessId { get; init; }
    public DateTimeOffset? CoreStartedAt { get; init; }
    public DateTimeOffset? CoreReadyAt { get; init; }
    public int? LastExitCode { get; init; }
    public DateTimeOffset? LastExitAt { get; init; }
    public int RestartAttemptsInWindow { get; init; }
    public bool AutoRestartEnabled { get; init; }
    public bool UserStopRequested { get; init; }
    public bool BootstrapBusy { get; init; }
    public bool FrontendDeployBusy { get; init; }
    public string? CoreAddress { get; init; }
    public string? WorkbenchAddress { get; init; }
    public string? DataRoot { get; init; }
    public string? CoreExecutablePath { get; init; }
    public string? LastError { get; init; }
    public IReadOnlyList<string> CoreLogTail { get; init; } = [];
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
    /// {"token":"...","requestedBy":"...","yolo":true,
    ///  "deploymentMode":"desktop-build","artifactDirectory":"...",
    ///  "artifactAssemblySha256":"..."}.
    /// Returns false when the body is empty or not valid JSON.
    /// </summary>
    public static bool TryParseStartBody(
        string? body,
        out string? token,
        out string? requestedBy,
        out bool yolo,
        out string? deploymentMode,
        out string? artifactDirectory,
        out string? artifactAssemblySha256)
    {
        token = null;
        requestedBy = null;
        yolo = false;
        deploymentMode = null;
        artifactDirectory = null;
        artifactAssemblySha256 = null;

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
            deploymentMode = payload.DeploymentMode;
            artifactDirectory = payload.ArtifactDirectory;
            artifactAssemblySha256 = payload.ArtifactAssemblySha256;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Parses the prebuilt frontend request body
    /// {"token":"...","artifactDirectory":"...","artifactIndexSha256":"..."}.
    /// </summary>
    public static bool TryParseFrontendBody(
        string? body,
        out string? token,
        out string? artifactDirectory,
        out string? artifactIndexSha256)
    {
        token = null;
        artifactDirectory = null;
        artifactIndexSha256 = null;

        if (string.IsNullOrWhiteSpace(body))
            return false;

        try
        {
            var payload = JsonSerializer.Deserialize<FrontendRequestBody>(body, JsonOptions);
            if (payload is null)
                return false;

            token = payload.Token;
            artifactDirectory = payload.ArtifactDirectory;
            artifactIndexSha256 = payload.ArtifactIndexSha256;
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
        public string? DeploymentMode { get; init; }
        public string? ArtifactDirectory { get; init; }
        public string? ArtifactAssemblySha256 { get; init; }
    }

    private sealed record FrontendRequestBody
    {
        public string? Token { get; init; }
        public string? ArtifactDirectory { get; init; }
        public string? ArtifactIndexSha256 { get; init; }
    }
}
