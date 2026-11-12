namespace PuddingWeightMemory;

/// <summary>
/// 一轮对话的可核对身份：会话、轮次、用户消息。
/// <para>
/// 所有权：这是"这条权重是由哪句用户话学来的"这一事实。组件用它做事件回执与去重
/// （同一消息对同一章节的第二条反馈不再训练），并随章节记录一起持久化，供回放核对。
/// </para>
/// </summary>
/// <param name="ConversationId">会话标识。</param>
/// <param name="TurnId">轮次标识。</param>
/// <param name="MessageId">该轮的用户消息标识。</param>
public sealed record TurnKey(string ConversationId, string TurnId, string MessageId);
