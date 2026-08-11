using System.Collections.Concurrent;
using PuddingCode.Orchestration;

namespace PuddingRuntime.Services;

/// <summary>
/// Process-local MOA run store used until the durable platform store is introduced. Updates are
/// compare-and-swap operations over immutable snapshots, so concurrent dispatchers cannot both
/// persist a claim derived from the same version.
/// </summary>
public sealed class InMemorySubAgentOrchestrationRunStore : ISubAgentOrchestrationRunStore
{
    private readonly ConcurrentDictionary<string, SubAgentOrchestrationRunSnapshot> _runs =
        new(StringComparer.Ordinal);

    public Task<SubAgentOrchestrationRunSnapshot?> GetAsync(
        string runId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ct.ThrowIfCancellationRequested();
        _runs.TryGetValue(runId.Trim(), out var snapshot);
        return Task.FromResult(snapshot);
    }

    public Task<SubAgentOrchestrationStoreWriteResult> TryCreateAsync(
        SubAgentOrchestrationRunSnapshot snapshot,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ct.ThrowIfCancellationRequested();

        var created = _runs.TryAdd(snapshot.RunId, snapshot);
        _runs.TryGetValue(snapshot.RunId, out var current);
        return Task.FromResult(new SubAgentOrchestrationStoreWriteResult
        {
            Status = created
                ? SubAgentOrchestrationStoreWriteStatus.Succeeded
                : SubAgentOrchestrationStoreWriteStatus.AlreadyExists,
            CurrentSnapshot = current
        });
    }

    public Task<SubAgentOrchestrationStoreWriteResult> TryUpdateAsync(
        SubAgentOrchestrationRunSnapshot snapshot,
        long expectedVersion,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ct.ThrowIfCancellationRequested();

        if (!_runs.TryGetValue(snapshot.RunId, out var current))
        {
            return Task.FromResult(new SubAgentOrchestrationStoreWriteResult
            {
                Status = SubAgentOrchestrationStoreWriteStatus.NotFound
            });
        }

        if (current.Version != expectedVersion)
        {
            return Task.FromResult(new SubAgentOrchestrationStoreWriteResult
            {
                Status = SubAgentOrchestrationStoreWriteStatus.VersionConflict,
                CurrentSnapshot = current
            });
        }

        if (snapshot.Version != expectedVersion + 1)
        {
            throw new ArgumentException(
                "An updated orchestration snapshot must advance Version by exactly one.",
                nameof(snapshot));
        }

        var updated = _runs.TryUpdate(snapshot.RunId, snapshot, current);
        _runs.TryGetValue(snapshot.RunId, out var latest);
        return Task.FromResult(new SubAgentOrchestrationStoreWriteResult
        {
            Status = updated
                ? SubAgentOrchestrationStoreWriteStatus.Succeeded
                : SubAgentOrchestrationStoreWriteStatus.VersionConflict,
            CurrentSnapshot = latest
        });
    }
}
