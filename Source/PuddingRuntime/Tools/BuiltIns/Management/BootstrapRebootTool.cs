using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Agent 自助提交 Guardian 生命周期命令：独立编译、Desktop/Core 重启与健康回滚。
/// 仅从 system.json 读取本地端口和控制令牌；Guardian 请求只在头中携带令牌。
/// 202 表示接单，最终成功或回滚由独立主管的状态接口确认。
/// 显式禁用 Guardian 时保留既有 Desktop Core 点火路径。
/// </summary>
[Tool(
    id: "bootstrap_reboot",
    name: "Bootstrap reboot",
    description: "通知独立 Guardian 编译、部署和重启产品。target=core（默认）只更新 Core；target=desktop 更新 Desktop 与随包 Core。action 可选 build-restart（默认）、build、restart、rollback、start、stop。先在隔离目录编译，成功后停止进程并部署；Desktop 响应与 Core 就绪未通过稳定观察期时自动恢复旧程序集。202 只是接单，使用返回的 statusUrl/requestId 查询最终结果；重启会中断当前 Core 会话及子代理。frontendMode=build/load 将前端加入候选产物。",
    category: ToolCategory.General,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.None)]
public sealed class BootstrapRebootTool : PuddingToolBase<BootstrapRebootArgs>
{
    private static readonly JsonSerializerOptions OutputJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PuddingDataPaths _paths;
    private readonly IRuntimeControlService _runtimeControl;
    private readonly ILogger<BootstrapRebootTool> _logger;

