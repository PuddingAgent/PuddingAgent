using PuddingAgents.Contracts;
using Pudding.Api.Contracts;

namespace PuddingAgents;

/// <summary>
/// PA-02A delegation: the parent identity always comes from the binding, child limits may only tighten, and
/// leases are acquired atomically through the pool port (never list-then-execute).
/// </summary>
public sealed class AgentDelegationService
{
    private readonly IChildWorkSubmitter _submitter;
    private readonly ISubAgentLeasePool _pool;
    private readonly TimeProvider _clock;

    public AgentDelegationService(IChildWorkSubmitter submitter, ISubAgentLeasePool pool, TimeProvider? clock = null)
    {
        _submitter = submitter ?? throw new ArgumentNullException(nameof(submitter));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _clock = clock ?? TimeProvider.System;
    }

    public async ValueTask<DelegationReceipt> SubmitChildAsync(AgentBinding binding, DelegateWorkRequest request,
        CancellationToken ct = default)
    {
        AgentOwnershipPolicy.EnsureBinding(binding);
        if (!AgentBinding.Valid(request?.OperationId) || !AgentBinding.Valid(request.ParentOperationId))
            throw Error(AgentErrorCodes.RequestInvalid, "Bounded parent and child operation ids are required.");
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 64_000)
            throw Error(AgentErrorCodes.RequestInvalid, "The delegated task text is out of bounds.");
        if (!string.Equals(request.ParentOperationId, binding.OwnerWorkId, StringComparison.Ordinal))
            throw Error(AgentErrorCodes.Forbidden, "The caller cannot delegate for another parent work.");
        if (!AgentBinding.Valid(request.DeliverMode))
            throw Error(AgentErrorCodes.RequestInvalid, "A bounded delivery mode is required.");
        var capabilities = AgentOwnershipPolicy.CheckCapabilities(binding, request.Capabilities);
        var maxOutput = AgentOwnershipPolicy.CheckChildOutputLimit(binding, request.MaxOutputTokens);
        var maxCost = AgentOwnershipPolicy.CheckChildCostLimit(binding, request.MaxCost);
        var result = await _submitter.SubmitAsync(new(request.OperationId, binding.OwnerWorkId, binding.WorkspaceId,
            binding.AgentId, request.Text, capabilities, maxOutput, maxCost, request.DeliverMode), ct)
            .ConfigureAwait(false);
        return new(request.OperationId, result.ChildOperationId, result.ParentOperationId, result.State,
            result.AcceptedAt);
    }

    public async ValueTask<SubAgentLease> AcquireAsync(AgentBinding binding, AcquireSubAgentRequest request,
        CancellationToken ct = default)
    {
        AgentOwnershipPolicy.EnsureWorkspace(binding, request?.WorkspaceId);
        if (!AgentBinding.Valid(request.OperationId) || !AgentBinding.Valid(request.AgentId))
            throw Error(AgentErrorCodes.RequestInvalid, "Bounded acquire and agent ids are required.");
        if (request.MaxWaitMilliseconds is < 0 or > 30_000)
            throw Error(AgentErrorCodes.RequestInvalid, "The acquire wait is out of bounds.");
        var capabilities = AgentOwnershipPolicy.CheckCapabilities(binding, request.RequiredCapabilities);
        var deadline = _clock.GetUtcNow().AddMilliseconds(request.MaxWaitMilliseconds);
        var result = await _pool.AcquireAsync(new(request.OperationId, request.WorkspaceId, request.AgentId,
            capabilities, binding.OwnerWorkId, binding.ParentMaxOutputTokens ?? 0, binding.ParentMaxCost ?? 0m,
            deadline), ct).ConfigureAwait(false);
        return result.Status switch
        {
            LeaseAcquireStatus.Acquired when result.LeaseId is not null => new(result.LeaseId, request.WorkspaceId,
                request.AgentId, result.Generation, binding.OwnerWorkId, result.ExpiresAt, "Active"),
            LeaseAcquireStatus.Busy => throw Error(AgentErrorCodes.LeaseBusy, "The sub-agent is already leased."),
            _ => throw Error(AgentErrorCodes.LeaseUnavailable, "No compatible sub-agent instance is available."),
        };
    }

    public async ValueTask ReleaseAsync(AgentBinding binding, ReleaseSubAgentRequest request,
        CancellationToken ct = default)
    {
        AgentOwnershipPolicy.EnsureBinding(binding);
        if (!AgentBinding.Valid(request?.LeaseId) || request.Generation < 1)
            throw Error(AgentErrorCodes.RequestInvalid, "A bounded lease id and generation are required.");
        if (!string.Equals(request.OwnerWorkId, binding.OwnerWorkId, StringComparison.Ordinal))
            throw Error(AgentErrorCodes.Forbidden, "The caller cannot release another owner's lease.");
        var released = await _pool.ReleaseAsync(request.LeaseId, request.Generation, binding.OwnerWorkId, ct)
            .ConfigureAwait(false);
        if (!released)
            throw Error(AgentErrorCodes.LeaseReleaseRejected,
                "The lease was already released, belongs to another owner, or the generation is stale.");
    }

    private static AgentBackendException Error(string code, string message) => new(code, message);
}
