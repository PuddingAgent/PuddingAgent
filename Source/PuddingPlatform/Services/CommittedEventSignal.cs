using System.Collections.Concurrent;
using PuddingCode.Platform;

namespace PuddingPlatform.Services;

/// <summary>Monotonic commit heads close the read/subscribe race; every waiter wakes.</summary>
public sealed class CommittedEventSignal : ICommittedEventSignal
{
    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);
    private sealed class State
    {
        public long Head;
        public TaskCompletionSource Next = NewSignal();
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask WaitForChangeAsync(string conversationId, long knownHead, CancellationToken ct)
    {
        var state = _states.GetOrAdd(conversationId, _ => new State());
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task next;
            lock (state)
            {
                if (state.Head > knownHead) return;
                next = state.Next.Task;
            }
            await next.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public void Signal(string conversationId, long committedThroughSequence)
    {
        var state = _states.GetOrAdd(conversationId, _ => new State());
        TaskCompletionSource next;
        lock (state)
        {
            if (committedThroughSequence <= state.Head) return;
            state.Head = committedThroughSequence;
            next = state.Next;
            state.Next = NewSignal();
        }
        next.TrySetResult();
    }
}
