using System.Threading.Channels;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;

namespace PuddingCode.Abstractions;

/// <summary>
/// 会话状态管理器 — 执行引擎与所有客户端之间的唯一中间层。
/// 
/// 三大职责：
///   1. 持久化事件日志 — append-only SQLite 存储所有执行帧
///   2. 实时推送通道 — Channel per session，生命周期独立于 HTTP 连接
///   3. 子代理状态追踪 — 跨父/子会话的状态协调
///
/// 设计原则：
///   · 执行引擎不感知客户端（前端/移动端/桌面端）
///   · 所有客户端通过此接口获取实时事件和历史事件
///   · Channel 生命周期 = 会话完全关闭，而非 HTTP 连接断开
/// 
/// 关联 ADR：Docs/07架构/16会话状态层与客户端解耦ADR.md
/// </summary>
public interface ISessionStateManager
{
    // ════════════════════════════════════════════════════════
    // 事件追加（append-only）
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 向会话事件日志追加一帧。返回全局递增序列号。
    /// 同时写入 SQLite（持久化）和内存 Channel（实时推送）。
    /// </summary>
    Task<long> AppendAsync(
        string sessionId, string workspaceId,
        ServerSentEventFrame frame,
        CancellationToken ct = default,
        RuntimeTraceContext? trace = null,
        string? component = null,
        string? operation = null);

    // ════════════════════════════════════════════════════════
    // 历史加载（分页/游标）
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 从指定序列号向前加载 N 条事件。
    /// fromSequence=null 表示从最新事件开始（获取最新 N 条）。
    /// fromSequence&gt;0 表示加载序列号 &lt;= fromSequence 的事件（加载更早的）。
    /// </summary>
    Task<SessionEventPage> GetEventsAsync(
        string sessionId,
        long? fromSequence = null,
        int limit = 50,
        CancellationToken ct = default);

    /// <summary>
    /// 获取会话中指定序列号之后的事件总数（用于增量加载判断）。
    /// </summary>
    Task<long> GetEventCountAfterAsync(string sessionId, long afterSequence, CancellationToken ct = default);

    /// <summary>
    /// 获取会话当前最大序列号（用于 eventCursor）。
    /// </summary>
    Task<long> GetLatestSequenceNumAsync(string sessionId, CancellationToken ct = default);

    // ════════════════════════════════════════════════════════
    // 实时订阅
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 获取会话的实时事件 Channel Reader。不存在则创建。
    /// Channel 生命周期由内部状态机管理，不随调用者释放而销毁。
    /// 返回 null 表示会话已完全关闭且 Channel 已清理。
    /// </summary>
    ChannelReader<ServerSentEventFrame>? Subscribe(string sessionId);

    /// <summary>
    /// 释放一次实时事件订阅。订阅者断开后应调用，避免无效 Channel 持续占用内存。
    /// </summary>
    void Unsubscribe(string sessionId, ChannelReader<ServerSentEventFrame> reader);

    /// <summary>
    /// 订阅工作区级别的通知 Channel（所有会话的摘要事件）。
    /// </summary>
    ChannelReader<SessionNotification> SubscribeWorkspace(string workspaceId);

    /// <summary>
    /// 释放一次工作区通知订阅。订阅者断开后应调用，避免无效 Channel 持续占用内存。
    /// </summary>
    void UnsubscribeWorkspace(string workspaceId, ChannelReader<SessionNotification> reader);

    // ════════════════════════════════════════════════════════
    // 会话状态
    // ════════════════════════════════════════════════════════

        /// <summary>获取会话当前运行时状态。</summary>
    Task<SessionState> GetSessionStateAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// 启动恢复时设置会话状态（仅在 SessionStateStore.LoadFromDisk 恢复时调用）。
    /// 与正常状态变更不同，此方法直接注入状态不触发事件。
    /// </summary>
    void Restore(string sessionId, SessionState state);

    // ════════════════════════════════════════════════════════
    // 子代理追踪
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 将子代理当前状态投影为 running。
    /// 首次启动创建记录；复用同一 SubSessionId 时原子重置上一轮终态。
    /// 每次执行的不可变历史由独立 runId 记录。
    /// </summary>
    Task TrackSubAgentStartAsync(
        string parentSessionId, SubAgentSpawnInfo info,
        CancellationToken ct = default);

    /// <summary>追踪子代理完成。</summary>
    Task TrackSubAgentCompleteAsync(
        string subSessionId, SubAgentResult result,
        CancellationToken ct = default);

    /// <summary>获取会话的所有子代理状态（含运行中和已完成的）。</summary>
    Task<IReadOnlyList<SubAgentStatus>> GetSubAgentsAsync(
        string sessionId, CancellationToken ct = default);

    /// <summary>获取会话中正在运行的子代理数量。</summary>
    Task<int> GetRunningSubAgentCountAsync(string parentSessionId, CancellationToken ct = default);

