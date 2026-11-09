using System.Collections.Immutable;
using PuddingAgents.Contracts;
using Pudding.Api.Contracts;
using BackendCancelEffect = PuddingAgents.Contracts.CancelWorkEffect;
using PublicCancelEffect = Pudding.Api.Contracts.CancelWorkEffect;

namespace PuddingAgents;

/// <summary>
/// PA-02A work control: observe binds an exact operation id and event cursor; cancel is a persistent business
/// command whose replay returns the original receipt (Requested never means terminated).
/// </summary>
public sealed class AgentWorkControlService
{
    private readonly IWorkEventSource _events;
    private readonly IWorkCancellationGateway _gateway;
    private readonly ICancelOperationStore _operations;

    public AgentWorkControlService(IWorkEventSource events, IWorkCancellationGateway gateway,
        ICancelOperationStore operations)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    public async ValueTask<WorkObservation> ObserveAsync(AgentBinding binding, ObserveWorkRequest request,
        CancellationToken ct = default)
    {
        AgentOwnershipPolicy.EnsureWorkspace(binding, request?.Agent?.WorkspaceId);
        if (!AgentBinding.Valid(request.OperationId)) throw Error(AgentErrorCodes.RequestInvalid, "A bounded operation id is required.");
        if (request.AfterSequence < 0 || request.MaxCount is < 1 or > 200)
            throw Error(AgentErrorCodes.RequestInvalid, "The observation cursor or page size is out of bounds.");
        var page = await _events.ReadAsync(binding.WorkspaceId, request.OperationId, request.AfterSequence,
            request.MaxCount, ct).ConfigureAwait(false);
        return new(request.OperationId, page.Status,
            page.Events.Select(e => new WorkEvent(e.Sequence, e.Kind, e.OccurredAt, e.ReasonCode, e.PayloadRef, e.PayloadHash))
                .ToImmutableArray(),
            page.NextSequence, page.Complete, page.SnapshotRequired, page.OldestAvailableSequence);
    }

    public async ValueTask<CancelWorkReceipt> CancelAsync(AgentBinding binding, CancelWorkRequest request,
        CancellationToken ct = default)
    {
        AgentOwnershipPolicy.EnsureWorkspace(binding, request?.Agent?.WorkspaceId);
        if (!AgentBinding.Valid(request.OperationId) || !AgentBinding.Valid(request.TargetOperationId))
            throw Error(AgentErrorCodes.RequestInvalid, "Bounded cancel and target operation ids are required.");
        if (request.ExpectedRevision < 0 || !AgentBinding.Valid(request.Reason))
            throw Error(AgentErrorCodes.RequestInvalid, "A non-negative expected revision and a reason are required.");
        var fingerprint = $"{request.TargetOperationId}|{request.ExpectedRevision}|{request.Reason}";
        var existing = await _operations.GetAsync(request.OperationId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                throw Error(AgentErrorCodes.OperationConflict,
                    "The cancel operation id was already used with different content.");
            return ToPublic(request, existing);
        }
        var outcome = await _gateway.CancelAsync(binding.WorkspaceId, request.TargetOperationId, request.Reason, ct)
            .ConfigureAwait(false);
        var record = new CancelCommandRecord(request.OperationId, fingerprint, outcome.Effect, outcome.Revision,
            outcome.CurrentStatus);
        await _operations.SaveAsync(record, ct).ConfigureAwait(false);
        return ToPublic(request, record);
    }

    private static CancelWorkReceipt ToPublic(CancelWorkRequest request, CancelCommandRecord record) =>
        new(request.OperationId, request.TargetOperationId, MapEffect(record.Effect), record.Revision, record.CurrentStatus);

    private static PublicCancelEffect MapEffect(BackendCancelEffect effect) => effect switch
    {
        BackendCancelEffect.Requested => PublicCancelEffect.Requested,
        BackendCancelEffect.AlreadyTerminal => PublicCancelEffect.AlreadyTerminal,
        BackendCancelEffect.NotFound => PublicCancelEffect.NotFound,
        BackendCancelEffect.Forbidden => PublicCancelEffect.Forbidden,
        _ => PublicCancelEffect.NotFound,
    };

    private static AgentBackendException Error(string code, string message) => new(code, message);
}
