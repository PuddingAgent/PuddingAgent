using System.Text.Json;

namespace PuddingBrowser.Protocol;

public enum BrowserBridgeMessageKind
{
    Hello,
    HelloAck,
    Command,
    CommandResult,
    Cancel,
    Event,
    Heartbeat,
    HeartbeatAck
}

public sealed record BrowserBridgeEnvelope
{
    public int ProtocolVersion { get; init; } = BrowserBridgeProtocol.CurrentVersion;
    public required Guid MessageId { get; init; }
    public Guid? CorrelationId { get; init; }
    public required BrowserBridgeMessageKind Kind { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required JsonElement Payload { get; init; }
}