    // ════════════════════════════════════════════════════════
    // 会话重放（ARCH-SESSION-003）
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 从指定序列号开始重放会话事件，用于前端完整重建会话状态。
    /// fromSequenceNum=null 表示从第一条事件开始。
    /// 同时返回当前会话状态和子代理列表。
    /// </summary>
    Task<SessionReplayResult> ReplaySessionAsync(
        string sessionId,
        long? fromSequenceNum = null,
        int limit = 200,
        CancellationToken ct = default);

    // ════════════════════════════════════════════════════════
    // 生命周期标记
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 标记主代理流式执行结束（done 帧已发送）。
    /// 不销毁 Channel — 异步子代理可能还在运行。
    /// </summary>
    Task MarkStreamCompleteAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// 标记会话完全关闭（无更多事件将产生，所有子代理完成）。
    /// 启动 Channel 清理倒计时（TTL 后可销毁）。
    /// </summary>
    Task MarkSessionClosedAsync(string sessionId, CancellationToken ct = default);

    // ════════════════════════════════════════════════════════
    // 一致性检查（ARCH-SESSION-002）
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 检查 SQLite 事件日志与 JSONL 文件的一致性。
    /// 比较 SQLite 中的事件计数与 JSONL 文件中的行数，返回差异报告。
    /// </summary>
    Task<SessionConsistencyReport> CheckConsistencyAsync(string sessionId, CancellationToken ct = default);

    // ════════════════════════════════════════════════════════
    // Trace 聚合（ARCH-SESSION-004）
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 获取会话级 Trace 聚合报告。
    /// 从 session_event_log 中查询该会话的所有事件，按 traceId 和 component 聚合。
    /// </summary>
    /// <param name="includeSubAgents">是否递归包含子代理的 Token 统计（默认 false，仅统计本会话）</param>
    Task<SessionTraceReport> GetTraceReportAsync(string sessionId, bool includeSubAgents = false, CancellationToken ct = default);
}

// ════════════════════════════════════════════════════════════
// DTO
// ════════════════════════════════════════════════════════════

/// <summary>会话事件分页结果。</summary>
public sealed record SessionEventPage
{
    /// <summary>事件列表（按序列号升序）。</summary>
    public required IReadOnlyList<SessionEventEntry> Events { get; init; }

    /// <summary>是否还有更早的事件可加载。</summary>
    public bool HasMore { get; init; }

    /// <summary>本页最小序列号（用于下次加载 from=minSeq-1）。</summary>
    public long MinSequence { get; init; }

    /// <summary>本页最大序列号。</summary>
    public long MaxSequence { get; init; }

    /// <summary>会话总事件数。</summary>
    public long TotalCount { get; init; }
}

/// <summary>事件日志中的单条事件。</summary>
public sealed record SessionEventEntry
{
    public required long SequenceNum { get; init; }
    public required string EventType { get; init; }
    public required string Data { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
}

/// <summary>会话运行时状态。</summary>
public enum SessionState
{
    /// <summary>会话已创建，尚未启动。</summary>
    Created,

    /// <summary>会话正在运行。</summary>
    Running,

    /// <summary>等待用户输入或确认。</summary>
    WaitingForUser,

    /// <summary>等待工具调用返回。</summary>
    WaitingForTool,

    /// <summary>用户或系统暂停。</summary>
    Paused,

    /// <summary>正在停止。</summary>
    Stopping,

    /// <summary>已停止。</summary>
    Stopped,

    /// <summary>异常熔断。</summary>
    Faulted,

    /// <summary>正在恢复。</summary>
    Recovering,

    /// <summary>正常完成。</summary>
    Completed,

    /// <summary>被强制终止。</summary>
    Terminated,

    /// <summary>会话已销毁。</summary>
    Destroyed,

    /// <summary>主代理正在流式执行。</summary>
    Streaming = Running,

    /// <summary>主代理流式已完成，可能有异步子代理在运行。</summary>
    StreamCompleted = Completed,

