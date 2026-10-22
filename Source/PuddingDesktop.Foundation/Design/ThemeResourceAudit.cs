using System.Xml;
using System.Xml.Linq;

namespace PuddingDesktop.Foundation.Design;

/// <summary>一处 <c>{ThemeResource …}</c> 引用。<see cref="Line"/> 是 1 基行号。</summary>
public sealed record ThemeResourceReference(string Key, int Line);

/// <summary>
/// 主题字典的结构审计：回答两个 <see cref="XamlLiteralLint"/> 回答不了的问题。
///
/// 1. **两套主题的键集合与取值是否一致**——只给 <c>Default</c> 加键、忘了 <c>Light</c>，
///    是这类文件最常见的漂移；它的症状只是"某个主题下颜色不对"，肉眼极难发现。
/// 2. **<c>{ThemeResource X}</c> 里的 X 是否真的存在**——WinUI 对 ThemeResource 的解析是
///    **运行期**的，拼错一个字母不会编译失败，而是启动/切主题时抛 XamlParseException。
///    也就是说这一类错误在今天的门禁里是完全裸奔的。
///
/// 和 <see cref="XamlLiteralLint"/> 一样刻意不引 WinUI：纯 <c>System.Xml.Linq</c> 文本分析，
/// 因此能在 BCL-only 的 Foundation 工程里跑单元测试，不启动窗口、不碰宿主正在使用的 XAML 产物。
/// </summary>
public static class ThemeResourceAudit
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// WinUI 3 内建的、可以在 XAML 里直接引用的主题资源白名单。
    ///
    /// 这份名单是**显式登记的摩擦点**：新引用一个平台资源时必须在这里加一行，
    /// 否则 <see cref="FindUnresolvedReferences"/> 会把它当拼写错误报出来。
    /// 宁可每次多改一行，也不要让"找不到定义"和"平台自带"混成同一团灰。
    /// </summary>
    public static readonly IReadOnlySet<string> PlatformResources = new HashSet<string>(StringComparer.Ordinal)
    {
        // 卡片 / 控件的填充与描边
        "CardBackgroundFillColorDefaultBrush",
        "CardStrokeColorDefaultBrush",
        "ControlFillColorDefaultBrush",
        "ControlStrokeColorDefaultBrush",
        // 高对比模式用的系统色
        "SystemColorGrayTextColor",
        "SystemColorHighlightColor",
        "SystemColorHighlightTextColor",
        "SystemColorWindowColor",
        "SystemColorWindowTextColor",
        // 字体族
        "ContentControlThemeFontFamily",
        "SymbolThemeFontFamily",
    };

    /// <summary>读出每一套主题（<c>Default</c> / <c>Light</c> / <c>HighContrast</c>）里的画刷取值。</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ReadThemeBrushColors(string xaml)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var theme in ReadThemeDictionaries(Load(xaml)))
        {
            var colors = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var brush in theme.Value.Elements().Where(e => e.Name.LocalName == "SolidColorBrush"))
            {
                var key = (string?)brush.Attribute(XamlNamespace + "Key");
                var color = (string?)brush.Attribute("Color");
                if (key is not null && color is not null)
                {
                    colors[key] = color;
                }
            }

            result[theme.Key] = colors;
        }

        return result;
    }

    /// <summary>读出文件里**所有** <c>x:Key</c>（含非主题字典），它们都算"已声明"。</summary>
    public static IReadOnlySet<string> ReadDeclaredKeys(string xaml)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in Load(xaml).Descendants())
        {
            var key = (string?)element.Attribute(XamlNamespace + "Key");
            if (key is not null)
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    /// <summary>读出全部 <c>{ThemeResource …}</c> 引用（含 <c>Setter Value=</c> 里的写法），带行号。</summary>
    public static IReadOnlyList<ThemeResourceReference> ReadReferences(string xaml)
    {
        var references = new List<ThemeResourceReference>();
        foreach (var element in Load(xaml).Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                if (TryReadThemeResourceKey(attribute.Value, out var key))
                {
                    references.Add(new ThemeResourceReference(key, LineOf(attribute)));
                }
            }

            // 文本节点里也可能有 {ThemeResource}，例如 <Setter.Value> 的内联写法。
            if (element.Value is { Length: > 0 } text && !element.HasElements && TryReadThemeResourceKey(text.Trim(), out var textKey))
            {
                references.Add(new ThemeResourceReference(textKey, LineOf(element)));
            }
        }

        return references;
    }

    /// <summary>
    /// 找出 <c>Default</c> 与 <c>Light</c> 之间**键集漂移**：某个键只出现在一侧。
    ///
    /// 刻意**只比键集、不比取值**：两个字典本来就是同一批令牌在两套主题下的取值，
    /// <c>Default</c> 是深色、<c>Light</c> 是浅色，取值不同才是正常的（第一版把取值一起比，
    /// 直接被 8 个真键打红——这条规则是被实测纠回来的，别再改回去）。
    ///
    /// <c>HighContrast</c> 不参与比较——按 WinUI 规则它允许缺键（缺了回落 <c>Default</c>）。
    /// </summary>
    public static IReadOnlyList<string> FindBrushKeySetDrift(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> colorsByTheme)
    {
        if (!colorsByTheme.TryGetValue("Default", out var def) || !colorsByTheme.TryGetValue("Light", out var light))
        {
            return ["<missing theme dictionary: Default 与 Light 都必须存在>"];
        }

        var problems = new List<string>();
        foreach (var key in def.Keys.Union(light.Keys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!light.ContainsKey(key))
            {
                problems.Add($"{key}: 只在 Default 里声明（Light 缺失）");
            }
            else if (!def.ContainsKey(key))
            {
                problems.Add($"{key}: 只在 Light 里声明（Default 缺失）");
            }
        }

        return problems;
    }

    /// <summary>
    /// 找出解析不了的 <c>{ThemeResource}</c> 引用。
    /// 合法来源三处：本文件声明的键 ∪ <paramref name="externalKeys"/>（通常是 App.xaml 的键）∪ <see cref="PlatformResources"/>。
    /// </summary>
    public static IReadOnlyList<ThemeResourceReference> FindUnresolvedReferences(string xaml, IEnumerable<string>? externalKeys = null)
    {
        var known = new HashSet<string>(ReadDeclaredKeys(xaml), StringComparer.Ordinal);
        known.UnionWith(PlatformResources);
        if (externalKeys is not null)
        {
            known.UnionWith(externalKeys);
        }

        return ReadReferences(xaml).Where(r => !known.Contains(r.Key)).ToList();
    }

    private static IReadOnlyDictionary<string, XElement> ReadThemeDictionaries(XDocument document)
    {
        var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var holder in document.Descendants().Where(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries"))
        {
            foreach (var theme in holder.Elements().Where(e => e.Name.LocalName == "ResourceDictionary"))
            {
                if ((string?)theme.Attribute(XamlNamespace + "Key") is { } name)
                {
                    result[name] = theme;
                }
            }
        }

        return result;
    }

    private static XDocument Load(string xaml) => XDocument.Parse(xaml, LoadOptions.SetLineInfo);

    private static int LineOf(XObject node) => node is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;

    /// <summary>只认整串就是一个 <c>{ThemeResource Xxx}</c> 的写法；<c>{x:Bind …}</c> 之类的混合表达式不参与判定。</summary>
    private static bool TryReadThemeResourceKey(string? value, out string key)
    {
        key = string.Empty;
        if (value is null)
        {
            return false;
        }

        var text = value.Trim();
        const string prefix = "{ThemeResource ";
        if (!text.StartsWith(prefix, StringComparison.Ordinal) || !text.EndsWith('}'))
        {
            return false;
        }

        var inner = text[prefix.Length..^1].Trim();
        if (inner.Length == 0 || inner.Contains(' ') || inner.Contains('{', StringComparison.Ordinal))
        {
            return false;
        }

        key = inner;
        return true;
    }
}
