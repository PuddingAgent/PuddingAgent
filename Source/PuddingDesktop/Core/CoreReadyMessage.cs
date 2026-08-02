using System.Text.Json;

namespace PuddingDesktop.Core;

/// <summary>
/// Parsed PUDDING_DESKTOP_READY signal from Core stdout.
/// Format: PUDDING_DESKTOP_READY {"protocolVersion":1,"processId":1234,"baseAddress":"http://127.0.0.1:52137"}
/// </summary>
public sealed record CoreReadyMessage
{
    public int ProtocolVersion { get; init; } = 1;
    public required int ProcessId { get; init; }
    public required Uri BaseAddress { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    internal sealed record RawReadyMessage
    {
        public int ProtocolVersion { get; init; }
        public int ProcessId { get; init; }
        public string? BaseAddress { get; init; }
    }
}
