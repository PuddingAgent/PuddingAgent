using System.Collections.Concurrent;

namespace PuddingRuntime.Services.Messaging;

/// <summary>
/// Coordinates foreground chat turns with durable background message deliveries
/// for the same workspace agent.
/// </summary>
/// <remarks>
/// Runtime execution remains serialized by <c>IAgentExecutionStateRegistry</c>.
/// This coordinator supplies the missing admission priority: a foreground Turn
/// reserves the agent, preempts an interruptible background delivery, and keeps
/// recovery/idle drains from immediately reclaiming that delivery first.
/// </remarks>
public sealed class AgentExecutionAdmissionCoordinator
{
    private readonly ConcurrentDictionary<string, AdmissionState> _states =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Brief reservation used while a gateway delivery is handed off to the
    /// canonical Turn worker.
    /// </summary>
    public static readonly TimeSpan ForegroundHandoffDuration = TimeSpan.FromSeconds(5);

    public ForegroundExecutionLease AcquireForeground(string workspaceId, string agentId)
    {
        var key = Key(workspaceId, agentId);
        var state = _states.GetOrAdd(key, static _ => new AdmissionState());
        BackgroundExecutionLease? background;

        lock (state.Gate)
        {
            state.ForegroundCount++;
            state.ReservedUntilUtc = null;
            background = state.Background;
        }

        background?.Preempt();
        return new ForegroundExecutionLease(this, key, state);
    }

    public void ReserveForeground(
        string workspaceId,
        string agentId,
        TimeSpan? duration = null)
    {
        var key = Key(workspaceId, agentId);
        var state = _states.GetOrAdd(key, static _ => new AdmissionState());
        var until = DateTimeOffset.UtcNow.Add(duration ?? ForegroundHandoffDuration);
        BackgroundExecutionLease? background;

        lock (state.Gate)
        {
            if (state.ReservedUntilUtc is null || state.ReservedUntilUtc < until)
                state.ReservedUntilUtc = until;
            background = state.Background;
        }

        background?.Preempt();
    }

    public bool HasForegroundDemand(string workspaceId, string agentId)
    {
        if (!_states.TryGetValue(Key(workspaceId, agentId), out var state))
            return false;

        lock (state.Gate)
            return HasForegroundDemand(state, DateTimeOffset.UtcNow);
    }

    public bool CanStartBackground(string workspaceId, string agentId)
    {
        if (!_states.TryGetValue(Key(workspaceId, agentId), out var state))
            return true;

        lock (state.Gate)
            return !HasForegroundDemand(state, DateTimeOffset.UtcNow)
                   && state.Background is null;
    }

    public BackgroundExecutionLease? TryRegisterBackground(
        string workspaceId,
        string agentId,
        CancellationTokenSource cancellation)
    {
        var key = Key(workspaceId, agentId);
        var state = _states.GetOrAdd(key, static _ => new AdmissionState());

        lock (state.Gate)
        {
            if (HasForegroundDemand(state, DateTimeOffset.UtcNow)
                || state.Background is not null)
                return null;

            var lease = new BackgroundExecutionLease(this, key, state, cancellation);
            state.Background = lease;
            return lease;
        }
    }

    private static bool HasForegroundDemand(AdmissionState state, DateTimeOffset now)
    {
        if (state.ForegroundCount > 0)
            return true;

        if (state.ReservedUntilUtc is not { } reservedUntil)
            return false;

        if (reservedUntil > now)
            return true;

        state.ReservedUntilUtc = null;
        return false;
    }

    private void ReleaseForeground(string key, AdmissionState state)
    {
        lock (state.Gate)
        {
            if (state.ForegroundCount > 0)
                state.ForegroundCount--;
        }
    }

    private void ReleaseBackground(
        string key,
        AdmissionState state,
        BackgroundExecutionLease lease)
    {
        lock (state.Gate)
        {
            if (ReferenceEquals(state.Background, lease))
                state.Background = null;
        }
    }

    private static string Key(string workspaceId, string agentId) =>
        $"{workspaceId}\u001f{agentId}";

    internal sealed class AdmissionState
    {
        public object Gate { get; } = new();
        public int ForegroundCount { get; set; }
        public DateTimeOffset? ReservedUntilUtc { get; set; }
        public BackgroundExecutionLease? Background { get; set; }
    }

    public sealed class ForegroundExecutionLease : IDisposable
    {
        private AgentExecutionAdmissionCoordinator? _owner;
        private readonly string _key;
        private readonly AdmissionState _state;

        internal ForegroundExecutionLease(
            AgentExecutionAdmissionCoordinator owner,
            string key,
            AdmissionState state)
        {
            _owner = owner;
            _key = key;
            _state = state;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.ReleaseForeground(_key, _state);
    }

    public sealed class BackgroundExecutionLease : IDisposable
    {
        private AgentExecutionAdmissionCoordinator? _owner;
        private readonly string _key;
        private readonly AdmissionState _state;
        private readonly CancellationTokenSource _cancellation;
        private int _preempted;

        internal BackgroundExecutionLease(
            AgentExecutionAdmissionCoordinator owner,
            string key,
            AdmissionState state,
            CancellationTokenSource cancellation)
        {
            _owner = owner;
            _key = key;
            _state = state;
            _cancellation = cancellation;
        }

        public bool WasPreempted => Volatile.Read(ref _preempted) != 0;

        internal void Preempt()
        {
            if (Interlocked.Exchange(ref _preempted, 1) != 0)
                return;

            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The background execution completed between selection and preemption.
            }
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseBackground(_key, _state, this);
        }
    }
}
