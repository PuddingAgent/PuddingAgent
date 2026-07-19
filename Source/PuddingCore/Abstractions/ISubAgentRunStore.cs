using PuddingCode.SubAgents;

namespace PuddingCode.Abstractions;

/// <summary>
/// 子代理运行归档存储 — 管理子代理单次运行的完整生命周期：创建、追加事件/工具审计、完成、查询。
/// 运行归档以文件为主，数据库仅做索引。
/// 关联 ADR：Docs/07架构/21子代理工作空间与运行归档ADR.md
/// </summary>
public interface ISubAgentRunStore
{
    Task<SubAgentRunHandle> CreateRunAsync(SubAgentRunCreateRequest request, CancellationToken ct = default);
    Task AppendEventAsync(string runId, string eventType, object payload, CancellationToken ct = default);
    Task AppendToolAuditAsync(string runId, SubAgentToolAuditEntry entry, CancellationToken ct = default);
    Task<SubAgentRunTerminalWriteResult> CompleteRunAsync(string runId, SubAgentRunCompletion completion, CancellationToken ct = default);
    Task<SubAgentRunArchive?> GetRunArchiveAsync(string runId, CancellationToken ct = default);
    /// <summary>
    /// 将本次进程启动前遗留的非终态运行提交为 interrupted。
    /// 子代理执行是进程内任务，进程重启后不得继续显示为 Running。
    /// </summary>
    Task<int> RecoverInterruptedRunsAsync(
        DateTimeOffset startedBeforeUtc,
        int maxRuns,
        CancellationToken ct = default);
    /// <summary>重放尚未投影到 canonical Conversation Event Store 的持久运行事件。</summary>
    Task<int> ReplayPendingConversationEventsAsync(int maxRuns, CancellationToken ct = default);
}
