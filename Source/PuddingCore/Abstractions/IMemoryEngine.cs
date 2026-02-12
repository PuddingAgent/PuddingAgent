namespace PuddingCode.Abstractions;

/// <summary>
/// 记忆引擎抽象。
/// Runtime 通过该接口消费记忆召回与写回能力，避免直接依赖实现细节。
/// </summary>
public interface IMemoryEngine
{
    /// <summary>
    /// 构建可注入系统提示词的记忆上下文。
    /// </summary>
    string? BuildMemoryContext(
        string sessionId,
        string? workspaceId,
        string? agentId,
        string? parentSessionId = null);

    /// <summary>
    /// 主动召回：基于用户消息进行意图理解和分层检索，返回可注入提示词的记忆片段。
    /// </summary>
    Task<string?> RecallWithIntentAsync(
        string userMessage,
        string workspaceId,
        string agentId,
        string? sessionId = null,
        int maxTokens = 2000,
        CancellationToken ct = default);

    /// <summary>
    /// 从回复文本中解析记忆标记并写回存储。
    /// </summary>
    void WriteBack(
        string llmReply,
        string sessionId,
        string? workspaceId,
        string source,
        string? agentId = null,
        string? parentSessionId = null);

    /// <summary>
    /// 清理 Session 级记忆。
    /// </summary>
    void ClearSession(string sessionId);
}
