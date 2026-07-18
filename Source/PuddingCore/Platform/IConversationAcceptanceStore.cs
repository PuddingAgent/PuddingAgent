namespace PuddingCode.Platform;

/// <summary>
/// ADR-059: 原子受理存储 — 同一数据库事务提交 Message + Batch + Turn + Command + turn.accepted Event + Conversation Head。
/// 替代分散调用的 Message Store + Command Store + Event Store 模式。
/// </summary>
public interface IConversationAcceptanceStore
{
    /// <summary>
    /// 原子受理批次：幂等检查 → 写 Message → 写 Batch → 为每个 Agent 写 Turn+Command → 写 turn.accepted Event → 更新 Head → 提交。
    /// </summary>
    /// <param name="request">提交请求。</param>
    /// <param name="workspaceId">工作区 ID。</param>
    /// <param name="conversationId">会话 ID（路由参数传入）。</param>
    /// <param name="userId">当前用户标识。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>受理结果：ConversationId、MessageId、TurnIds[]、CommandIds[]、AcceptedSequence（真实 sequence）。</returns>
    Task<AcceptanceResult> AcceptBatchAsync(
        SubmitTurnRequest request,
        string workspaceId,
        string conversationId,
        string? userId,
        CancellationToken ct);
}
