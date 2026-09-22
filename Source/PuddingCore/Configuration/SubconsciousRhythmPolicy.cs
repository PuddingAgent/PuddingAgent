using PuddingCode.Operators;

namespace PuddingCode.Configuration;

/// <summary>
/// 潜意识周期作业的**节奏策略**（一等对象，RSI-G8-D1）：把「何时跑得更密」从**散落的 <c>if</c>** 
/// 变成可版本化、可审计、可被用例取红的判据。
/// <para>
/// <b>为什么是基础设施而不是一段 <c>if</c></b>：间隔阈值必须外置成类型事实。判定内**不得**出现裸阈值字面量
/// （宪法 C1），否则事后无法回答「这一轮为什么跑得这么密」，且自我改进候选可以悄悄移动判据。
/// 本类型照抄 <see cref="Skills.Portfolio.SkillPortfolioPolicy"/> 的形状
/// （<see cref="IVersionedCriterion"/> + <see cref="EnsureValid"/> + 校验式 <see cref="Create"/>），
/// 并<b>不新增阈值类型</b>。
/// </para>
/// <para>
/// ⚠️ <b>落点说明（D1 冻结）</b>：本类型是<b>纯判据</b>（无 IO、无 LLM、无写盘），
/// 因此 ① <b>不放 <c>PuddingRuntime</c></b> —— 那里无法被 <c>PuddingCoreTests</c> 直接引用；
/// ② <b>不放 <c>Skills/*</c></b> —— 节奏不是技能域判据；
/// ③ <b>不放 <c>Operators/</c></b> —— 那个目录受「算子架构门禁」扫描，塞非算子进去会稀释该门禁的语义。
/// 放在 <c>Configuration/</c> 是因为它与 <c>SubconsciousOptions</c> 同属潜意识配置域，且**不新建目录树**。
/// </para>
/// <para>
/// ⛔ <b>本切片（D1）只交付类型与纯判定，零行为接线</b>：策略的<b>消费</b>（判定"现在是否非工作时段"、接线、
/// 条件触发）属 D2–D5。因此本类型<b>刻意不提供</b> <c>ToApplied()</c> 一类的「已应用」快照 ——
/// 尚无消费者时产出快照，只会让读者以为节奏已被策略改变（幻影区间）。
/// </para>
/// <para>
/// ⭐ <b>语义冻结（RSI-G8 §11-1 二选一）</b>：非工作时段优先取 <b>(a) 窗口内加速 / 窗口外照旧</b>。
/// <list type="bullet">
/// <item>(b)「窗口外延后」会改变<b>入队时刻</b>，而入队幂等键由 <c>UtcTicks / interval.Ticks</c> 派生
/// （<c>SubconsciousWorkerService.EnqueuePeriodicJobAsync</c>）⇒ 延后策略与幂等语义的相容性<b>无法</b>在不引入
/// 新 bucket 口径的前提下证明；而新增 bucket 口径又直接违反不变式 P6。</item>
/// <item>且 (b) 存在"窗口永不到达 ⇒ 作业永不入队"的丢作业风险，与 P5（fail-closed，不得丢作业）冲突。</item>
/// </list>
/// ⇒ 依 §11-1「给不出不破坏幂等的证据就选 (a)」，冻结为 (a)。
/// </para>
/// </summary>
/// <remarks>
/// 采用间隔的判定序（fail-closed，永不延长）：
/// <list type="number">
/// <item>默认采用<b>配置间隔</b>（<c>SubconsciousSchedulingOptions</c> 的 8 个键之一）。</item>
/// <item>命中的候选间隔（非工作时段 / 条件触发）**只有严格更短时才生效**。</item>
/// <item>结论是 <see cref="SubconsciousRhythmDecision"/>：采用间隔 + 命中事实 + 绑定的理由码
/// （P8：无记录 ⇒ 用例红）。</item>
/// </list>
/// ⛔ <b>策略只能缩短、永不延长</b>：配置间隔是采用间隔的<b>上界</b>。若允许延长，
/// 一个配置笔误就能把作业从"每 4 小时"变成"每 8 小时"，而表面上看起来只是"策略生效了"。
/// </remarks>
public sealed record SubconsciousRhythmPolicy : IVersionedCriterion
{
    /// <summary>策略 id（连同版本落库，供事后解释「这一轮为什么是这个节奏」）。</summary>
    public required string PolicyId { get; init; }

