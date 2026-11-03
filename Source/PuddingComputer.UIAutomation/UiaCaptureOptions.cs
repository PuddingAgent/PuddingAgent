namespace PuddingComputer.UIAutomation;

/// <summary>
/// 枚举的参数上限。
/// <para>
/// **必须全部有界**：UIA 遍历一个复杂应用（比如带虚拟化列表的 IDE）可能返回
/// 上万节点并耗时数秒，而不设界的遍历会把一个 UI 线程拖死。
/// </para>
/// </summary>
/// <param name="MaxDepth">最大子树深度，根为 0。</param>
/// <param name="MaxNodes">最多归一化多少个节点。</param>
/// <param name="Timeout">整体时限；超时后返回 <see cref="UiaAvailability.TimedOut"/>。</param>
public sealed record UiaCaptureOptions(
    int MaxDepth = 12,
    int MaxNodes = 2000,
    TimeSpan? Timeout = null)
{
    /// <summary>默认时限：3 秒。超过这个时间说明该应用的无障碍树不适合直接遍历。</summary>
    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromSeconds(3);

    /// <summary>参数自检。</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (MaxDepth < 0) problems.Add($"MaxDepth 必须 ≥0，实际 {MaxDepth}");
        if (MaxNodes < 1) problems.Add($"MaxNodes 必须 ≥1，实际 {MaxNodes}");
        if (EffectiveTimeout <= TimeSpan.Zero) problems.Add("Timeout 必须为正");
        return problems;
    }
}

/// <summary>
/// 只读的无障碍观察器。
/// <para>
/// 刻意是接口：真实实现依赖 FlaUI 与活动窗口，测试时用假实现喂固定快照，
/// 这样上层（排序、验证、orchestration）都能脱离桌面单测。
/// </para>
/// </summary>
public interface IUiaInspector
{
    /// <summary>
    /// 观察指定窗口的无障碍子树。
    /// </summary>
    /// <param name="windowHandle">已确认归属的顶层窗口句柄。</param>
    /// <param name="options">枚举上限。</param>
    /// <returns>归一化快照；失败时 <see cref="UiaSnapshot.Nodes"/> 为空并带 <see cref="UiaSnapshot.Reason"/>。</returns>
    UiaSnapshot Capture(IntPtr windowHandle, UiaCaptureOptions options);
}
