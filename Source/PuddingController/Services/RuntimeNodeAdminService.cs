using Microsoft.Extensions.Logging;
using PuddingCode.Platform;


namespace PuddingController.Services;

/// <summary>
/// 运行时节点管理应用操作——把「冻结/解冻 + 审计」从 RuntimeRegistryController 原位下沉。
///
/// 与工具授权同样的理由：审计写入原先只发生在控制器里，任何不经 HTTP 的管理面（原生客户端直接调用
/// RuntimeRegistryService）冻结节点都**不留痕**，而冻结会直接拒绝该节点的原生能力调用，属于安全相关操作。
/// 现在冻结/解冻与其审计记录在同一个应用操作里完成，两个管理面得到同一条轨迹。
/// </summary>
public sealed class RuntimeNodeAdminService(
    RuntimeRegistryService registry,
    InMemoryAuditEventStore audit,
    ILogger<RuntimeNodeAdminService> logger)
{
    public Task<bool> FreezeAsync(string nodeId, string reason, string operatorId, CancellationToken ct = default)
        => MutateAsync(nodeId, reason, operatorId, freeze: true, ct);

    public Task<bool> UnfreezeAsync(string nodeId, string reason, string operatorId, CancellationToken ct = default)
        => MutateAsync(nodeId, reason, operatorId, freeze: false, ct);

    private async Task<bool> MutateAsync(
        string nodeId, string reason, string operatorId, bool freeze, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) throw new ArgumentException("NodeId 不能为空。", nameof(nodeId));
        var applied = freeze ? registry.FreezeNode(nodeId) : registry.UnfreezeNode(nodeId);
        if (!applied) return false;

        // 冻结原因只存在于审计记录里：节点模型没有该字段（页面不得凭空显示一个原因）。
        await audit.RecordAsync(new AuditEventRecord
        {
            EventType = freeze ? AuditEventType.EmbeddedNodeFrozen : AuditEventType.EmbeddedNodeUnfrozen,
            Detail = freeze
                ? $"Node={nodeId} reason={reason} operator={operatorId}"
                : $"Node={nodeId} operator={operatorId}",
        }, ct);

        if (freeze)
            logger.LogWarning("[RuntimeRegistry] Node={NodeId} frozen by operator={Op}", nodeId, operatorId);
        else
            logger.LogInformation("[RuntimeRegistry] Node={NodeId} unfrozen by operator={Op}", nodeId, operatorId);
        return true;
    }
}