    /// <summary>策略版本。策略被移动过时必须递增，历史结论才能自证用的是哪一版。</summary>
    public required int Version { get; init; }

    /// <summary>
    /// 是否启用「非工作时段优先」。
    /// <para>
    /// ⭐ <b>默认关闭，且关闭时不得改变任何行为</b>（P4）：为 <c>false</c> 时，
    /// <see cref="OffPeakWindowStart"/> / <see cref="OffPeakWindowEnd"/> / <see cref="OffPeakIntervalSeconds"/>
    /// 一律<b>不被消费</b>，<see cref="Resolve"/> 恒返回配置间隔。
    /// </para>
    /// </summary>
    public required bool OffPeakPriorityEnabled { get; init; }

    /// <summary>
    /// 非工作时段窗口起点（<b>本地</b>墙钟时间，须由调用方用 <c>TimeProvider.GetLocalNow()</c> 提供）。
    /// <para>⛔ 不得由 UTC ticks 反推本地小时（P3）；跨午夜窗口（如 22:00 → 08:00）是<b>合法</b>的常规情形。</para>
    /// <para><c>null</c> = 未配置。启用非工作时段优先时窗口<b>必须</b>齐备，否则构造期即被拒绝。</para>
    /// </summary>
    public required TimeOnly? OffPeakWindowStart { get; init; }

    /// <summary>非工作时段窗口终点（含边界语义由 D2 的判定器实现；本类型只校验"窗口非空"）。</summary>
    public required TimeOnly? OffPeakWindowEnd { get; init; }

    /// <summary>
    /// 非工作时段采用的间隔（秒）。<c>null</c> = 不改变。
    /// <para>
    /// ⛔ 必须 <b>&gt; 0</b>：0 或负值会让作业在窗口内以零间隔自旋（退化成忙循环），
    /// 而外壳看起来像"策略已配置"。
    /// </para>
    /// <para>
    /// ⚠️ 允许在 <see cref="OffPeakPriorityEnabled"/> 为 <c>false</c> 时先行配置（便于分两步灰度上线），
    /// 但此时该值被<b>忽略</b>；任何结果<b>不得</b>声称它已生效。
    /// </para>
    /// </summary>
    public required int? OffPeakIntervalSeconds { get; init; }

    /// <summary>
    /// 条件触发（堆积：<c>enabled &gt; SoftTarget</c>）采用的间隔（秒）。<c>null</c> = 不改变。
    /// <para>
    /// 用「<b>缩短后的间隔</b>」而不是「缩短因子」：因子会引入浮点乘法与取整歧义，
    /// 使"采用间隔究竟是多少"无法被逐字段断言。显式秒数是可审计、可比较的整数事实。
    /// </para>
    /// <para>⚠️ 实际是否命中断言由 D5 判定；本类型只定义"命中后采用多少"。</para>
    /// </summary>
    public required int? BacklogShortenIntervalSeconds { get; init; }

    /// <summary>配置来源标注（如 <c>config:Subconscious:Scheduling:Rhythm</c> 或 <c>built-in/zero-regression</c>），用于事后溯源「这组节奏是谁给的」。</summary>
    public required string SourceLabel { get; init; }

