namespace Pudding.Contracts.Desktop;

/// <summary>
/// 快照输出的字段白名单（设计方案 §151：<c>browser_snapshot</c> 提供 <c>fields</c>）。
///
/// ① <b>核心字段恒在</b>：<c>mode</c>（到底读的哪种表示）、<c>truncated</c> 与
///    <c>truncationReason</c>（这份结果能不能信、缺不缺整片区域）、<c>nextCursor</c>（还有没有下一页）。
///    它们都很小，却是"能不能用"的判断依据，因此不可裁剪。
/// ② <b>可裁剪字段</b>：三种表示（<c>accessibilityTree</c>/<c>domText</c>/<c>html</c>）与三个预算事实
///    （<c>nodeCount</c>/<c>returnedLines</c>/<c>bytes</c>）。表示是 payload 的绝对大头，
///    因此设计要求「同次默认只返回一种表示」—— 用 <c>fields</c> 明确指定要哪一种即可。
/// </summary>
public static class BrowserSnapshotFields
{
    public static OutputFieldSet Definitions { get; } = new(
        core: ["mode", "truncated", "truncationReason", "nextCursor"],
        selectable: ["accessibilityTree", "domText", "html", "nodeCount", "returnedLines", "bytes"]);

    public static IReadOnlyList<string> Core => Definitions.Core;

    public static IReadOnlyList<string> Selectable => Definitions.Selectable;

    public static IReadOnlyList<string> All => Definitions.All;

    /// <summary>默认集合：不做裁剪（保持既有输出行为）。</summary>
    public static OutputFieldSet Default => Definitions;

    public static string SelectableList => Definitions.SelectableList;

    /// <summary>严格解析：未知字段名返回 <c>false</c> 并给出第一个未知项。</summary>
    public static bool TryParse(IEnumerable<string>? names, out OutputFieldSet fields, out string? unknown) =>
        Definitions.TryParse(names, out fields, out unknown);
}
