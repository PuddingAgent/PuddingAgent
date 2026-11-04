using FlaUI.UIA2;

namespace PuddingComputer.UIAutomation;

/// <summary>
/// Chromium 系窗口的无障碍树不会被 UIA3 客户端唤醒：首次枚举往往只读到 0~1 个子节点，
/// 而先用 UIA2 客户端读一次之后，同一个 UIA3 客户端立刻能读到完整子树。
/// <para>
/// 为什么把它单独成类：观察器（<see cref="FlaUiInspector"/>）与写值器（<see cref="UiaValueWriter"/>）
/// 都必须走同一条唤醒通道。此前只有观察器做了预探测，写值器直接 <c>FromHandle</c>，
/// 于是同一个窗口「观察得到、写值报 NotFound」，且两边无法交叉验证。
/// </para>
/// <para>
/// 归因纪律：这里的「UIA2 客户端先探一次」是**动作顺序**层面的观测。
/// FlaUI.UIA2 实际引用的是托管 <c>UIAutomationClient</c>（2026-11-04 核实），
/// 不是 IAccessible，因此不能把差异解释成「MSAA 与 UIA 之别」；底层机制仍未单独证明。
/// </para>
/// </summary>
public static class UiaPreprobe
{
    /// <summary>
    /// 用 UIA2 客户端读一次根节点的子节点数。
    /// <para>
    /// 探测失败一律返回 0：这个数字只是「要不要继续等」的输入，
    /// 不是可用性判据 —— 读不到就照实当作没读到，不能当成应用没有无障碍能力。
    /// </para>
    /// </summary>
    public static int Probe(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
            return 0;

        try
        {
            using var automation = new UIA2Automation();
            var root = automation.FromHandle(windowHandle);
            return root?.FindAllChildren().Length ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 在取得 UIA3 根元素**之前**调用。必须在 <c>FromHandle</c> 之前，
    /// 因为唤醒前取得的根元素会缓存一个空子树，使本次采集直接失效。
    /// </summary>
    /// <param name="windowHandle">目标窗口句柄。</param>
    /// <param name="probe">可注入的探测器，仅用于测试；为 null 时走真实 UIA2。</param>
    public static void Wake(IntPtr windowHandle, Func<IntPtr, int>? probe = null)
    {
        try
        {
            _ = (probe ?? Probe)(windowHandle);
        }
        catch
        {
            // 唤醒失败不阻断主流程：后续的匹配与读回才是真实性判据。
        }
    }
}