    /// <summary>会话完全关闭，无更多事件将产生。</summary>
    Closed = Stopped
}

/// <summary>工作区级通知。</summary>
public sealed record SessionNotification
{
    public required string Type { get; init; }
    public required string SessionId { get; init; }
    public required string WorkspaceId { get; init; }
    public string? AgentId { get; init; }
    public string? SessionTitle { get; init; }
    public object? Data { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>子代理创建信息。</summary>
public sealed record SubAgentSpawnInfo
{
    public required string SubSessionId { get; init; }
    public required string ParentSessionId { get; init; }
    public string? ParentAgentId { get; init; }
    public string? TemplateId { get; init; }
    public string? ModelId { get; init; }
    public required string TaskSummary { get; init; }
    public required DateTimeOffset SpawnedAt { get; init; }
}

/// <summary>子代理完成结果。</summary>
public sealed record SubAgentResult
{
    public required bool Success { get; init; }
    public string? Reply { get; init; }
    public string? Error { get; init; }
    public TokenUsageDto? Usage { get; init; }
    /// <summary>
    /// 子代理运行期间失败的工具调用数。
    ///
    /// 这是基础设施事实，不由 Agent 自然语言回复推断；用于避免“最终有文本回复”
    /// 被上层误当成“子代理任务成功”。
    /// </summary>
    public int ToolFailureCount { get; init; }
    /// <summary>子代理运行期间检测到被工具层截断的输出数量。</summary>
    public int ToolOutputTruncatedCount { get; init; }
    /// <summary>子代理工具输出和错误文本的原始字符数合计。</summary>
    public long ToolOutputChars { get; init; }
    /// <summary>首个工具失败摘要，供状态面板和父 Agent 通知展示。</summary>
    public string? ToolFailureSummary { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
}

/// <summary>
/// 会话重放结果 — 包含从指定序列号开始的事件、当前会话状态和子代理列表。
/// 用于前端从任意点完整重建会话状态（ARCH-SESSION-003）。
/// </summary>
public sealed record SessionReplayResult
{
    public required string SessionId { get; init; }
    public required string CurrentState { get; init; }
    public required IReadOnlyList<SessionEventEntry> Events { get; init; }
    public long TotalEventCount { get; init; }
    public bool HasMore { get; init; }
    public required IReadOnlyList<SubAgentStatus> SubAgents { get; init; }
}

/// <summary>子代理当前状态（查询用）。</summary>
public sealed record SubAgentStatus
{
    public required string SubSessionId { get; init; }
    public required string Status { get; init; }
    public string? TemplateId { get; init; }
    public string? ModelId { get; init; }
    public required string TaskSummary { get; init; }
    public required DateTimeOffset SpawnedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? ResultSummary { get; init; }
    public bool? Success { get; init; }
    /// <summary>子代理 Token 用量摘要（如不可用则为 null）。</summary>
    public SubAgentTokenSummary? TokenSummary { get; init; }
}

/// <summary>子代理 Token 用量摘要。</summary>
public sealed record SubAgentTokenSummary
{
    public long TotalTokens { get; init; }
    public long CacheHitTokens { get; init; }
    public long CacheMissTokens { get; init; }
    public decimal TotalCost { get; init; }
    public int RequestCount { get; init; }
}

// ════════════════════════════════════════════════════════════
// ARCH-SESSION-002: 双写一致性
// ════════════════════════════════════════════════════════════

/// <summary>
/// SQLite 与 JSONL 双写一致性检查报告。
/// 关联 ADR：Docs/07架构/20会话状态机与事件规范ADR.md §6
/// </summary>
public sealed record SessionConsistencyReport
{
    public required string SessionId { get; init; }
    public long SqliteEventCount { get; init; }
    public long JsonlLineCount { get; init; }
    public bool IsConsistent { get; init; }
    public long Difference { get; init; }
    public string? Details { get; init; }
}

// ════════════════════════════════════════════════════════════
// ARCH-SESSION-004: Trace 聚合
// ════════════════════════════════════════════════════════════

/// <summary>
/// 会话级 Trace 聚合报告。
/// 从 session_event_log 查询该会话的所有事件，按 traceId 和 component 聚合。
/// 关联 ADR：Docs/07架构/20会话状态机与事件规范ADR.md §6
/// </summary>
public sealed record SessionTraceReport
{
    public required string SessionId { get; init; }
    public required IReadOnlyList<string> TraceIds { get; init; }
    public required IReadOnlyList<ComponentTimelineEntry> ComponentTimeline { get; init; }
    public required IReadOnlyList<LlmCallEntry> LlmCalls { get; init; }
    public required IReadOnlyList<ToolCallEntry> ToolCalls { get; init; }
    public required IReadOnlyList<SubAgentTraceEntry> SubAgents { get; init; }
    public long TotalDurationMs { get; init; }
    public long TotalTokens { get; init; }
}

/// <summary>按组件分组的事件时序条目。</summary>
public sealed record ComponentTimelineEntry
{
    public required string Component { get; init; }
    public required string Operation { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public long? DurationMs { get; init; }
}

/// <summary>LLM 调用聚合条目。</summary>
public sealed record LlmCallEntry
{
    public string? Model { get; init; }
    public string? Endpoint { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? DurationMs { get; init; }
}

/// <summary>工具调用聚合条目。</summary>
public sealed record ToolCallEntry
{
    public required string ToolName { get; init; }
    public bool Success { get; init; }
    public long? DurationMs { get; init; }
}

/// <summary>子代理 Trace 条目（含父子关系）。</summary>
public sealed record SubAgentTraceEntry
{
    public required string SubAgentId { get; init; }
    public required string Status { get; init; }
    public long? DurationMs { get; init; }
    public string? ParentExecutionId { get; init; }
}
