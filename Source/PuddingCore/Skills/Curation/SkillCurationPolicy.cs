using PuddingCode.Operators;

namespace PuddingCode.Skills.Curation;

/// <summary>
/// 技能整理（curation）策略（一等对象）：把「提炼 / 合并 / 淘汰」的判据从**散落在代码里的常量**
/// 变成可版本化、可审计、可随结果落库的策略。
/// <para>
/// <b>为什么是基础设施而不是一段 <c>if</c></b>：与 <c>SkillPortfolioPolicy</c> 同源 —— 阈值必须外置成类型事实，
/// 否则事后无法回答「为什么这次提炼被拒绝」，且自我改进候选可以悄悄移动判据
/// （宪法 §5 <b>C1</b>：候选不得修改判定自己的标准）。
/// </para>
/// <para>
/// ⚠️ <b>本类型刻意不提供"默认策略"</b>：<see cref="MinRetainedValueRatio"/> 的下限**没有**可论证的默认值
/// （不像组合预算的"零回归"有明确依据），因此<b>禁止</b>写一个"看起来合理"的数字进产品 ——
/// <see cref="Create"/> 的所有旋钮都必须由调用方显式给出。
/// </para>
/// <para>
/// ⚠️ <b>落点说明</b>：本类型是<b>纯判据</b>（不接 LLM、无 IO、不产出 <c>JudgementEnvelope</c>），
/// 因此**刻意不放进 <c>PuddingCore/Operators/</c>** —— 那个目录受「算子架构门禁」扫描，
/// 把非算子塞进去会稀释该门禁的语义（与 <c>Skills/Portfolio/</c> 同一处理）。
/// </para>
/// </summary>
/// <remarks>
/// 判据被 <c>SkillCurationGate</c> 消费（整理轨 C1–C5，fail-closed）：
/// <list type="bullet">
/// <item><see cref="MinRetainedValueRatio"/> ⇒ C4（价值不降）：合并后价值不得低于被取代者之和的该比例；</item>
/// <item><see cref="ApplicabilityHeadings"/> / <see cref="PitfallHeadings"/> / <see cref="MinMarkdownLength"/>
/// ⇒ 提炼产物契约的 P1/P2/P6（反笔记不变式：必须写"何时该用 / 何时不该用 / 踩过什么坑"）。</item>
/// </list>
/// </remarks>
public sealed record SkillCurationPolicy : IVersionedCriterion
{
    /// <summary>策略 id（连同版本落库，供事后解释「为什么拒绝 / 按哪一版判的」）。</summary>
    public required string PolicyId { get; init; }

    /// <summary>策略版本。策略被移动过时必须递增，历史结论才能自证用的是哪一版。</summary>
    public required int Version { get; init; }

    /// <summary>
    /// 价值保留比例下限（C4）：合并后产物价值必须 ≥ 本值 × 被取代者价值之和。
    /// <para>
    /// ⚠️ <b>只在两侧都有可比较分数时才生效</b>；任一侧为"无观测数据"⇒ 判定降级为"记录但不阻断"
    /// （冷启动纪律：无数据 ≠ 低价值，与 §13.10 同源）。本类型**不**提供默认值。
    /// </para>
    /// </summary>
    public required double MinRetainedValueRatio { get; init; }

    /// <summary>
    /// 适用条件段的标题集合（P1）：产物正文必须含其中**至少一个**标题。
    /// <para>判定语义：按行匹配，去掉前导 <c>#</c> 与空白后**以该标题文本开头**（大小写不敏感）。</para>
    /// </summary>
    public required IReadOnlyList<string> ApplicabilityHeadings { get; init; }

    /// <summary>
    /// 陷阱 / 反例段的标题集合（P2）：语义同 <see cref="ApplicabilityHeadings"/>。
    /// <para>这一条是「提炼 vs 记笔记」的分水岭：只写"怎么做"、不写"什么时候会翻车"的产物一律不合格。</para>
    /// </summary>
    public required IReadOnlyList<string> PitfallHeadings { get; init; }

    /// <summary>产物正文长度下限（P6）：低于本值即视为"没有实质内容"。</summary>
    public required int MinMarkdownLength { get; init; }

