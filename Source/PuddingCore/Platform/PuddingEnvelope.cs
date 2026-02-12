namespace PuddingCode.Platform;

/// <summary>统一入站消息信封，由 Gateway Adapter 产生。</summary>
public sealed record PuddingIngressEnvelope
{
    public string EnvelopeId { get; init; } = Guid.NewGuid().ToString("N");
    public required string ChannelId { get; init; }
    public required string ChannelType { get; init; }
    public required string UserExternalId { get; init; }
    public required string MessageText { get; init; }
    public string? MessageType { get; init; }
    public string? CorrelationId { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public Dictionary<string, string> Metadata { get; init; } = [];
}

/// <summary>统一出站回复信封，由 Controller 产生、Adapter 回写。</summary>
public sealed record PuddingEgressEnvelope
{
    public string EnvelopeId { get; init; } = Guid.NewGuid().ToString("N");
    public required string ChannelId { get; init; }
    public required string SessionId { get; init; }
    public required string ReplyText { get; init; }
    public string? CorrelationId { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public Dictionary<string, string> Metadata { get; init; } = [];
}
