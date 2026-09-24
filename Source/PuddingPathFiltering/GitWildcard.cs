namespace PuddingPathFiltering;

/// <summary>
/// gitignore 通配符匹配：<c>git</c> 的 <c>wildmatch()</c> 在 <c>WM_PATHNAME</c>（路径分隔符为 <c>/</c>）下的行为，
/// 大小写折叠由参数控制。语义逐条与 git 对齐（以真实 <c>git check-ignore</c> 为 oracle，见
/// <c>PuddingPathFilteringTests</c> 的两份冻结语料）：
/// <list type="bullet">
/// <item><c>*</c> / <c>?</c> 不跨越 <c>/</c>。</item>
/// <item><c>**</c> 只在「模式开头」或「紧跟在 <c>/</c> 之后」<b>且</b>「后面是 <c>/</c> 或串尾」时才是「跨目录」双星；
///       其它位置退化为单星（不跨 <c>/</c>）。</item>
/// <item><c>**/</c> 可匹配零个目录（<c>a/**/b</c> 命中 <c>a/b</c> 与 <c>a/m/b</c>）。</item>
/// <item><c>[abc]</c> / <c>[a-c]</c> / <c>[!abc]</c> / <c>[^abc]</c>；<c>[]]</c> 的首个 <c>]</c> 是字面量；
///       未闭合的 <c>[</c> 判定为「不匹配」。</item>
/// <item><c>\x</c> 取 <c>x</c> 字面量；串尾孤立 <c>\</c> 判定为「不匹配」。</item>
/// <item>只有 <c>* ? [ \</c> 是特殊字符，其余（含 <c>{ }</c>）一律字面量。</item>
/// </list>
/// 本类型是纯函数：无 I/O、无状态、无静态可变缓存。
/// </summary>
public static class GitWildcard
{
    /// <summary>整串匹配（两侧都锚定）：<paramref name="pattern"/> 必须完整吃掉 <paramref name="text"/>。</summary>
    public static bool IsMatch(string pattern, string text, bool ignoreCase = false)
        => Match(pattern, 0, text, 0, ignoreCase) == Outcome.Match;

    private enum Outcome
    {
        /// <summary>该位置不匹配，调用方可以继续回溯。</summary>
        NoMatch,

        /// <summary>整体匹配。</summary>
        Match,

        /// <summary>该位置不匹配，且不必回溯（git 的 <c>WM_ABORT_ALL</c> / <c>WM_ABORT_TO_STARSTAR</c>）。</summary>
        Abort,
    }

    private static bool IsGlobSpecial(char c) => c is '*' or '?' or '[' or '\\';

    private static char Fold(char c, bool ignoreCase) => ignoreCase ? char.ToLowerInvariant(c) : c;

    private static char CharAt(string s, int index) => index < s.Length ? s[index] : '\0';

