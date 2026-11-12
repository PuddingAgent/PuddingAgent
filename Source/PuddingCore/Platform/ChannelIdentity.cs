namespace PuddingCode.Platform;

/// <summary>
/// 渠道身份元数据。渠道只负责回答「这条消息是谁发的」，不参与授权：
/// 工具与数据权限的主体是 Agent —— 管理员把某个 Agent 绑到某个渠道，
/// 就等于决定该渠道能触达哪些能力；要对外公开就绑一个低权限 Agent。
/// </summary>
public static class ChannelIdentityMetadata
{
    public const string Channel = "channel_identity_channel";
    public const string ExternalUserId = "channel_identity_external_user_id";
    public const string UserId = "channel_identity_pudding_user_id";
    public const string Role = "channel_identity_pudding_role";
}

/// <summary>
/// 一条入站消息的发送者。未绑定的发送者只有外部 id；已绑定的额外携带内部用户与权限组。
/// </summary>
public sealed record ChannelIdentity(string Channel, string ExternalUserId, string? UserId = null, string? Role = null)
{
    /// <summary>
    /// 附加在消息正文前的可见前缀，让 Agent 不必从元数据反推就知道「现在是谁在说话」。
    /// </summary>
    public string Prefix => UserId is null
        ? $"[{Channel} user={ExternalUserId}]"
        : $"[{Channel} user={ExternalUserId} pudding={UserId} role={Role}]";
}
