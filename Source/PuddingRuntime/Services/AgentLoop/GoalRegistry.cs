namespace PuddingRuntime.Services.AgentLoop;

/// <summary>一个会话当前的目标槽位：状态 + 账本。</summary>
public sealed record GoalSlot(GoalState State, GoalRoundLedger Ledger);

/// <summary>
/// 目标的**进程内注册表**：把生命周期与轮次账本按会话绑在一起。
/// <para>
/// 为什么需要这一层：`GoalLifecycle` 与 `GoalRoundLedger` 都是纯状态机，
/// 但"某个 session 现在是什么目标"必须有人记着，否则 `GoalLoopDecider` 拿不到输入。
/// 这里刻意**只做进程内**：持久化是另一件事（见差距对照文档的存算分离），
/// 而且进程内恰好实现了"重启后 activation 回到 disarmed"——
/// 重启即丢失，activation 自然归零。
/// </para>
/// <para>
/// 线程安全：用锁保护字典与槽位内的状态替换。状态本身是不可变 record，
/// 所以读取方拿到的是快照，不会读到半更新的值。
/// </para>
/// </summary>
public sealed class GoalRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GoalSlot> _slots = new(StringComparer.Ordinal);

    /// <summary>为会话创建一个目标。已存在时抛错 —— 一个会话同时只有一个目标。</summary>
    public GoalState Create(string sessionId, string objective, int maxRounds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (_gate)
        {
            if (_slots.ContainsKey(sessionId))
                throw new InvalidOperationException($"session '{sessionId}' already has a goal; edit or clear it first.");

            var state = GoalLifecycle.Create($"goal-{sessionId}", objective, maxRounds);
            _slots[sessionId] = new GoalSlot(state, new GoalRoundLedger(state.GoalId, maxRounds));
            return state;
        }
    }

    /// <summary>读取会话当前目标；没有则 null。</summary>
    public GoalState? Get(string sessionId)
    {
        lock (_gate)
            return _slots.TryGetValue(sessionId, out var slot) ? slot.State : null;
    }

    /// <summary>读取会话当前账本；没有则 null。</summary>
    public GoalRoundLedger? GetLedger(string sessionId)
    {
        lock (_gate)
            return _slots.TryGetValue(sessionId, out var slot) ? slot.Ledger : null;
    }

    public GoalState Pause(string sessionId) => Mutate(sessionId, GoalLifecycle.Pause);

    public GoalState Resume(string sessionId) => Mutate(sessionId, GoalLifecycle.Resume);

    public GoalState Complete(string sessionId, IReadOnlyList<GoalEvidence> evidence) =>
        Mutate(sessionId, s => GoalLifecycle.Complete(s, evidence));

    public GoalState Block(string sessionId, string code, string message) =>
        Mutate(sessionId, s => GoalLifecycle.Block(s, code, message));

    /// <summary>清空目标（连同账本）。</summary>
    public bool Clear(string sessionId)
    {
        lock (_gate)
            return _slots.Remove(sessionId);
    }

    /// <summary>
    /// 进程重启：把所有目标打回 disarmed，**phase 与轮次保持**。
    /// 这个方法是幂等的，可以放心在启动路径反复调用。
    /// </summary>
    public int ApplyRestart()
    {
        lock (_gate)
        {
            var count = 0;
            foreach (var key in _slots.Keys.ToList())
            {
                var slot = _slots[key];
                _slots[key] = slot with { State = GoalLifecycle.AfterRestart(slot.State) };
                count++;
            }

            return count;
        }
    }

    private GoalState Mutate(string sessionId, Func<GoalState, GoalState> operation)
    {
        lock (_gate)
        {
            if (!_slots.TryGetValue(sessionId, out var slot))
                throw new InvalidOperationException($"session '{sessionId}' has no goal.");

            var next = operation(slot.State);
            _slots[sessionId] = slot with { State = next };
            return next;
        }
    }
}
