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
/// Bootstrap 点火工具 —— Agent 自助触发 Desktop 引导式 rebuild-restart（ADR-068 自举闭环）。
/// 从 &lt;DataRoot&gt;/config/system.json 读取 desktop.core.controlToken 与
/// desktop.bootstrap.httpPort，向 Desktop 环回控制端点
/// POST /desktop/bootstrap/start（X-Control-Token 头 + JSON body）。
/// Desktop 接受后：停 Core → dotnet 增量构建 → 按需写 yolo.signal → 重启 Core。
/// 成功触发后本进程将在数秒内被停止，会话于重启后自动恢复（心跳/消息唤醒）。
/// </summary>
[Tool(
    id: "bootstrap_reboot",
    name: "Bootstrap reboot",
    description: "Trigger a Desktop-guided rebuild-and-restart of the Core process (self-ignition). Use after committing code or configuration changes that require a restart to take effect. Desktop stops Core, runs an incremental dotnet build and restarts it; this process terminates within seconds of a successful trigger and the session resumes automatically after the restart. yolo.signal is written so YOLO mode survives the restart when it is currently active (override with keepYolo). WARNING: all running sub-agents are terminated by the restart.",
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

        var yolo = args.KeepYolo ?? _runtimeControl.Mode == RuntimeExecutionMode.Yolo;
        var requestedBy = $"agent:{context.AgentInstanceId}";
        var url = $"http://127.0.0.1:{port}/desktop/bootstrap/start";
        var body = BuildStartRequestJson(token, requestedBy, yolo);

        _logger.LogWarning(
            "[BootstrapReboot] Agent {Agent} triggered rebuild-restart. endpoint={Endpoint} yolo={Yolo} reason={Reason}",
            context.AgentInstanceId, url, yolo, args.Reason ?? "(none)");

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
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
                401 => "unauthorized",
                409 => "busy",
                _ => $"unexpected_{statusCode}",
            };

            var notice = statusCode switch
            {
                202 => "Rebuild-restart accepted. Core will stop within seconds; the session resumes after the restart.",
                409 => "A rebuild-restart is already in progress; check GET /desktop/bootstrap/status.",
                _ => "Trigger rejected by Desktop.",
            };

            return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
            {
                status = statusText,
                http_status = statusCode,
                yolo_signal_requested = yolo,
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

    /// <summary>Builds the POST /desktop/bootstrap/start JSON body.</summary>
    public static string BuildStartRequestJson(string token, string requestedBy, bool yolo)
        => JsonSerializer.Serialize(new { token, requestedBy, yolo }, OutputJsonOptions);

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
    [ToolParam("Why the reboot is triggered; recorded in the Core log for audit. Recommended.")]
    public string? Reason { get; init; }

    [ToolParam("Whether Desktop should write yolo.signal so YOLO mode survives the restart. Default: preserve the current mode (true only when currently in YOLO).")]
    public bool? KeepYolo { get; init; }
}
