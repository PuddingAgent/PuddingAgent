using PuddingCode.Operators;

namespace PuddingCode.Skills.Family;

/// <summary>
/// 技能**合并**判据策略（RSI-G4 交付物 D6）：把「合并四条件」里**唯一可调**的那个阈值，
/// 从散落在代码里的字面量变成可版本化、可审计、可随结论落库的类型事实。
/// <para>
/// 四条件里只有一条带阈值（任务书 §2.5）：<b>同家族</b>（结构：同一 <c>FamilyKey</c>）、
/// <b>关键词重叠</b>（结构：交集非空）、<b>程序性文本相似 ≥ 阈值</b>（← 本类型）、
/// <b>证据可归并</b>（结构：共享来源标识）。⛔ 本类型**只**承载那一个阈值，不得借机塞入"默认权重"之类的东西。
/// </para>
/// <para>
/// ⚠️ <b>刻意不提供"默认策略"</b>（与 <see cref="SkillFamilyPolicy"/> 同一纪律）：
/// 只读报告里的 <c>0.25 / 0.4 / 0.6</c> 原文是「只陈述事实，不设阀值判据」，
/// ⛔ 不得直接复制成产品默认值。要用某个档位，必须由调用方**显式给出并说明理由**、且递增版本号。
/// </para>
/// <para>
/// ⚠️ <b>落点说明</b>：本类型是纯判据（无 IO、不接 LLM），因此与 <see cref="SkillFamilyPolicy"/> 同目录，
/// **刻意不放进 <c>PuddingCore/Operators/</c>**（那个目录受算子架构门禁扫描）。
/// </para>
/// </summary>
/// <remarks>
/// <b>与既有确定性闸门的关系</b>（任务书 §2.5，⛔ 不得混淆）：
/// <c>SkillEvolutionDeduplicationService.IsDeterministicallyEligible</c> 是**另一条**判据
/// （工具集合完全相等 + 元数据文本 ≥ 0.20 + (共享 turn 或 ≥ 0.35)）。
/// 本策略的阈值作用于**程序性文本**（技能正文/步骤），不是元数据文本（名称+描述）——
/// 两者共享**同一个 token-Jaccard 实现**，但输入不同、阈值不同、判定位置不同。
/// 可行性探针（D6b）会把既有闸门**叠加**在本判据之上单独统计，⛔ 不替换、不放宽它。
/// </remarks>
public sealed record SkillMergePolicy : IVersionedCriterion
{
    /// <summary>策略 id（连同版本落库，供事后解释"这条合并结论是按哪一版口径算的"）。</summary>
    public required string PolicyId { get; init; }

    /// <summary>策略版本。阈值被移动过时必须递增，历史结论才能自证用的是哪一版。</summary>
    public required int Version { get; init; }

    /// <summary>
    /// 程序性文本相似度下限，合法区间 <c>(0, 1]</c>。
    /// <para>
    /// <c>0</c> 会让**任意两条技能**都通过这一条（判据退化成"恒真条件"，等于这一条不存在）；
    /// 比 <c>1</c> 则连逐字相同的正文也不通过（判据恒不触发）。两者都是**静默缺陷**，
    /// 因此构造期即拒绝，而不是等到判定时才发现。
    /// </para>
    /// </summary>
    public required double MinimumProceduralTextSimilarity { get; init; }

    /// <summary>配置是否自洽。</summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(PolicyId)
        && Version >= 1
        && double.IsFinite(MinimumProceduralTextSimilarity)
        && MinimumProceduralTextSimilarity > 0
        && MinimumProceduralTextSimilarity <= 1;

    /// <summary>
    /// 校验配置。<b>非法配置一律拒绝</b>，不得静默产生退化的合并结论
    /// （例如阈值为 0 ⇒ 四条件里的文本条件恒真；阈值 > 1 ⇒ 该条件恒假）。
    /// </summary>
    /// <exception cref="InvalidOperationException">配置非法时抛出。</exception>
    public void EnsureValid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException(
                $"技能合并策略配置非法（{PolicyId} v{Version}）：PolicyId 非空、Version ≥ 1、"
                + $"MinimumProceduralTextSimilarity({MinimumProceduralTextSimilarity}) 必须落在 (0, 1]。");
        }
    }

    /// <summary>
    /// 物化为可落库的已应用策略快照（做事后解释："这条合并结论是用哪一版、哪个阈值算的"）。
    /// <para>快照只含**本次判定实际消费**的字段 —— 本判据消费全部字段，故全部进快照。</para>
    /// </summary>
    public AppliedSkillMergePolicy ToApplied()
    {
        EnsureValid();
        return new AppliedSkillMergePolicy
        {
            PolicyId = PolicyId,
            Version = Version,
            MinimumProceduralTextSimilarity = MinimumProceduralTextSimilarity,
        };
    }

    /// <summary>
    /// 校验式工厂：非法配置在**构造期**即被拒绝。
    /// <para>⛔ 所有旋钮都必须显式传参：本类型没有默认实参，也没有"默认策略"工厂。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">配置非法时抛出。</exception>
    public static SkillMergePolicy Create(
        string policyId,
        int version,
        double minimumProceduralTextSimilarity)
    {
        var policy = new SkillMergePolicy
        {
            PolicyId = policyId,
            Version = version,
            MinimumProceduralTextSimilarity = minimumProceduralTextSimilarity,
        };
        policy.EnsureValid();
        return policy;
    }
}

/// <summary>
/// 已应用的合并策略快照（连同合并结论落库，供事后解释「按哪一版口径算出的结论」）。
/// </summary>
public sealed record AppliedSkillMergePolicy
{
    /// <summary>策略 id。</summary>
    public required string PolicyId { get; init; }

    /// <summary>策略版本。</summary>
    public required int Version { get; init; }

    /// <summary>本次判定实际消费的程序性文本相似度下限。</summary>
    public required double MinimumProceduralTextSimilarity { get; init; }
}
