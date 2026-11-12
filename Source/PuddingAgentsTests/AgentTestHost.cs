using System.Collections.Immutable;
using PuddingAgents.Contracts;

namespace PuddingAgentsTests;

/// <summary>Independent fake backends for the PA-02A ports. The lease pool performs acquire/release under one
/// lock so the atomic-ownership contract is testable without the real SubAgentPool.</summary>
public sealed class AgentTestHost : IAgentDirectoryStore, IWorkEventSource, IWorkCancellationGateway,
    ICancelOperationStore, IChildWorkSubmitter, ISubAgentLeasePool
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<AgentDirectoryRecord>> _agents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkState> _work = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancelCommandRecord> _cancelOperations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LeaseState> _leaseById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _leaseByOperation = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _activeLeaseByKey = new(StringComparer.Ordinal);
    private readonly List<ChildWorkCommand> _commands = [];
    private long _generation;

    public int CancelCalls { get; private set; }
    public WorkCancelOutcome? ForcedCancelOutcome { get; set; }
    public IReadOnlyList<ChildWorkCommand> SubmittedChildren { get { lock (_gate) return _commands.ToArray(); } }

    public void AddAgent(AgentDirectoryRecord record)
    {
        lock (_gate)
        {
            if (!_agents.TryGetValue(record.WorkspaceId, out var list)) _agents[record.WorkspaceId] = list = [];
            list.Add(record);
        }
    }

    public void AddWork(string workspaceId, string operationId, string status, params WorkEventRecord[] events)
    {
        lock (_gate)
            _work[workspaceId + "|" + operationId] = new WorkState(status, [.. events], 1);
    }

    public void SetRetention(string workspaceId, string operationId, long oldestAvailable)
    {
        lock (_gate)
        {
            var state = _work[workspaceId + "|" + operationId];
            _work[workspaceId + "|" + operationId] = state with { OldestAvailable = oldestAvailable };
        }
    }

    public ValueTask<AgentDirectoryQueryResult> ListAsync(string workspaceId, string? cursor, int maxCount,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_agents.TryGetValue(workspaceId, out var list))
                return ValueTask.FromResult(new AgentDirectoryQueryResult([], null, true));
            var ordered = list.OrderBy(a => a.AgentId, StringComparer.Ordinal).ToArray();
            var start = 0;
            if (cursor is not null)
            {
                var index = Array.FindIndex(ordered, a => string.Equals(a.AgentId, cursor, StringComparison.Ordinal));
                if (index < 0) throw new AgentBackendException(AgentErrorCodes.RequestInvalid, "Unknown cursor.");
                start = index + 1;
            }
            var page = ordered.Skip(start).Take(maxCount).ToArray();
            var complete = start + page.Length >= ordered.Length;
            return ValueTask.FromResult(new AgentDirectoryQueryResult([.. page],
                complete || page.Length == 0 ? null : page[^1].AgentId, complete));
        }
    }

    public ValueTask<AgentDirectoryRecord?> GetAsync(string workspaceId, string agentId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var record = _agents.TryGetValue(workspaceId, out var list)
                ? list.FirstOrDefault(a => string.Equals(a.AgentId, agentId, StringComparison.Ordinal))
                : null;
            return ValueTask.FromResult(record);
        }
    }

    public ValueTask<WorkEventPage> ReadAsync(string workspaceId, string operationId, long afterSequence, int maxCount,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_work.TryGetValue(workspaceId + "|" + operationId, out var state))
                throw new AgentBackendException(AgentErrorCodes.NotFound, "The work operation is missing.");
            if (afterSequence + 1 < state.OldestAvailable)
                return ValueTask.FromResult(new WorkEventPage([], afterSequence, state.Status, true, true,
                    state.OldestAvailable));
            var events = state.Events.Where(e => e.Sequence > afterSequence).OrderBy(e => e.Sequence)
                .Take(maxCount).ToImmutableArray();
            var next = events.IsEmpty ? afterSequence : events[^1].Sequence;
            return ValueTask.FromResult(new WorkEventPage(events, next, state.Status, true, false,
                state.OldestAvailable));
        }
    }

    public ValueTask<WorkCancelOutcome> CancelAsync(string workspaceId, string targetOperationId, string reason,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            CancelCalls++;
            return ValueTask.FromResult(ForcedCancelOutcome ?? new WorkCancelOutcome(CancelWorkEffect.Requested, 1, "Cancelling"));
        }
    }

    public ValueTask<CancelCommandRecord?> GetAsync(string operationId, CancellationToken ct = default)
    {
        lock (_gate)
            return ValueTask.FromResult(_cancelOperations.TryGetValue(operationId, out var record) ? record : null);
    }

    public ValueTask SaveAsync(CancelCommandRecord record, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _cancelOperations[record.OperationId] = record;
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask<DelegationResult> SubmitAsync(ChildWorkCommand command, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _commands.Add(command);
            return ValueTask.FromResult(new DelegationResult($"child-{_commands.Count}", command.ParentOperationId,
                "Accepted", DateTimeOffset.UtcNow));
        }
    }

    public ValueTask<LeaseAcquireResult> AcquireAsync(LeaseAcquireCommand command, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = command.WorkspaceId + "|" + command.AgentId;
            if (_leaseByOperation.TryGetValue(command.OperationId, out var existingId)
                && _leaseById.TryGetValue(existingId, out var existing) && existing.Active)
                return ValueTask.FromResult(new LeaseAcquireResult(LeaseAcquireStatus.Acquired, existingId,
                    existing.Generation, existing.ExpiresAt));
            if (_activeLeaseByKey.TryGetValue(key, out var activeId) && _leaseById[activeId].Active)
                return ValueTask.FromResult(new LeaseAcquireResult(LeaseAcquireStatus.Busy, null, 0, default));
            _generation++;
            var lease = new LeaseState(key, _generation, command.OwnerWorkId, command.Deadline, true);
            var leaseId = $"lease-{_generation}";
            _leaseById[leaseId] = lease;
            _activeLeaseByKey[key] = leaseId;
            _leaseByOperation[command.OperationId] = leaseId;
            return ValueTask.FromResult(new LeaseAcquireResult(LeaseAcquireStatus.Acquired, leaseId,
                lease.Generation, lease.ExpiresAt));
        }
    }

    public ValueTask<bool> ReleaseAsync(string leaseId, long generation, string ownerWorkId,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_leaseById.TryGetValue(leaseId, out var state) || !state.Active) return ValueTask.FromResult(false);
            if (state.Generation != generation
                || !string.Equals(state.OwnerWorkId, ownerWorkId, StringComparison.Ordinal))
                return ValueTask.FromResult(false);
            _leaseById[leaseId] = state with { Active = false };
            _activeLeaseByKey.Remove(state.Key);
            return ValueTask.FromResult(true);
        }
    }

    private sealed record WorkState(string Status, ImmutableArray<WorkEventRecord> Events, long OldestAvailable);

    private sealed record LeaseState(string Key, long Generation, string OwnerWorkId, DateTimeOffset ExpiresAt, bool Active);
}
