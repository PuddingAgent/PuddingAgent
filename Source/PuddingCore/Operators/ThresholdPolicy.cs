namespace PuddingCode.Operators;

/// <summary>
/// 判断阈值策略（一等对象）：<b>三区间</b>——「单一命题的一个分数 ⇒ Yes / No / Abstain」。
/// <para>
/// <b>阈值必须外置</b>：打分器不得自行决定通过与否，否则事后无法回答「为什么放行」，
/// 且自我改进候选可悄悄移动判据。
/// </para>
/// <para>
/// 实现 <see cref="IVersionedCriterion"/> 属<b>纯增量</b>：本类型本就携带
/// <see cref="PolicyId"/> / <see cref="Version"/>，只是把「可版本化」抬成类型事实，
/// 三区间判定语义<b>未</b>改动。逐标签单侧验收门是另一种形状，见 <see cref="AcceptanceThresholdPolicy"/>，
/// 不得与本类型互相塞入（会造出语义上并不存在的区间）。
/// </para>
/// </summary>
public sealed record ThresholdPolicy : IVersionedCriterion
{
    /// <summary>策略 id（连同版本落库，供事后解释）。</summary>
    public required string PolicyId { get; init; }

    /// <summary>策略版本。</summary>
    public required int Version { get; init; }

    /// <summary>score &gt;= 本值 ⇒ Yes（含等于）。</summary>
    public required double YesAtOrAbove { get; init; }

    /// <summary>score &lt;= 本值 ⇒ No（含等于）。</summary>
    public required double NoAtOrBelow { get; init; }

    /// <summary>风险类别（例 <c>high_risk</c>）；影响分档与门槛选择。</summary>
    public string? RiskClass { get; init; }

    /// <summary>策略绑定的场景键；跨场景共用策略时为 null。</summary>
    public string? SceneKey { get; init; }

    /// <summary>
    /// 配置是否自洽：<see cref="YesAtOrAbove"/> 必须<b>严格大于</b>
    /// <see cref="NoAtOrBelow"/>，否则三个区间退化（会出现无 Abstain 带或全 Abstain）。
    /// </summary>
    public bool IsValid => YesAtOrAbove > NoAtOrBelow;

    /// <summary>
    /// 校验配置。<b>非法配置一律拒绝</b>，不得静默产生全 Yes / 全 No / 全 Abstain。
    /// </summary>
    /// <exception cref="InvalidOperationException">阈值区间非法时抛出。</exception>
    public void EnsureValid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException(
                $"阈值策略配置非法（{PolicyId} v{Version}）：YesAtOrAbove({YesAtOrAbove}) 必须严格大于 NoAtOrBelow({NoAtOrBelow})。");
        }
    }

    /// <summary>
    /// 确定性三区间判定：
    /// <list type="bullet">
    /// <item><c>score &gt;= YesAtOrAbove</c> ⇒ <see cref="JudgeOutcome.Yes"/>（含等于）</item>
    /// <item><c>score &lt;= NoAtOrBelow</c> ⇒ <see cref="JudgeOutcome.No"/>（含等于）</item>
    /// <item>其余（两阈值之间的开区间）⇒ <see cref="JudgeOutcome.Abstain"/></item>
    /// </list>
    /// </summary>
    /// <param name="score">打分器给出的分数（量纲由 <see cref="OperatorOutputShape.ScoreScale"/> 声明）。</param>
    /// <exception cref="InvalidOperationException">阈值配置非法。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="score"/> 为 NaN。</exception>
    public JudgeOutcome Apply(double score)
    {
        EnsureValid();
        if (double.IsNaN(score))
        {
            throw new ArgumentOutOfRangeException(nameof(score), score, "分数不得为 NaN：无法落入任何区间。");
        }

        if (score >= YesAtOrAbove)
        {
            return JudgeOutcome.Yes;
        }

        if (score <= NoAtOrBelow)
        {
            return JudgeOutcome.No;
        }

        return JudgeOutcome.Abstain;
    }

    /// <summary>物化为可落库的已应用阈值快照。</summary>
    public AppliedThreshold ToApplied()
    {
        EnsureValid();
        return new AppliedThreshold
        {
            PolicyId = PolicyId,
            Version = Version,
            YesAtOrAbove = YesAtOrAbove,
            NoAtOrBelow = NoAtOrBelow,
        };
    }

    /// <summary>
    /// 校验式工厂：非法配置在<b>构造期</b>即被拒绝（调用方不必等到 Apply 才发现）。
    /// </summary>
    /// <exception cref="InvalidOperationException">阈值区间非法时抛出。</exception>
    public static ThresholdPolicy Create(
        string policyId,
        int version,
        double yesAtOrAbove,
        double noAtOrBelow,
        string? riskClass = null,
        string? sceneKey = null)
    {
        var policy = new ThresholdPolicy
        {
            PolicyId = policyId,
            Version = version,
            YesAtOrAbove = yesAtOrAbove,
            NoAtOrBelow = noAtOrBelow,
            RiskClass = riskClass,
            SceneKey = sceneKey,
        };
        policy.EnsureValid();
        return policy;
    }
}

/// <summary>
/// 已应用的阈值快照（连同结果落库，供事后解释「为什么通过」）。
/// </summary>
public sealed record AppliedThreshold
{
    /// <summary>策略 id。</summary>
    public required string PolicyId { get; init; }

    /// <summary>策略版本。</summary>
    public required int Version { get; init; }

    /// <summary>score &gt;= 本值 ⇒ Yes（含等于）。</summary>
    public required double YesAtOrAbove { get; init; }

    /// <summary>score &lt;= 本值 ⇒ No（含等于）。</summary>
    public required double NoAtOrBelow { get; init; }
}
