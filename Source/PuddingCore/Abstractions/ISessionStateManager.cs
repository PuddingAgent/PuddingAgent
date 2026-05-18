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
    /// 订阅工作区级别的通知 Channel（所有会话的摘要事件）。
    /// </summary>
    ChannelReader<SessionNotification> SubscribeWorkspace(string workspaceId);

    // ════════════════════════════════════════════════════════
    // 会话状态
    // ════════════════════════════════════════════════════════

    /// <summary>获取会话当前运行时状态。</summary>
    Task<SessionState> GetSessionStateAsync(string sessionId, CancellationToken ct = default);

    // ════════════════════════════════════════════════════════
    // 子代理追踪
    // ════════════════════════════════════════════════════════

    /// <summary>追踪子代理创建。</summary>
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
    /// <summary>主代理正在流式执行。</summary>
    Streaming,

    /// <summary>主代理流式已完成，可能有异步子代理在运行。</summary>
    StreamCompleted,

    /// <summary>会话完全关闭，无更多事件将产生。</summary>
    Closed
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
    public required DateTimeOffset CompletedAt { get; init; }
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
}
