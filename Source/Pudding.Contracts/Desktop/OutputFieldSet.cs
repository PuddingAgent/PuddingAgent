namespace Pudding.Contracts.Desktop;

/// <summary>
/// 输出字段白名单的**通用**实现（设计方案 §6.2/§151：读取类工具都有 <c>fields</c>）。
///
/// 语义（各工具共用，避免每个工具各写一套解析与错误行为）：
/// ① <b>核心字段**恒在**</b>：调用方判断"能不能用/还有没有"所必需、且都很小的那些字段；
/// ② <b>可裁剪字段</b>才是 <c>fields</c> 的作用面（体积大头通常在这里）；
/// ③ 线名与工具输出的 **JSON 键一一对应**（调用方照着看到的键名写即可）；
/// ④ 未知线名**明确拒绝**（静默忽略会让调用方以为"字段真的没了"）；
/// ⑤ 全空白等价于**没给**（不能意外变成"只剩核心字段"）。
/// </summary>
public sealed class OutputFieldSet
{
    private readonly string[] _core;
    private readonly string[] _selectable;
    private readonly HashSet<string>? _included;

    public OutputFieldSet(IReadOnlyList<string> core, IReadOnlyList<string> selectable, HashSet<string>? included = null)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(selectable);

        _core = [.. core];
        _selectable = [.. selectable];
        All = [.. _core, .. _selectable];
        _included = included;
    }

    /// <summary>恒在的核心字段。</summary>
    public IReadOnlyList<string> Core => _core;

    /// <summary>可裁剪字段（<c>fields</c> 的合法取值）。</summary>
    public IReadOnlyList<string> Selectable => _selectable;

    /// <summary>全部字段名（核心 + 可裁剪）。</summary>
    public IReadOnlyList<string> All { get; }

    /// <summary>调用方是否显式给了白名单。</summary>
    public bool IsExplicit => _included is not null;

    /// <summary>该字段是否应出现在输出里（核心字段恒为真；未知名字恒为假）。</summary>
    public bool Includes(string name) =>
        All.Contains(name, StringComparer.Ordinal)
        && (_included is null
            || _core.Contains(name, StringComparer.Ordinal)
            || _included.Contains(name));

    /// <summary>供错误信息使用：可裁剪字段的清单。</summary>
    public string SelectableList => string.Join(", ", _selectable);

    /// <summary>核心字段清单（错误信息用）。</summary>
    public string CoreList => string.Join(", ", _core);

    /// <summary>
    /// 严格解析：未知字段名返回 <c>false</c> 并给出第一个未知项；空/未给 ⇒ 不裁剪（返回自身语义）。
    /// </summary>
    public bool TryParse(IEnumerable<string>? names, out OutputFieldSet fields, out string? unknown)
    {
        fields = this;
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

        fields = included.Count == 0 ? this : new OutputFieldSet(_core, _selectable, included);
        return true;
    }
}
