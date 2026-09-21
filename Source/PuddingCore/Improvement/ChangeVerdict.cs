namespace PuddingCode.Improvement;

/// <summary>
/// 变更裁决：判据层对 <see cref="ImprovementProposal"/> 的结论，也是**唯一**允许驱动落点动作的凭据。
/// </summary>
/// <remarks>
/// <para>
/// 关键立场：<b>提案者（含 LLM）不能自己给自己发通行证。</b>
/// 提案只表达意图；能否落点由本类型承载的裁决决定，而裁决来自**版本化的判据对象**
/// （<see cref="PolicyId"/> + <see cref="PolicyVersion"/>），不是来自提案者的自述置信度。
/// </para>
/// <para>
/// 三条硬约束在此实现（构造期强制，不靠调用方自觉）：
/// <list type="number">
/// <item><b>默认 shadow</b>：新作业 / 新判据首次上线走 <see cref="ChangeDecision.ApplyShadow"/>；</item>
/// <item><b>可回滚</b>：<see cref="ChangeDecision.Apply"/> 必须携带 <see cref="RollbackHandle"/>，
///       缺失即拒绝 —— 不可回滚的自动变更不允许存在；</item>
/// <item><b>非法不静默</b>：<see cref="ChangeDecision.Unknown"/> 一律构造失败，避免被下游当成放行。</item>
/// </list>
/// </para>
/// </remarks>
public sealed record ChangeVerdict
{
    /// <summary>决策。</summary>
    public required ChangeDecision Decision { get; init; }

    /// <summary>作出本裁决的判据 id（必须能反查到具体判据对象，而非某处硬编码常量）。</summary>
    public required string PolicyId { get; init; }

    /// <summary>判据版本（&gt;= 1）：事后解释"当时按哪一版判据判的"必须可回答。</summary>
    public required int PolicyVersion { get; init; }

    /// <summary>机器可读的理由码（如 <c>unexpected_failures</c> / <c>budget_exhausted</c>），供统计与回归。</summary>
    public required string ReasonCode { get; init; }

    /// <summary>
    /// 回滚句柄：<b>仅当</b> <see cref="ChangeDecision.Apply"/> 时必填。
    /// <para>
    /// 非 Apply 决策不存在落点动作，因此也不存在可回滚句柄；此时携带句柄会被拒绝 ——
    /// 否则会出现"看起来能回滚、实际没有动作可回滚"的假象。
    /// </para>
    /// </summary>
    public string? RollbackHandle { get; init; }

    /// <summary>人类可读补充（不参与判定，只用于审计与排障）。</summary>
    public string? Note { get; init; }

    /// <summary>
    /// 校验式工厂：非法裁决在**构造期**即被拒绝。
    /// </summary>
    /// <exception cref="ArgumentException">决策为 Unknown，判据 id / 理由码为空，或回滚句柄与决策不匹配。</exception>
    /// <exception cref="ArgumentOutOfRangeException">判据版本 &lt; 1。</exception>
    public static ChangeVerdict Create(
        ChangeDecision decision,
        string policyId,
        int policyVersion,
        string reasonCode,
        string? rollbackHandle = null,
        string? note = null)
    {
        if (decision == ChangeDecision.Unknown)
        {
            throw new ArgumentException(
                "决策不得为 Unknown：未产生有效裁决不得当作放行（与 JudgeOutcome.Unknown 同一条纪律）。",
                nameof(decision));
        }

        if (string.IsNullOrWhiteSpace(policyId))
        {
            throw new ArgumentException("判据 id 不得为空：无 id 的裁决无法事后解释按哪一版判据作出。", nameof(policyId));
        }

        if (policyVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(policyVersion), policyVersion, "判据版本必须 >= 1。");
        }

        if (string.IsNullOrWhiteSpace(reasonCode))
        {
            throw new ArgumentException("理由码不得为空：无理由码的裁决无法统计、无法回归。", nameof(reasonCode));
        }

        var hasRollback = !string.IsNullOrWhiteSpace(rollbackHandle);

        if (decision == ChangeDecision.Apply && !hasRollback)
        {
            throw new ArgumentException(
                "Apply 必须携带 rollbackHandle：不可回滚的自动变更不允许存在（缺句柄即拒绝应用）。",
                nameof(rollbackHandle));
        }

        if (decision != ChangeDecision.Apply && hasRollback)
        {
            throw new ArgumentException(
                $"决策 {decision} 不得携带 rollbackHandle：没有落点动作就没有可回滚的句柄，带上会造成"
                + "「看起来能回滚、实际无动作」的假象。",
                nameof(rollbackHandle));
        }

        return new ChangeVerdict
        {
            Decision = decision,
            PolicyId = policyId.Trim(),
            PolicyVersion = policyVersion,
            ReasonCode = reasonCode.Trim(),
            RollbackHandle = hasRollback ? rollbackHandle!.Trim() : null,
            Note = note,
        };
    }
}
