namespace Pudding.Contracts.Desktop;

/// <summary>
/// 元素输出的字段白名单（设计方案 §6.2：locate 提供 <c>fields</c>）。
///
/// 两类字段刻意分开：
/// ① <b>核心字段</b>（<c>ref</c>/<c>tag</c>/<c>visible</c>/<c>enabled</c>）**永远返回**：
///    调用方要靠 <c>ref</c> 才能继续操作，靠 <c>visible</c>/<c>enabled</c> 才能判断能不能动；
///    它们也都是小字段，省下来没有意义。
/// ② <b>可裁剪字段</b>（其余）才是 <c>fields</c> 的作用面，其中 <c>text</c>/<c>name</c>/<c>boundingBox</c>
///    正是 payload 的大头。
///
/// 线名与工具输出里的 **JSON 键名一一对应**（调用方照着看到的键名写即可），未知线名一律拒绝、不猜测。
/// 解析与错误语义在 <see cref="OutputFieldSet"/>（各读取类工具共用）。
/// </summary>
public static class BrowserElementFields
{
    /// <summary>字段定义（核心 + 可裁剪）。</summary>
    public static OutputFieldSet Definitions { get; } = new(
        core: ["ref", "tag", "visible", "enabled"],
        selectable: ["role", "name", "text", "inViewport", "checked", "value", "selectedValues", "boundingBox"]);

    /// <summary>永远返回的核心字段。</summary>
    public static IReadOnlyList<string> Core => Definitions.Core;

    /// <summary>可裁剪字段（<c>fields</c> 的合法取值）。</summary>
    public static IReadOnlyList<string> Selectable => Definitions.Selectable;

    public static IReadOnlyList<string> All => Definitions.All;

    /// <summary>默认集合：不做裁剪（保持既有输出行为）。</summary>
    public static OutputFieldSet Default => Definitions;

    /// <summary>供错误信息使用：可裁剪字段的清单。</summary>
    public static string SelectableList => Definitions.SelectableList;

    /// <summary>严格解析：未知字段名返回 <c>false</c> 并给出第一个未知项（fail closed，不静默忽略）。</summary>
    public static bool TryParse(IEnumerable<string>? names, out OutputFieldSet fields, out string? unknown) =>
        Definitions.TryParse(names, out fields, out unknown);
}