    /// <summary>
    /// 配置是否自洽（fail-closed）：
    /// <list type="bullet">
    /// <item>启用非工作时段优先 ⇒ 窗口起点/终点<b>齐备</b>、窗口<b>非空</b>（起点 ≠ 终点）、间隔<b>&gt; 0</b>；</item>
    /// <item>窗口两个端点<b>要么都给、要么都不给</b>（半配置 ⇒ 拒绝，避免"看起来配了窗口"）；</item>
    /// <item>两个候选间隔（若给）必须 <b>&gt; 0</b>。</item>
    /// </list>
    /// </summary>
    public bool IsValid =>
        Version >= 1
        && !string.IsNullOrWhiteSpace(PolicyId)
        && !string.IsNullOrWhiteSpace(SourceLabel)
        && HasConsistentWindow
        && HasConsistentOffPeakInterval
        && IsPositiveOrNull(OffPeakIntervalSeconds)
        && IsPositiveOrNull(BacklogShortenIntervalSeconds);

    /// <summary>窗口两端点同给或同不给，且（给了则）非空窗口；启用非工作时段优先时窗口必须齐备。</summary>
    private bool HasConsistentWindow
    {
        get
        {
            if (OffPeakWindowStart is null || OffPeakWindowEnd is null)
            {
                // "两端都不给"只在**未启用**时合法；启用却无窗口 ⇒ 判据存在但不可用（fail-closed 必须拒绝的形状）。
                // 只给一端同样非法：否则"看起来配了窗口"会成为事实错误。
                return OffPeakWindowStart is null && OffPeakWindowEnd is null && !OffPeakPriorityEnabled;
            }

            return OffPeakWindowStart.Value != OffPeakWindowEnd.Value;
        }
    }

    /// <summary>启用非工作时段优先时，间隔必须齐备且为正。</summary>
    private bool HasConsistentOffPeakInterval
        => !OffPeakPriorityEnabled || OffPeakIntervalSeconds is > 0;

    private static bool IsPositiveOrNull(int? seconds) => seconds is null or > 0;

