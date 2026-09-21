using PuddingCode.Skills.Retrieval;

namespace PuddingMemoryEngine.Services;

/// <summary>
/// 探针输入：一个技能的**关键词来源**（manifest 投影，<b>未经归一</b>）。
/// <para>
/// 刻意收原始字段（keywords/tags/skillId/name/enabled）而不是"已归一的集合"：
/// 归一只允许有<b>一份</b>实现（<see cref="SkillKeywordNormalization"/>，任务书 §2.4），
/// 若调用方自带一套归一再传进来，口径分叉不会有任何断言失败 —— 数字照样算得出来，只是换了一套口径。
/// </para>
/// </summary>
public sealed record SkillKeywordSubject
{
    /// <summary>技能 id（归一后无条件成为该技能的关键词之一，与原实现一致）。</summary>
    public required string SkillId { get; init; }

    /// <summary>显式关键词（最高优先级来源）。</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>治理/溯源标签（次级来源）。</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>名称（兜底来源，含分词）。</summary>
    public string? Name { get; init; }

    /// <summary>是否启用。<b>只有启用技能产生注入机会</b>，禁用技能必须整体排除。</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// 一个关键词的**归属事实**：声明它的<b>全部</b>技能。
/// <para>
/// ⚠️ <b>本类型刻意没有"实际命中者/胜者"字段</b>：真实命中者取决于运行时索引的构造顺序，
/// 只读探针不读运行时索引，任何"胜者"都只能是**近似**（G1 报告明确如此标注）。
/// 把近似值写成字段，读者就会把它当事实用 —— 而 G4 的裁决判据（D5）正需要在这里
/// 看清"到底有多少个竞争者"，不是把一个近似胜者当真。
/// </para>
/// </summary>
public sealed record SkillKeywordOwnership
{
    /// <summary>关键词（<b>小写归一后</b>的写法：G1 报告的计数口径即"小写归一后计"，也与输入顺序无关）。</summary>
    public required string Keyword { get; init; }

    /// <summary>声明该关键词的全部技能 id，**按序数序升序**（与输入顺序无关）。</summary>
    public required IReadOnlyList<string> DeclaringSkillIds { get; init; }

    /// <summary>声明数（见 <see cref="DeclaringSkillIds"/> 的完整清单，不是"第一个"）。</summary>
    public int DeclaredCount => DeclaringSkillIds.Count;

    /// <summary>被<b>先到先得</b>挤掉的注入机会数（<c>声明数 − 1</c>；单声明者为 0）。</summary>
    public int DisplacedCount => DeclaredCount - 1;

    /// <summary>是否被 ≥2 个启用技能共享（= 存在未裁决的归属冲突）。</summary>
    public bool IsShared => DeclaredCount >= 2;

