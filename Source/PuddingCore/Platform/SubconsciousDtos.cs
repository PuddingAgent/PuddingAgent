using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCode.Platform;

/// <summary>
/// 潜意识整合任务。
/// </summary>
public record ConsolidationJob
{
    public required string SessionId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string AgentId { get; init; }
    public required string AgentTemplateId { get; init; }
    /// <summary>用户刚发送的消息文本（可选，避免 SessionId 跨系统映射问题）。</summary>
    public string? LastUserMessage { get; init; }
    /// <summary>Agent 的回复文本（可选，同上）。</summary>
    public string? LastAssistantReply { get; init; }
    /// <summary>会话压缩阶段显意识 LLM 提取的待保存记忆线索。</summary>
    public IReadOnlyList<string> MemoryNotes { get; init; } = [];
}

public sealed record SubconsciousMemoryScope
{
    public required string WorkspaceId { get; init; }
    public required string AgentId { get; init; }
    public string? AgentTemplateId { get; init; }
    public required string SessionId { get; init; }
    public string? MemoryLibraryId { get; init; }
}

public static class SubconsciousJobTypes
{
    public const string MemoryConsolidateSession = "memory.consolidate_session";
}

public sealed record SubconsciousJobEnqueueRequest
{
    public required string JobType { get; init; }
    public required string IdempotencyKey { get; init; }
    public string? SourceHookName { get; init; }
    public string? SourceEventId { get; init; }
    public string? SourceCompactionId { get; init; }
    public required ConsolidationJob Job { get; init; }
}

