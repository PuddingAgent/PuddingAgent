namespace PuddingCode.Platform;

/// <summary>
/// 子代理会话标识生成器。
/// 会话标识属于父会话；每次执行历史使用独立 runId，不应通过创建空运行来获取标识。
/// </summary>
public static class SubAgentSessionId
{
    public static string Create(string parentSessionId)
    {
        if (string.IsNullOrWhiteSpace(parentSessionId))
            throw new ArgumentException("Parent session ID cannot be null or empty.", nameof(parentSessionId));

        return $"{parentSessionId}-sub-{Guid.NewGuid().ToString("N")[..8]}";
    }
}
