using PuddingCode.Operators;

namespace PuddingCode.Skills.Portfolio;

/// <summary>
/// 技能组合预算策略（一等对象）：把「技能组合的容量」从**没有任何约束的「增」**
/// 变成可版本化、可审计、可随结果落库的判据。
/// <para>
/// <b>为什么是基础设施而不是一段 <c>if</c></b>：阈值必须外置成类型事实。判定器内**不得**出现裸阈值字面量，
/// 否则事后无法回答「为什么放行 / 为什么被置换」，且自我改进候选可以悄悄移动判据。
/// 本类型照抄 <see cref="ThresholdPolicy"/> 的形状（<see cref="IVersionedCriterion"/> + <see cref="EnsureValid"/>
/// + <see cref="ToApplied"/> 落库快照 + <see cref="Create"/> 校验式工厂），并<b>不新增阈值类型</b>：
/// 判定所需的比较直接用本对象上的 <see cref="double"/> 字段完成，不再造第二套阈值 record。
/// </para>
/// <para>
/// ⚠️ <b>落点说明</b>：本类型是<b>纯判据</b>（不接 LLM、不产出 <c>JudgementEnvelope</c>、无 IO），
/// 因此**刻意不放进 <c>PuddingCore/Operators/</c>** —— 那个目录受「算子架构门禁」扫描，
/// 把非算子塞进去会稀释该门禁的语义。
/// </para>
/// </summary>
/// <remarks>
/// 判定序（fail-closed）由 <c>SkillPortfolioAdmissionJudge</c> 实施：
/// <list type="number">
/// <item><c>enabled &lt; SoftTarget</c> ⇒ 走既有去重；通过 ⇒ 准入（<c>create</c>）</item>
/// <item><c>SoftTarget ≤ enabled &lt; HardCap</c> ⇒ 仅当 <c>candidateScore − minEnabledScore &gt; MinMarginalGain</c> ⇒ 准入；否则合并 / 延后</item>
/// <item><c>enabled ≥ HardCap</c> ⇒ <b>必须置换</b>：<c>candidateScore &gt; minEnabledScore + MinMarginalGain</c> ⇒ 置换价值最低者；否则合并 / 延后</item>
/// </list>
/// </remarks>
public sealed record SkillPortfolioPolicy : IVersionedCriterion
{
    /// <summary>策略 id（连同版本落库，供事后解释「为什么放行 / 为什么被置换」）。</summary>
    public required string PolicyId { get; init; }

    /// <summary>策略版本。策略被移动过时必须递增，历史结论才能自证用的是哪一版。</summary>
    public required int Version { get; init; }

    /// <summary>硬上限：启用数达到本值时，准入**必须**以置换（禁用价值最低者）为代价。</summary>
    public required int HardCap { get; init; }

    /// <summary>软目标：启用数达到本值后，准入必须证明「边际收益」大于 <see cref="MinMarginalGain"/>。</summary>
    public required int SoftTarget { get; init; }

    /// <summary>
    /// 同家族启用数上限。
    /// <para>
    /// ⚠️ <b>本切片只承载字段、不消费</b>（家族归类属 G4）：写进结果里的"生效值"也不得声称它已生效。
    /// </para>
    /// </summary>
    public required int PerFamilyCap { get; init; }

    /// <summary>最小边际收益：候选分必须比"已启用最低分"高出至少本值，才配得上一次准入或一次置换。</summary>
    public required double MinMarginalGain { get; init; }

    /// <summary>
    /// 陈旧度天数。
    /// <para>⚠️ <b>本切片只承载字段、不消费</b>（陈旧度策略属后续切片）。</para>
    /// </summary>
    public required int StalenessDays { get; init; }

    /// <summary>
    /// 配置是否自洽。<see cref="HardCap"/> 必须<b>不小于</b> <see cref="SoftTarget"/>，
    /// 否则三档判定退化（软区间为空 ⇒ "先软后硬"的阶梯不存在，会直接跳到置换）。
    /// </summary>
    public bool IsValid =>
        HardCap >= SoftTarget
        && SoftTarget >= 0
        && PerFamilyCap >= 0
        && MinMarginalGain >= 0
        && !double.IsNaN(MinMarginalGain)
        && StalenessDays >= 0;

