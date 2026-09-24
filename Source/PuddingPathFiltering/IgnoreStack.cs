namespace PuddingPathFiltering;

/// <summary>
/// 忽略栈：一组按<b>优先级从低到高</b>排列的 <see cref="IgnoreRule"/>（根 .gitignore 在前、更深的在后；
/// 同一文件内按行序）。判定规则与 git 一致：
/// <list type="number">
/// <item><b>祖先剪枝优先</b>：若路径的任一祖先是目录且被忽略，则整棵子树被忽略 —— 此时任何
///       <c>!</c> 取反都无法把它救回来（gitignore(5)：<i>It is not possible to re-include a file if a parent
///       directory of that file is excluded</i>）。oracle 实测：<c>docs/**</c> + <c>!docs/keep/**</c> 下
///       <c>docs/keep/y.md</c> 仍被判为忽略，而 <c>.axoCover/*</c> + <c>!.axoCover/settings.json</c> 下
///       <c>.axoCover/settings.json</c> 被取反救回（因为 <c>.axoCover</c> 目录本身没被忽略）。</item>
/// <item><b>后命中者胜出</b>：否则取命中的最后一条规则，<c>Negated</c> 为真即「不忽略」，无命中即「不忽略」。</item>
/// </list>
/// 纯逻辑：无 I/O，不读文件。<see cref="GitIgnoreFileLoader"/> 负责把文件内容喂进来。
/// </summary>
public sealed class IgnoreStack
{
    /// <summary>空栈：任何路径都不被忽略。</summary>
    public static IgnoreStack Empty { get; } = new([]);

    private readonly IgnoreRule[] _rules;

    /// <summary>按「优先级从低到高」的顺序构造。</summary>
    public IgnoreStack(IEnumerable<IgnoreRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules.ToArray();
    }

    /// <summary>规则条数。</summary>
    public int Count => _rules.Length;

    /// <summary>规则快照（低优先级在前）。</summary>
    public IReadOnlyList<IgnoreRule> Rules => _rules;

    /// <summary>
    /// 对单条路径做「最后命中者胜出」判定；返回命中的规则，无命中返回 <c>null</c>。
    /// 不做祖先剪枝（内部由 <see cref="IsIgnored"/> 负责）。
    /// </summary>
    public IgnoreRule? LastMatch(string relativePath, bool isDirectory)
    {
        var normalized = PathText.Normalize(relativePath);
        if (normalized.Length == 0)
            return null;

        IgnoreRule? hit = null;
        foreach (var rule in _rules)
        {
            if (rule.Matches(normalized, isDirectory))
                hit = rule; // 后命中者覆盖
        }

        return hit;
    }

    /// <summary>
    /// 完整判定（含祖先剪枝）。<paramref name="relativePath"/> 相对本栈的忽略根。
    /// </summary>
    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        var normalized = PathText.Normalize(relativePath);
        if (normalized.Length == 0)
            return false;

        var scanner = new AncestorScanner(this);
        return scanner.IsIgnored(normalized, isDirectory);
    }

    /// <summary>等同 <see cref="IsIgnored"/>，但把「是哪一条规则 / 哪一个祖先导致的」一并返回（诊断用）。</summary>
    public bool IsIgnored(string relativePath, bool isDirectory, out string? reason)
    {
        var normalized = PathText.Normalize(relativePath);
        reason = null;
        if (normalized.Length == 0)
            return false;

        var scanner = new AncestorScanner(this);
        var ignored = scanner.IsIgnored(normalized, isDirectory, out var rule, out var culprit);
        if (ignored && rule is not null)
            reason = culprit is null ? rule.ToString() : $"{rule} (via ancestor '{culprit}')";
        return ignored;
    }

    /// <summary>
    /// 完整判定并返回「是哪条规则定的案」。用于诊断，以及与 <c>git check-ignore -v</c> 的逐条对照：
    /// git 对命中取反规则的路径也会打印该规则（表示「因此不忽略」），本方法同样把取反规则交还给调用方。
    /// </summary>
    public bool TryDecide(string relativePath, bool isDirectory, out IgnoreRule? rule, out string? culpritDirectory)
    {
        var normalized = PathText.Normalize(relativePath);
        rule = null;
        culpritDirectory = null;
        if (normalized.Length == 0)
            return false;

        var scanner = new AncestorScanner(this);
        return scanner.IsIgnored(normalized, isDirectory, out rule, out culpritDirectory);
    }

    private sealed class AncestorScanner(IgnoreStack stack)
    {
        public bool IsIgnored(string normalizedPath, bool isDirectory)
            => IsIgnored(normalizedPath, isDirectory, out _, out _);

        public bool IsIgnored(string normalizedPath, bool isDirectory, out IgnoreRule? rule, out string? culprit)
        {
            rule = null;
            culprit = null;

            var offset = 0;
            while (true)
            {
                var slash = normalizedPath.IndexOf('/', offset);
                if (slash < 0)
                    break;

                var ancestor = normalizedPath[..slash];
                var ancestorRule = stack.LastMatch(ancestor, isDirectory: true);
                if (ancestorRule is not null && !ancestorRule.Negated)
                {
                    rule = ancestorRule;
                    culprit = ancestor;
                    return true;
                }

                offset = slash + 1;
            }

            // 未命中任何规则 -> rule 保持 null；命中取反规则 -> 交还该规则但返回「不忽略」,
            // 这正是 git check-ignore -v 的展示语义（打印取反规则 = 因它而不忽略）。
            var selfRule = stack.LastMatch(normalizedPath, isDirectory);
            rule = selfRule;
            return selfRule is not null && !selfRule.Negated;
        }
    }
}
