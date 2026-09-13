// ADR-089 U0 残差 G1：统一 glob 匹配合同（纯新增，未接入任何既有工具）。
// 背景：仓库现存三套互不相同的文件模式语义（G1 任务书第 2 节取证）——
//   A) SearchGrepTool 候选准入：FileSystemName.MatchesSimpleExpression（仅文件名、无通配时精确）；
//   B) Directory.GetFiles 的 Win32 searchPattern（*.txt 会匹配 a.txtx）；
//   C) FileSearchTool.FileSearchPatternMatcher（无通配时子串包含）。
// 本类型是三者未来的唯一替代品（迁移留 G2/G3 切片），语义逐条实现任务书第 4 节 canonical 规范。
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

// 任务书要求本类型为 internal 且测试项目可见。仓库现状（PuddingCore.csproj 与既有 .cs）没有
// 任何 InternalsVisibleTo 声明，故在本新增文件内以程序集属性授予 PuddingCoreTests：
// 不修改任何现有文件、不改 csproj，也不把类型放宽为 public。
[assembly: InternalsVisibleTo("PuddingCoreTests")]

namespace PuddingCode.Tools.Retrieval;

/// <summary>
/// 统一 glob 匹配合同（ADR-089 G1，canonical 规范逐条落地）：
/// <list type="bullet">
/// <item>规范 1：null / 空串 / 全空白 glob → 匹配一切。</item>
/// <item>规范 2：<c>**</c>、<c>**/</c>、<c>**\</c> 前缀剥离后再匹配。</item>
/// <item>规范 3：通配符仅 <c>*</c>（任意长度，可为 0）与 <c>?</c>（恰好一个字符）；
///       <c>[</c> <c>]</c> <c>{</c> <c>}</c> 等一律按字面处理。</item>
/// <item>规范 4：glob 不含路径分隔符 → 只匹配文件名。</item>
/// <item>规范 5：glob 含路径分隔符 → 匹配相对路径，比较前两侧分隔符统一归一为 /。</item>
/// <item>规范 6：glob 无任何通配符 → 精确比较（禁止子串包含语义）。</item>
/// <item>规范 7：大小写由 <c>ignoreCase</c> 控制（默认 true）；恒用 Ordinal/OrdinalIgnoreCase 与
///       RegexOptions.CultureInvariant，禁止 culture 敏感折叠。</item>
/// <item>规范 8：<c>*</c> 与 <c>?</c> 不跨越路径分隔符（文件名目标天然满足；相对路径目标用
///       [^/] 字符类保证）。</item>
/// <item>规范 9：正则编译并缓存，缓存键 = (glob, 目标类型, ignoreCase)，有界（512 条，超限整体清空）。</item>
/// </list>
/// </summary>
internal static class RetrievalGlobMatcher
{
    /// <summary>正则缓存条目上限（规范 9）。淘汰策略：达到上限时整体清空重建——
    /// 代价仅是重新编译若干正则，正确性不受影响；Count 检查与 Clear 之间的并发竞态
    /// 只会导致缓存短暂超过上限，可接受。</summary>
    private const int RegexCacheCapacity = 512;

    private static readonly ConcurrentDictionary<CacheKey, Regex> RegexCache = new();

    /// <summary>缓存键（规范 9）：(归一化 glob, 目标类型, ignoreCase)。</summary>
    private readonly record struct CacheKey(string NormalizedGlob, GlobTargetKind TargetKind, bool IgnoreCase);

    /// <summary>匹配目标种类：文件名或相对路径。作为缓存键组成部分，防止不同目标的正则互相污染。</summary>
    private enum GlobTargetKind
    {
        FileName,
        RelativePath,
    }

