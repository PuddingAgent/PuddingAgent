namespace PuddingRuntime.Services.AgentLoop;

/// <summary>一次轮次预约。绑定 {goalId, revision, round}，三者任一不符即作废。</summary>
public sealed record GoalRoundReservation(
    string ReservationId,
    string GoalId,
    long Revision,
    int Round,
    DateTimeOffset ReservedAt);

/// <summary>
/// goal 轮次账本（纯状态机，无 IO）。
/// <para>
/// 对齐 DeepSeek Harness `goal-round-driver` 的记账口径，核心只有一条：
/// **只有真正进入模型历史的 goal round 才消耗轮次编号**。
/// 人类消息、过期预约、被竞争作废的预约都不计数。
/// </para>
/// <para>
/// 准入校验与上游一致地要求 **round == roundsStarted + 1**：
/// 只比 revision 挡不住「同一 revision 下的重复投递或跳号 prompt」，
/// 而轮号连续性检查能同时挡住这两者。
/// </para>
/// </summary>
public sealed class GoalRoundLedger
{
    /// <summary>轮次耗尽时记录的稳定阻塞码（对齐 DeepSeek 的 `round-limit`）。</summary>
    public const string RoundLimitBlockerCode = "round-limit";

    private readonly Dictionary<string, GoalRoundReservation> _pending = new(StringComparer.Ordinal);
    private readonly List<string> _blockers = [];

    public GoalRoundLedger(string goalId, int maxRounds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);
        GoalId = goalId;
        MaxRounds = Math.Max(0, maxRounds);
    }

    public string GoalId { get; }

    public int MaxRounds { get; }

    /// <summary>已经**真正进入历史**的轮次数。预约不算。</summary>
    public int RoundsStarted { get; private set; }

    public IReadOnlyList<string> Blockers => _blockers;

    public IReadOnlyCollection<GoalRoundReservation> PendingReservations => _pending.Values;

    public bool IsExhausted => RoundsStarted >= MaxRounds;

    /// <summary>下一个合法轮号（无预约时为 RoundsStarted + 1）。</summary>
    public int NextRound => RoundsStarted + 1;

    /// <summary>
    /// 预约下一轮。轮次已耗尽时**不预约**，并记录一次 `round-limit` 阻塞。
    /// 轮号固定取 <see cref="NextRound"/>，调用方不能自行指定。
    /// </summary>
    public GoalRoundReservation? TryReserve(string reservationId, long revision, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);

        if (IsExhausted)
        {
            RecordRoundLimitBlocker();
            return null;
        }

        if (_pending.TryGetValue(reservationId, out var existing))
            return existing;

        var reservation = new GoalRoundReservation(reservationId, GoalId, revision, NextRound, now);
        _pending[reservationId] = reservation;
        return reservation;
    }

    /// <summary>
    /// 准入：确认预约仍然有效才计入轮次。返回 false 表示这次预约作废（不消耗额度）。
    /// <para>作废的四种情形：预约不存在、目标已切换、revision 已变、轮号不再连续。</para>
    /// </summary>
    public bool TryAdmit(string reservationId, string currentGoalId, long currentRevision)
    {
        if (!_pending.TryGetValue(reservationId, out var reservation))
            return false;

        var stale = !string.Equals(reservation.GoalId, currentGoalId, StringComparison.Ordinal)
                    || reservation.Revision != currentRevision
                    // 对齐上游：source.round 必须等于 roundsStarted + 1。
                    // 挡住同一 revision 下的重复投递与跳号 prompt。
                    || reservation.Round != NextRound;

        if (stale)
        {
            // 过期预约：不计数，也不记为阻塞（这是正常的编辑/切换，不是额度上限）。
            _pending.Remove(reservationId);
            return false;
        }

        _pending.Remove(reservationId);
        RoundsStarted++;
        return true;
    }

    /// <summary>释放预约但不计数（人类插队、取消、竞争落败）。</summary>
    public bool Release(string reservationId) => _pending.Remove(reservationId);

    /// <summary>目标被编辑后，把所有旧 revision 的预约作废。返回作废数量。</summary>
    public int InvalidateStale(long currentRevision)
    {
        var stale = _pending.Values.Where(r => r.Revision != currentRevision).Select(r => r.ReservationId).ToList();
        foreach (var id in stale)
            _pending.Remove(id);
        return stale.Count;
    }

    /// <summary>把同一阻塞码去重记录（避免每轮都追加一条）。</summary>
    private void RecordRoundLimitBlocker()
    {
        if (!_blockers.Contains(RoundLimitBlockerCode))
            _blockers.Add(RoundLimitBlockerCode);
    }
}
