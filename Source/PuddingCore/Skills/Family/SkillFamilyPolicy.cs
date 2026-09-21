using PuddingCode.Operators;

namespace PuddingCode.Skills.Family;

/// <summary>
/// 技能家族判据策略（一等对象）：把「哪些技能算同一个家族」的**口径**从散落在脚本/代码里的字面量
/// 变成可版本化、可审计、可随结果落库的类型事实。
/// <para>
/// <b>为什么这是基础设施而不是一段 <c>if</c></b>：与 <c>SkillPortfolioPolicy</c> / <c>SkillCurationPolicy</c> 同源 ——
/// <c>PerFamilyCap</c>（同家族启用数上限）的验收判据是「上限<b>强制生效</b>」，即**同一输入必须得到同一裁决**。
/// 家族键一旦由不稳定的输入产生（例如 LLM 分组），上限就不可复现，「生效」二字将无法被任何断言固定。
/// 因此本类型只承载**确定性**口径。
/// </para>
/// <para>
/// ⚠️ <b>本类型刻意不提供"默认策略"</b>：<see cref="NameTokenJaccardThreshold"/> 没有可论证的默认值
/// （只读报告里的 <c>0.25 / 0.4 / 0.6</c> 原文是「只陈述事实，不设阀值判据」）⇒
/// <see cref="Create"/> 的所有旋钮都必须由调用方显式给出，⛔ 不得写一个"看起来合理"的数字进产品。
/// </para>
/// <para>
/// ⚠️ <b>落点说明</b>：本类型是<b>纯判据</b>（不接 LLM、无 IO、不产出 <c>JudgementEnvelope</c>），
/// 因此**刻意不放进 <c>PuddingCore/Operators/</c>** —— 那个目录受「算子架构门禁」扫描，
/// 把非算子塞进去会稀释该门禁的语义（与 <c>Skills/Portfolio/</c>、<c>Skills/Curation/</c> 同一处理）。
/// </para>
/// </summary>
/// <remarks>
/// 口径与既有事实的对应关系（⛔ 不得各自发明第二套）：
/// <list type="bullet">
/// <item><see cref="NameSeparators"/> 与 <see cref="MinTokenLength"/> 必须与
/// <c>SkillEnforcerService.CollectKeywords</c> 的名称分词、以及只读盘点脚本的名字分词**同源**
/// （两者的分隔符集合在实测中完全一致：空格 / <c>|</c> / <c>,</c> / <c>/</c> / 全角冒号 / 顿号）；</item>
/// <item>本口径是**保守下界**：它只能发现「名字就像同一件事」的重复，**发现不了措辞完全不同的同类**
/// ⇒ ⛔ 不得把「本口径下没找到家族」读成「没有重复」。</item>
/// </list>
/// </remarks>
public sealed record SkillFamilyPolicy : IVersionedCriterion
{
    /// <summary>策略 id（连同版本落库，供事后解释「这条家族上限是按哪一版口径算的」）。</summary>
    public required string PolicyId { get; init; }

    /// <summary>策略版本。策略被移动过时必须递增，历史结论才能自证用的是哪一版。</summary>
    public required int Version { get; init; }

    /// <summary>
    /// 同族阈值：两个技能的**名称分词 token 的 Jaccard 相似度** ≥ 本值即视为同族（并查集传递闭包）。
    /// <para>
    /// 合法区间是 <c>(0, 1]</c>：<c>0</c> 会让**所有技能**两两同族（退化成"一个大家族"的假象），
    /// 比 <c>1</c> 则连完全相同的名称也不算同族（判据恒不触发）—— 两者都是静默缺陷，故在构造期拒绝。
    /// </para>
    /// </summary>
    public required double NameTokenJaccardThreshold { get; init; }

    /// <summary>
    /// token 长度下限：短于本值的分词不计入 token 集合（单字符分词几乎必然制造假重叠）。
    /// <para>实测口径取 <c>2</c>；本类型不写死该值，只要求调用方显式给出。</para>
    /// </summary>
    public required int MinTokenLength { get; init; }