    /// <summary>
    /// 校验配置。<b>非法配置一律拒绝</b>，不得静默产生退化的判定序
    /// （例如 <c>HardCap &lt; SoftTarget</c> 会让"软区间"恒为空）。
    /// </summary>
    /// <exception cref="InvalidOperationException">配置非法时抛出。</exception>
    public void EnsureValid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException(
                $"技能组合预算策略配置非法（{PolicyId} v{Version}）："
                + $"HardCap({HardCap}) 必须 >= SoftTarget({SoftTarget})，且 PerFamilyCap({PerFamilyCap}) / "
                + $"MinMarginalGain({MinMarginalGain}) / StalenessDays({StalenessDays}) 必须非负。");
        }
    }

    /// <summary>
    /// 物化为可落库的已应用策略快照（做事后解释："这条准入是用哪一版、哪组阈值放行的"）。
    /// <para>
    /// 快照只含**本次判定实际消费**的字段 —— 本切片不消费 <see cref="PerFamilyCap"/> 与
    /// <see cref="StalenessDays"/>，故它们不进快照，避免让读者以为已生效（防幻影区间）。
    /// </para>
    /// </summary>
    public AppliedPortfolioPolicy ToApplied()
    {
        EnsureValid();
        return new AppliedPortfolioPolicy
        {
            PolicyId = PolicyId,
            Version = Version,
            HardCap = HardCap,
            SoftTarget = SoftTarget,
            MinMarginalGain = MinMarginalGain,
        };
    }

    /// <summary>
    /// 校验式工厂：非法配置在<b>构造期</b>即被拒绝（调用方不必等到判定时才发现）。
    /// </summary>
    /// <exception cref="InvalidOperationException">配置非法时抛出。</exception>
    public static SkillPortfolioPolicy Create(
        string policyId,
        int version,
        int hardCap,
        int softTarget,
        int perFamilyCap,
        double minMarginalGain,
        int stalenessDays)
    {
        var policy = new SkillPortfolioPolicy
        {
            PolicyId = policyId,
            Version = version,
            HardCap = hardCap,
            SoftTarget = softTarget,
            PerFamilyCap = perFamilyCap,
            MinMarginalGain = minMarginalGain,
            StalenessDays = stalenessDays,
        };
        policy.EnsureValid();
        return policy;
    }

    /// <summary>过渡期默认策略 id。</summary>
    public const string ZeroRegressionPolicyId = "skill-portfolio/zero-regression";

    /// <summary>
    /// <b>零回归</b>过渡默认：阀门刻意开到**现状之上**，使本切片落地时既有行为不变
    /// （<c>enabled &lt; SoftTarget</c> ⇒ 完全走既有去重，判定器等于是透明通道）。
    /// <para>
    /// <b>为什么不给固定数字</b>：现网真实启用技能数必须由调用方实测后传入（写死常量就等于把
    /// "现状"冻进代码，且一旦现状超过常量，本切片一落地就**悄悄改变生产行为**）。
    /// </para>
    /// <para>
    /// 收窄本默认值必须：① 递增 <see cref="Version"/>；② 有人工裁决记录。默认值是**过渡物**，
    /// 不是治理结论。
    /// </para>
    /// </summary>
    /// <param name="currentEnabledCount">当前实测启用技能数（由调用方取实测值）。</param>
    /// <param name="headroom">期望的过渡余量：软目标设在现状之上这么多。</param>
    public static SkillPortfolioPolicy ZeroRegressionDefault(int currentEnabledCount, int headroom = 20)
    {
        if (currentEnabledCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentEnabledCount), currentEnabledCount, "启用技能数不得为负。");
        }

        if (headroom < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(headroom), headroom, "过渡余量不得为负。");
        }

        var softTarget = currentEnabledCount + headroom;
        return Create(
            ZeroRegressionPolicyId,
            version: 1,
            hardCap: softTarget + headroom,
            softTarget: softTarget,
            perFamilyCap: 0,
            minMarginalGain: 0.05,
            stalenessDays: 90);
    }
}

/// <summary>
/// 已应用的预算策略快照（连同判定结果落库，供事后解释「为什么放行 / 为什么被置换」）。
/// </summary>
public sealed record AppliedPortfolioPolicy
{
    /// <summary>策略 id。</summary>
    public required string PolicyId { get; init; }

    /// <summary>策略版本。</summary>
    public required int Version { get; init; }

    /// <summary>本次判定实际消费的硬上限。</summary>
    public required int HardCap { get; init; }

    /// <summary>本次判定实际消费的软目标。</summary>
    public required int SoftTarget { get; init; }

    /// <summary>本次判定实际消费的最小边际收益。</summary>
    public required double MinMarginalGain { get; init; }
}

/// <summary>
/// 技能价值分快照 —— <b>带刻度的分数</b>。
/// <para>
/// <b>为什么必须是两态类型而不是 <c>double</c></b>：打分器的契约里「无观测数据」与「数据表明很差」
/// 在数值上相同、在含义上不同（冷启动 <c>Score = 0</c> 只是占位、不可解读）。
/// 若判定器接收裸 <c>double</c>，冷启动就会被当成"最低分"，进而在预算满时**静默误杀**技能 ——
/// 这正是 §13.10 R2「误杀是静默的」的成因。
/// </para>
/// <para>
/// 本类型在<b>类型层</b>消灭该错误：<see cref="Unavailable"/> 分支**根本没有 <c>Score</c> 字段**，
/// 于是"把无数据当低分"这件事<b>无法被表达</b>。
/// </para>
/// </summary>
public abstract record SkillScoreSnapshot
{
    private SkillScoreSnapshot()
    {
    }

    /// <summary>有观测数据、可用于比较的分数。</summary>
    /// <param name="ScoreScale">刻度串（量纲由打分器声明，判定器不解释其含义，只要求两侧同刻度才可比较）。</param>
    /// <param name="Score">分数。</param>
    public sealed record Observed(string ScoreScale, double Score) : SkillScoreSnapshot;

    /// <summary>无观测数据：<b>不可解读、不可参与排序</b>。唯一正确消费方式是"降级到 Defer"。</summary>
    /// <param name="ReasonCode">原因码（用于区分"从未被命中"与"采集器未激活"）。</param>
    public sealed record Unavailable(string ReasonCode) : SkillScoreSnapshot;

    /// <summary>该快照是否可用于比较。判定器**只**在两侧都为 <see cref="Observed"/> 且刻度一致时才比较。</summary>
    public bool IsComparable => this is Observed;
}
