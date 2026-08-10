using System.Collections.Concurrent;
using PuddingCode.Orchestration;

namespace PuddingPlatform.Services.Orchestration;

/// <summary>
/// Process-local broadcast wake-up signal backed by the durable orchestration event head.
/// The retained head closes the query/subscribe race; every waiter on the current generation wakes.
/// </summary>
public sealed class AgentOrchestrationCommittedEventSignal : IAgentOrchestrationCommittedEventSignal
{
    private readonly ConcurrentDictionary<string, SignalState> _states = new(StringComparer.Ordinal);

    public ValueTask WaitForChangeAsync(string runId, long knownHead, CancellationToken ct)
    {
        var state = _states.GetOrAdd(runId, _ => new SignalState());
        Task<long> waitTask;
        lock (state.Gate)
        {
            if (state.CommittedHead > knownHead)
                return ValueTask.CompletedTask;
            waitTask = state.NextChange.Task;
        }

        return new ValueTask(WaitAsync(waitTask, ct));
    }

    public void Signal(string runId, long committedThroughSequence)
    {
        var state = _states.GetOrAdd(runId, _ => new SignalState());
        TaskCompletionSource<long>? completed = null;
        lock (state.Gate)
        {
            if (committedThroughSequence <= state.CommittedHead)
                return;
            state.CommittedHead = committedThroughSequence;
            completed = state.NextChange;
            state.NextChange = CreateCompletionSource();
        }

        completed.TrySetResult(committedThroughSequence);
    }

    private static async Task WaitAsync(Task<long> waitTask, CancellationToken ct)
        => await waitTask.WaitAsync(ct);

    private static TaskCompletionSource<long> CreateCompletionSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class SignalState
    {
        public object Gate { get; } = new();
        public long CommittedHead { get; set; }
        public TaskCompletionSource<long> NextChange { get; set; } = CreateCompletionSource();
    }
}
