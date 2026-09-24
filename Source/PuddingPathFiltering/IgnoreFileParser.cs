namespace PuddingPathFiltering;

/// <summary>
/// .gitignore 行解析器（纯函数，无 I/O）。逐条实现 gitignore(5) 的行级语义，
/// 行为以真实 <c>git check-ignore</c> 为 oracle 校准：
/// <list type="bullet">
/// <item>空行 / 全空白行不构成规则。</item>
/// <item>首个非空格字符是 <c>#</c> 的行是注释；<c>\#</c> 是字面量 <c>#</c>。</item>
/// <item>行尾空格被裁掉，除非被反斜杠转义（<c>foo\ </c>）；<b>行首空格有意义，不被裁掉</b>
///       （oracle 实测：模式 <c>   sptest.txt</c> 只命中带三个前导空格的同名文件）。</item>
/// <item><c>!</c> 前缀是取反；<c>\!</c> 是字面量 <c>!</c>。</item>
/// <item>行尾 <c>/</c> 表示「目录专属」，随后被剥掉。</item>
/// <item>行首 <c>/</c> 表示「锚定到本 .gitignore 所在目录」，随后被剥掉。</item>
/// </list>
/// </summary>
public static class IgnoreFileParser
{
    /// <summary>解析一份 .gitignore 的内容，返回按行序排列的规则（行序即优先级：后出现的胜出）。</summary>
    /// <param name="source">用于诊断/对照的来源名（通常是 .gitignore 的相对路径）。</param>
    /// <param name="baseDirectory">该 .gitignore 所在目录（相对忽略根；空串 = 忽略根）。</param>
    /// <param name="lines">文件行（不含换行符）。</param>
    /// <param name="ignoreCase">是否大小写折叠。</param>
    public static IReadOnlyList<IgnoreRule> Parse(
        string source,
        string baseDirectory,
        IEnumerable<string> lines,
        bool ignoreCase = false)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var rules = new List<IgnoreRule>();
        var normalizedBase = PathText.Normalize(baseDirectory);
        var lineNumber = 0;

        foreach (var rawLine in lines)
        {
            lineNumber++;
            // 兼容 CRLF：ReadLines 已去 \n，但保留的 \r 会破坏行尾空格裁剪与取反判定。
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            var rule = ParseLine(source, normalizedBase, lineNumber, line, ignoreCase);
            if (rule is not null)
                rules.Add(rule);
        }

        return rules;
    }

    /// <summary>把一份 .gitignore 的完整文本解析为规则。</summary>
    public static IReadOnlyList<IgnoreRule> ParseText(
        string source,
        string baseDirectory,
        string text,
        bool ignoreCase = false)
        => Parse(source, baseDirectory, SplitLines(text), ignoreCase);

    // 手写切行（不用 StringReader）：本文件属于「判定链零 I/O」的边界内，
    // 连 System.IO 的类型也不引入，使 ComponentBoundaryTests 的静态断言可以机械成立。
    internal static IReadOnlyList<string> SplitLines(string text)
    {
        if (text.Length == 0)
            return [];

        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
                continue;

            var end = i > start && text[i - 1] == '\r' ? i - 1 : i;
            lines.Add(text[start..end]);
            start = i + 1;
        }

        if (start < text.Length)
            lines.Add(text[start..]);

        return lines;
    }

    /// <summary>裁剪行尾未转义的空格（逐字对应 git dir.c 的 <c>trim_trailing_spaces()</c>）。</summary>
    public static string TrimTrailingSpaces(string line)
    {
        var lastSpace = -1;
        for (var i = 0; i < line.Length; i++)
        {
            switch (line[i])
            {
                case ' ':
                    if (lastSpace < 0)
                        lastSpace = i;
                    break;
                case '\\':
                    i++;
                    if (i >= line.Length)
                        return line; // 串尾反斜杠：git 直接返回，不裁剪
                    lastSpace = -1;
                    break;
                default:
                    lastSpace = -1;
                    break;
            }
        }

        return lastSpace < 0 ? line : line[..lastSpace];
    }

    /// <summary>解析单行；返回 <c>null</c> 表示该行不构成规则（注释 / 空行 / 只剩 <c>/</c>）。</summary>
    public static IgnoreRule? ParseLine(
        string source,
        string baseDirectory,
        int lineNumber,
        string rawLine,
        bool ignoreCase = false)
    {
        var line = TrimTrailingSpaces(rawLine);
        if (line.Length == 0)
            return null;

        var negated = false;
        var pattern = line;

        if (pattern[0] == '\\' && pattern.Length > 1 && (pattern[1] is '#' or '!'))
        {
            // 转义的首字符：剥掉反斜杠，按字面量处理。
            pattern = pattern[1..];
        }
        else if (pattern[0] == '#')
        {
            return null;
        }
        else if (pattern[0] == '!')
        {
            negated = true;
            pattern = pattern[1..];
        }

        var directoryOnly = pattern.EndsWith('/');
        if (directoryOnly)
            pattern = pattern.TrimEnd('/');

        var anchored = false;
        if (pattern.StartsWith('/'))
        {
            anchored = true;
            pattern = pattern.TrimStart('/');
        }

        if (pattern.Length == 0)
            return null;

        // 中间含 '/' 也属于锚定（gitignore(5)：分隔符出现在开头或中间即相对本文件所在目录）。
        anchored |= pattern.Contains('/');

        return new IgnoreRule(
            baseDirectory,
            source,
            lineNumber,
            line,
            negated,
            directoryOnly,
            anchored,
            pattern,
            ignoreCase);
    }
}