    public BootstrapRebootTool(
        PuddingDataPaths paths,
        IRuntimeControlService runtimeControl,
        ILogger<BootstrapRebootTool> logger)
    {
        _paths = paths;
        _runtimeControl = runtimeControl;
        _logger = logger;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        BootstrapRebootArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        var configPath = _paths.SystemConfigFile("system.json");
        if (!File.Exists(configPath))
            return Fail($"system.json not found at {configPath}; cannot resolve Desktop control token.");

        string configJson;
        try
        {
            configJson = await File.ReadAllTextAsync(configPath, ct);
        }
        catch (Exception ex)
        {
            return Fail($"Failed to read system.json: {ex.Message}");
        }

        if (!TryExtractBootstrapTarget(configJson, out var token, out var httpEnabled, out var port))
            return Fail("system.json is not valid JSON or lacks the desktop section.");

        if (string.IsNullOrWhiteSpace(token))
            return Fail("desktop.core.controlToken is empty in system.json; Desktop has not generated the token yet.");

        if (!httpEnabled)
            return Fail("Desktop bootstrap HTTP endpoint is disabled (desktop.bootstrap.httpEnabled=false).");

        if (!TryExtractGuardianTarget(configJson, out var guardianEnabled, out var guardianPort))
            return Fail("Invalid guardian configuration.");
        var target = args.Target?.Trim().ToLowerInvariant() ?? "core";
        var action = args.Action?.Trim().ToLowerInvariant() ?? "build-restart";
        if (target is not ("core" or "desktop") || action is not ("build-restart" or "build" or "restart" or "rollback" or "start" or "stop"))
            return Fail("Invalid guardian target or action.");
        if (!guardianEnabled && (target != "core" || action != "build-restart"))
            return Fail("This operation requires desktop.guardian.enabled=true.");
        var requestId = args.RequestId ?? Guid.NewGuid().ToString("N");
        if (!Guid.TryParse(requestId, out _)) return Fail("requestId must be a UUID.");

        var yolo = args.KeepYolo ?? _runtimeControl.Mode == RuntimeExecutionMode.Yolo;
        var deploymentMode = NormalizeDeploymentMode(args.DeploymentMode);
        if (deploymentMode is null)
            return Fail("deploymentMode must be desktop-build, prebuilt-artifact, or restart-only.");
        if (deploymentMode == "prebuilt-artifact" && string.IsNullOrWhiteSpace(args.ArtifactDirectory))
            return Fail("prebuilt-artifact mode requires artifactDirectory.");

        var frontendMode = NormalizeFrontendMode(args.FrontendMode);
        if (frontendMode is null)
            return Fail("frontendMode must be skip, build, or load.");
        if (frontendMode == "load" && string.IsNullOrWhiteSpace(args.FrontendArtifactDirectory))
            return Fail("frontendMode=load requires frontendArtifactDirectory.");

        var requestedBy = $"agent:{context.AgentInstanceId}";
        var url = guardianEnabled ? $"http://127.0.0.1:{guardianPort}/guardian/commands" : $"http://127.0.0.1:{port}/desktop/bootstrap/start";
        var body = guardianEnabled ? BuildGuardianRequestJson(args with { Target = target, Action = action, RequestId = requestId,
            DeploymentMode = deploymentMode, FrontendMode = frontendMode }, requestedBy, yolo) : BuildStartRequestJson(
            token,
            requestedBy,
            yolo,
            deploymentMode,
            args.ArtifactDirectory,
            args.ArtifactAssemblySha256,
            frontendMode,
            args.FrontendArtifactDirectory,
            args.FrontendArtifactIndexSha256);

        _logger.LogWarning(
            "[BootstrapReboot] Agent {Agent} triggered deployment restart. endpoint={Endpoint} mode={Mode} yolo={Yolo} frontend={Frontend} reason={Reason}",
            context.AgentInstanceId, url, deploymentMode, yolo, frontendMode, args.Reason ?? "(none)");

        try
        {
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-Control-Token", token);

            using var response = await client.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var statusCode = (int)response.StatusCode;

            var statusText = statusCode switch
            {
                202 => "accepted",
                200 when guardianEnabled => "accepted",
                401 => "unauthorized",
                409 => "busy",
                _ => $"unexpected_{statusCode}",
            };

            var notice = statusCode switch
            {
                200 or 202 when guardianEnabled => "Guardian accepted the command. Acceptance is not completion; query the returned statusUrl and requestId. Build preparation precedes shutdown, and failed health acceptance rolls back automatically.",
                202 => "Rebuild-restart accepted. Core will stop within seconds; the session resumes after the restart.",
                409 => guardianEnabled ? "Guardian is busy or the requestId conflicts; check GET /guardian/status." : "A rebuild-restart is already in progress; check GET /desktop/bootstrap/status.",
                _ => "Trigger rejected by Desktop.",
            };

            return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
            {
                status = statusText,
                http_status = statusCode,
                yolo_signal_requested = yolo,
                deployment_mode = deploymentMode,
                frontend_mode = frontendMode,
                target,
                action,
                request_id = guardianEnabled ? requestId : null,
                desktop_response = responseBody,
                notice,
            }, OutputJsonOptions));
        }
        catch (HttpRequestException ex)
        {
            return Fail($"Cannot reach Desktop control endpoint {url}: {ex.Message}. Is PuddingDesktop running?");
        }
        catch (TaskCanceledException)
        {
            return Fail($"Desktop control endpoint {url} timed out after 30 seconds.");
        }
        catch (Exception ex)
        {
            return Fail($"Unexpected error while triggering rebuild-restart: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses system.json and extracts the bootstrap target triple:
    /// control token, HTTP endpoint switch and port (default 8199 comes from
    /// the PuddingDesktopBootstrapConfig record default).
    /// </summary>
    public static bool TryExtractBootstrapTarget(
        string json, out string? token, out bool httpEnabled, out int port)
    {
        token = null;
        httpEnabled = true;
        port = 8199;

        try
        {
            var config = JsonSerializer.Deserialize<PuddingSystemConfig>(json, ConfigJsonOptions);
            if (config is null)
                return false;

            token = config.Desktop.Core.ControlToken;
            httpEnabled = config.Desktop.Bootstrap.HttpEnabled;
            port = config.Desktop.Bootstrap.HttpPort;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryExtractGuardianTarget(string json, out bool enabled, out int port)
    {
        enabled = true; port = 8200;
        try
        {
            var config = JsonSerializer.Deserialize<PuddingSystemConfig>(json, ConfigJsonOptions);
            if (config is null) return false;
            enabled = config.Desktop.Guardian.Enabled;
            port = config.Desktop.Guardian.HttpPort;
            return port is >= 1024 and <= 65535 && port != config.Desktop.Bootstrap.HttpPort;
        }
        catch (JsonException) { return false; }
    }

    public static string BuildGuardianRequestJson(BootstrapRebootArgs args, string requestedBy, bool yolo)
        => JsonSerializer.Serialize(new {
            requestId = args.RequestId, action = args.Action ?? "build-restart", target = args.Target ?? "core",
            requestedBy, yolo, deploymentMode = args.DeploymentMode ?? "desktop-build", args.ArtifactDirectory,
            args.ArtifactAssemblySha256, frontendMode = args.FrontendMode ?? "skip", args.FrontendArtifactDirectory,
            args.FrontendArtifactIndexSha256
        }, OutputJsonOptions);

    /// <summary>Builds the POST /desktop/bootstrap/start JSON body.</summary>
    public static string BuildStartRequestJson(
        string token,
        string requestedBy,
        bool yolo,
        string deploymentMode = "desktop-build",
        string? artifactDirectory = null,
        string? artifactAssemblySha256 = null,
        string? frontendMode = null,
        string? frontendArtifactDirectory = null,
        string? frontendArtifactIndexSha256 = null)
        => JsonSerializer.Serialize(
            new
            {
                token,
                requestedBy,
                yolo,
                deploymentMode,
                artifactDirectory,
                artifactAssemblySha256,
                frontendMode,
                frontendArtifactDirectory,
                frontendArtifactIndexSha256,
            },
            OutputJsonOptions);

    public static string? NormalizeDeploymentMode(string? mode)
    {
        var value = string.IsNullOrWhiteSpace(mode) ? "desktop-build" : mode;
        return value.Trim().ToLowerInvariant().Replace('_', '-') switch
        {
            "desktop-build" or "build" => "desktop-build",
            "prebuilt-artifact" or "prebuilt" => "prebuilt-artifact",
            "restart-only" or "restart" => "restart-only",
            _ => null,
        };
    }

    /// <summary>Normalizes frontendMode aliases; null means unsupported. Empty/omitted means "skip".</summary>
    public static string? NormalizeFrontendMode(string? mode)
    {
        var value = string.IsNullOrWhiteSpace(mode) ? "skip" : mode;
        return value.Trim().ToLowerInvariant() switch
        {
            "skip" => "skip",
            "build" => "build",
            "load" => "load",
            _ => null,
        };
    }

    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private ToolExecutionResult Fail(string message)
    {
        _logger.LogWarning("[BootstrapReboot] {Message}", message);
        return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
        {
            status = "failed",
            error = message,
        }, OutputJsonOptions));
    }
}

/// <summary>bootstrap_reboot 工具参数。</summary>
public sealed record BootstrapRebootArgs
{
    [ToolParam("core (default) or desktop. desktop updates/restarts the Shell and its bundled Core together.")]
    public string? Target { get; init; }

    [ToolParam("build-restart (default), build (prepare only), restart, rollback, start, or stop. Guardian must be enabled for explicit control actions.")]
    public string? Action { get; init; }

    [ToolParam("Optional UUID for idempotent retries. Reuse only for the identical command; omitted means generate a new request.")]
    public string? RequestId { get; init; }
    [ToolParam("Why the reboot is triggered; recorded in the Core log for audit. Recommended.")]
    public string? Reason { get; init; }

    [ToolParam("Whether Desktop should write yolo.signal so YOLO mode survives the restart. Default: preserve the current mode (true only when currently in YOLO).")]
    public bool? KeepYolo { get; init; }

    [ToolParam("Deployment mode: desktop-build (default; Desktop compiles, deploys, verifies and restarts), prebuilt-artifact (deploy an Agent-built directory), or restart-only.")]
    public string? DeploymentMode { get; init; }

    [ToolParam("Absolute prepared PuddingAgent output directory. Required only for prebuilt-artifact mode; it must be inside the configured repository root.")]
    public string? ArtifactDirectory { get; init; }

    [ToolParam("Optional expected SHA-256 of artifactDirectory/PuddingAgent.dll. Desktop rejects deployment on mismatch.")]
    public string? ArtifactAssemblySha256 { get; init; }

    [ToolParam("Frontend step: skip (default; never touch the Admin frontend), build (pnpm run build + deploy to wwwroot/admin BEFORE the dotnet build), or load (deploy an already-built dist). A failing frontend step aborts the whole restart (fail-closed).")]
    public string? FrontendMode { get; init; }

    [ToolParam("Absolute prebuilt frontend dist directory. Required only for frontendMode=load; it must be inside the configured repository root.")]
    public string? FrontendArtifactDirectory { get; init; }

    [ToolParam("Optional expected SHA-256 of the dist index.html. Only used with frontendMode=load; Desktop rejects the deployment on mismatch.")]
    public string? FrontendArtifactIndexSha256 { get; init; }
}
