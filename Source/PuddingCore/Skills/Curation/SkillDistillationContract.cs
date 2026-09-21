namespace PuddingCode.Skills.Curation;

/// <summary>
/// 提炼产物 —— 技能整理的**输出货币**（设计 §14.5）。
/// <para>
/// 它与"记笔记式积累"的区别**不是风格问题**，而是四条可机械检查的差别：
/// ① 有<see cref="Markdown"/>中的适用条件段（何时该加载 / 何时不该）；
/// ② 程序写"做什么"而不是"用过哪些工具"；
/// ③ 有陷阱 / 反例段；
/// ④ <b>provenance 只进 <see cref="EvidenceTags"/></b>（审计元数据），<b>不进正文主结构、不进 <see cref="Keywords"/></b>。
/// </para>
/// <para>
/// <see cref="ReplacedSkillIds"/> **必须非空**才是"整理"：本类型描述的是"把多份既有技能提炼成一份"，
/// 而不是"再新增一份"（L3-a 已把「add 不是默认」变成字段级强制，见 <c>ImprovementOperation.Create</c> 的
/// <c>WhyNotUpdateOrMerge</c>）。
/// </para>
/// </summary>
public sealed record DistilledSkillProduct
{
    /// <summary>产物名称（技能名）。</summary>
    public required string Name { get; init; }

    /// <summary>产物正文（必须含适用条件段与陷阱段，见 <see cref="SkillDistillationContract"/>）。</summary>
    public required string Markdown { get; init; }

    /// <summary>用于自动匹配 / 预加载的关键词（不得含工具名、不得含会话或回合 id）。</summary>
    public required IReadOnlyList<string> Keywords { get; init; }

    /// <summary>
    /// 证据引用（provenance）：<b>唯一</b>允许携带 <c>source-turn:</c> / <c>source-session:</c> 的位置。
    /// <para>与既有创建路径同形（<c>SubconsciousOrchestrator</c> / <c>SkillEvolutionDeduplicationService</c> 写的正是这两个前缀的 tag）。</para>
    /// </summary>
    public required IReadOnlyList<string> EvidenceTags { get; init; }

    /// <summary>被本产物取代（合并 / 降级）的既有技能 id；驱动 C1/C2/C3 的判定域。</summary>
    public required IReadOnlyList<string> ReplacedSkillIds { get; init; }

    /// <summary>产物描述（可空）。</summary>
    public string? Description { get; init; }
}

/// <summary>
/// 提炼产物契约（反笔记不变式）—— <b>纯函数、零 IO、零 LLM</b>。
/// <para>
/// 判定语义（与 <c>SkillCurationPolicy</c> 的标题集合配合）：
/// 标题命中 = 正文按行扫描，去掉前导 <c>#</c> 与空白后，<b>以</b>策略给出的标题文本开头（大小写不敏感）。
/// </para>
/// <para>
/// ⚠️ <b>工具名判定由调用方注入</b>（<c>Func&lt;string, bool&gt;</c>）：工具名的唯一来源是
/// <c>SkillEvolutionDeduplicationService.ToolKeywordRegex()</c>（记忆引擎侧），而本文件在 Core 层、
/// **不得**反向依赖记忆引擎 ⇒ 由调用方传入既有正则的包装，保证"检测逻辑只有一份"。
/// </para>
/// <para>
/// ⚠️ <b>身份字面量同样由调用方注入</b>：<paramref name="forbiddenLiterals"/> 必须是从**真实 provenance**
/// 收集来的会话 / 回合 id 集合 —— 不得用正则去"猜"正文里有没有 id（那既不可测也取不了红）。
/// </para>
/// </summary>
public static class SkillDistillationContract
{
    /// <summary>违规码：缺少适用条件段（P1）。</summary>
    public const string MissingApplicabilitySection = "missing_applicability_section";

    /// <summary>违规码：缺少陷阱 / 反例段（P2）。</summary>
    public const string MissingPitfallSection = "missing_pitfall_section";

    /// <summary>违规码：正文出现身份字面量（P3）。</summary>
    public const string IdentityLiteralInContent = "identity_literal_in_content";

    /// <summary>违规码：关键词出现身份字面量（P3）。</summary>
    public const string IdentityLiteralInKeyword = "identity_literal_in_keyword";

    /// <summary>违规码：关键词出现工具名（P4）。</summary>
    public const string ToolNameInKeyword = "tool_name_in_keyword";

    /// <summary>违规码：没有任何 <c>source-turn:</c> 证据（P5）。</summary>
    public const string MissingProvenance = "missing_provenance";