    /// <summary>
    /// 校验配置。<b>非法配置一律拒绝</b>，不得静默产生退化的判定序
    /// （例如"启用了非工作时段优先但没有间隔"会让特性看起来已开启、实际什么都不做）。
    /// </summary>
    /// <exception cref="InvalidOperationException">配置非法时抛出。</exception>
    public void EnsureValid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException(
                $"潜意识节奏策略配置非法（{PolicyId} v{Version}）："
                + $"窗口({OffPeakWindowStart?.ToString("HH:mm") ?? "null"} → {OffPeakWindowEnd?.ToString("HH:mm") ?? "null"})、"
                + $"OffPeakPriorityEnabled({OffPeakPriorityEnabled})、"
                + $"OffPeakIntervalSeconds({OffPeakIntervalSeconds?.ToString() ?? "null"})、"
                + $"BacklogShortenIntervalSeconds({BacklogShortenIntervalSeconds?.ToString() ?? "null"})、"
                + $"SourceLabel({SourceLabel})。规则：窗口两端同给或同不给且非空；"
                + "启用非工作时段优先时窗口与间隔齐备；间隔一律 > 0（0/负值会让作业自旋）；Version >= 1。");
        }
    }

    /// <summary>
    /// 纯判定：给定**配置间隔**与两个命中事实，返回这一轮采用的间隔与理由码。
    /// <para>
    /// ⛔ <b>不做任何 IO、不读时钟</b>：两个命中事实由调用方判定（非工作时段属 D2，堆积属 D5）。
    /// 本方法只负责"命中之后采用哪个间隔"，因此它的结论可被逐字段断言。
    /// </para>
    /// <para>
    /// ⛔ <b>默认关闭即透明</b>（P4）：<see cref="OffPeakPriorityEnabled"/> 为 <c>false</c> 时，
    /// 即使 <paramref name="isOffPeak"/> 为 <c>true</c> 也<b>不</b>缩短。
    /// </para>
    /// <para>
    /// ⛔ <b>永不延长</b>：结论间隔始终 ≤ <paramref name="configuredIntervalSeconds"/>。
    /// 候选间隔不短于配置间隔时，绑定来源为 <see cref="SubconsciousRhythmReasonCodes.Default"/>
    /// （记录事实，而不是伪装成"已加速"）。
    /// </para>
    /// </summary>
    /// <param name="configuredIntervalSeconds">配置间隔（秒），须 ≥ 1（与 <c>SubconsciousSchedulingOptions</c> 的 <c>Math.Max(1, …)</c> 口径一致）。</param>
    /// <param name="isOffPeak">调用方判定的"当前是否落在非工作时段窗口内"。</param>
    /// <param name="isBacklog">调用方判定的"是否处于堆积状态"（D5：<c>enabled &gt; SoftTarget</c>）。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="configuredIntervalSeconds"/> &lt; 1 时抛出。</exception>
    public SubconsciousRhythmDecision Resolve(int configuredIntervalSeconds, bool isOffPeak, bool isBacklog)
    {
        if (configuredIntervalSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuredIntervalSeconds),
                configuredIntervalSeconds,
                "配置间隔必须 ≥ 1 秒（否则会退化成自旋，且与既有 Math.Max(1, …) 口径不符）。");
        }

        EnsureValid();

        var offPeakCandidate = OffPeakPriorityEnabled && isOffPeak ? OffPeakIntervalSeconds : null;
        var backlogCandidate = isBacklog ? BacklogShortenIntervalSeconds : null;

        var adopted = configuredIntervalSeconds;
        if (offPeakCandidate is int offPeak && offPeak < adopted)
        {
            adopted = offPeak;
        }

        if (backlogCandidate is int backlog && backlog < adopted)
        {
            adopted = backlog;
        }

        // 绑定来源 = **确实把间隔改短了**、且取值等于采用间隔的候选；两者并列时按固定顺序合成
        // （⛔ 不引入"哪个条件优先"的隐含裁决：两个条件都命中时取更短者）。
        // ⛔ 候选不短于配置间隔时**不得**认领理由码：理由码要解释"采用间隔为什么是这个值"，
        // 认领了 off_peak 却一个字节都没改，会让读日志的人以为窗口起了作用（幻影生效）。
        var offPeakBinds = offPeakCandidate is int offPeakBinding
            && offPeakBinding < configuredIntervalSeconds
            && offPeakBinding == adopted;
        var backlogBinds = backlogCandidate is int backlogBinding
            && backlogBinding < configuredIntervalSeconds
            && backlogBinding == adopted;
        var reasonCode = (offPeakBinds, backlogBinds) switch
        {
            (true, true) => SubconsciousRhythmReasonCodes.OffPeakAndBacklog,
            (true, false) => SubconsciousRhythmReasonCodes.OffPeak,
            (false, true) => SubconsciousRhythmReasonCodes.Backlog,
            _ => SubconsciousRhythmReasonCodes.Default,
        };

        return new SubconsciousRhythmDecision
        {
            ConfiguredIntervalSeconds = configuredIntervalSeconds,
            IntervalSeconds = adopted,
            IsOffPeak = isOffPeak,
            IsBacklog = isBacklog,
            ReasonCode = reasonCode,
        };
    }

    /// <summary>
    /// 校验式工厂：非法配置在<b>构造期</b>即被拒绝（调用方不必等到判定时才发现）。
    /// </summary>
    /// <exception cref="InvalidOperationException">配置非法时抛出。</exception>
    public static SubconsciousRhythmPolicy Create(
        string policyId,
        int version,
        bool offPeakPriorityEnabled,
        TimeOnly? offPeakWindowStart,
        TimeOnly? offPeakWindowEnd,
        int? offPeakIntervalSeconds,
        int? backlogShortenIntervalSeconds,
        string sourceLabel)
    {
        var policy = new SubconsciousRhythmPolicy
        {
            PolicyId = policyId,
            Version = version,
            OffPeakPriorityEnabled = offPeakPriorityEnabled,
            OffPeakWindowStart = offPeakWindowStart,
            OffPeakWindowEnd = offPeakWindowEnd,
            OffPeakIntervalSeconds = offPeakIntervalSeconds,
            BacklogShortenIntervalSeconds = backlogShortenIntervalSeconds,
            SourceLabel = sourceLabel,
        };
        policy.EnsureValid();
        return policy;
    }

    /// <summary>过渡期默认策略 id。</summary>
    public const string ZeroRegressionPolicyId = "subconscious-rhythm/zero-regression";

    /// <summary>
    /// <b>零回归</b>默认：非工作时段优先<b>关闭</b>、两个候选间隔均为 <c>null</c>、窗口为 <c>null</c>。
    /// <para>
    /// ⭐ 语义：<see cref="Resolve"/> 对它恒返回配置间隔且理由码为
    /// <see cref="SubconsciousRhythmReasonCodes.Default"/> ⇒ 本片落地时 4 个作业的
    /// <c>initialDelay</c> / <c>interval</c> / 幂等键<b>逐字段不变</b>（P1）。
    /// </para>
    /// <para>
    /// ⛔ <b>窗口刻意留空</b>（而不是填 22:00–08:00）：填一个具体时段会被读成"默认已选择该时段"这一**治理结论**。
    /// 时段取值必须有人工裁决记录，并由配置显式给出。
    /// </para>
    /// <para>
    /// 收紧本默认值必须：① 递增 <see cref="Version"/>；② 有人工裁决记录。默认值是<b>过渡物</b>，不是治理结论。
    /// </para>
    /// </summary>
    public static SubconsciousRhythmPolicy Default { get; } = new()
    {
        PolicyId = ZeroRegressionPolicyId,
        Version = 1,
        OffPeakPriorityEnabled = false,
        OffPeakWindowStart = null,
        OffPeakWindowEnd = null,
        OffPeakIntervalSeconds = null,
        BacklogShortenIntervalSeconds = null,
        SourceLabel = "built-in/zero-regression",
    };
}