public sealed record SubconsciousJobQueueItem
{
    public required string JobId { get; init; }
    public required string JobType { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string Status { get; init; }
    public int RetryCount { get; init; }
    public string? LeaseOwner { get; init; }
    public long? LeaseUntil { get; init; }
    public string? SourceHookName { get; init; }
    public string? SourceEventId { get; init; }
    public string? SourceCompactionId { get; init; }
    public required ConsolidationJob Job { get; init; }
}

public static class SubconsciousJobResultKinds
{
    public const string MemoryMaintenancePlanDryRun = "memory_maintenance_plan.dry_run";
    public const string MemoryWikiPageUpdate = "memory_wiki_page_update.v1";
}

public static class SubconsciousJobResultStatuses
{
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string Quarantined = "quarantined";
}

public static class SubconsciousJobResultDecisions
{
    public const string AcceptForExecution = "accept_for_execution";
    public const string RejectComplete = "reject_complete";
    public const string RetryLater = "retry_later";
    public const string DeferForRecheck = "defer_for_recheck";
}

public static class SubconsciousJobResultNextActions
{
    public const string EnqueueForExecution = "enqueue_for_execution";
    public const string CompleteRejected = "complete_rejected";
    public const string RetryJob = "retry_job";
    public const string CompleteQuarantined = "complete_quarantined";
}

public sealed record SubconsciousJobResultEnvelope
{
    public string Schema { get; init; } = "pudding.subconscious_job_result.v1";
    public required string Kind { get; init; }
    public required string Status { get; init; }
    public string Decision { get; init; } = SubconsciousJobResultDecisions.RejectComplete;
    public string NextAction { get; init; } = SubconsciousJobResultNextActions.CompleteRejected;
    public string? PlanId { get; init; }
    public bool Valid { get; init; }
    public int OperationCount { get; init; }
    public int ErrorCount { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<string> ErrorCodes { get; init; } = [];
    public IReadOnlyList<MemoryWriteResultEnvelope> MemoryWriteResults { get; init; } = [];
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public static class SubconsciousSchedulingSkipReasons
{
    public const string Disabled = "skip_disabled";
    public const string DryRun = "would_lease";
    public const string ForegroundBusy = "skip_foreground_busy";
    public const string Cooldown = "skip_cooldown";
    public const string WorkspaceLimit = "skip_workspace_limit";
    public const string GlobalLimit = "skip_global_limit";
    public const string SessionLimit = "skip_session_limit";
    public const string BudgetExhausted = "skip_budget_exhausted";
    public const string BackoffNotElapsed = "skip_backoff_not_elapsed";
    public const string NoEligibleJob = "skip_no_eligible_job";
}

public sealed record SubconsciousJobLeaseQuery
{
    public IReadOnlySet<string> ExcludedWorkspaceIds { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlySet<string> ExcludedSessionIds { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    public int? MaxRetryCount { get; init; }
}

public sealed record SubconsciousJobQueueStats
{
    public int Pending { get; init; }
    public int Retrying { get; init; }
    public int Processing { get; init; }
    public int Completed { get; init; }
    public int DeadLetter { get; init; }

    public IReadOnlyDictionary<string, int> ProcessingByWorkspace { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, int> ProcessingBySession { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
}

public static class SubconsciousRuntimeStates
{
    public const string Running = "running";
    public const string Paused = "paused";
}

public sealed record SubconsciousRuntimeControlRequest
{
    public string? Reason { get; init; }
    public string? RequestedBy { get; init; }
}

public sealed record SubconsciousRuntimeControlSnapshot
{
    public required string State { get; init; }
    public bool IsPaused { get; init; }
    public string? LastCommand { get; init; }
    public string? Reason { get; init; }
    public string? RequestedBy { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public SubconsciousJobQueueStats QueueStats { get; init; } = new();
    public IReadOnlyDictionary<string, string> Scheduling { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> Diagnostics { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed record SubconsciousSchedulingSkipRequest
{
    public required string Reason { get; init; }
    public string? JobId { get; init; }
    public string? JobType { get; init; }
    public string? WorkspaceId { get; init; }
    public string? SessionId { get; init; }
    public string? AgentId { get; init; }
    public string? AgentTemplateId { get; init; }
    public string? SourceHookName { get; init; }
    public string? SourceEventId { get; init; }
    public string? SourceCompactionId { get; init; }

    public IReadOnlyDictionary<string, string> Details { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// 会话结构化摘要。
/// </summary>
public record SessionSummary
{
    public required string SessionId { get; init; }
    public string? Title { get; init; }
    public List<ExtractedFact> Facts { get; init; } = [];
    public List<ExtractedPreference> Preferences { get; init; } = [];
    public string? OneLineSummary { get; init; }
    public List<string> SuggestedTags { get; init; } = [];
}

/// <summary>
/// 抽取出的事实项。
/// </summary>
public record ExtractedFact
{
    public required string Statement { get; init; }
    public double Confidence { get; init; } = 0.8;
    public string? SourceMessageId { get; init; }
    /// <summary>事实类型：user|project|feedback|reference（Background Extractor 增强）</summary>
    public string? FactType { get; init; }
    /// <summary>简短标题（≤20字，Background Extractor 增强）</summary>
    public string? Title { get; init; }
    /// <summary>一句话概述（≤50字，Background Extractor 增强）</summary>
    public string? Summary { get; init; }
    /// <summary>推荐标签（Background Extractor 增强）</summary>
    public IReadOnlyList<string>? SuggestedTags { get; init; }
    /// <summary>是否来自 PreCompactFlush（Background Extractor 增强）</summary>
    public bool FromFlush { get; init; }
}

/// <summary>
/// 抽取出的偏好项。
/// </summary>
public record ExtractedPreference
{
    public required string Category { get; init; }
    public required string Key { get; init; }
    public required string Value { get; init; }
    public string? SourceMessageId { get; init; }
}

/// <summary>
/// 记忆仪表盘摘要。
/// </summary>
public record MemoryDashboard
{
    public int TotalBooks { get; init; }
    public int TotalChapters { get; init; }
    public int TotalFacts { get; init; }
    public int TotalPointers { get; init; }
    public DateTimeOffset? LastConsolidationAt { get; init; }
    public List<TagTreeNode> TopTags { get; init; } = [];
}

/// <summary>
/// 记忆搜索请求。
/// </summary>
public record MemorySearchRequest
{
    public string? WorkspaceId { get; init; }
    public string? Query { get; init; }
    public string? TagFilter { get; init; }
    public string? SortBy { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}

/// <summary>
/// 记忆搜索结果。
/// </summary>
public record MemorySearchResult
{
    public List<MemoryEntryDto> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Page { get; init; }
}

/// <summary>
/// 记忆条目 DTO。
/// </summary>
public record MemoryEntryDto
{
    public required string EntryId { get; init; }
    public required string EntryType { get; init; }
    public required string Title { get; init; }
    public required string Content { get; init; }
    public double Importance { get; init; }
    public string? SourceSessionId { get; init; }
    public List<string> Tags { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// 记忆专用 LLM 配置。
/// </summary>
public record MemoryLlmConfig(
    string? Endpoint,
    string? ApiKey,
    string? ModelId);
﻿
/// <summary>Auto-Dream 执行报告。由 AutoDreamAsync 返回。</summary>
public sealed record AutoDreamReport
{
    public long DurationMs { get; init; }
    public int Merged { get; init; }
    public int Archived { get; init; }
    public int Deleted { get; init; }
    public int Suggested { get; init; }
    public int Executed { get; init; }
    public string? Summary { get; init; }
    public DateTime Timestamp { get; init; }
}

/// <summary>给 Flash LLM 的记忆库快照。</summary>
public sealed record MemorySnapshot
{
    public int TotalBooks { get; init; }
    public int ActiveBooks { get; init; }
    public int ArchivedBooks { get; init; }
    public int TotalChapters { get; init; }
    public MemorySnapshotBook[] Books { get; init; } = [];
}

public sealed record MemorySnapshotBook
{
    public string BookId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Status { get; init; } = "";
    public string Summary { get; init; } = "";
    public int ChapterCount { get; init; }
    public DateTime? LastUpdated { get; init; }
    public string[] ChapterTitles { get; init; } = [];
}

// ============================================================
// 管道2：经验→SKILL — Pattern Extraction DTOs
// ============================================================

/// <summary>
/// 经验→SKILL 管道第1阶段：从会话中检测到的黄金路径候选。
/// </summary>
public sealed record PatternCandidate
{
    public string SessionId { get; init; } = "";
    /// <summary>简短描述（≤30字）</summary>
    public string Title { get; init; } = "";
    /// <summary>这个模式解决什么问题</summary>
    public string Goal { get; init; } = "";
    public int StepsCount { get; init; }
    public bool AllSucceeded { get; init; }
    public int RetryCount { get; init; }
    /// <summary>工具调用序列（按顺序）</summary>
    public string[] ToolSequence { get; init; } = [];
    /// <summary>用户纠正内容（如有）</summary>
    public string? UserCorrection { get; init; }
    /// <summary>置信度 0-1</summary>
    public double Confidence { get; init; }
    /// <summary>提取时的证据摘要</summary>
    public string? Evidence { get; init; }
}

/// <summary>3条件过滤的单一条件评估结果</summary>
public sealed record ConditionCheckResult
{
    public string ConditionName { get; init; } = "";
    public bool Passed { get; init; }
    public string? Reason { get; init; }
}

/// <summary>3条件过滤的完整评估结果</summary>
public sealed record CandidateEvaluation
{
    public bool Promoted { get; init; }
    public string Decision { get; init; } = "skip";  // promote | demote | skip
    public string? Reason { get; init; }
    public ConditionCheckResult[] Checks { get; init; } = [];
}

/// <summary>
/// 经验→SKILL 管道执行报告。
/// </summary>
public sealed record PatternExtractionReport
{
    public long DurationMs { get; init; }
    public int CandidatesFound { get; init; }
    public int Promoted { get; init; }
    public int DemotedToMemory { get; init; }
    public int Skipped { get; init; }
    public string[] CreatedSkillIds { get; init; } = [];
    public string? Summary { get; init; }
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// Skill 自改进评估结果（Flash LLM 分析单个技能后输出）。
/// </summary>
public sealed record SkillEvaluation
{
    public string SkillId { get; init; } = "";
    public string SkillName { get; init; } = "";
    public string CurrentVersion { get; init; } = "";
    public bool NeedsUpdate { get; init; }
    public string? Reason { get; init; }
    public string[] Findings { get; init; } = [];
}

/// <summary>
/// Skill 自改进执行报告。
/// 由 SubconsciousWorkerService 定时触发（每 4h 检查一次），
/// Flash LLM 评估 auto-generated 技能是否需要原地修补。
/// </summary>
public sealed record SkillImprovementReport
{
    public long DurationMs { get; init; }
    public int Evaluated { get; init; }
    public int Patched { get; init; }
    public int Skipped { get; init; }
    public string[] ImprovedSkillIds { get; init; } = [];
    public string? Summary { get; init; }
    public DateTime Timestamp { get; init; }
}
