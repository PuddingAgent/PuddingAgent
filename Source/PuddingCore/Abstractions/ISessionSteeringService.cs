namespace PuddingCode.Abstractions;

/// <summary>
/// Result of consuming a pending steering message for injection into the agent loop.
/// </summary>
public sealed record ConsumedSessionSteeringMessage(
    string SteeringId,
    string WorkspaceId,
    string SessionId,
    string TargetTurnId,
    string? AgentId,
    string MessageText,
    int Priority,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ConsumedAtUtc,
    int Round);

/// <summary>
/// 入队一条 steering 消息。
/// <para>
/// <c>TargetTurnId</c> 必须与目标 loop 实际使用的 turn 对齐，否则消息会静默永不被消费
/// （不会报错，只是躺在表里等到 TTL 过期）。子代理的键是 (SubSessionId, 父 turn)，
/// 见 <c>SubAgentManager</c> 里 SessionId = subSessionId、TurnId = parent.TurnId 的赋值。
/// </para>
/// <para>
/// <c>SourceQueueItemId</c> 是幂等键：同一 (workspace, session, turn, source) 重复入队时
/// 返回既有条目而不是新建，便于调用方安全重试。
/// </para>
/// </summary>
public sealed record SessionSteeringEnqueueRequest(
    string WorkspaceId,
    string SessionId,
    string TargetTurnId,
    string? AgentId,
    string MessageText,
    string? SourceQueueItemId = null,
    string? CreatedBy = null,
    int Priority = 100);

/// <summary>
/// 入队结果。Status 是既有状态值（pending / consumed / expired …），
/// 调用方据此区分「新入队」与「幂等命中已有条目」，也据此判断是否已被消费。
/// </summary>
public sealed record SessionSteeringEnqueueResult(
    string SteeringId,
    string Status,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Manages session steering messages injected into the agent loop.
/// </summary>
public interface ISessionSteeringService
{
    Task<ConsumedSessionSteeringMessage?> ConsumeNextAsync(
        string sessionId,
        string? agentId,
        string targetTurnId,
        int round,
        CancellationToken ct = default);

    /// <summary>
    /// 入队一条等待注入的消息。
    /// 消费端在下一次 LLM 调用前按 (sessionId, agentId, turnId) 取出并作为 User 消息插入，
    /// 因此入队侧唯一要做对的事就是把这三个键写准。
    /// </summary>
    Task<SessionSteeringEnqueueResult> EnqueueAsync(
        SessionSteeringEnqueueRequest request,
        CancellationToken ct = default);
}
