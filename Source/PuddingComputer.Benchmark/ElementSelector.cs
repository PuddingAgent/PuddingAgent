namespace PuddingComputer.Benchmark;

/// <summary>
/// 元素定位条件。所有给出的字段都必须匹配；至少给一个。
/// <para>
/// **为什么不能只用 AutomationId**（2026-11-03 实测）：新版 Windows 记事本的
/// 编辑区根本没有 AutomationId —— 它只能靠 <c>ControlType=Document</c> +
/// <c>ClassName=RichEditD2DPT</c> + <c>Name=文本编辑器</c> 三项一起定位。
/// 只认 AutomationId 的 schema 会把这类最常见的目标直接排除在外。
/// </para>
/// <para>
/// 反过来，只用 Name 也不行：Name 会随界面语言和值变化（计算器显示区的 Name
/// 就是"显示为 42"），拿它当唯一键会在值一变就失效。
/// </para>
/// </summary>
/// <param name="AutomationId">应用的自动化标识，最稳定但**可能不存在**。</param>
/// <param name="Name">元素名，注意它可能随语言/值变化。</param>
/// <param name="ControlType">控件类型，如 <c>Text</c> / <c>Button</c> / <c>Document</c>。</param>
/// <param name="ClassName">底层类名，如 <c>RichEditD2DPT</c>、<c>Button</c>。</param>
public sealed record ElementSelector(
    string? AutomationId = null,
    string? Name = null,
    string? ControlType = null,
    string? ClassName = null)
{
    /// <summary>是否至少给了一个定位条件。</summary>
    public bool HasAny =>
        !string.IsNullOrWhiteSpace(AutomationId)
        || !string.IsNullOrWhiteSpace(Name)
        || !string.IsNullOrWhiteSpace(ControlType)
        || !string.IsNullOrWhiteSpace(ClassName);

    /// <summary>人类可读描述，用于报告与失败诊断。</summary>
    public override string ToString()
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(AutomationId)) parts.Add($"id={AutomationId}");
        if (!string.IsNullOrWhiteSpace(Name)) parts.Add($"name={Name}");
        if (!string.IsNullOrWhiteSpace(ControlType)) parts.Add($"type={ControlType}");
        if (!string.IsNullOrWhiteSpace(ClassName)) parts.Add($"class={ClassName}");
        return parts.Count == 0 ? "<empty>" : string.Join(", ", parts);
    }
}
