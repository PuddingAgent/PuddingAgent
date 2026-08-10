using System.Runtime.CompilerServices;
using PuddingCode.Orchestration;

namespace PuddingPlatform.Services.Orchestration;

/// <summary>
/// Durable replay-to-live reader for orchestration events. The SQLite event log is the source of
/// truth; the process-local signal only closes the replay/subscribe race and wakes the reader.
/// </summary>
public sealed class AgentOrchestrationEventFollower(
    IAgentOrchestrationQueryStore store,
    IAgentOrchestrationCommittedEventSignal signal)
{
    private const int ReplayBatchSize = 250;

    public async IAsyncEnumerable<AgentOrchestrationRunEvent> FollowAsync(
        string runId,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (afterSequence < 0)
            throw new ArgumentOutOfRangeException(nameof(afterSequence), "afterSequence cannot be negative.");

        var normalizedRunId = runId.Trim();
        var cursor = afterSequence;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var run = await store.GetRunAsync(normalizedRunId, ct)
                ?? throw new AgentOrchestrationRunNotFoundException(normalizedRunId);
            var replayThrough = run.HeadSequence;

            while (cursor < replayThrough)
            {
                var events = await store.GetEventsAfterAsync(
                    normalizedRunId,
                    cursor,
                    ReplayBatchSize,
                    ct);
                if (events.Count == 0)
                {
                    throw new AgentOrchestrationEventGapException(
                        normalizedRunId,
                        cursor + 1,
                        replayThrough);
                }

                foreach (var envelope in events)
                {
                    if (envelope.Sequence <= cursor)
                        continue;
                    if (envelope.Sequence != cursor + 1)
                    {
                        throw new AgentOrchestrationEventGapException(
                            normalizedRunId,
                            cursor + 1,
                            envelope.Sequence);
                    }

                    cursor = envelope.Sequence;
                    yield return envelope;
                }
            }

            // Signal retains the committed high-water mark. If a commit happened after the run
            // snapshot or during replay, this returns immediately; otherwise it waits for live data.
            await signal.WaitForChangeAsync(normalizedRunId, cursor, ct);
        }
    }
}

public sealed class AgentOrchestrationRunNotFoundException(string runId)
    : Exception($"Orchestration run '{runId}' was not found.")
{
    public string RunId { get; } = runId;
}

public sealed class AgentOrchestrationEventGapException(
    string runId,
    long expectedSequence,
    long actualOrHeadSequence)
    : Exception(
        $"Orchestration run '{runId}' expected event sequence {expectedSequence}, " +
        $"but observed {actualOrHeadSequence}.")
{
    public string RunId { get; } = runId;
    public long ExpectedSequence { get; } = expectedSequence;
    public long ActualOrHeadSequence { get; } = actualOrHeadSequence;
}
