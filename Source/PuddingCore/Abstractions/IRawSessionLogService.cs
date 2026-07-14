namespace PuddingCode.Abstractions;

/// <summary>
/// 原始会话日志查询服务。
/// <para>
/// 该服务提供证据层查询能力，面向 Agent 工具和 Admin 诊断复用。
/// 它查询未压缩的会话事件日志，不承担记忆提纯、摘要或知识管理职责。
/// </para>
/// </summary>
public interface IRawSessionLogService
{
    /// <summary>列出工作区内存在原始会话日志的日期，按日期倒序。</summary>
    Task<RawSessionLogDayList> ListDaysAsync(
        string workspaceId,
        string? fromDay = null,
        string? toDay = null,
        int limit = 31,
        string? agentInstanceId = null,
        CancellationToken ct = default);

    /// <summary>列出工作区某天的会话日志摘要。</summary>
    Task<RawSessionLogSessionList> ListSessionsAsync(
        string workspaceId,
        string day,
        int limit = 100,
        string? agentInstanceId = null,
        CancellationToken ct = default);

    /// <summary>在原始会话日志中搜索文本或正则，返回带证据坐标的片段。</summary>
    Task<RawSessionLogSearchResult> GrepAsync(
        RawSessionLogSearchRequest request,
        CancellationToken ct = default);

    /// <summary>在会话消息转录中搜索文本或正则，默认不展开 thinking/tool/raw event 帧。</summary>
    Task<RawSessionLogSearchResult> GrepMessagesAsync(
        RawSessionLogSearchRequest request,
        CancellationToken ct = default);

    /// <summary>读取面向 Agent 的会话消息转录，默认只返回用户消息和助手最终回复。</summary>
    Task<RawSessionLogMessagePage> ReadMessagesAsync(
        string workspaceId,
        string sessionId,
        string? agentInstanceId = null,
        long? before = null,
        int limit = 20,
        CancellationToken ct = default);

    /// <summary>按 sequence 游标读取指定会话的原始事件。</summary>
    Task<RawSessionLogReadResult> ReadSessionAsync(
        string workspaceId,
        string sessionId,
        long? afterSequence = null,
        int limit = 100,
        string? agentInstanceId = null,
        CancellationToken ct = default);

    /// <summary>按消息ID查询单条消息（ChatMessages.Id）。</summary>
    Task<RawSessionLogMessage?> GetMessageByIdAsync(
        string workspaceId,
        long messageId,
        CancellationToken ct = default);

    /// <summary>全文搜索引擎检索原始日志中的文本，返回带证据坐标的片段。</summary>
    Task<RawSessionLogSearchResult> GrepFtsAsync(
        RawSessionLogSearchRequest request,
        CancellationToken ct = default);
}

/// <summary>原始日志日期列表。</summary>
public sealed record RawSessionLogDayList(IReadOnlyList<RawSessionLogDaySummary> Days);

/// <summary>单日原始日志摘要。</summary>
public sealed record RawSessionLogDaySummary(
    string Day,
    int SessionCount,
    int EventCount);

/// <summary>某日会话列表。</summary>
public sealed record RawSessionLogSessionList(IReadOnlyList<RawSessionLogSessionSummary> Sessions);

/// <summary>会话原始日志摘要。</summary>
public sealed record RawSessionLogSessionSummary(
    string SessionId,
    string WorkspaceId,
    string Day,
    int EventCount,
    long FirstSequence,
    long LastSequence,
    string FirstRecordedAt,
    string LastRecordedAt);

/// <summary>原始日志搜索请求。</summary>
public sealed record RawSessionLogSearchRequest
{
    public string WorkspaceId { get; init; } = string.Empty;
    public string? AgentInstanceId { get; init; }
    public string Query { get; init; } = string.Empty;
    public string? Day { get; init; }
    public string? FromDay { get; init; }
    public string? ToDay { get; init; }
    public string? SessionId { get; init; }
    public bool Regex { get; init; }
    public int Limit { get; init; } = 20;
}

/// <summary>原始日志搜索结果。</summary>
public sealed record RawSessionLogSearchResult(
    IReadOnlyList<RawSessionLogMatch> Matches,
    bool HasMore);

/// <summary>原始日志搜索命中。</summary>
public sealed record RawSessionLogMatch(
    string SessionId,
    string WorkspaceId,
    string Day,
    long SequenceNum,
    string EventType,
    string RecordedAt,
    string Snippet,
    string EvidenceRef,
    string? FullContent = null);

/// <summary>面向 Agent 的会话消息转录页。</summary>
public sealed record RawSessionLogMessagePage(
    IReadOnlyList<RawSessionLogMessage> Messages,
    bool HasMore,
    long? NextCursor);

/// <summary>单条会话消息转录。</summary>
public sealed record RawSessionLogMessage(
    string MessageId,
    string SessionId,
    string WorkspaceId,
    string Role,
    string Content,
    string CreatedAt,
    string EvidenceRef,
    string EventType = "message");

/// <summary>原始会话事件读取结果。</summary>
public sealed record RawSessionLogReadResult(
    IReadOnlyList<RawSessionLogEvent> Events,
    bool HasMore,
    long? NextSequence);

/// <summary>原始会话事件。</summary>
public sealed record RawSessionLogEvent(
    string SessionId,
    string WorkspaceId,
    string Day,
    long SequenceNum,
    string EventType,
    string Data,
    string RecordedAt,
    string EvidenceRef);
