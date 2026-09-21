namespace PuddingCode.Operators;

/// <summary>
/// 逐标签验收门槛（单侧）：某标签的置信度达到要求时才可接受。
/// <para>
/// 与 <see cref="ThresholdPolicy"/> 的分工：后者管「单一命题的一个分数 ⇒ 三区间」，
/// 本类型管「某个标签的置信度是否够格」。<b>不得</b>把本类型塞进三区间形状——
/// 单侧门没有 No 侧语义，硬造一个 <c>NoAtOrBelow</c> 会造成语义混同（同一字段在两处含义不同，
/// 事后无法解释、也无法与既有实现逐位对齐）。
/// </para>
/// <para>
/// 判定语义（必须与既有实现逐位一致，不得改动）：
/// <list type="bullet">
/// <item><c>confidence &gt;= RequiredConfidence</c> ⇒ <c>true</c>（<b>含等</b>）；</item>
/// <item><c>null</c> ⇒ <c>false</c>（<b>保守</b>：缺失置信度一律视为不达门槛，绝不放行）；</item>
/// <item>非法门槛（NaN / 超出 [0,1]）由 <see cref="Create"/> 在<b>构造期</b>拒绝，不静默放行。</item>
/// </list>
/// </para>
/// </summary>
public sealed record AcceptanceThresholdPolicy : IVersionedCriterion
{
    /// <inheritdoc />
    public required string PolicyId { get; init; }

    /// <inheritdoc />
    public required int Version { get; init; }

    /// <summary>要求的置信度下界（含等）。</summary>
    public required double RequiredConfidence { get; init; }

    /// <summary>
    /// 适用标签集（规范化标签键，如 <c>allow_permanent</c> / <c>deny_permanent</c>）；
    /// <b>空 = 不限定标签</b>。
    /// <para>
    /// 空集表示「调用方自行判定该门槛是否适用于当前标签」，而不是「适用于所有标签」的隐式授权：
    /// 本类型不替调用方猜适用性。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> AppliesToLabels { get; init; } = [];

    /// <summary>判据绑定的场景键；跨场景共用判据时为 null。</summary>
    public string? SceneKey { get; init; }

    /// <summary>人类可读备注（不参与判定，只用于审计与排障）。</summary>
    public string? Note { get; init; }

    /// <summary>
    /// 是否达门槛（单侧：达到即接受，没有「否定」侧语义，也绝不返回第三态）。
    /// <para>
    /// 与既有实现逐位一致：<b>缺失（null）⇒ false</b>；NaN 输入同样返回 false
    /// （不抛异常，也绝不放行）。输入不做 [0,1] 夹取——既有实现用的是裸 <c>&gt;=</c>，
    /// 夹取会改变输入语义。
    /// </para>
    /// <para>
    /// 若绕过 <see cref="Create"/> 手搓出非法门槛（RequiredConfidence 为 NaN），本方法对任何输入
    /// 都返回 false（保守不满足）——失败方向永远是「不放行」。
    /// </para>
    /// </summary>
    /// <param name="confidence">待验收的置信度；缺失为 null。</param>
    public bool IsMet(double? confidence)
        => confidence is double value && value >= RequiredConfidence;

    /// <summary>
    /// 校验式工厂：非法配置在<b>构造期</b>即被拒绝（调用方不必等到判定才发现门槛根本没生效）。
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="policyId"/> 为空。</exception>
    /// <exception cref="ArgumentOutOfRangeException">版本 &lt; 1，或门槛为 NaN / 超出 [0,1]。</exception>
    public static AcceptanceThresholdPolicy Create(
        string policyId,
        int version,
        double requiredConfidence,
        IReadOnlyList<string>? appliesToLabels = null,
        string? sceneKey = null,
        string? note = null)
    {
        if (string.IsNullOrWhiteSpace(policyId))
        {
            throw new ArgumentException("判据 id 不得为空：无 id 的判据无法落库、无法事后解释。", nameof(policyId));
        }

        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "判据版本必须 >= 1。");
        }

        if (double.IsNaN(requiredConfidence) || requiredConfidence < 0d || requiredConfidence > 1d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requiredConfidence),
                requiredConfidence,
                "要求的置信度必须是 [0,1] 内的确定数值（NaN / 越界一律拒绝，不静默放行）。");
        }

        return new AcceptanceThresholdPolicy
        {
            PolicyId = policyId,
            Version = version,
            RequiredConfidence = requiredConfidence,
            AppliesToLabels = appliesToLabels ?? [],
            SceneKey = sceneKey,
            Note = note,
        };
    }
}
