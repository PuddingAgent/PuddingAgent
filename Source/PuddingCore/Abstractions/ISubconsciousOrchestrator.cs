using PuddingCode.Platform;
using PuddingCode.Skills.Family;
using PuddingCode.Skills.Portfolio;

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
        string agentInstanceId,
        MemoryLlmConfig? memoryLlmConfig = null,
        CancellationToken ct = default);

    /// <summary>
    /// Skill 自改进：扫描 auto-generated 技能 → Flash LLM 评估 → 原地修补过时步骤。
    /// 由 SubconsciousWorkerService 定时触发（每 4h 检查一次）。版本号自动 +0.0.1。
    /// </summary>
    Task<SkillImprovementReport> ImproveSkillsAsync(
        string workspaceId,
        string agentInstanceId,
        MemoryLlmConfig? memoryLlmConfig = null,
        CancellationToken ct = default);

    /// <summary>
    /// Skill 组合治理：只读扫描启用技能 → 产出组合变化报告（N_before → N_after + 未降原因）。
    /// G6 只报告，不修改/不禁用/不删除任何技能（I3 零写盘）。
    /// 由 SubconsciousWorkerService 定时触发（skill.curate）。
    /// <para>
    /// G4-D7 起可传入家族策略以**额外**产出家族评审计数（<see cref="SkillCurationReport.FamilyReviewCount"/>）；
    /// 两个策略都是**尾随可选参数**，默认 <c>null</c> ⇒ 不划分家族、计数恒为 0（逐字段零回归）。
    /// </para>
    /// </summary>
    /// <param name="familyPolicy">
    /// G4 家族划分策略（可选）。与 <paramref name="portfolioPolicy"/> **必须同时给出**才启用家族评审：
    /// 任一为 <c>null</c> ⇒ 不划分家族（不产生“半套结论”）。
    /// </param>
    /// <param name="portfolioPolicy">G4 组合策略（<c>PerFamilyCap</c> 是家族内上限的唯一来源；该值为 <c>null</c> 表示不设限）。</param>
    Task<SkillCurationReport> SkillCurateAsync(
        string workspaceId,
        string agentInstanceId,
        MemoryLlmConfig? memoryLlmConfig = null,
        SkillFamilyPolicy? familyPolicy = null,
        SkillPortfolioPolicy? portfolioPolicy = null,
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
