using System.Net.Http.Json;
using PuddingCode.Platform;

namespace PuddingRuntime.Services;

/// <summary>
/// Runtime 节点自注册服务——启动时向 Controller 注册本节点，并按心跳间隔续约。
/// 通过 appsettings["Pudding:ControllerEndpoint"] 配置 Controller 地址，
/// 未配置时静默跳过（单进程嵌入模式无需注册）。
/// 嵌入式宿主模式：配置 Pudding:EmbeddedMode=true 与 Pudding:HostType=&lt;name&gt; 后，
/// 注册请求中携带原生能力列表，Controller 将该节点识别为可调度嵌入式节点。
/// </summary>
public sealed class RuntimeSelfRegistrationService : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly IConfiguration _configuration;
    private readonly AgentSessionManager _sessionManager;
    private readonly NativeCapabilityExecutor _capabilityExecutor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RuntimeSelfRegistrationService> _logger;

    // 本 Runtime 节点在本次进程生命周期内的固定 ID
    private readonly string _nodeId = Guid.NewGuid().ToString("N")[..8];

    public RuntimeSelfRegistrationService(
        IConfiguration configuration,
        AgentSessionManager sessionManager,
        NativeCapabilityExecutor capabilityExecutor,
        IHttpClientFactory httpClientFactory,
        ILogger<RuntimeSelfRegistrationService> logger)
    {
        _configuration = configuration;
        _sessionManager = sessionManager;
        _capabilityExecutor = capabilityExecutor;
        _httpClientFactory = httpClientFactory;
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
            var embeddedMode = _configuration.GetValue<bool>("Pudding:EmbeddedMode");
            var hostType = _configuration["Pudding:HostType"];

            var request = new RuntimeRegisterRequest
            {
                NodeId = _nodeId,
                Endpoint = selfEndpoint,
                ActiveSessionCount = activeSessions,
                EmbeddedMode = embeddedMode,
                HostType = hostType,
                NativeCapabilities = embeddedMode
                    ? _capabilityExecutor.GetAllCapabilities()
                    : [],
            };

            using var http = _httpClientFactory.CreateClient();
            http.BaseAddress = new Uri(controllerEndpoint);
            var resp = await http.PostAsJsonAsync("/api/runtime-registry/register", request, ct);
            if (resp.IsSuccessStatusCode)
            {
                // 读取 Controller 返回的冻结状态，更新本地执行器
                var body = await resp.Content.ReadFromJsonAsync<RuntimeRegisterResponse>(ct);
                if (body is not null && embeddedMode)
                {
                    _capabilityExecutor.UpdateFrozenState(body.IsFrozen);
                    _logger.LogDebug("[RuntimeReg] Registered/heartbeat OK, activeSessions={N}, frozen={F}",
                        activeSessions, body.IsFrozen);
                }
                else
                {
                    _logger.LogDebug("[RuntimeReg] Registered/heartbeat OK, activeSessions={N}", activeSessions);
                }
            }
            else
            {
                _logger.LogWarning("[RuntimeReg] Register failed: {Status}", resp.StatusCode);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "[RuntimeReg] Could not reach Controller to register (will retry)");
        }
    }
}
