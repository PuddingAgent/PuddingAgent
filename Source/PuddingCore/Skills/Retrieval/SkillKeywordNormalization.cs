namespace PuddingCode.Skills.Retrieval;

/// <summary>
/// 技能关键词**归一**的唯一定义（RSI-G4 任务书 §2.4：G7-C3 与 G4 守卫必须共享同一来源，⛔ 不得各写一份）。
/// <para>
/// 它逐字复现原 <c>SkillEnforcerService.CollectKeywords</c> 的行为。那个行为不是"随手写的工具函数"，
/// 而是只读盘点报告 `Docs/Reports/skill-portfolio-G1-2026-09-21.md` 里 **165**（被 ≥2 技能共享的关键词数）
/// 与 **1735**（被先到先得挤掉的注入机会数）这两个数的**唯一口径**。
/// </para>
/// <para>
/// ⚠️ 因此本类型的第一约束是**逐字等价**，而不是"看起来更合理"：
/// 任何"顺手改进"（补 null 校验、改分词最小长度、去掉 Tags、把大小写敏感当更严谨）
/// 都会让 G4 的验收数字与 G1 报告**失去可比性**，而那种失效是**静默的** —— 数字仍然会算出来，只是换了一套口径。
/// </para>
/// <para>
/// ⛔ 本类型不做"哪些关键词是好关键词"的价值判断（工具名/治理标签的判别属 D2/D5 谓词，
/// 见 <c>SkillEvolutionDeduplicationService.IsToolLikeKeyword</c>）：它只负责"同一个关键词的同一个写法"。
/// </para>
/// </summary>
public static class SkillKeywordNormalization
{
    /// <summary>关键词比较器：大小写不敏感（与既有 `map`/`HashSet` 一致，⛔ 不得改为序数敏感）。</summary>
    public static readonly StringComparer KeywordComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// 名称分词用的分隔符（与 <c>SkillEnforcerService</c> 原实现逐字一致）。
    /// <para>
    /// ⚠️ 与 <c>PuddingCode.Skills.Family.SkillNameTokenization.StandardSeparators</c> 是**同一份字符集**的两处表达
    /// （由来：家族聚类口径同样来自该生产实现）。两处都必须保持相等，且**有专门用例钉住这一点** ——
    /// 否则将来只改一处，家族聚类与关键词归属就会用两套分词规则，且不会有任何断言失败。
    /// </para>
    /// </summary>
    public static IReadOnlyList<char> NameTokenSeparators { get; } = [' ', '|', ',', '/', '：', '、'];

    /// <summary>名称分词片段的**最小长度**（= 原实现 <c>trimmed.Length &gt; 1</c>，即长度 1 的片段被丢弃）。</summary>
    public const int MinimumNameTokenLength = 2;

    /// <summary>
    /// 关键词是否**可用于注入**（原实现 map 构建处的 <c>!string.IsNullOrWhiteSpace(kw)</c>）。
    /// <para>
    /// 抽出来的理由：G4 的归属探针（D4）与裁决判据（D5）必须与本处**同口径**统计"注入机会"，
    /// 否则探针会把空白关键词也算成一个可被挤掉的槽位，从而算不出 G1 的 1735。
    /// </para>
    /// </summary>
    public static bool IsUsableKeyword(string? keyword) => !string.IsNullOrWhiteSpace(keyword);

    /// <summary>
    /// 从技能元数据中提取关键词：<c>Keywords → Tags → SkillId → Name → Name 分词</c>。
    /// </summary>
    /// <remarks>
    /// ⚠️ 保留原实现的两个刻意的"不干净"之处，⛔ 不得顺手清理：
    /// <list type="number">
    /// <item><paramref name="skillId"/> <b>无条件</b>加入（含空白 id）—— 由调用方用 <see cref="IsUsableKeyword"/> 过滤；</item>
    /// <item>集合可能包含 <c>null</c> 与空白串（若 manifest 里就有）—— 同样由调用方过滤。
    /// 若在此处过滤，则"技能声明了多少槽位"这一事实会与 G1 报告的口径分叉。</item>
    /// </list>
    /// </remarks>
    public static HashSet<string> Collect(
        IReadOnlyList<string>? keywords,
        IReadOnlyList<string>? tags,
        string? skillId,
        string? name)
    {
        var collected = new HashSet<string>(KeywordComparer);

        // 1. 显式 Keywords（最高优先级）
        if (keywords is { Count: > 0 })
        {
            foreach (var keyword in keywords)
            {
                collected.Add(keyword);
            }
        }

        // 2. Tags（次级）
        if (tags is { Count: > 0 })
        {
            foreach (var tag in tags)
            {
                collected.Add(tag);
            }
        }

        // 3. SkillId + Name 作为兜底关键词
        collected.Add(skillId!);
        if (!string.IsNullOrWhiteSpace(name))
        {
            collected.Add(name);

            // 也加入单个词（如 "开发工作流" → "开发", "工作流"）
            foreach (var word in name.Split([.. NameTokenSeparators]))
            {
                var trimmed = word.Trim();
                if (trimmed.Length >= MinimumNameTokenLength)
                {
                    collected.Add(trimmed);
                }
            }
        }

        return collected;
    }
}
