namespace PuddingDesktop.Core;

/// <summary>
/// A bounded startup-lease renewal emitted by a Desktop-managed Core while it
/// is still initializing. It is not a Ready signal and never bypasses the
/// subsequent /health/ready gate.
/// </summary>
public sealed record CoreStartupProgressMessage
{
    public required int ProtocolVersion { get; init; }
    public required int ProcessId { get; init; }
    public required long Sequence { get; init; }
    public required string Phase { get; init; }
    public long ElapsedMilliseconds { get; init; }

    internal sealed record RawProgressMessage
    {
        public int ProtocolVersion { get; init; }
        public int ProcessId { get; init; }
        public long Sequence { get; init; }
        public string? Phase { get; init; }
        public long ElapsedMilliseconds { get; init; }
    }
}
