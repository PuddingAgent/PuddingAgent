using PuddingCode.Platform;

namespace PuddingCode.Abstractions;

/// <summary>
/// 潜意识编排器抽象：在主对话链路外异步执行记忆整合、摘要和增强召回。
/// 阶段 1 先定义稳定契约，阶段 2 再补齐 LLM 抽取与合并逻辑。
/// </summary>
public interface ISubconsciousOrchestrator
{
    /// <summary>
    /// 异步记忆整合：从已完成会话中抽取事实/偏好并执行去重合并。
    /// </summary>
    Task ConsolidateAsync(
        ConsolidationJob job,
        string memorySearchMode,
        MemoryLlmConfig? memoryLlmConfig = null,
        CancellationToken ct = default);

    /// <summary>
    /// 生成会话结构化摘要。
    /// </summary>
    Task<SessionSummary> SummarizeSessionAsync(
        string sessionId,
        string workspaceId,
        string agentId,
        CancellationToken ct = default);

    /// <summary>
    /// 增强召回（deep 模式入口）：在基础召回上叠加潜意识补充结果。
    /// </summary>
    Task<string?> RecallAugmentedAsync(
        string userMessage,
        string workspaceId,
        string agentId,
        string? sessionId = null,
        int maxTokens = 2000,
        MemoryLlmConfig? memoryLlmConfig = null,
        CancellationToken ct = default);

    /// <summary>
    /// 获取记忆仪表盘摘要数据。
    /// </summary>
    Task<MemoryDashboard> GetMemoryDashboardAsync(
        string workspaceId,
        CancellationToken ct = default);

    /// <summary>
    /// 分页搜索记忆条目（供管理界面使用）。
    /// </summary>
    Task<MemorySearchResult> SearchMemoriesAsync(
        MemorySearchRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// 定期记忆整理：扫描记忆库 → Flash LLM 分析 → 去重合并 → 过期清理 → 报告。
    /// 由 SubconsciousWorkerService 定时触发（每 12h 检查一次）。
    /// </summary>
    Task<AutoDreamReport> AutoDreamAsync(
        string workspaceId,
        MemoryLlmConfig? memoryLlmConfig = null,
        CancellationToken ct = default);

    /// <summary>
    /// 经验→SKILL 管道：扫描最近的会话 → 检测黄金路径 → 3条件过滤 → 生成 SKILL.md。
    /// 由 SubconsciousWorkerService 定时触发（每 12h 检查一次）。
    /// </summary>
    Task<PatternExtractionReport> ExtractPatternsAsync(
        string workspaceId,
        MemoryLlmConfig? memoryLlmConfig = null,
        CancellationToken ct = default);

    /// <summary>
    /// Skill 自改进：扫描 auto-generated 技能 → Flash LLM 评估 → 原地修补过时步骤。
    /// 由 SubconsciousWorkerService 定时触发（每 4h 检查一次）。版本号自动 +0.0.1。
    /// </summary>
    Task<SkillImprovementReport> ImproveSkillsAsync(
        string workspaceId,
        MemoryLlmConfig? memoryLlmConfig = null,
        CancellationToken ct = default);
}

public interface ISubconsciousJobQueue
{
    Task<SubconsciousJobQueueItem> EnqueueAsync(
        SubconsciousJobEnqueueRequest request,
        CancellationToken ct = default);

    Task<SubconsciousJobQueueItem?> LeaseNextAsync(
        string leaseOwner,
        TimeSpan leaseDuration,
        SubconsciousJobLeaseQuery? query = null,
        CancellationToken ct = default);

    Task<SubconsciousJobQueueStats> GetStatsAsync(CancellationToken ct = default);

    Task<SubconsciousJobQueueItem?> FindLatestAsync(
        SubconsciousJobLookupQuery query,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, int>> GetWorkspaceLeaseCountsAsync(
        DateTimeOffset since,
        CancellationToken ct = default);

    Task RecordSchedulingSkipAsync(
        SubconsciousSchedulingSkipRequest request,
        CancellationToken ct = default);

    Task RecordResultAsync(
        string jobId,
        string leaseOwner,
        SubconsciousJobResultEnvelope result,
        CancellationToken ct = default);

    Task<SubconsciousJobResultEnvelope?> GetResultAsync(
        string jobId,
        CancellationToken ct = default);

    Task CompleteAsync(
        string jobId,
        string leaseOwner,
        CancellationToken ct = default);

    Task<string> RetryAsync(
        string jobId,
        string leaseOwner,
        string error,
        TimeSpan? retryDelay = null,
        CancellationToken ct = default);

    Task DeadLetterAsync(
        string jobId,
        string leaseOwner,
        string error,
        CancellationToken ct = default);
}

public sealed record SubconsciousJobLookupQuery
{
    public string? JobId { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? SourceHookName { get; init; }
    public string? SourceCompactionId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? SessionId { get; init; }
}

public interface ISubconsciousRuntimeControl
{
    bool IsPaused { get; }

    Task<SubconsciousRuntimeControlSnapshot> StartAsync(
        SubconsciousRuntimeControlRequest request,
        CancellationToken ct = default);

    Task<SubconsciousRuntimeControlSnapshot> StopAsync(
        SubconsciousRuntimeControlRequest request,
        CancellationToken ct = default);

    Task<SubconsciousRuntimeControlSnapshot> GetSnapshotAsync(CancellationToken ct = default);
}

public interface ISubconsciousDiagnosticLog
{
    string? LogDirectory { get; }

    void Write(
        string name,
        IReadOnlyDictionary<string, object?> fields);
}
