using Pudding.Api.Contracts;
using PuddingAgentWork.Contracts;

namespace PuddingPlatform.Services.AgentWork;

/// <summary>Host adapter for canonical Work Observe/Cancel (no parallel work state).</summary>
public interface IAgentWorkControlBackend
{
    Task<WorkObservation> ObserveAsync(WorkCaller caller, AgentRef agent, string operationId, long afterSequence,
        int maxCount, CancellationToken ct);
    Task<WorkCancelBackendResult> CancelAsync(WorkCaller caller, AgentRef agent, string operationId,
        string targetOperationId, long expectedRevision, string reason, CancellationToken ct);
}

/// <summary>
/// PA-02 I2 control API: Observe reads the canonical receipt/events for one addressed Agent; Cancel submits the canonical
/// cancellation control command and reports Requested only (never claims the provider stopped).
/// </summary>
public sealed class PlatformAgentWorkControlApi(IAgentWorkControlBackend backend, WorkCaller caller, AgentRef scope)
    : IAgentWorkControlApi
{
    public Task<WorkObservation> ObserveAsync(ObserveWorkRequest request, CancellationToken ct = default)
    {
        if (request is null || request.Agent is null || !Bounded(request.Agent.WorkspaceId)
            || !Bounded(request.Agent.AgentId) || !Bounded(request.OperationId) || request.AfterSequence < 0
            || request.MaxCount is < 1 or > 256)
            throw new AgentWorkControlException("request.invalid", "A bounded observe request is required.");
        if (request.Agent != scope)
            throw new AgentWorkControlException("permission.denied", "The bound caller cannot observe another Agent.");
        return backend.ObserveAsync(caller, scope, request.OperationId, request.AfterSequence, request.MaxCount, ct);
    }

    public Task<CancelWorkReceipt> CancelAsync(CancelWorkRequest request, CancellationToken ct = default)
    {
        if (request is null || request.Agent is null || !Bounded(request.OperationId)
            || !Bounded(request.TargetOperationId) || request.ExpectedRevision < 0 || !Bounded(request.Reason))
            throw new AgentWorkControlException("request.invalid", "A bounded cancel request is required.");
        if (request.Agent != scope)
            throw new AgentWorkControlException("permission.denied", "The bound caller cannot cancel another Agent.");
        return CancelCoreAsync(request, ct);
    }

    private async Task<CancelWorkReceipt> CancelCoreAsync(CancelWorkRequest request, CancellationToken ct)
    {
        var result = await backend.CancelAsync(caller, scope, request.OperationId, request.TargetOperationId,
            request.ExpectedRevision, request.Reason, ct).ConfigureAwait(false);
        var effect = result.Effect switch
        {
            "terminal" => CancelWorkEffect.AlreadyTerminal,
            "not_found" => CancelWorkEffect.NotFound,
            "forbidden" => CancelWorkEffect.Forbidden,
            _ => CancelWorkEffect.Requested,
        };
        return new(request.OperationId, request.TargetOperationId, effect, result.Revision, result.CurrentStatus);
    }

    private static bool Bounded(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
}
