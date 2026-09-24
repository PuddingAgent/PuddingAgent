namespace PuddingPathFiltering;

/// <summary>
/// 一条已解析的 .gitignore 规则。构造只经由 <see cref="IgnoreFileParser"/>，以保证
/// 「注释 / 空行 / 行尾空格 / 取反 / 目录专属 / 锚定」的判定只实施一次。
/// </summary>
public sealed class IgnoreRule
{
    internal IgnoreRule(
        string baseDirectory,
        string source,
        int lineNumber,
        string rawLine,
        bool negated,
        bool directoryOnly,
        bool anchored,
        string pattern,
        bool ignoreCase)
    {
        BaseDirectory = baseDirectory;
        Source = source;
        LineNumber = lineNumber;
        RawLine = rawLine;
        Negated = negated;
        DirectoryOnly = directoryOnly;
        Anchored = anchored;
        Pattern = pattern;
        IgnoreCase = ignoreCase;
    }

    /// <summary>该规则所属 .gitignore 文件所在目录（相对于忽略根；空串 = 忽略根）。</summary>
    public string BaseDirectory { get; }

    /// <summary>规则来源（.gitignore 文件的展示路径），仅用于诊断与 oracle 对照。</summary>
    public string Source { get; }

    /// <summary>规则在源文件中的 1 基行号，仅用于诊断与 oracle 对照。</summary>
    public int LineNumber { get; }

    /// <summary>去除行尾空格后的原始行（含 <c>!</c> 前缀），用于与 <c>git check-ignore -v</c> 的输出逐字对照。</summary>
    public string RawLine { get; }

    /// <summary><c>!</c> 取反规则：命中它意味着「不忽略」。</summary>
    public bool Negated { get; }

    /// <summary>目录专属（源行以 <c>/</c> 结尾）：只对目录生效。</summary>
    public bool DirectoryOnly { get; }

    /// <summary>锚定：源行去掉首尾 <c>/</c> 后仍含 <c>/</c>，因此只在 <see cref="BaseDirectory"/> 下按整条相对路径匹配。</summary>
    public bool Anchored { get; }

    /// <summary>剥离首尾 <c>/</c> 之后的 glob 本体。</summary>
    public string Pattern { get; }

    /// <summary>是否大小写折叠（来源仓库的 <c>core.ignoreCase</c>）。</summary>
    public bool IgnoreCase { get; }

    /// <summary><c>git check-ignore -v</c> 的 pattern 字段：原始行（含取反前缀，不含行尾空格）。</summary>
    public string DiagnosticPattern => RawLine;

    /// <summary>
    /// 判断 <paramref name="relativePath"/>（相对忽略根、<c>/</c> 分隔、已规范化）是否命中本规则。
    /// 语义对应 git：锚定规则按「相对 BaseDirectory 的整条路径」匹配；非锚定规则只匹配<b>最后一段</b>。
    /// </summary>
    public bool Matches(string relativePath, bool isDirectory)
    {
        if (relativePath.Length == 0)
            return false;

        if (DirectoryOnly && !isDirectory)
            return false;

        if (!PathText.TryGetBelow(relativePath, BaseDirectory, out var rest))
            return false;

        var target = Anchored ? rest : PathText.Basename(rest);
        return GitWildcard.IsMatch(Pattern, target, IgnoreCase);
    }

    /// <inheritdoc />
    public override string ToString() => $"{Source}:{LineNumber}:{RawLine}";
}