    /// <summary>
    /// 配置是否自洽：id / 版本 / 比例 / 标题集合 / 长度下限都必须有效。
    /// <para>空标题集合会让 P1/P2 退化成"永远违规"或"永远通过"，两者都是静默缺陷。</para>
    /// </summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(PolicyId)
        && Version >= 1
        && double.IsFinite(MinRetainedValueRatio)
        && MinRetainedValueRatio >= 0
        && MinMarkdownLength > 0
        && HasUsableHeadings(ApplicabilityHeadings)
        && HasUsableHeadings(PitfallHeadings);

    /// <summary>
    /// 校验配置。<b>非法配置一律拒绝</b>，不得静默产生退化的判定（例如空标题集合 ⇒ 契约恒违规）。
    /// </summary>
    /// <exception cref="InvalidOperationException">配置非法时抛出。</exception>
    public void EnsureValid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException(
                $"技能整理策略配置非法（{PolicyId} v{Version}）：PolicyId 非空、Version ≥ 1、"
                + $"MinRetainedValueRatio ≥ 0 且有限、MinMarkdownLength > 0，"
                + $"ApplicabilityHeadings({ApplicabilityHeadings?.Count ?? 0}) 与 "
                + $"PitfallHeadings({PitfallHeadings?.Count ?? 0}) 必须非空且不含空白项。");
        }
    }

    /// <summary>
    /// 物化为可落库的已应用策略快照（做事后解释："这次整理是用哪一版、哪组阈值判的"）。
    /// <para>快照只含**本次判定实际消费**的字段 —— 全部字段都会被消费，故全部进快照。</para>
    /// </summary>
    public AppliedSkillCurationPolicy ToApplied()
    {
        EnsureValid();
        return new AppliedSkillCurationPolicy
        {
            PolicyId = PolicyId,
            Version = Version,
            MinRetainedValueRatio = MinRetainedValueRatio,
            ApplicabilityHeadings = ApplicabilityHeadings,
            PitfallHeadings = PitfallHeadings,
            MinMarkdownLength = MinMarkdownLength,
        };
    }

    /// <summary>
    /// 校验式工厂：非法配置在<b>构造期</b>即被拒绝（调用方不必等到判定时才发现）。
    /// <para>
    /// ⛔ <b>所有旋钮都必须显式传参</b>：本类型**没有**默认实参，也没有"默认策略"工厂 ——
    /// <see cref="MinRetainedValueRatio"/> 的下限是治理结论（需人工裁决），不是可以猜的常量。
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">配置非法时抛出。</exception>
    public static SkillCurationPolicy Create(
        string policyId,
        int version,
        double minRetainedValueRatio,
        IReadOnlyList<string> applicabilityHeadings,
        IReadOnlyList<string> pitfallHeadings,
        int minMarkdownLength)
    {
        var policy = new SkillCurationPolicy
        {
            PolicyId = policyId,
            Version = version,
            MinRetainedValueRatio = minRetainedValueRatio,
            ApplicabilityHeadings = applicabilityHeadings,
            PitfallHeadings = pitfallHeadings,
            MinMarkdownLength = minMarkdownLength,
        };
        policy.EnsureValid();
        return policy;
    }

    private static bool HasUsableHeadings(IReadOnlyList<string>? headings)
        => headings is { Count: > 0 } && headings.All(h => !string.IsNullOrWhiteSpace(h));
}

/// <summary>
/// 已应用的整理策略快照（连同裁决落库，供事后解释「按哪一版判据拒绝/放行」）。
/// </summary>
public sealed record AppliedSkillCurationPolicy
{
    /// <summary>策略 id。</summary>
    public required string PolicyId { get; init; }

    /// <summary>策略版本。</summary>
    public required int Version { get; init; }

    /// <summary>本次判定实际消费的价值保留比例下限。</summary>
    public required double MinRetainedValueRatio { get; init; }

    /// <summary>本次判定实际消费的适用条件段标题集合。</summary>
    public required IReadOnlyList<string> ApplicabilityHeadings { get; init; }

    /// <summary>本次判定实际消费的陷阱段标题集合。</summary>
    public required IReadOnlyList<string> PitfallHeadings { get; init; }

    /// <summary>本次判定实际消费的正文长度下限。</summary>
    public required int MinMarkdownLength { get; init; }
}