    /// <summary>名称分词的分隔符集合（必须非空、无重复）。</summary>
    public required IReadOnlyList<char> NameSeparators { get; init; }

    /// <summary>
    /// 配置是否自洽。
    /// <para>空分隔符集合会让分词退化成"整句一个 token"（等于按名称原文精确比较，判据几乎不触发）；
    /// 重复分隔符与 <c>'\0'</c> 是配置错误，不是"宽容输入"。</para>
    /// </summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(PolicyId)
        && Version >= 1
        && double.IsFinite(NameTokenJaccardThreshold)
        && NameTokenJaccardThreshold > 0
        && NameTokenJaccardThreshold <= 1
        && MinTokenLength >= 1
        && NameSeparators is { Count: > 0 }
        && NameSeparators.Distinct().Count() == NameSeparators.Count
        && !NameSeparators.Contains('\0');

    /// <summary>
    /// 校验配置。<b>非法配置一律拒绝</b>，不得静默产生退化的家族划分
    /// （例如阈值为 0 ⇒ 全体同族 ⇒ 家族上限对每个技能都"超限"）。
    /// </summary>
    /// <exception cref="InvalidOperationException">配置非法时抛出。</exception>
    public void EnsureValid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException(
                $"技能家族策略配置非法（{PolicyId} v{Version}）：PolicyId 非空、Version ≥ 1、"
                + $"NameTokenJaccardThreshold({NameTokenJaccardThreshold}) 必须落在 (0, 1]、"
                + $"MinTokenLength({MinTokenLength}) ≥ 1，"
                + $"NameSeparators({NameSeparators?.Count ?? 0}) 必须非空且不含重复项与 '\\0'。");
        }
    }

    /// <summary>
    /// 物化为可落库的已应用策略快照（做事后解释："这条家族上限是用哪一版、哪个阈值算的"）。
    /// <para>快照只含**本次判定实际消费**的字段 —— 本切片的判定会消费全部字段，故全部进快照。</para>
    /// </summary>
    public AppliedSkillFamilyPolicy ToApplied()
    {
        EnsureValid();
        return new AppliedSkillFamilyPolicy
        {
            PolicyId = PolicyId,
            Version = Version,
            NameTokenJaccardThreshold = NameTokenJaccardThreshold,
            MinTokenLength = MinTokenLength,
            NameSeparators = NameSeparators,
        };
    }

    /// <summary>
    /// 校验式工厂：非法配置在<b>构造期</b>即被拒绝（调用方不必等到判定时才发现）。
    /// <para>
    /// ⛔ <b>所有旋钮都必须显式传参</b>：本类型**没有**默认实参，也没有"默认策略"工厂 ——
    /// 阈值是治理结论（需人工裁决并说明理由），不是可以猜的常量。若要用只读报告里的某个档位作默认，
    /// 必须在调用方**说明理由并递增版本号**。
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">配置非法时抛出。</exception>
    public static SkillFamilyPolicy Create(
        string policyId,
        int version,
        double nameTokenJaccardThreshold,
        int minTokenLength,
        IReadOnlyList<char> nameSeparators)
    {
        var policy = new SkillFamilyPolicy
        {
            PolicyId = policyId,
            Version = version,
            NameTokenJaccardThreshold = nameTokenJaccardThreshold,
            MinTokenLength = minTokenLength,
            NameSeparators = nameSeparators,
        };
        policy.EnsureValid();
        return policy;
    }
}

/// <summary>
/// 已应用的家族策略快照（连同家族划分结果落库，供事后解释「按哪一版口径算出的家族」）。
/// </summary>
public sealed record AppliedSkillFamilyPolicy
{
    /// <summary>策略 id。</summary>
    public required string PolicyId { get; init; }

    /// <summary>策略版本。</summary>
    public required int Version { get; init; }

    /// <summary>本次判定实际消费的同族阈值。</summary>
    public required double NameTokenJaccardThreshold { get; init; }

    /// <summary>本次判定实际消费的 token 长度下限。</summary>
    public required int MinTokenLength { get; init; }

    /// <summary>本次判定实际消费的分隔符集合。</summary>
    public required IReadOnlyList<char> NameSeparators { get; init; }
}
