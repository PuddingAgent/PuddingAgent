namespace PuddingRuntime.Services.AgentLoop;

/// <summary>
/// 主语干侧的唯一判定点：**模型说完成之后，要不要再来一轮**。
/// <para>
/// 为什么需要它：goal 线的其余部分都是纯逻辑，但没有一处能在执行循环里改变控制流 ——
/// 没有这一跳，决策再准也只能记日志。
/// </para>
/// <para>
/// 开关 <c>PUDDING_GOAL_CONTINUE=1</c> 默认关：关着时本类所有方法立即返回 false，
/// 执行循环行为与改动前逐字等价。
/// </para>
/// <para>
/// ⚠️ 已知架构债：这里用静态配置是因为 <c>AgentExecutionService</c> 的构造函数不在本仓
/// 可改范围内（partial 文件里没有 ctor）。正确做法是构造函数注入 —— 记在
/// `Docs/12_features/2026-11-01-goal续跑最后一跳方案与影响面.md` 的后续项里，不假装它很干净。
/// </para>
/// </summary>
public static class GoalContinuationGate
{
    private static GoalRegistry? _registry;

    /// <summary>
    /// 开关来源。默认读 <c>PUDDING_GOAL_CONTINUE=1</c>；做成可替换的委托是为了让测试
    /// 能验证「开启后的行为」，而不是只能测「关闭等于没改」。
    /// </summary>
    public static Func<bool> IsEnabled { get; set; } =
        () => string.Equals(Environment.GetEnvironmentVariable("PUDDING_GOAL_CONTINUE"), "1", StringComparison.Ordinal);

    /// <summary>当前是否开启。</summary>
    public static bool Enabled => IsEnabled();

    /// <summary>由 DI 在注册 goal 组件后调用一次。未调用时本门永远关闭。</summary>
    public static void Configure(GoalRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <summary>供测试重置（生产路径不调用）。</summary>
    internal static void Reset() => _registry = null;

    /// <summary>是否被配置过（诊断用）。</summary>
    public static bool IsConfigured => _registry is not null;

    /// <summary>
    /// 判定这一轮结束后是否继续。
    /// <para>
    /// 三重条件缺一不可：开关开 + 目标处于 active/armed + 账本还能准入一轮。
    /// 准入成功会**真的消耗**一次轮次额度 —— 这正是「只有进入历史的轮次才计数」的落点。
    /// </para>
    /// </summary>
    public static bool TryContinue(string sessionId, int round, out int goalRound)
    {
        goalRound = round;

        if (!Enabled || _registry is null || string.IsNullOrWhiteSpace(sessionId))
            return false;

        var goal = _registry.Get(sessionId);
        if (goal is null || !GoalLifecycle.CanAutoContinue(goal))
            return false;

        var ledger = _registry.GetLedger(sessionId);
        if (ledger is null)
            return false;

        // 先预约再准入：两条都必须成功，否则这次续跑不成立、也不消耗额度。
        var reservation = ledger.TryReserve($"round-{round + 1}", goal.Revision, DateTimeOffset.UtcNow);
        if (reservation is null)
            return false;

        if (!ledger.TryAdmit(reservation.ReservationId, goal.GoalId, goal.Revision))
            return false;

        goalRound = reservation.Round;
        return true;
    }
}
