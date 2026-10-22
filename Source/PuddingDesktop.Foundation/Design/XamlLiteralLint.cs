using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PuddingDesktop.Foundation.Design;

/// <summary>一条字面量违规。<see cref="Line"/> 是 1 基行号，方便直接跳转。</summary>
public sealed record XamlLiteralFinding(string Rule, string Attribute, string Value, int Line, string Message);

/// <summary>
/// 扫描 XAML 里的视觉裸字面量，让"字号/圆角/颜色只有令牌一个来源"变成**能失败的测试**，
/// 而不是评审时靠眼睛数。
///
/// 刻意不引 WinUI：本类是纯文本分析，因此可以在 BCL-only 的 Foundation 工程里跑单元测试，
/// 不需要启动窗口，也不会碰到宿主正在使用的 XAML 产物。
///
/// 两种写法都要抓，漏一种就等于门禁有洞：
/// 1. 直接写在元素上：<c>FontSize="13"</c>；
/// 2. 写在样式里：<c>&lt;Setter Property="FontSize" Value="13"/&gt;</c>——
///    WinUI 里绝大多数视觉值其实住在样式 Setter 里，只扫属性会低估大半个文件。
/// </summary>
public static partial class XamlLiteralLint
{
    /// <summary>字号写成了裸数字。</summary>
    public const string RawFontSizeRule = "PDT-TOKEN-001";

    /// <summary>圆角写成了裸数字。</summary>
    public const string RawCornerRadiusRule = "PDT-TOKEN-002";

    /// <summary>画刷属性写成了裸颜色。</summary>
    public const string RawColorRule = "PDT-TOKEN-003";

    /// <summary>出现这些属性时，值必须是 <c>{StaticResource}</c> / <c>{ThemeResource}</c>。</summary>
    private static readonly HashSet<string> TokenizedAttributes = new(StringComparer.Ordinal)
    {
        "FontSize", "CornerRadius", "Background", "Foreground", "BorderBrush", "Fill", "Stroke"
    };

    [GeneratedRegex("""
        (?<attr>[A-Za-z][A-Za-z0-9.]*)\s*=\s*"(?<value>[^"]*)"
        """, RegexOptions.CultureInvariant)]
    private static partial Regex AttributePattern();

    /// <summary>匹配一个 <c>&lt;Setter .../&gt;</c> 元素体；属性顺序与换行都不敏感。</summary>
    [GeneratedRegex("""<Setter\b(?<body>[^>]*)>""", RegexOptions.CultureInvariant)]
    private static partial Regex SetterPattern();

    [GeneratedRegex("""^\d+(?:\.\d+)?$""", RegexOptions.CultureInvariant)]
    private static partial Regex PlainNumber();

    [GeneratedRegex("""^\s*\d+(?:\.\d+)?(\s*,\s*\d+(?:\.\d+)?)*\s*$""", RegexOptions.CultureInvariant)]
    private static partial Regex CornerRadiusLiteral();

    [GeneratedRegex("""^\s*#[0-9A-Fa-f]{3,8}\s*$""", RegexOptions.CultureInvariant)]
    private static partial Regex RawColor();

    [GeneratedRegex("""^\s*\{(StaticResource|ThemeResource|x:Bind|Binding)\s""", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceReference();

    /// <summary>扫描一段 XAML 文本，返回全部违规（保持文档顺序）。</summary>
    public static IReadOnlyList<XamlLiteralFinding> Scan(string xaml)
    {
        ArgumentNullException.ThrowIfNull(xaml);

        var found = new List<(int Index, XamlLiteralFinding Finding)>();
        foreach (Match match in AttributePattern().Matches(xaml))
        {
            Inspect(xaml, match.Groups["attr"].Value, match.Groups["value"].Value, match.Index, found, isSetter: false);
        }

        foreach (Match setter in SetterPattern().Matches(xaml))
        {
            var body = setter.Groups["body"];
            string? property = null;
            Match? value = null;
            foreach (Match attribute in AttributePattern().Matches(body.Value))
            {
                switch (attribute.Groups["attr"].Value)
                {
                    case "Property":
                        property = attribute.Groups["value"].Value;
                        break;
                    case "Value":
                        value = attribute;
                        break;
                }
            }

            if (property is null || value is null) continue;

            // Group.Index 本来就是相对原始输入串的，所以只需再加上 body 自己的起始位置；
            // body 在这里是另起一次 Matches 的输入，其内 Match.Index 才是相对 body 的。
            var valueIndex = body.Index + value.Index;
            Inspect(xaml, property, value.Groups["value"].Value, valueIndex, found, isSetter: true);
        }

        return found.OrderBy(item => item.Index).Select(item => item.Finding).ToList();
    }

    /// <summary>扫描磁盘上的 XAML 文件；文件不存在时抛 <see cref="FileNotFoundException"/>，不静默返回空。</summary>
    public static IReadOnlyList<XamlLiteralFinding> ScanFile(string path) =>
        Scan(File.ReadAllText(path, Encoding.UTF8));

    private static void Inspect(
        string xaml,
        string attribute,
        string value,
        int index,
        List<(int Index, XamlLiteralFinding Finding)> found,
        bool isSetter)
    {
        if (!TokenizedAttributes.Contains(attribute)) return;

        // 资源引用、绑定、空值、Auto/* 都是合法写法。
        if (value.Length == 0 || ResourceReference().IsMatch(value)) return;
        if (value.Trim() is "Auto" or "*" or "Transparent") return;

        var line = LineOf(xaml, index);
        var where = isSetter
            ? $"Setter Property=\"{attribute}\" Value=\"{value}\""
            : $"{attribute}=\"{value}\"";

        if (string.Equals(attribute, "FontSize", StringComparison.Ordinal) && PlainNumber().IsMatch(value))
        {
            var mapped = DesignTokens.MapFontSize(double.Parse(value, CultureInfo.InvariantCulture));
            found.Add((index, new(RawFontSizeRule, attribute, value, line,
                $"{where} 是裸数字；改用 {{StaticResource {mapped}}}。")));
            return;
        }

        if (string.Equals(attribute, "CornerRadius", StringComparison.Ordinal) && CornerRadiusLiteral().IsMatch(value))
        {
            // 四角写法（0,12,0,12）取最大值：决定形状的是最大的那一个角。
            var radius = value.Split(',')
                .Select(part => double.Parse(part.Trim(), CultureInfo.InvariantCulture))
                .Max();
            found.Add((index, new(RawCornerRadiusRule, attribute, value, line,
                $"{where} 是裸数字；改用 {{StaticResource {DesignTokens.MapCornerRadius(radius)}}}。")));
            return;
        }

        // SolidColorBrush 的 Color 属性就是画刷的定义处，那里允许写颜色字面量；
        // 使用处（Background/Foreground/...）才必须引用命名画刷。
        if (RawColor().IsMatch(value))
        {
            found.Add((index, new(RawColorRule, attribute, value, line,
                $"{where} 是裸颜色；改用命名画刷资源。")));
        }
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n') line++;
        }

        return line;
    }
}
