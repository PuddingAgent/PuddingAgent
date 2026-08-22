namespace PuddingCode.Security;

/// <summary>
/// ADR-075: 第三方 External Access Token 的跨层常量。
/// 只放与 ASP.NET/EF 无关的稳定事实；认证 Handler 与 Policy 实现位于 PuddingPlatform。
/// </summary>
public static class ExternalAccessTokenDefaults
{
    /// <summary>独立认证 scheme 名称。不得作为全局默认 scheme。</summary>
    public const string Scheme = "PuddingExternalAccessToken";

    /// <summary>wire 格式前缀：pdt_v1_&lt;keyId&gt;.&lt;secret&gt;</summary>
    public const string TokenPrefix = "pdt_v1_";

    /// <summary>canonical token 硬上限；解析前拒绝异常长 Header。</summary>
    public const int MaxCanonicalLength = 256;

    /// <summary>actor 类型 claim 值。</summary>
    public const string ActorType = "external_access_token";

    /// <summary>actorId 前缀：access-token:{tokenId}。</summary>
    public const string ActorIdPrefix = "access-token:";
}

/// <summary>External Access Token 相关 claim 名称。</summary>
public static class ExternalAccessTokenClaimNames
{
    public const string ActorType = "pudding.actor_type";
    public const string TokenId = "pudding.token_id";
    public const string OwnerUserId = "pudding.owner_user_id";
    public const string Scope = "pudding.scope";
    public const string Workspace = "pudding.workspace";
}

/// <summary>
/// Token 派生状态（存储层只保存 revoked/expires 等事实，状态由这些事实计算）。
/// OwnerDisabled 在认证时通过 join AppUsers 判定。
/// </summary>
public enum ExternalAccessTokenStatus
{
    Active = 0,
    Expired = 1,
    Revoked = 2,
    OwnerDisabled = 3,
}
