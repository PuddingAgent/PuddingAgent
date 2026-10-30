namespace PuddingGrep;

/// <summary>
/// 匹配意图：字面量或正则。构造即校验 —— 非法正则、空字面量在**构造期**失败，
/// 不让它变成「运行时静默不命中」（那会被上层读成「没有匹配」）。
/// </summary>
public sealed class GrepPattern
{
    private readonly string? _validationError;

    private GrepPattern(string text, bool isRegex, bool caseSensitive, string? validationError)
    {
        Text = text;
        IsRegex = isRegex;
        CaseSensitive = caseSensitive;
        _validationError = validationError;
    }

    /// <summary>原始模式文本。</summary>
    public string Text { get; }

    /// <summary>是否按正则解释。</summary>
    public bool IsRegex { get; }

    /// <summary>是否区分大小写。</summary>
    public bool CaseSensitive { get; }

    /// <summary>构造期就存在的错误；非 null 时本模式不可用。</summary>
    public string? ValidationError => _validationError;

    public static GrepPattern Literal(string text, bool caseSensitive = false)
    {
        ArgumentNullException.ThrowIfNull(text);

        // 空字面量会匹配任何位置 —— 把它当成合法模式等于让调用方「搜了个通配」。
        // 上层要的是明确结果，因此这里判为无效而不是接受。
        var error = text.Length == 0 ? "literal pattern must not be empty" : null;
        return new GrepPattern(text, isRegex: false, caseSensitive, error);
    }

    public static GrepPattern Regex(string pattern, bool caseSensitive = false)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        string? error = null;
        if (pattern.Length == 0)
        {
            error = "regex pattern must not be empty";
        }
        else
        {
            try
            {
                // 只为了在构造期验证模式语法；Regex 不实现 IDisposable，不能 using。
                _ = new System.Text.RegularExpressions.Regex(
                    pattern,
                    caseSensitive ? System.Text.RegularExpressions.RegexOptions.None
                                  : System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch (ArgumentException ex)
            {
                error = $"invalid regex: {ex.Message}";
            }
        }

        return new GrepPattern(pattern, isRegex: true, caseSensitive, error);
    }
}