    /// <summary>glob 是否含通配符。仅 <c>*</c> 与 <c>?</c> 算通配（规范 3）；null/空串 → false。</summary>
    public static bool HasWildcards(string? glob)
    {
        if (string.IsNullOrEmpty(glob))
        {
            return false;
        }

        foreach (var ch in glob)
        {
            if (ch is '*' or '?')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 剥离 <c>**</c> / <c>**\</c> 前缀并把 <c>\</c> 归一为 <c>/</c>（规范 2/5）。null/空串返回空串。
    /// 仅剥离规范定义的三种前缀：<c>**</c>（整串）、<c>**/</c>、<c>**\</c>；
    /// 其余写法（如 <c>**a</c>）不剥离，按通配符字面语义处理。
    /// </summary>
    public static string Normalize(string? glob)
    {
        if (string.IsNullOrEmpty(glob))
        {
            return string.Empty;
        }

        var normalized = glob.Replace('\\', '/');
        if (normalized == "**")
        {
            return string.Empty;
        }

        return normalized.StartsWith("**/", StringComparison.Ordinal) ? normalized[3..] : normalized;
    }

    /// <summary>
    /// 按规范 4/5 选择目标并判断：无分隔符 → 文件名；含分隔符 → 相对路径（两侧已归一为 /）。
    /// glob 含分隔符而 <paramref name="relativePath"/> 未提供时返回 false（不能假装命中）。
    /// </summary>
    public static bool Matches(string fileName, string? relativePath, string? glob, bool ignoreCase = true)
    {
        if (IsMatchAll(glob))
        {
            return true;
        }

        var normalized = Normalize(glob);
        // 分隔符判断必须用归一化后的 glob：**/x 剥离后是 x（文件名语义），而非含分隔符。
        if (normalized.Contains('/'))
        {
            return relativePath is not null
                && MatchCore(relativePath.Replace('\\', '/'), normalized, GlobTargetKind.RelativePath, ignoreCase);
        }

        return MatchCore(fileName, normalized, GlobTargetKind.FileName, ignoreCase);
    }

    /// <summary>仅按文件名判断（供只需要文件名语义的调用方）。含分隔符的 glob 对文件名目标恒不命中。</summary>
    public static bool MatchesFileName(string fileName, string? glob, bool ignoreCase = true)
    {
        if (IsMatchAll(glob))
        {
            return true;
        }

        var normalized = Normalize(glob);
        return MatchCore(fileName, normalized, GlobTargetKind.FileName, ignoreCase);
    }

    /// <summary>相对路径判断（目标分隔符已归一为 /）。</summary>
    public static bool MatchesRelativePath(string relativePath, string? glob, bool ignoreCase = true)
    {
        if (IsMatchAll(glob))
        {
            return true;
        }

        var normalized = Normalize(glob);
        return MatchCore(relativePath.Replace('\\', '/'), normalized, GlobTargetKind.RelativePath, ignoreCase);
    }

    /// <summary>规范 1 + 规范 2：null/空/全空白 glob，或前缀剥离后为空（如 <c>**</c>、<c>**/</c>）→ 匹配一切。</summary>
    private static bool IsMatchAll(string? glob) =>
        string.IsNullOrWhiteSpace(glob) || Normalize(glob).Length == 0;

    /// <summary>规范 6/9 的公共核心：无通配符走精确比较（不经正则），有通配符走编译缓存正则。</summary>
    private static bool MatchCore(string target, string normalizedGlob, GlobTargetKind targetKind, bool ignoreCase)
    {
        if (!HasWildcards(normalizedGlob))
        {
            return string.Equals(
                target,
                normalizedGlob,
                ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        // Win32 searchPattern 的历史规范化：整串 *.* 与 * 等价（8.3 时代所有文件名均含点）。
        // 任务书第 6 节用例 4 明确要求该语义（*.* 必须命中无扩展名的 Makefile，以区别于实现 A），
        // 且 G2 迁移后 SearchGrepTool 默认 filePattern="*.*" 不得静默丢失无扩展名文件。
        // 注意：这与第 4 节规范 3「. 按字面」的字面读法存在张力，按验收用例执行并已在交付报告上报。
        // 特判范围仅限整串 *.*，其它含点模式（如 a.* 、*.*x）一律按字面处理。
        var effective = normalizedGlob == "*.*" ? "*" : normalizedGlob;
        return GetOrAddRegex(effective, targetKind, ignoreCase).IsMatch(target);
    }

    private static Regex GetOrAddRegex(string normalizedGlob, GlobTargetKind targetKind, bool ignoreCase)
    {
        if (RegexCache.Count >= RegexCacheCapacity)
        {
            RegexCache.Clear();
        }

        return RegexCache.GetOrAdd(
            new CacheKey(normalizedGlob, targetKind, ignoreCase),
            static key => CompileRegex(key.NormalizedGlob, key.IgnoreCase));
    }

    /// <summary>把归一化 glob 编译为整串锚定正则（规范 3/7/8）。</summary>
    private static Regex CompileRegex(string normalizedGlob, bool ignoreCase)
    {
        var sb = new StringBuilder(normalizedGlob.Length * 3 + 8);
        sb.Append(@"\A");
        foreach (var ch in normalizedGlob)
        {
            if (ch == '*')
            {
                sb.Append("[^/]*"); // 规范 3/8：任意长度（可为 0）且不跨路径分隔符
            }
            else if (ch == '?')
            {
                sb.Append("[^/]"); // 规范 3/8：恰好一个字符且不跨路径分隔符
            }
            else
            {
                sb.Append(Regex.Escape(ch.ToString())); // 规范 3：其余字符（含 []{}{}）按字面
            }
        }

        sb.Append(@"\z");
        var options = RegexOptions.Compiled | RegexOptions.CultureInvariant; // 规范 7：禁止 culture 敏感折叠
        if (ignoreCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        return new Regex(sb.ToString(), options);
    }
}
