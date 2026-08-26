using PuddingCode.Tasks;

namespace PuddingCode.Scheduling;

public enum ExecutionWindowVerdict
{
    Allow = 0,
    Defer = 1,
    Unknown = 2,
}

/// <summary>
/// Immutable result used by automatic admission. Unknown is intentionally
/// distinct from Defer: it means the provider/model price route cannot be
/// proven and automatic execution must fail closed.
/// </summary>
public sealed record ExecutionWindowDecision
{
    public required ExecutionWindowVerdict Verdict { get; init; }
    public required string Code { get; init; }
    public required DateTimeOffset EvaluatedAtUtc { get; init; }
    public required DateTimeOffset ValidUntilUtc { get; init; }
    public DateTimeOffset? NextEligibleAtUtc { get; init; }
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public string? WindowKey { get; init; }
    public string? ProfileVersion { get; init; }
    public bool IsUserOverride { get; init; }
}

public interface IExecutionWindowResolver
{
    Task<ExecutionWindowDecision> EvaluateAsync(
        string workspaceId,
        string agentId,
        TaskExecutionWindow requestedWindow,
        DateTimeOffset now,
        CancellationToken ct = default);
}
