namespace PuddingApproval;

public sealed partial class ApprovalService
{
    /// <summary>Revoke only a permit that has not been consumed. This does not cancel or roll back an executing tool.</summary>
    public async Task<ApprovalResult> CancelAsync(string id, long version, ApprovalBinding binding,
        ApprovalCancellation command, CancellationToken ct = default)
    {
        ValidateBinding(binding);
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(command.CancellationId) || string.IsNullOrWhiteSpace(command.Actor))
            throw new ArgumentException("A stable cancellation ID and authenticated actor are required.");
        ct.ThrowIfCancellationRequested();
        var current = await store.ReadAsync(id, ct);
        if (current is null) return new(ApprovalOutcome.NotFound, null);
        current.ValidateOperation();
        if (current.Binding != binding) return new(ApprovalOutcome.BindingMismatch, current);
        if (current.Cancellation?.CancellationId == command.CancellationId)
            return new(current.Cancellation == command ? ApprovalOutcome.Replayed : ApprovalOutcome.Conflict, current);
        if (current.Version != version) return new(ApprovalOutcome.Conflict, current);
        if (current.State is not (ApprovalState.Pending or ApprovalState.Approved)) return new(ApprovalOutcome.Unavailable, current);
        if (current.ExpiresAt <= _clock.GetUtcNow()) return await ExpireAsync(current, ct);
        return await ReplaceAsync(current, current with
        {
            Version = checked(current.Version + 1), State = ApprovalState.Cancelled, Cancellation = command
        }, ApprovalOutcome.Applied, ct);
    }
}