    /// <summary>违规码：正文长度低于下限（P6）。</summary>
    public const string MarkdownTooShort = "markdown_too_short";

    /// <summary>违规码：关键词为空 —— 缺词意味着该技能永远无法被预加载 ⇒ 产物不可消费。</summary>
    public const string KeywordsEmpty = "keywords_empty";

    /// <summary>回合证据 tag 前缀（与既有创建路径相同）。</summary>
    public const string SourceTurnTagPrefix = "source-turn:";

    /// <summary>会话证据 tag 前缀（与既有创建路径相同）。</summary>
    public const string SourceSessionTagPrefix = "source-session:";

    /// <summary>
    /// 校验提炼产物是否满足契约。
    /// </summary>
    /// <returns>违规码集合（**空集合 = 通过**）；顺序固定，便于断言与回归。</returns>
    /// <exception cref="ArgumentNullException">产物 / 策略 / 工具名谓词为 null（fail-closed，不静默放行）。</exception>
    public static IReadOnlyList<string> Validate(
        DistilledSkillProduct product,
        IReadOnlyList<string> forbiddenLiterals,
        Func<string, bool> isToolLikeKeyword,
        SkillCurationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(isToolLikeKeyword);
        ArgumentNullException.ThrowIfNull(policy);
        policy.EnsureValid();

        var violations = new List<string>();

        var keywords = (product.Keywords ?? [])
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Select(keyword => keyword.Trim())
            .ToList();

        if (keywords.Count == 0)
        {
            violations.Add(KeywordsEmpty);
        }

        if ((product.Markdown?.Length ?? 0) < policy.MinMarkdownLength)
        {
            violations.Add(MarkdownTooShort);
        }

        if (!HasAnyHeading(product.Markdown, policy.ApplicabilityHeadings))
        {
            violations.Add(MissingApplicabilitySection);
        }

        if (!HasAnyHeading(product.Markdown, policy.PitfallHeadings))
        {
            violations.Add(MissingPitfallSection);
        }

        var literals = (forbiddenLiterals ?? [])
            .Where(literal => !string.IsNullOrWhiteSpace(literal))
            .Select(literal => literal.Trim())
            .ToList();

        if (literals.Count > 0)
        {
            var markdown = product.Markdown ?? string.Empty;
            if (literals.Any(literal => markdown.Contains(literal, StringComparison.OrdinalIgnoreCase)))
            {
                violations.Add(IdentityLiteralInContent);
            }

            if (keywords.Any(keyword => literals.Any(literal => keyword.Contains(literal, StringComparison.OrdinalIgnoreCase))))
            {
                violations.Add(IdentityLiteralInKeyword);
            }
        }

        if (keywords.Any(isToolLikeKeyword))
        {
            violations.Add(ToolNameInKeyword);
        }

        if (SourceTurnsOf(product.EvidenceTags ?? []).Count == 0)
        {
            violations.Add(MissingProvenance);
        }

        return violations.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>从证据 tag 中抽出回合 id（前缀 <c>source-turn:</c>，去重、忽略空白）。</summary>
    public static IReadOnlyList<string> SourceTurnsOf(IReadOnlyList<string> tags)
        => ExtractTagged(tags, SourceTurnTagPrefix);

    /// <summary>从证据 tag 中抽出会话 id（前缀 <c>source-session:</c>，去重、忽略空白）。</summary>
    /// <remarks>
    /// C2（一般性不降）的度量口径：<c>coveringSessions(x)</c> := 本方法与 <c>x.Tags</c> 算出的**去重计数**。
    /// ⚠️ 该口径**不是**"被注入过的会话数"（那需要遥测）；若将来改用遥测口径，**必须显式改写并版本化**。
    /// </remarks>
    public static IReadOnlyList<string> SourceSessionsOf(IReadOnlyList<string> tags)
        => ExtractTagged(tags, SourceSessionTagPrefix);

    private static IReadOnlyList<string> ExtractTagged(IReadOnlyList<string>? tags, string prefix)
        => (tags ?? [])
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Where(tag => tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(tag => tag[prefix.Length..].Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool HasAnyHeading(string? markdown, IReadOnlyList<string> headings)
    {
        if (string.IsNullOrWhiteSpace(markdown) || headings is not { Count: > 0 })
        {
            return false;
        }

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('#').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            foreach (var heading in headings)
            {
                var text = heading?.Trim();
                if (!string.IsNullOrEmpty(text) && line.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
