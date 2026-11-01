namespace PuddingRuntime.Services.AgentLoop;

/// <summary>一次轮次预约。绑定 {goalId, revision}，过期即作废。</summary>
public sealed record GoalRoundReservation(string ReservationId, string GoalId, long Revision, DateTimeOffset ReservedAt);

/// <summary>
/// goal 轮次账本（纯状态机，无 IO）。
/// <para>
/// 对齐 DeepSeek Harness `goal-round-driver` 的记账口径，核心只有一条：
/// **只有真正进入模型历史的 goal round 才消耗轮次编号**。
/// 人类消息、过期预约、被竞争作废的预约都不计数。
/// </para>
/// <para>
/// 为什么不能"发起即计数"：那样一次被取消/被人类插队的预约就会吃掉额度，
/// 用户看到的是"上限到了"，而实际上模型一次都没跑。额度必须可审计。
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

    /// <summary>
    /// 预约下一轮。轮次已耗尽时**不预约**，并记录一次 `round-limit` 阻塞。
    /// </summary>
    public GoalRoundReservation? TryReserve(string reservationId, long revision, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);

        if (IsExhausted)
        {
            RecordRoundLimitBlocker();
            return null;
        }

        if (_pending.ContainsKey(reservationId))
            return _pending[reservationId];

        var reservation = new GoalRoundReservation(reservationId, GoalId, revision, now);
        _pending[reservationId] = reservation;
        return reservation;
    }

    /// <summary>
    /// 准入：确认预约仍然有效才计入轮次。返回 false 表示这次预约作废（不消耗额度）。
    /// <para>
    /// 作废的三种情形：预约不存在（已被释放/已被消费）、目标已切换、revision 已变（目标被编辑）。
    /// </para>
    /// </summary>
    public bool TryAdmit(string reservationId, string currentGoalId, long currentRevision)
    {
        if (!_pending.TryGetValue(reservationId, out var reservation))
            return false;

        if (!string.Equals(reservation.GoalId, currentGoalId, StringComparison.Ordinal)
            || reservation.Revision != currentRevision)
        {
            // 过期预约：不计数，也不留痕为阻塞（这是正常的编辑/切换，不是上限）。
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