/// <summary>
/// 节奏决定的理由码（P8：每次决定都必须能被事后解释）。
/// <para>
/// 理由码是本片可观测性的<b>唯一货币</b>：没有理由码，"采用间隔变了"就无法区分
/// 「非工作时段生效」「堆积生效」「策略给的值不比配置更短」三种完全不同的原因。
/// </para>
/// </summary>
public static class SubconsciousRhythmReasonCodes
{
    /// <summary>沿用配置间隔（策略未命中，或候选不短于配置间隔）。</summary>
    public const string Default = "rhythm/default";

    /// <summary>采用非工作时段间隔。</summary>
    public const string OffPeak = "rhythm/off_peak";

    /// <summary>采用堆积（条件触发）间隔。</summary>
    public const string Backlog = "rhythm/backlog";

    /// <summary>非工作时段与堆积同时命中，且两者取值相同（取更短者的结果未产生分歧）。</summary>
    public const string OffPeakAndBacklog = "rhythm/off_peak+backlog";
}

/// <summary>
/// 一次节奏决定的结论（纯数据，可直接进作业 <c>metadata</c> 通道做可观测性）。
/// <para>
/// ⛔ <b>不含任何执行面</b>：没有"应用 / 落库 / 写盘"方法，也没有函数字段 —— 决定与执行分离，
/// 才可能在不产生副作用的前提下把判定逐字段取红。
/// </para>
/// </summary>
public sealed record SubconsciousRhythmDecision
{
    /// <summary>配置给出的间隔（秒）—— 采用间隔的<b>上界</b>。</summary>
    public required int ConfiguredIntervalSeconds { get; init; }

    /// <summary>本次实际采用的间隔（秒）。</summary>
    public required int IntervalSeconds { get; init; }

    /// <summary>调用方判定的"当前是否非工作时段"（原样记录，便于事后核对判定输入）。</summary>
    public required bool IsOffPeak { get; init; }

    /// <summary>调用方判定的"是否堆积"（原样记录）。</summary>
    public required bool IsBacklog { get; init; }

    /// <summary>绑定的理由码，见 <see cref="SubconsciousRhythmReasonCodes"/>。</summary>
    public required string ReasonCode { get; init; }

    /// <summary>本次是否真的缩短了（派生量，⛔ 不另存字段：否则两个字段可能自相矛盾）。</summary>
    public bool IsShortened => IntervalSeconds < ConfiguredIntervalSeconds;
}
