using System.Globalization;
using PuddingCode.Skills.Family;
using PuddingCode.Skills.Portfolio;

namespace PuddingMemoryEngine.Services;

/// <summary>
/// 单条「家族超限 ⇒ 进入合并评审」的**待裁决记录**（任务书 §2.3 冻结的必含字段）。
/// <para>
/// ⚠️ <b>本类型刻意没有"要禁用谁 / 要合并到谁"这类字段</b>：超限的**唯一**后果是产出评审请求。
/// 一旦它长出一个可执行字段（例如 <c>DisableTargetSkillId</c>），G4 就变成了**绕过遥测的删除通道**
/// （§13.10 R1「冷启动只降不杀」、R2「误杀是静默的」）—— 判据层不得兼职执行层。
/// </para>
/// </summary>
public sealed record SkillFamilyCapReview
{
    /// <summary>家族键（= 簇内按序数序最小的成员 id，由 <c>SkillFamilyClusterer</c> 保证与输入顺序无关）。</summary>
    public required string FamilyKey { get; init; }

    /// <summary>成员 id，**按序数序升序**（直接来自簇，调用方无需再排序）。</summary>
    public required IReadOnlyList<string> Members { get; init; }

    /// <summary>该家族的**启用**成员数（计数口径见 <see cref="SkillFamilyCapJudge"/> 的 remarks）。</summary>
    public required int EnabledCount { get; init; }

    /// <summary>本家族适用的上限（唯一来源是策略对象；判定器内不得出现裸字面量）。</summary>
    public required int Cap { get; init; }

    /// <summary>超出上限的成员数（<c>EnabledCount − Cap</c>），恒 ≥ 1。</summary>
    public required int OverBy { get; init; }

    /// <summary>原因码。当前只有 <c>family_over_cap</c> 一种。</summary>
    public required string ReasonCode { get; init; }

    /// <summary>策略引用 <c>policyId@version</c> —— 事后必须能回答"这条评审是用哪一版阈值产生的"。</summary>
    public required string PolicyRef { get; init; }

