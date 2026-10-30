using System.Text.RegularExpressions;

namespace PuddingGrep;

/// <summary>
/// ADR-089 R2-00：grep 的**独立匹配内核**——只回答「这一行是否命中、命中在哪」。
/// <para>
/// 刻意不知道：文件系统、目录排除、扩展名过滤、候选索引、Provider、最大结果数。
/// 那些是扫描/编排层的职责；混进来会让「同一语义在不同工具里各写一遍」的老问题复发。
/// </para>
/// <para>
/// 失败语义：模式无效时<b>抛</b> <see cref="ArgumentException"/>，返回「无匹配」是禁止的 ——
/// 上层无法区分「没匹配」与「模式坏了」，那正是错误否定的来源。
/// </para>
/// </summary>
public static class GrepMatcher
{
    /// <summary>
    /// 单次匹配的墙钟上限。正则可能灾难性回溯，没有它一次坏模式就能挂住扫描线程。
    /// </summary>
    public static readonly TimeSpan DefaultMatchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>该行是否命中。模式无效时抛 <see cref="ArgumentException"/>。</summary>
    public static bool IsMatch(GrepPattern pattern, string line, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(line);
        EnsureValid(pattern);

        return pattern.IsRegex
            ? CreateRegex(pattern, timeout).IsMatch(line)
            : line.Contains(pattern.Text, ComparisonFor(pattern));
    }

    /// <summary>
    /// 该行所有命中位置（按出现顺序，允许重叠吗？——**不允许**：与 <see cref="Regex.Matches(string)"/> 一致，
    /// 下一次搜索从上一个命中结束处继续）。
    /// </summary>
    public static IReadOnlyList<GrepSpan> FindAll(GrepPattern pattern, string line, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(line);
        EnsureValid(pattern);

        var spans = new List<GrepSpan>();

        if (pattern.IsRegex)
        {
            foreach (Match match in CreateRegex(pattern, timeout).Matches(line))
            {
                if (match.Success && match.Length > 0)
                    spans.Add(new GrepSpan(match.Index, match.Length));
            }

            return spans;
        }

        var comparison = ComparisonFor(pattern);
        var start = 0;
        while (start <= line.Length - pattern.Text.Length)
        {
            var index = line.IndexOf(pattern.Text, start, comparison);
            if (index < 0)
                break;

            spans.Add(new GrepSpan(index, pattern.Text.Length));
            start = index + pattern.Text.Length;
        }

        return spans;
    }

    private static void EnsureValid(GrepPattern pattern)
    {
        if (pattern.ValidationError is { } error)
            throw new ArgumentException(error, nameof(pattern));
    }

    private static StringComparison ComparisonFor(GrepPattern pattern) =>
        pattern.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static Regex CreateRegex(GrepPattern pattern, TimeSpan? timeout) =>
        new(pattern.Text,
            (pattern.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant,
            timeout ?? DefaultMatchTimeout);
}
