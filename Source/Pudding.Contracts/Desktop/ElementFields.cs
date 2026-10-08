namespace Pudding.Contracts.Desktop;

/// <summary>
/// 元素输出字段的**白名单**（设计方案 §6.2：locate 提供 <c>fields</c>）。
///
/// 两类字段刻意分开：
/// ① <b>核心字段</b>（<c>ref</c>/<c>tag</c>/<c>visible</c>/<c>enabled</c>）**永远返回**：
///    调用方要靠 <c>ref</c> 才能继续操作，靠 <c>visible</c>/<c>enabled</c> 才能判断能不能动；
///    它们也都是小字段，省下来没有意义。
/// ② <b>可裁剪字段</b>（其余）才是 <c>fields</c> 的作用面，其中 <c>text</c>/<c>name</c>/<c>boundingBox</c>
///    正是 payload 的大头。
///
/// 线名与工具输出里的 **JSON 键名一一对应**（调用方照着看到的键名写即可），未知线名一律拒绝、不猜测。
/// </summary>
public sealed class BrowserElementFields
{
    /// <summary>永远返回的核心字段。</summary>
    public static IReadOnlyList<string> Core { get; } = ["ref", "tag", "visible", "enabled"];

    /// <summary>可裁剪字段（<c>fields</c> 的合法取值）。</summary>
    public static IReadOnlyList<string> Selectable { get; } =
        ["role", "name", "text", "inViewport", "checked", "value", "selectedValues", "boundingBox"];

    public static IReadOnlyList<string> All { get; } = [.. Core, .. Selectable];

    private readonly HashSet<string>? _included;

    private BrowserElementFields(HashSet<string>? included) => _included = included;

    /// <summary>默认集合：不做裁剪（保持既有输出行为）。</summary>
    public static BrowserElementFields Default { get; } = new(included: null);

    /// <summary>调用方是否显式给了白名单。</summary>
    public bool IsExplicit => _included is not null;

    /// <summary>该字段是否应出现在输出里（核心字段恒为真；未知名字恒为假）。</summary>
    public bool Includes(string name) =>
        All.Contains(name, StringComparer.Ordinal)
        && (_included is null
            || Core.Contains(name, StringComparer.Ordinal)
            || _included.Contains(name));

    /// <summary>
    /// 严格解析：未知字段名返回 <c>false</c> 并给出第一个未知项（fail closed，不静默忽略）。
    /// 空/未给 ⇒ <see cref="Default"/>。
    /// </summary>
    public static bool TryParse(
        IEnumerable<string>? names,
        out BrowserElementFields fields,
        out string? unknown)
    {
        fields = Default;
        unknown = null;

        if (names is null)
        {
            return true;
        }

        var included = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in names)
        {
            var name = raw?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (!All.Contains(name, StringComparer.Ordinal))
            {
                unknown = name;
                return false;
            }

            included.Add(name);
        }

        // 全是空白 ⇒ 等价于没给（不要把它变成"只剩核心字段"这种意外行为）。
        fields = included.Count == 0 ? Default : new BrowserElementFields(included);
        return true;
    }

    /// <summary>供错误信息使用：可裁剪字段的清单。</summary>
    public static string SelectableList => string.Join(", ", Selectable);
}
