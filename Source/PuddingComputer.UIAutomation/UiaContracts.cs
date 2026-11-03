using Pudding.Contracts.Computer;

namespace PuddingComputer.UIAutomation;

/// <summary>
/// 一次无障碍观察的可用性。
/// <para>
/// **四态而不是「成功/失败」两态**（2026-11-03 实测驱动）：
/// </para>
/// <list type="bullet">
/// <item>Chromium 只暴露 7 个 framework Panes，没有 Edit/Document 节点
///   —— 那是 <see cref="Sparse"/>，不是失败，但**绝不能**当成"有内容"。</item>
/// <item>UWP 应用的主窗口不在自己进程里（<c>MainWindowHandle == 0</c>）
///   —— 定位失败属于 <see cref="Unavailable"/>，需要换定位方式而不是重试。</item>
/// <item>枚举超时是 <see cref="TimedOut"/>，它**不等于**"这个应用没有无障碍树"。</item>
/// </list>
/// <para>
/// 把这四态压成布尔，调用方就分不清"该换渠道"和"该等一下再试"。
/// </para>
/// </summary>
public enum UiaAvailability
{
    /// <summary>无障碍树可正常读取，且给出的可操作节点足以支撑定位。</summary>
    Supported,

    /// <summary>读到了树，但可操作节点过少 —— 需要视觉/OCR 兜底，不能只靠它。</summary>
    Sparse,

    /// <summary>完全读不到（窗口无效、无 UIA 支持、或抛错）。</summary>
    Unavailable,

    /// <summary>枚举超时。**与 Unavailable 不同**，不能据此断言该应用不支持无障碍。</summary>
    TimedOut,
}

/// <summary>
/// 归一化后的无障碍节点。
/// <para>
/// 刻意只保留定位与判定需要的最小字段。原始 UIA 有几十个属性，
/// 全都搬进来会让快照巨大且难以比较；需要更多时再按用例加。
/// </para>
/// </summary>
/// <param name="Ref">本次快照内的稳定引用。执行前必须重新解析并复核归属。</param>
/// <param name="AutomationId">应用的自动化标识。**可能为空** —— 实测记事本编辑区就没有。</param>
/// <param name="Name">元素名。注意它可能随语言和值变化（计算器显示区就是"显示为 42"）。</param>
/// <param name="ControlType">控件类型，如 <c>Button</c> / <c>Text</c> / <c>Document</c>。</param>
/// <param name="ClassName">底层类名，如 <c>RichEditD2DPT</c>。</param>
/// <param name="Bounds">屏幕坐标下的包围盒。</param>
/// <param name="IsEnabled">是否可用。</param>
/// <param name="IsOffscreen">是否在屏幕外（不可见但仍存在于树中）。</param>
/// <param name="HasKeyboardFocus">是否持有键盘焦点。</param>
/// <param name="Patterns">支持的 UIA 模式，如 <c>Invoke</c> / <c>Value</c> / <c>SelectionItem</c>。</param>
/// <param name="ParentRef">父节点的 <see cref="Ref"/>；根为 null。</param>
public sealed record UiaNode(
    string Ref,
    string? AutomationId,
    string Name,
    string ControlType,
    string ClassName,
    PhysicalRect Bounds,
    bool IsEnabled,
    bool IsOffscreen,
    bool HasKeyboardFocus,
    IReadOnlyList<string> Patterns,
    string? ParentRef)
{
    /// <summary>
    /// 是否"可操作"：可用、可见、且至少支持一个动作类模式。
    /// <para>
    /// 判据故意宽松一点（Invoke/Value/SelectionItem/Toggle 任一即可），
    /// 因为漏判一个可点元素会让它彻底消失；多判一个只是多一个候选。
    /// </para>
    /// </summary>
    public bool IsActionable =>
        IsEnabled && !IsOffscreen && Patterns.Count > 0;
}

/// <summary>
/// 一次无障碍观察的结果。
/// </summary>
/// <param name="Availability">可用性四态。</param>
/// <param name="Nodes">归一化节点。按枚举顺序，父在子前。</param>
/// <param name="Reason">当不是 <see cref="UiaAvailability.Supported"/> 时的可读原因。</param>
public sealed record UiaSnapshot(
    UiaAvailability Availability,
    IReadOnlyList<UiaNode> Nodes,
    string? Reason)
{
    /// <summary>可操作节点数。</summary>
    public int ActionableCount => Nodes.Count(n => n.IsActionable);

    /// <summary>空快照（用于初始化或失败路径）。</summary>
    public static UiaSnapshot Empty(UiaAvailability availability, string reason) =>
        new(availability, [], reason);
}
