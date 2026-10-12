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
        return new ForegroundExecutionLease(() => ReleaseForeground(key, state));
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

            BackgroundExecutionLease? lease = null;
            lease = new BackgroundExecutionLease(cancellation, () => ReleaseBackground(key, state, lease!));
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

    // ── R2：**会话键**的前后台准入（后台维护与下一条会话消息的直接冲突面） ──────────────
    // 为什么需要它：终态后的可选维护按 session 工作，而新消息受理时调用方拿到的是
    // (workspaceId, sessionId)；若只在 workspace+agent 键上注册，两边的键可能对不上，
    // 「新消息受理 ⇒ 立刻取消同会话可选工作」就会落空（复核 §3 探针）。
    private readonly ConcurrentDictionary<string, SessionAdmissionState> _sessionStates =
        new(StringComparer.Ordinal);

    private static string SessionKey(string workspaceId, string sessionId) =>
        $"{workspaceId}\u001f{sessionId}";

    /// <summary>会话级前台预留：新消息受理/预留时调用 ⇒ 立刻抢占同会话的后台可选工作。</summary>
    public void ReserveSessionForeground(
        string workspaceId,
        string sessionId,
        TimeSpan? duration = null)
    {
        var key = SessionKey(workspaceId, sessionId);
        var state = _sessionStates.GetOrAdd(key, static _ => new SessionAdmissionState());
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

    /// <summary>会话级前台租约（真实 Turn 执行期间持有）。</summary>
    public ForegroundExecutionLease AcquireSessionForeground(string workspaceId, string sessionId)
    {
        var key = SessionKey(workspaceId, sessionId);
        var state = _sessionStates.GetOrAdd(key, static _ => new SessionAdmissionState());
        BackgroundExecutionLease? background;

        lock (state.Gate)
        {
            state.ForegroundCount++;
            state.ReservedUntilUtc = null;
            background = state.Background;
        }

        background?.Preempt();
        return new ForegroundExecutionLease(() => ReleaseSessionForeground(key, state));
    }

    /// <summary>同会话是否仍有前台需求（维护启动前与提交前的重验依据）。</summary>
    public bool HasSessionForegroundDemand(string workspaceId, string sessionId)
    {
        if (!_sessionStates.TryGetValue(SessionKey(workspaceId, sessionId), out var state))
            return false;

        lock (state.Gate)
            return HasSessionForegroundDemand(state, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 注册会话级后台工作（可选维护）。有前台需求或已有同会话后台工作时返回 null。
    /// <paramref name="cancellation"/> 必须是**该维护自己的** CTS：前台预留/受理会取消它。
    /// </summary>
    public BackgroundExecutionLease? TryRegisterSessionBackground(
        string workspaceId,
        string sessionId,
        CancellationTokenSource cancellation)
    {
        var key = SessionKey(workspaceId, sessionId);
        var state = _sessionStates.GetOrAdd(key, static _ => new SessionAdmissionState());

        lock (state.Gate)
        {
            if (HasSessionForegroundDemand(state, DateTimeOffset.UtcNow)
                || state.Background is not null)
                return null;

            BackgroundExecutionLease? lease = null;
            lease = new BackgroundExecutionLease(cancellation, () => ReleaseSessionBackground(key, state, lease!));
            state.Background = lease;
            return lease;
        }
    }

    private static bool HasSessionForegroundDemand(SessionAdmissionState state, DateTimeOffset now)
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

    private void ReleaseSessionForeground(string key, SessionAdmissionState state)
    {
        lock (state.Gate)
        {
            if (state.ForegroundCount > 0)
                state.ForegroundCount--;
        }
    }

    private void ReleaseSessionBackground(
        string key,
        SessionAdmissionState state,
        BackgroundExecutionLease lease)
    {
        lock (state.Gate)
        {
            if (ReferenceEquals(state.Background, lease))
                state.Background = null;
        }
    }

    internal sealed class SessionAdmissionState
    {
        public object Gate { get; } = new();
        public int ForegroundCount { get; set; }
        public DateTimeOffset? ReservedUntilUtc { get; set; }
        public BackgroundExecutionLease? Background { get; set; }
    }

    internal sealed class AdmissionState
    {
        public object Gate { get; } = new();
        public int ForegroundCount { get; set; }
        public DateTimeOffset? ReservedUntilUtc { get; set; }
        public BackgroundExecutionLease? Background { get; set; }
    }

    public sealed class ForegroundExecutionLease : IDisposable
    {
        private Action? _onDispose;

        internal ForegroundExecutionLease(Action onDispose) => _onDispose = onDispose;

        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }

    public sealed class BackgroundExecutionLease : IDisposable
    {
        private Action? _onDispose;
        private readonly CancellationTokenSource _cancellation;
        private int _preempted;

        internal BackgroundExecutionLease(CancellationTokenSource cancellation, Action onDispose)
        {
            _cancellation = cancellation;
            _onDispose = onDispose;
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

        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }
}
