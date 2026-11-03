using System.Text.Json.Serialization;
using Pudding.Contracts.Computer;

namespace PuddingComputer.Benchmark;

/// <summary>
/// 结果判定方式。**"像素变了"不是一种 oracle** —— 它只能说明屏幕动了，
/// 不能说明任务做成了。
/// <para>
/// 每种 oracle 都要求给出**任务专属的证据**：某个控件属性的值、某个对话框是否出现、
/// 某个文件的内容、某块区域是否发生预期变化。
/// </para>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(UiaPropertyOracle), "uia-property")]
[JsonDerivedType(typeof(ElementStateOracle), "element-state")]
[JsonDerivedType(typeof(WindowOrDialogOracle), "window-or-dialog")]
[JsonDerivedType(typeof(FileContentOracle), "file-content")]
[JsonDerivedType(typeof(VisualRegionOracle), "visual-region")]
public abstract record OutcomeOracle
{
    /// <summary>人类可读的一句话说明，用于报告与失败诊断。</summary>
    [JsonIgnore]
    public abstract string Description { get; }
}

/// <summary>
/// 无障碍属性断言 —— 最可靠的一种，因为值由应用自己提供，不受渲染影响。
/// </summary>
public sealed record UiaPropertyOracle(
    ElementSelector Selector,
    string Property,
    string Operator,
    string Expected) : OutcomeOracle
{
    public override string Description =>
        $"uia[{Selector}].{Property} {Operator} \"{Expected}\"";
}

/// <summary>
/// 元素状态断言（存在 / 可见 / 可用 / 选中 / 聚焦）。
/// </summary>
public sealed record ElementStateOracle(
    ElementSelector Selector,
    string State,
    bool Expected = true) : OutcomeOracle
{
    public override string Description =>
        $"uia[{Selector}].{State} == {Expected}";
}

/// <summary>
/// 窗口或对话框是否出现 / 消失。用于"保存对话框弹出了"这类模态流程。
/// </summary>
public sealed record WindowOrDialogOracle(
    string TitlePattern,
    bool ShouldExist = true) : OutcomeOracle
{
    public override string Description =>
        $"window \"{TitlePattern}\" {(ShouldExist ? "exists" : "absent")}";
}

/// <summary>
/// 文件内容断言 —— 对"导出 / 保存"类任务最直接。
/// </summary>
public sealed record FileContentOracle(
    string Path,
    string Operator,
    string Expected) : OutcomeOracle
{
    public override string Description =>
        $"file({Path}) {Operator} \"{Expected}\"";
}

/// <summary>
/// 视觉区域断言：**指定区域**发生**预期比例**的变化。
/// <para>
/// 这是尺度最弱的一种，所以它要求三样东西缺一不可：明确的区域、明确的阈值、
/// 以及"基准截图必须事先采集"的前提。任何一处模糊都会退化成
/// "屏幕动了就算成功"，那正是要避免的。
/// </para>
/// </summary>
public sealed record VisualRegionOracle(
    int X,
    int Y,
    int Width,
    int Height,
    double MinChangedRatio,
    int PixelThreshold = 20) : OutcomeOracle
{
    public override string Description =>
        $"region({X},{Y},{Width}x{Height}) changed >= {MinChangedRatio:P1} (pixel>={PixelThreshold})";
}
