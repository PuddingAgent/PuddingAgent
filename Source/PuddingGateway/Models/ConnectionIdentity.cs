namespace PuddingGateway.Models;

/// <summary>连接身份信息 — 携带渠道和用户标识。</summary>
public sealed record ConnectionIdentity
{
    public string ConnectorId { get; init; } = "";
    public string SourceType { get; init; } = "";
    public string? AuthenticatedUser { get; init; }
    public string? AuthMethod { get; init; } // "sm2" | "whitelist" | "none"
    public DateTimeOffset AuthenticatedAt { get; init; } = DateTimeOffset.UtcNow;
}
