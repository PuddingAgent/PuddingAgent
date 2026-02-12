using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingCode.Abstractions;

/// <summary>
/// 子代理管理器 — 统一的子代理编排接口。
/// 职责：
///   1. 子代理生命周期管理（创建/执行/完成/取消）
///   2. 注册表（Running / Completed / Failed 按父会话查询）
///   3. 诊断查询（grep、time-range、status filter）
///   4. 级联取消（父代理停止 → 标记所有子代理 cancelled）
/// 
/// 替代原先分散在 SubAgentTool / SessionStateManager / AgentEventHandler 中的子代理逻辑。
/// 关联 ADR：Docs/07架构/16会话状态层与客户端解耦ADR.md
/// </summary>
public interface ISubAgentManager
{
    // ════════════════════════════════════════════════════════
    // 子代理执行
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 派生子代理并异步执行。
    /// 返回子代理 ID，立即返回不阻塞。
    /// 完成后自动触发事件通知（agent.sub_completed）和诊断日志。
    /// </summary>
    Task<SubAgentSpawnResult> SpawnAsync(
        SubAgentSpawnRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// 同步执行子代理（等待完成，结果直接注入父代理上下文）。
    /// </summary>
    Task<SubAgentExecuteResult> ExecuteSyncAsync(
        SubAgentSpawnRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// 级联取消指定父会话的所有运行中子代理。
    /// </summary>
    Task<int> CancelAllAsync(string parentSessionId, CancellationToken ct = default);

    // ════════════════════════════════════════════════════════
    // 状态查询
    // ════════════════════════════════════════════════════════

    /// <summary>获取指定父会话的所有子代理状态。</summary>
    Task<IReadOnlyList<SubAgentStatus>> GetSubAgentsAsync(
        string parentSessionId,
        SubAgentQueryFilter? filter = null,
        CancellationToken ct = default);

    /// <summary>获取运行中的子代理数量。</summary>
    Task<int> GetRunningCountAsync(string parentSessionId, CancellationToken ct = default);

    /// <summary>获取单个子代理的详细状态。</summary>
    Task<SubAgentStatus?> GetStatusAsync(string subSessionId, CancellationToken ct = default);

    // ════════════════════════════════════════════════════════
    // 诊断查询（供 Agent 工具使用）
    // ════════════════════════════════════════════════════════

    /// <summary>Grep 搜索子代理（按 task_summary / reply_summary）。</summary>
    Task<IReadOnlyList<SubAgentStatus>> GrepAsync(
        string parentSessionId, string keyword,
        CancellationToken ct = default);

    /// <summary>时间范围查询（最近 N 天）。</summary>
    Task<IReadOnlyList<SubAgentStatus>> GetRecentAsync(
        string parentSessionId, int days,
        CancellationToken ct = default);

    /// <summary>子代理统计信息（总数、完成/失败/运行中各多少）。</summary>
    Task<SubAgentStats> GetStatsAsync(
        string parentSessionId, CancellationToken ct = default);

    // ════════════════════════════════════════════════════════
    // Run ID 映射（避免双创建）
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 查询 subSessionId 对应的 runId（如果已在 SpawnAsync 中创建）。
    /// 返回 null 表示尚未创建，调用方应自行创建。
    /// </summary>
    string? TryGetRunId(string subSessionId);
}

// ════════════════════════════════════════════════════════
// DTO
// ════════════════════════════════════════════════════════

/// <summary>子代理创建请求。</summary>
public sealed record SubAgentSpawnRequest
{
    public required string ParentSessionId { get; init; }
    public string? ParentAgentId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string TaskDescription { get; init; }
    public string TemplateId { get; init; } = "workspace-task-agent";
    public string? ModelId { get; init; }
    public LlmConfig? LlmConfig { get; init; }
    public int MaxRounds { get; init; } = 10;
    public CapabilityPolicy? CapabilityPolicy { get; init; }
    public string? TaskPlanId { get; init; }
    public string? TaskNodeId { get; init; }
    public string? ParentTaskNodeId { get; init; }
    public int? DelegationDepth { get; init; }
    public int? MaxDelegationDepth { get; init; }
    public string? RoleInPlan { get; init; }
    public bool? AllowSubDelegation { get; init; }
    public bool? AllowAgentCreation { get; init; }
    public string? AssignedObjective { get; init; }
    public string? ExpectedOutputContract { get; init; }
    public int? TimeoutSeconds { get; init; }
}

/// <summary>子代理创建结果。</summary>
public sealed record SubAgentSpawnResult
{
    public required string SubSessionId { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}

/// <summary>同步执行结果。</summary>
public sealed record SubAgentExecuteResult
{
    public required string SubSessionId { get; init; }
    public bool Success { get; init; }
    public string? Reply { get; init; }
    public string? Error { get; init; }
    public TokenUsageDto? Usage { get; init; }
}

/// <summary>子代理查询过滤器。</summary>
public sealed record SubAgentQueryFilter
{
    public string? Status { get; init; }       // running | completed | failed
    public string? TemplateId { get; init; }
    public int? MaxResults { get; init; }
}

/// <summary>子代理统计信息。</summary>
public sealed record SubAgentStats
{
    public int Total { get; init; }
    public int Running { get; init; }
    public int Completed { get; init; }
    public int Failed { get; init; }
    public string? LastCompletedId { get; init; }
    public string? LastFailedId { get; init; }
}