    /// <summary>
    /// 按**内容**比较（与 <c>SkillFamilyCluster</c>/<c>SkillFamilyCapReview</c> 同一理由）：
    /// <see cref="DeclaringSkillIds"/> 是集合字段，而 <c>record</c> 自动生成的相等性对它用**引用相等**，
    /// 会让"两次分析结果一致"这类断言**静默失效**。
    /// </summary>
    public bool Equals(SkillKeywordOwnership? other)
        => other is not null
            && string.Equals(Keyword, other.Keyword, StringComparison.Ordinal)
            && DeclaringSkillIds.SequenceEqual(other.DeclaringSkillIds, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Keyword, StringComparer.Ordinal);
        foreach (var skillId in DeclaringSkillIds)
        {
            hash.Add(skillId, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// 一次关键词归属盘点的**完整事实**。
/// <para>
/// 报告同时给出"总量"与"明细"：总量（<see cref="SharedKeywordCount"/> /
/// <see cref="DisplacedInjectionCount"/>）<b>由明细推导</b>，不允许独立算一遍 ——
/// 否则两处口径可以悄悄分叉，而报告看起来仍然自洽。
/// </para>
/// </summary>
public sealed record SkillKeywordOwnershipReport
{
    /// <summary>被纳入统计的**启用**技能数（分母，用于说明口径；⛔ 不是评审窗口的子集）。</summary>
    public required int EnabledSkillCount { get; init; }

    /// <summary>
    /// 关键词槽位数 = Σ(每个启用技能的关键词集合大小)，**含重复声明、小写归一后计**
    /// （与 G1 报告同口径；空白关键词已按 <see cref="SkillKeywordNormalization.IsUsableKeyword"/> 排除，
    /// 因为它们不产生注入机会）。
    /// </summary>
    public required int SlotCount { get; init; }

    /// <summary>去重后的关键词数（= <see cref="Ownerships"/> 的条目数）。</summary>
    public required int DistinctKeywordCount { get; init; }

    /// <summary>被 ≥2 个启用技能共享的关键词数（G1 报告实测：165）。</summary>
    public required int SharedKeywordCount { get; init; }

    /// <summary>被"先到先得"静默挤掉的注入机会总数 = Σ(声明数 − 1)（G1 报告实测：1735）。</summary>
    public required int DisplacedInjectionCount { get; init; }

    /// <summary>
    /// **全部**关键词的归属明细（含只有 1 个声明者的），按关键词序数序升序。
    /// 刻意不做"只看共享关键词"的过滤：过滤掉的部分正是本报告要暴露的事实本身，
    /// 只列共享项就无法回答"有没有关键词被漏掉"。
    /// </summary>
    public required IReadOnlyList<SkillKeywordOwnership> Ownerships { get; init; }

    /// <summary>按内容比较（理由同 <see cref="SkillKeywordOwnership.Equals"/>：<see cref="Ownerships"/> 是集合字段）。</summary>
    public bool Equals(SkillKeywordOwnershipReport? other)
        => other is not null
            && EnabledSkillCount == other.EnabledSkillCount
            && SlotCount == other.SlotCount
            && DistinctKeywordCount == other.DistinctKeywordCount
            && SharedKeywordCount == other.SharedKeywordCount
            && DisplacedInjectionCount == other.DisplacedInjectionCount
            && Ownerships.SequenceEqual(other.Ownerships);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(EnabledSkillCount);
        hash.Add(SlotCount);
        hash.Add(DistinctKeywordCount);
        hash.Add(SharedKeywordCount);
        hash.Add(DisplacedInjectionCount);
        foreach (var ownership in Ownerships)
        {
            hash.Add(ownership);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// 关键词归属**只读事实探针**（任务书 §2.4-1：先暴露事实，且暴露的是"每个关键词的**全部**竞争者"）。
/// <para>
/// 纯函数：零 IO、零 LLM、零写盘、零缓存。它回答一个问题 ——
/// 「在给定这批技能下，哪些关键词被多个技能声明、每个关键词的声明者是谁、
/// 因此有多少次注入机会会被『<c>map[kw]</c> 先到先得」静默挤掉」。
/// </para>
/// <para>
/// ⛔ 它<b>不是</b>既有 map 构建结果的替代品，也不改动它：<c>SkillEnforcerService</c> 的关键词映射仍然
/// 由它自己的代码构建（本片明令不得改变其构建结果，且已有零回归用例钉住）。
/// 本探针的作用是**把先到先得掩盖掉的事实摆出来**，供 D5 的裁决判据使用。
/// </para>
/// <remarks>
/// <b>计数口径（必须显式声明，见任务书 §9-7）</b>：分母是<b>传入的、且 <c>Enabled</c> 为真的全部技能</b>。
/// ⛔ 探针<b>不套用</b>既有合并通道 <c>ConsolidateExistingAsync</c> 的评审窗口（<c>.Take(50)</c>）——
/// 窗口内的结论与全量结论可能不同，把窗口偷偷混进来会让"被挤掉多少次"取决于技能恰好排在第几行。
/// <para>
/// <b>与 G1 报告的关系</b>：G1 报告（<c>Docs/Reports/skill-portfolio-G1-2026-09-21.md</c>）的 165 / 1735
/// 用的就是同一口径（逐字对齐原 <c>CollectKeywords</c>）。真实索引上的逐数复核属 D4c。
/// </para>
/// <para>
/// <b>归一与小写</b>：分组用大小写不敏感比较器（且把键小写归一后再输出），因此
/// 「Alpha」与「alpha」是<b>同一个</b>关键词 —— 这与既有 map（<c>StringComparer.OrdinalIgnoreCase</c>）
/// 的合并行为一致；若按序数敏感分组，就会把"实际互相挤占"的关键词算成两个，导致归属于无声中变松。
/// </para>
/// <para>
/// <b>确定性</b>：<see cref="SkillKeywordOwnershipReport.Ownerships"/> 按关键词序数序升序，
/// 每个 <see cref="SkillKeywordOwnership.DeclaringSkillIds"/> 按技能 id 序数序升序 ⇒
/// 输出与输入顺序无关，"被挤掉数"与顺序无关（G1 亦如此声明）。
/// </para>
/// <para>
/// <b>失败即抛</b>：null 入参、null 元素、重复 skillId 一律抛异常，⛔ 不用"跳过 / 默认值"掩盖 ——
/// 掩盖会让统计数字变小，而报告本身看不出少了什么。
/// </para>
/// </remarks>
public static class SkillKeywordOwnershipProbe
{
    /// <summary>分析给定技能的**关键词归属事实**（只读；不改任何技能状态）。</summary>
    /// <exception cref="ArgumentNullException"><paramref name="subjects"/> 或其元素为 null。</exception>
    /// <exception cref="InvalidOperationException">同一启用技能出现多次（会让槽位与竞争者数被重复计数）。</exception>
    public static SkillKeywordOwnershipReport Analyze(IReadOnlyList<SkillKeywordSubject> subjects)
    {
        ArgumentNullException.ThrowIfNull(subjects);

        var owners = new Dictionary<string, HashSet<string>>(SkillKeywordNormalization.KeywordComparer);
        var seenSkillIds = new HashSet<string>(StringComparer.Ordinal);
        var enabledSkillCount = 0;
        var slotCount = 0;

        foreach (var subject in subjects)
        {
            ArgumentNullException.ThrowIfNull(subject);

            if (!subject.Enabled)
            {
                continue;
            }

            if (!seenSkillIds.Add(subject.SkillId))
            {
                throw new InvalidOperationException(
                    $"重复的 skillId：{subject.SkillId}（会让槽位与竞争者数被重复计数）");
            }

            enabledSkillCount++;

            // 归一只有一份实现：本探针不自己分词/去重，直接复用共享定义。
            var collected = SkillKeywordNormalization.Collect(
                subject.Keywords, subject.Tags, subject.SkillId, subject.Name);

            foreach (var keyword in collected)
            {
                if (!SkillKeywordNormalization.IsUsableKeyword(keyword))
                {
                    continue;
                }

                slotCount++;

                var normalized = keyword!.ToLowerInvariant();
                if (!owners.TryGetValue(normalized, out var declaringSkillIds))
                {
                    declaringSkillIds = new HashSet<string>(StringComparer.Ordinal);
                    owners[normalized] = declaringSkillIds;
                }

                declaringSkillIds.Add(subject.SkillId);
            }
        }

        var ownerships = new List<SkillKeywordOwnership>(owners.Count);
        foreach (var (keyword, declaringSkillIds) in owners)
        {
            var ids = declaringSkillIds.ToList();
            ids.Sort(StringComparer.Ordinal);
            ownerships.Add(new SkillKeywordOwnership
            {
                Keyword = keyword,
                DeclaringSkillIds = ids,
            });
        }

        ownerships.Sort(static (left, right) => string.CompareOrdinal(left.Keyword, right.Keyword));

        // 总量由明细推导 ⇒ 总量不可能与摆出来的事实分叉。
        var sharedKeywordCount = 0;
        var displacedInjectionCount = 0;
        foreach (var ownership in ownerships)
        {
            if (ownership.IsShared)
            {
                sharedKeywordCount++;
            }

            displacedInjectionCount += ownership.DisplacedCount;
        }

        return new SkillKeywordOwnershipReport
        {
            EnabledSkillCount = enabledSkillCount,
            SlotCount = slotCount,
            DistinctKeywordCount = ownerships.Count,
            SharedKeywordCount = sharedKeywordCount,
            DisplacedInjectionCount = displacedInjectionCount,
            Ownerships = ownerships,
        };
    }
}
