namespace Pudding.Contracts.Computer;

/// <summary>
/// 执行后端。同一个动作在两个后端上的语义与可用性**不同**（设计 §5.2 的动作矩阵）。
/// <para>
/// <see cref="Uia"/> 走控件模式（Invoke / Value / Selection / Toggle / Scroll），
/// 语义明确、与窗口位置无关；<see cref="Input"/> 走合成输入（SendInput），
/// 需要绑定图片观察且依赖焦点与遮挡。
/// </para>
/// </summary>
public enum ComputerBackend
{
    Uia,
    Input,
}

/// <summary>
/// 动作种类。**不是所有动作都两个后端都支持** —— 见设计 §5.2：
/// <list type="bullet">
/// <item>只在 Uia 上成立：<c>Invoke</c> / <c>SetValue</c> / <c>Select</c> / <c>Toggle</c> / <c>Scroll</c></item>
/// <item>只在 Input 上成立：<c>Click</c> / <c>DoubleClick</c> / <c>RightClick</c> / <c>Move</c> / <c>TypeText</c> / <c>Press</c></item>
/// <item>首版两边都不支持：<c>Drag</c>（明确报 unsupported，不做半成品）</item>
/// </list>
/// </summary>
public enum ComputerAction
{
    /// <summary>控件语义点击（UIA）。用 Input 的 Click 表达"点这个像素"是另一回事，不要混用。</summary>
    Invoke,

    /// <summary>
    /// **替换**控件的值（可传空字符串表示清空）。
    /// <para>⚠️ 与 <see cref="TypeText"/> 不是同一件事：那是**插入**文本，空文本为 no-op。</para>
    /// </summary>
    SetValue,

    Select,
    Toggle,

    /// <summary>滚动（UIA 可报告边界；Input 走滚轮且必须先验证命中区域）。</summary>
    Scroll,

    Click,
    DoubleClick,
    RightClick,
    Move,

    /// <summary>
    /// **插入**文本（Input 后端，Unicode）。空文本是 no-op，**不清空**内容。
    /// <para>⚠️ 与 <see cref="SetValue"/> 刻意区分：把"插入空串"当成"清空"是常见的静默错误。</para>
    /// </summary>
    TypeText,

    /// <summary>按键或组合键（键名白名单）。不暴露跨调用的裸 key_down。</summary>
    Press,

    /// <summary>首版**明确不支持**，调用应返回 unsupported 而不是"尽力而为"。</summary>
    Drag,
}
