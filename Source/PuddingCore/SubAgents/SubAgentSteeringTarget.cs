namespace PuddingCode.SubAgents;

/// <summary>
/// 向一个子代理投递 steering 消息所需的定位三元组。
/// <para>
/// 子代理 loop 的消费键是 (<c>SubSessionId</c>, 父 turn)：<c>SubAgentManager</c> 用
/// <c>SessionId = subSessionId</c>、<c>TurnId = parent.TurnId</c> 构造执行请求。
/// 两边必须完全一致，否则消息不会报错、只是永远不被消费。
/// </para>
/// </summary>
public sealed record SubAgentSteeringTarget(string WorkspaceId, string SessionId, string TargetTurnId)
{
    /// <summary>
    /// 从 run manifest 推导定位三元组。
    /// <para>
    /// 任一必要字段缺失都返回 null（fail closed）。这里刻意不退化成
    /// 「投到父会话」或「投到任意 turn」——那会把消息注入到错误的主体上，
    /// 而调用方无法从回执上看出投错了。
    /// </para>
    /// </summary>
    public static SubAgentSteeringTarget? FromManifest(SubAgentRunManifest? manifest)
    {
        if (manifest is null)
            return null;
        if (string.IsNullOrWhiteSpace(manifest.WorkspaceId))
            return null;
        if (string.IsNullOrWhiteSpace(manifest.SubSessionId))
            return null;

        var turnId = manifest.ParentExecutionIdentity?.TurnId;
        if (string.IsNullOrWhiteSpace(turnId))
            return null;

        return new SubAgentSteeringTarget(
            manifest.WorkspaceId.Trim(),
            manifest.SubSessionId.Trim(),
            turnId.Trim());
    }
}
