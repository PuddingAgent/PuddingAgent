namespace PuddingApproval;

public enum ApprovalState { Pending, Approved, Denied, Expired, Consumed, DispatchUnknown }
public enum HumanDecision { AllowOnce, Deny }
public enum ApprovalOutcome { Applied, Replayed, NotFound, Conflict, BindingMismatch, Expired, Unavailable }

/// <summary>The operation fingerprint covers normalized arguments, resources, definition and execution root.</summary>
public sealed record ApprovalBinding(string WorkspaceId, string AgentId, string SessionId, string RunId,
    string TurnId, string InvocationId, string OperationFingerprint, string PolicyRevision);
public sealed record DecisionCommand(string DecisionId, HumanDecision Decision, string Actor, string? Reason);
public sealed record ApprovalRecord(string Id, ApprovalBinding Binding, DateTimeOffset ExpiresAt,
    long Version = 0, ApprovalState State = ApprovalState.Pending, DecisionCommand? Decision = null);
public sealed record ApprovalResult(ApprovalOutcome Outcome, ApprovalRecord? Record);

/// <summary>Implementations must atomically replace an existing version. Production also persists the corresponding outbox in that transaction.</summary>
public interface IApprovalStore
{
    Task<ApprovalRecord?> ReadAsync(string id, CancellationToken ct);
    Task<bool> CompareExchangeAsync(string id, long expectedVersion, ApprovalRecord next, CancellationToken ct);
}

/// <summary>Human decisions for requests already admitted by Core as AwaitingHuman. This is not a policy bypass or executor.</summary>
public sealed class ApprovalService(IApprovalStore store, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<ApprovalResult> DecideAsync(string id, long version, ApprovalBinding binding,
        DecisionCommand command, CancellationToken ct = default)
    {
        ValidateBinding(binding);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(command.DecisionId) || string.IsNullOrWhiteSpace(command.Actor)
            || !Enum.IsDefined(command.Decision)) throw new ArgumentException("A valid decision, actor and stable decision ID are required.");
        var current = await store.ReadAsync(id, ct);
        if (current is null) return new(ApprovalOutcome.NotFound, null);
        if (current.Binding != binding) return new(ApprovalOutcome.BindingMismatch, current);
        if (current.Decision?.DecisionId == command.DecisionId)
            return new(current.Decision == command ? ApprovalOutcome.Replayed : ApprovalOutcome.Conflict, current);
        if (current.Version != version) return new(ApprovalOutcome.Conflict, current);
        if (current.State != ApprovalState.Pending) return new(ApprovalOutcome.Unavailable, current);
        if (current.ExpiresAt <= _clock.GetUtcNow()) return await ExpireAsync(current, ct);
        return await ReplaceAsync(current, current with
        {
            Version = checked(current.Version + 1), Decision = command,
            State = command.Decision == HumanDecision.AllowOnce ? ApprovalState.Approved : ApprovalState.Denied
        }, ApprovalOutcome.Applied, ct);
    }

    /// <summary>Call only at execution commit, after current hard boundaries pass. Only Applied grants permission to dispatch.</summary>
    public async Task<ApprovalResult> ConsumeAsync(string id, ApprovalBinding currentBinding, bool hardBoundariesPassed,
        CancellationToken ct = default)
    {
        ValidateBinding(currentBinding);
        var current = await store.ReadAsync(id, ct);
        if (current is null) return new(ApprovalOutcome.NotFound, null);
        if (current.Binding != currentBinding) return new(ApprovalOutcome.BindingMismatch, current);
        if (!hardBoundariesPassed || current.State != ApprovalState.Approved) return new(ApprovalOutcome.Unavailable, current);
        if (current.ExpiresAt <= _clock.GetUtcNow()) return await ExpireAsync(current, ct);
        return await ReplaceAsync(current, current with { Version = checked(current.Version + 1), State = ApprovalState.Consumed }, ApprovalOutcome.Applied, ct);
    }

    /// <summary>A committed dispatch with uncertain external effects must not become executable again automatically.</summary>
    public async Task<ApprovalResult> MarkDispatchUnknownAsync(string id, ApprovalBinding binding, CancellationToken ct = default)
    {
        ValidateBinding(binding);
        var current = await store.ReadAsync(id, ct);
        if (current is null) return new(ApprovalOutcome.NotFound, null);
        if (current.Binding != binding) return new(ApprovalOutcome.BindingMismatch, current);
        if (current.State != ApprovalState.Consumed) return new(ApprovalOutcome.Unavailable, current);
        return await ReplaceAsync(current, current with { Version = checked(current.Version + 1), State = ApprovalState.DispatchUnknown }, ApprovalOutcome.Applied, ct);
    }

    private Task<ApprovalResult> ExpireAsync(ApprovalRecord current, CancellationToken ct) => ReplaceAsync(current,
        current with { Version = checked(current.Version + 1), State = ApprovalState.Expired }, ApprovalOutcome.Expired, ct);
    private async Task<ApprovalResult> ReplaceAsync(ApprovalRecord current, ApprovalRecord next, ApprovalOutcome outcome, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (await store.CompareExchangeAsync(current.Id, current.Version, next, ct)) return new(outcome, next);
        return new(ApprovalOutcome.Conflict, await store.ReadAsync(current.Id, ct));
    }
    private static void ValidateBinding(ApprovalBinding binding)
    {
        if (new[] { binding.WorkspaceId, binding.AgentId, binding.SessionId, binding.RunId, binding.TurnId,
            binding.InvocationId, binding.OperationFingerprint, binding.PolicyRevision }.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Approval must bind a complete execution identity and operation/policy fingerprint.");
    }
}
