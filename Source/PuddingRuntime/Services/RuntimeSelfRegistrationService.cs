using System.Net.Http.Json;
using PuddingCode.Platform;

namespace PuddingRuntime.Services;

/// <summary>
/// Runtime 节点自注册服务——启动时向 Controller 注册本节点，并按心跳间隔续约。
/// 通过 appsettings["Pudding:ControllerEndpoint"] 配置 Controller 地址，
/// 未配置时静默跳过（单进程嵌入模式无需注册）。
/// </summary>
public sealed class RuntimeSelfRegistrationService : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly IConfiguration _configuration;
    private readonly AgentSessionManager _sessionManager;
    private readonly ILogger<RuntimeSelfRegistrationService> _logger;

    // 本 Runtime 节点在本次进程生命周期内的固定 ID
    private readonly string _nodeId = Guid.NewGuid().ToString("N")[..8];

    public RuntimeSelfRegistrationService(
        IConfiguration configuration,
        AgentSessionManager sessionManager,
        ILogger<RuntimeSelfRegistrationService> logger)
    {
        _configuration = configuration;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var controllerEndpoint = _configuration["Pudding:ControllerEndpoint"];
        if (string.IsNullOrWhiteSpace(controllerEndpoint))
        {
            _logger.LogDebug("[RuntimeReg] Pudding:ControllerEndpoint not configured, self-registration skipped.");
            return;
        }

        var selfEndpoint = _configuration["Pudding:SelfEndpoint"] ?? "http://localhost:5100";

        _logger.LogInformation("[RuntimeReg] NodeId={NodeId} will register to {Controller} as {Self}",
            _nodeId, controllerEndpoint, selfEndpoint);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RegisterOnceAsync(controllerEndpoint, selfEndpoint, stoppingToken);
            try
            {
                await Task.Delay(HeartbeatInterval, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("[RuntimeReg] Self-registration service stopping.");
    }

    private async Task RegisterOnceAsync(string controllerEndpoint, string selfEndpoint, CancellationToken ct)
    {
        try
        {
            var activeSessions = _sessionManager.ListActive().Count;
            var request = new RuntimeRegisterRequest
            {
                NodeId = _nodeId,
                Endpoint = selfEndpoint,
                ActiveSessionCount = activeSessions,
            };

            using var http = new HttpClient { BaseAddress = new Uri(controllerEndpoint) };
            var resp = await http.PostAsJsonAsync("/api/runtime-registry/register", request, ct);
            if (resp.IsSuccessStatusCode)
                _logger.LogDebug("[RuntimeReg] Registered/heartbeat OK, activeSessions={N}", activeSessions);
            else
                _logger.LogWarning("[RuntimeReg] Register failed: {Status}", resp.StatusCode);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "[RuntimeReg] Could not reach Controller to register (will retry)");
        }
    }
}