    /// <summary>
    /// 按**内容**比较（与 <c>SkillFamilyCluster</c> 同一理由）：<see cref="Members"/> 是集合字段，
    /// 而 <c>record</c> 自动生成的相等性对它用的是**引用相等** —— 那会让"两次判定结果一致"
    /// 这类断言**静默失效**（两个内容相同的记录被判为不等，用例反而可能因为断言写反而永远绿）。
    /// </summary>
    public bool Equals(SkillFamilyCapReview? other)
        => other is not null
            && string.Equals(FamilyKey, other.FamilyKey, StringComparison.Ordinal)
            && EnabledCount == other.EnabledCount
            && Cap == other.Cap
            && OverBy == other.OverBy
            && string.Equals(ReasonCode, other.ReasonCode, StringComparison.Ordinal)
            && string.Equals(PolicyRef, other.PolicyRef, StringComparison.Ordinal)
            && Members.SequenceEqual(other.Members, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(FamilyKey, StringComparer.Ordinal);
        hash.Add(EnabledCount);
        hash.Add(Cap);
        hash.Add(OverBy);
        hash.Add(ReasonCode, StringComparer.Ordinal);
        hash.Add(PolicyRef, StringComparer.Ordinal);
        foreach (var member in Members)
        {
            hash.Add(member, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// 一次家族上限判定的**完整结论**。
/// <para>
/// <see cref="Cap"/> 必须如实保留 <c>null</c>（不设限）：<b>"0 条评审"有两种完全不同的含义</b>
/// —— "本片没设限"与"设了限但所有家族都合规"。把它们压成同一个 <c>0</c>，
/// 读者就无法区分"判据没在工作"与"判据工作且无发现"。
/// </para>
/// </summary>
public sealed record SkillFamilyCapReviewReport
{
    /// <summary>本次判定使用的上限；<c>null</c> = **不设限**（任务书 I2）。</summary>
    public required int? Cap { get; init; }

    /// <summary>被评估的家族总数（含单成员家族与合规家族，用于说明评审数的分母）。</summary>
    public required int EvaluatedFamilyCount { get; init; }

    /// <summary>超限家族，按 <see cref="SkillFamilyCapReview.FamilyKey"/> 序数序升序。</summary>
    public required IReadOnlyList<SkillFamilyCapReview> Reviews { get; init; }

    /// <summary>策略引用 <c>policyId@version</c>。</summary>
    public required string PolicyRef { get; init; }

    /// <summary>按内容比较（理由同 <see cref="SkillFamilyCapReview.Equals"/>：<see cref="Reviews"/> 是集合字段）。</summary>
    public bool Equals(SkillFamilyCapReviewReport? other)
        => other is not null
            && Cap == other.Cap
            && EvaluatedFamilyCount == other.EvaluatedFamilyCount
            && string.Equals(PolicyRef, other.PolicyRef, StringComparison.Ordinal)
            && Reviews.SequenceEqual(other.Reviews);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Cap);
        hash.Add(EvaluatedFamilyCount);
        hash.Add(PolicyRef, StringComparer.Ordinal);
        foreach (var review in Reviews)
        {
            hash.Add(review);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// 家族内上限判据（**纯判定**：零 IO、零 LLM、零裸阈值、**零写盘**）。
/// <para>
/// 职责边界：本类型只回答「哪些家族的**启用**成员数超过了上限」，并把答案组织成**待裁决记录**。
/// 它不查技能仓、不改启用状态、不删技能、不改关键词、不发起合并 —— 那些职权全部属 **L3-b**
/// （任务书 §1 写盘职权、§2.3 冻结后果）。
/// </para>
/// <remarks>
/// <b>计数口径（必须显式声明，见任务书 §9-7）</b>：分子分母都取自**调用方传入的簇集合**，
/// 即"该集合所覆盖的全部启用技能"这一口径。⛔ 本判据**不隐式套用**既有合并通道的
/// <c>ConsolidateExistingAsync</c> 评审窗口（<c>.Take(50)</c>）—— 两个口径的结论可能不同，
/// 把窗口偷偷混进来会让"上限是否生效"取决于技能恰好排在第几行。
/// <para>
/// <b>为什么"不设限"要单独一条路径</b>：<c>PerFamilyCap = 0</c> 曾是未定义的哨兵值。
/// 若把它读成"上限 0"，则**每个非空家族**都立刻超限 ⇒ 一次性把全部多成员家族推进评审，
/// 而这是一次**没有任何断言会失败的静默行为爆炸**（任务书 §2.2）。故 <c>null</c> 直接短路。
/// </para>
/// </remarks>
/// <param name="policy">预算策略（<see cref="SkillPortfolioPolicy.PerFamilyCap"/> 是上限的唯一来源）。</param>
public sealed class SkillFamilyCapJudge(SkillPortfolioPolicy policy)
{
    private readonly SkillPortfolioPolicy _policy =
        policy ?? throw new ArgumentNullException(nameof(policy));

    /// <summary>本判定器使用的策略（供调用方落库/日志，说明结论用的是哪一版）。</summary>
    public SkillPortfolioPolicy Policy => _policy;

    /// <summary>
    /// 判定家族超限。返回**只读结论**，可直接作为"待裁决事实"上报，**不得**被当作执行指令。
    /// </summary>
    /// <remarks>
    /// 判定序：
    /// <list type="number">
    /// <item><c>PerFamilyCap is null</c> ⇒ 报告 <c>Cap = null</c> 且零评审（不设限，不是"上限 0"）；</item>
    /// <item>逐簇比较：<c>Size ≤ Cap</c> ⇒ 合规（**恰好等于上限不算超限**，设计原文是 <c>&gt;</c>）；</item>
    /// <item>超限簇 ⇒ 产出一条评审记录（家族键 / 成员 / 超限数 / 原因码 / <c>policyId@version</c>）。</item>
    /// </list>
    /// 输出按家族键序数序排序，与输入顺序无关（与 <c>SkillFamilyClusterer</c> 同一确定性纪律）。
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="clusters"/> 或其元素为 null。</exception>
    /// <exception cref="InvalidOperationException">策略配置非法（入口即校验，不依赖调用方先调 <c>Create</c>）。</exception>
    public SkillFamilyCapReviewReport Apply(IReadOnlyList<SkillFamilyCluster> clusters)
    {
        ArgumentNullException.ThrowIfNull(clusters);
        _policy.EnsureValid();

        var policyRef = _policy.PolicyId + "@" + _policy.Version.ToString(CultureInfo.InvariantCulture);

        if (_policy.PerFamilyCap is not { } cap)
        {
            return new SkillFamilyCapReviewReport
            {
                Cap = null,
                EvaluatedFamilyCount = clusters.Count,
                Reviews = [],
                PolicyRef = policyRef,
            };
        }

        var reviews = new List<SkillFamilyCapReview>();
        foreach (var cluster in clusters)
        {
            ArgumentNullException.ThrowIfNull(cluster);

            if (cluster.Size <= cap)
            {
                continue;
            }

            reviews.Add(new SkillFamilyCapReview
            {
                FamilyKey = cluster.FamilyKey,
                Members = cluster.Members,
                EnabledCount = cluster.Size,
                Cap = cap,
                OverBy = cluster.Size - cap,
                ReasonCode = "family_over_cap",
                PolicyRef = policyRef,
            });
        }

        reviews.Sort(static (left, right) => string.CompareOrdinal(left.FamilyKey, right.FamilyKey));

        return new SkillFamilyCapReviewReport
        {
            Cap = cap,
            EvaluatedFamilyCount = clusters.Count,
            Reviews = reviews,
            PolicyRef = policyRef,
        };
    }
}
