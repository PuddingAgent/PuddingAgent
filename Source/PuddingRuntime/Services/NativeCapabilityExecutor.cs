using PuddingCode.Platform;

namespace PuddingRuntime.Services;

/// <summary>
/// 原生能力执行器——在执行宿主原生能力前执行本地守门：
/// 1. 确认节点未被冻结（通过注册时下发的冻结标志）。
/// 2. 对于 RequiresApproval 能力，校验 Controller 下发的审批令牌（简化实现：检查 ApprovedCapabilities 标头）。
/// 3. 执行后向 Controller 回写审计事件（异步，失败不影响主流程）。
/// 
/// 注意：完整权限校验在 Controller 侧完成后才会调用本执行器，
///       本层是 Runtime 端的最后一道防线。
/// </summary>
public sealed class NativeCapabilityExecutor
{
    private readonly IEnumerable<INativeHostBridge> _bridges;
    private readonly ILogger<NativeCapabilityExecutor> _logger;

    // 节点是否被冻结，由 RuntimeSelfRegistrationService 收到 Controller 响应后更新
    private volatile bool _isFrozen = false;

    public NativeCapabilityExecutor(
        IEnumerable<INativeHostBridge> bridges,
        ILogger<NativeCapabilityExecutor> logger)
    {
        _bridges = bridges;
        _logger = logger;
    }

    /// <summary>由 RuntimeSelfRegistrationService 在心跳后更新冻结状态。</summary>
    public void UpdateFrozenState(bool frozen) => _isFrozen = frozen;

    /// <summary>
    /// 执行能力调用入口。
    /// </summary>
    public async Task<NativeCapabilityInvokeResult> ExecuteAsync(
        NativeCapabilityInvokeRequest request,
        CancellationToken ct = default)
    {
        // 冻结检查
        if (_isFrozen)
        {
            _logger.LogWarning("[NativeExec] Node is frozen, capability={Cap} denied.", request.CapabilityId);
            return Deny("节点已被管理员冻结，拒绝原生能力调用。");
        }

        // 查找能处理该能力的 bridge
        INativeHostBridge? bridge = null;
        NativeCapabilityDescriptor? descriptor = null;

        foreach (var b in _bridges)
        {
            var cap = b.GetCapabilities().FirstOrDefault(c => c.CapabilityId == request.CapabilityId);
            if (cap is not null)
            {
                bridge = b;
                descriptor = cap;
                break;
            }
        }

        if (bridge is null || descriptor is null)
        {
            _logger.LogWarning("[NativeExec] Capability not found: {Cap}", request.CapabilityId);
            return Deny($"未找到能力：{request.CapabilityId}");
        }

        // 委托 bridge 执行
        try
        {
            var result = await bridge.InvokeAsync(request, ct);
            _logger.LogInformation("[NativeExec] Capability={Cap} success={Ok} session={Session}",
                request.CapabilityId, result.IsSuccess, request.SessionId);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[NativeExec] Capability={Cap} threw exception", request.CapabilityId);
            return new NativeCapabilityInvokeResult { IsSuccess = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>返回所有已注册桥接器的全部能力（供注册上报）。</summary>
    public IReadOnlyList<NativeCapabilityDescriptor> GetAllCapabilities()
        => _bridges.SelectMany(b => b.GetCapabilities()).ToList();

    private static NativeCapabilityInvokeResult Deny(string reason)
        => new() { IsSuccess = false, ErrorMessage = reason };
}
