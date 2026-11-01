namespace PuddingToolGuidance;

/// <summary>
/// 规则求值器：把一次调用翻译成「最多 N 条提示」。
/// <para>
/// 为什么要有配额：提示是**挤占上下文**的成本。同一轮里贴五条只会让模型更乱，
/// 所以默认最多 3 条，且按规则 id 去重。
/// </para>
/// </summary>
public sealed class GuidanceEngine
{
    private readonly IReadOnlyList<IGuidanceRule> _rules;

    /// <param name="rules">规则集合；不传则用默认集（当前为 shell 方言组）。</param>
    /// <param name="maxAdvices">单次求值最多返回几条，默认 3，最小 1。</param>
    public GuidanceEngine(IEnumerable<IGuidanceRule>? rules = null, int maxAdvices = 3)
    {
        _rules = (rules ?? Rules.ShellDialectRules.All).ToArray();
        MaxAdvices = Math.Max(1, maxAdvices);
    }

    public int MaxAdvices { get; }

    public GuidelineSummary Evaluate(GuidanceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var advices = new List<GuidanceAdvice>();

        foreach (var rule in _rules)
        {
            if (advices.Count >= MaxAdvices)
                break;

            // 规则不得抛异常：热路径上抛一次就毁掉整个工具调用。范围兜底比信任实现更重要。
            GuidanceAdvice? advice;
            try
            {
                advice = rule.Evaluate(input);
            }
            catch (Exception)
            {
                continue;
            }

            if (advice is null || !seen.Add(advice.RuleId))
                continue;

            advices.Add(advice);
        }

        return new GuidelineSummary(advices);
    }
}

/// <summary>一次求值的结果。<see cref="HasAdvice"/> 为 false 表示「没什么可说的」，这是绝大多数情况。</summary>
public sealed record GuidelineSummary(IReadOnlyList<GuidanceAdvice> Advices)
{
    public bool HasAdvice => Advices.Count > 0;

    /// <summary>
    /// 把提示追加到工具输出末尾。**只追加文本**：不碰成功位、不碰退出码、不改原有内容。
    /// </summary>
    /// <param name="output">工具原有输出，可为空。</param>
    /// <param name="separator">原有输出非空时使用的分隔符。</param>
    public string AppendTo(string? output, string separator = "\n")
    {
        if (!HasAdvice)
            return output ?? string.Empty;

        var notes = string.Join(separator, Advices.Select(a => "NOTE: " + a.Message));
        return string.IsNullOrEmpty(output) ? notes : output + separator + notes;
    }
}
