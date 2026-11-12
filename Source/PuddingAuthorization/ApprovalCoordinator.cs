using PuddingAuthorization.Contracts;

namespace PuddingAuthorization;

public sealed record ApprovalRequest(string OperationId, string Action, string Resource, string ArgumentHash,
    long PolicyVersion, TimeSpan Ttl);

/// <summary>
/// PA-05A approval lifecycle: requests are idempotent by OperationId, only a host/system identity may decide,
/// decisions are CAS on the approval revision, and an expired or non-pending approval never becomes a grant.
/// </summary>
public sealed class ApprovalCoordinator
{
    private readonly IApprovalStore _store;
    private readonly TimeProvider _clock;

    public ApprovalCoordinator(IApprovalStore store, TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? TimeProvider.System;
    }

    public async ValueTask<ApprovalRecord> RequestAsync(ApprovalBinding binding, ApprovalRequest request,
        CancellationToken ct = default)
    {
        ValidateBinding(binding);
        if (request is null || !AuthorizationRequest.Valid(request.OperationId) || !AuthorizationRequest.Valid(request.Action)
            || !AuthorizationRequest.Valid(request.Resource) || !AuthorizationRequest.Valid(request.ArgumentHash)
            || request.PolicyVersion < 0 || request.Ttl < TimeSpan.FromSeconds(1) || request.Ttl > TimeSpan.FromHours(1))
            throw Error(AuthorizationErrorCodes.RequestInvalid, "A bounded approval request is required.");
        var existing = await _store.GetAsync(request.OperationId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            var same = string.Equals(existing.Action, request.Action, StringComparison.Ordinal)
                && string.Equals(existing.Resource, request.Resource, StringComparison.Ordinal)
                && string.Equals(existing.ArgumentHash, request.ArgumentHash, StringComparison.Ordinal)
                && existing.PolicyVersion == request.PolicyVersion;
            if (!same) throw Error(AuthorizationErrorCodes.ApprovalConflict,
                "The operation id was already used with a different approval request.");
            return existing;
        }
        var now = _clock.GetUtcNow();
        var record = new ApprovalRecord(request.OperationId, binding.WorkspaceId, binding.AgentId, request.Action,
            request.Resource, request.ArgumentHash, request.PolicyVersion, binding.RequestedBy, ApprovalState.Pending,
            now, now.Add(request.Ttl), null, Revision: 0);
        return await _store.CreateAsync(record, ct).ConfigureAwait(false);
    }

    public ValueTask<ApprovalRecord?> GetAsync(string approvalId, CancellationToken ct = default)
    {
        if (!AuthorizationRequest.Valid(approvalId)) throw Error(AuthorizationErrorCodes.RequestInvalid, "A bounded approval id is required.");
        return _store.GetAsync(approvalId, ct);
    }

    public async ValueTask<ApprovalRecord> DecideAsync(ApprovalBinding binding, string approvalId,
        ApprovalState decision, long expectedRevision, CancellationToken ct = default)
    {
        ValidateBinding(binding);
        if (binding.DeciderKind != PolicySourceKind.System)
            throw Error(AuthorizationErrorCodes.ApprovalForbidden, "Only a host/system identity may decide an approval.");
        if (decision is not (ApprovalState.Approved or ApprovalState.Denied))
            throw Error(AuthorizationErrorCodes.RequestInvalid, "A decision must be Approved or Denied.");
        if (!AuthorizationRequest.Valid(approvalId) || expectedRevision < 0)
            throw Error(AuthorizationErrorCodes.RequestInvalid, "A bounded approval id and revision are required.");
        var record = await _store.GetAsync(approvalId, ct).ConfigureAwait(false)
            ?? throw Error(AuthorizationErrorCodes.ApprovalMissing, "The approval is missing.");
        if (record.Revision != expectedRevision)
            throw Error(AuthorizationErrorCodes.ApprovalConflict, "The approval revision changed.");
        if (record.State != ApprovalState.Pending)
            throw Error(AuthorizationErrorCodes.ApprovalNotPending, "Only a pending approval can be decided.");
        var now = _clock.GetUtcNow();
        if (now >= record.ExpiresAt) throw Error(AuthorizationErrorCodes.ApprovalExpired, "The approval expired.");
        return await _store.DecideAsync(approvalId, decision, binding.ActorId, expectedRevision, now, ct)
            .ConfigureAwait(false);
    }

    private static void ValidateBinding(ApprovalBinding binding)
    {
        if (binding is null || !AuthorizationRequest.Valid(binding.WorkspaceId)
            || !AuthorizationRequest.Valid(binding.AgentId) || !AuthorizationRequest.Valid(binding.ActorId)
            || !AuthorizationRequest.Valid(binding.RequestedBy))
            throw Error(AuthorizationErrorCodes.RequestInvalid, "A host-bound approval identity is required.");
    }

    private static AuthorizationException Error(string code, string message) => new(code, message);
}