    // 逐字符回溯匹配，结构对应 git compat/wildmatch.c 的 dowild()。
    private static Outcome Match(string pattern, int pStart, string text, int tStart, bool ignoreCase)
    {
        var p = pStart;
        var t = tStart;

        while (p < pattern.Length)
        {
            var pc = pattern[p];
            var tc = CharAt(text, t);

            // git: if ((t_ch = *text) == '\0' && p_ch != '*') return WM_ABORT_ALL;
            if (tc == '\0' && pc != '*')
                return Outcome.Abort;

            pc = Fold(pc, ignoreCase);
            tc = Fold(tc, ignoreCase);

            switch (pc)
            {
                case '\\':
                    // 字面量转义；串尾孤立反斜杠 -> 不匹配（git 的 default 分支处理该失败）。
                    p++;
                    if (p >= pattern.Length)
                        return Outcome.NoMatch;
                    if (tc != Fold(pattern[p], ignoreCase))
                        return Outcome.NoMatch;
                    break;

                case '?':
                    // WM_PATHNAME：不匹配 '/'
                    if (tc == '/')
                        return Outcome.NoMatch;
                    break;

                case '*':
                {
                    p++; // 吃掉第一个 '*'

                    bool matchSlash;
                    if (CharAt(pattern, p) == '*')
                    {
                        var prev = p - 2; // 第一个 '*' 之前的字符
                        while (CharAt(pattern, p) == '*')
                            p++;

                        if ((prev < pStart || pattern[prev] == '/') &&
                            (CharAt(pattern, p) == '/' ||
                             CharAt(pattern, p) == '\0' ||
                             (CharAt(pattern, p) == '\\' && CharAt(pattern, p + 1) == '/')))
                        {
                            // 「**/」：也允许匹配零个目录 —— 先试着让剩余模式直接吃下剩余文本。
                            if (CharAt(pattern, p) == '/' &&
                                Match(pattern, p + 1, text, t, ignoreCase) == Outcome.Match)
                                return Outcome.Match;

                            matchSlash = true;
                        }
                        else
                        {
                            matchSlash = false; // 非边界的 '**' 退化为 '*'
                        }
                    }
                    else
                    {
                        matchSlash = false;
                    }

                    if (CharAt(pattern, p) == '\0')
                    {
                        // 串尾 '**' 匹配一切；串尾 '*' 只是不跨 '/'。
                        if (!matchSlash && text.IndexOf('/', t) >= 0)
                            return Outcome.NoMatch;
                        return Outcome.Match;
                    }

                    if (!matchSlash && CharAt(pattern, p) == '/')
                    {
                        // 「*」后紧跟 '/'：跳到下一段（该 '/' 由本层循环推进吃掉）。
                        var slash = text.IndexOf('/', t);
                        if (slash < 0)
                            return Outcome.NoMatch;
                        t = slash;
                        break;
                    }

                    while (true)
                    {
                        if (t >= text.Length)
                            return Outcome.Abort;

                        // 星号后是字面量时跳过不可能的位置（纯优化，不改语义）。
                        if (!IsGlobSpecial(CharAt(pattern, p)))
                        {
                            var literal = Fold(CharAt(pattern, p), ignoreCase);
                            while (t < text.Length && (matchSlash || text[t] != '/'))
                            {
                                if (Fold(text[t], ignoreCase) == literal)
                                    break;
                                t++;
                            }

                            if (t >= text.Length || Fold(text[t], ignoreCase) != literal)
                                return Outcome.NoMatch;
                        }

                        var matched = Match(pattern, p, text, t, ignoreCase);
                        if (matched != Outcome.NoMatch)
                        {
                            if (!matchSlash || matched != Outcome.Abort)
                                return matched;
                        }
                        else if (!matchSlash && text[t] == '/')
                        {
                            return Outcome.Abort;
                        }

                        t++;
                    }
                }

                case '[':
                {
                    if (tc == '\0')
                        return Outcome.Abort;

                    var i = p + 1;
                    var negated = false;
                    if (CharAt(pattern, i) is '!' or '^')
                    {
                        negated = true;
                        i++;
                    }

                    var matched = false;
                    var firstMember = true;
                    while (true)
                    {
                        if (i >= pattern.Length)
                            return Outcome.Abort; // 未闭合的字符类

                        if (pattern[i] == ']' && !firstMember)
                        {
                            i++;
                            break;
                        }

                        firstMember = false;

                        var low = pattern[i];
                        if (low == '\\' && i + 1 < pattern.Length)
                        {
                            i++;
                            low = pattern[i];
                        }

                        if (i + 2 < pattern.Length && pattern[i + 1] == '-' && pattern[i + 2] != ']')
                        {
                            var high = pattern[i + 2];
                            if (high == '\\' && i + 3 < pattern.Length)
                            {
                                high = pattern[i + 3];
                                i++;
                            }

                            i += 2;
                            if (InRange(low, high, tc, ignoreCase))
                                matched = true;
                        }
                        else if (Fold(low, ignoreCase) == tc)
                        {
                            matched = true;
                        }

                        i++;
                    }

                    if (matched == negated)
                        return Outcome.NoMatch;

                    p = i - 1; // 本层循环的 p++ 会跳过 ']'
                    break;
                }

                default:
                    if (tc != pc)
                        return Outcome.NoMatch;
                    break;
            }

            p++;
            t++;
        }

        return t >= text.Length ? Outcome.Match : Outcome.NoMatch;
    }

    private static bool InRange(char low, char high, char c, bool ignoreCase)
    {
        low = Fold(low, ignoreCase);
        high = Fold(high, ignoreCase);
        return low <= c && c <= high;
    }
}
