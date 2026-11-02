using System.Runtime.InteropServices;
using Pudding.Contracts.Computer;
using PuddingComputer.Automation;

namespace PuddingComputer.Windows;

/// <summary>一次点击尝试的结局。刻意不是 bool —— 「为什么没点成」比「没点成」有用得多。</summary>
public enum ClickOutcome
{
    /// <summary>点击已发送。</summary>
    Clicked,

    /// <summary>目标不在窗口内。这类点击会被穿透到桌面，所以直接拒绝，不发事件。</summary>
    RejectedOutsideWindow,

    /// <summary>该坐标没有任何窗口（WindowFromPoint 返回 NULL）。</summary>
    RejectedNoWindowAtPoint,

    /// <summary>坐标不在屏幕范围内。</summary>
    RejectedOffScreen,
}

/// <summary>
/// 鼠标输入模拟。所有点击都必须先过三道检查，任一不过就**不发事件**并返回原因。
/// <para>
/// 这三道检查都来自 2026-11-02 的实测，不是预防性设计：
/// </para>
/// <list type="number">
/// <item><b>InsideWindow</b>：Chromium 会把内容渲染到窗口 rect 之外的合成层，
///   那部分不参与 Win32 命中测试。实测点发送键 (4483,1327) 时输入框毫无反应，
///   查下来 <c>WindowFromPoint</c> 返回 NULL。</item>
/// <item><b>WindowFromPoint 非空</b>：与上一条互补。前者靠已知窗口几何，
///   后者直接把问题问给系统。</item>
/// <item><b>屏幕范围</b>：副屏坐标可为负，越界时直接拒绝而不是让事件飞掉。</item>
/// </list>
/// <para>
/// 拒绝比"点了没反应"好：调用方至少能知道是坐标问题、窗口问题，还是目标本身不可点。
/// </para>
/// </summary>
public static class WindowsInputSimulator
{
    /// <summary>点击一个已标注目标。会先校验 <see cref="AnnotatedTarget.InsideWindow"/>。</summary>
    /// <param name="allowOutsideWindow">
    /// 允许点击标为可窗口外（合成层）的目标。见 <see cref="ClickAt"/> 的说明。
    /// </param>
    /// <param name="geometry">
    /// 生成这个 <paramref name="target"/> 的那次观察的图像几何。
    /// <para>
    /// **必须传**：<c>target.Clickable</c> 是图内坐标，不是屏幕坐标。
    /// 旧版直接把图内坐标交给 <see cref="ClickAt"/>，中间没有映射 ——
    /// 这就是“点击总是偏几百像素却从不报错”的根源（2026-11-03 实测定位）。
    /// 现在签名强制要求几何，漏传会直接编译失败。
    /// </para>
    /// </param>
    public static ClickOutcome Click(
        AnnotatedTarget target,
        ComputerImageGeometry geometry,
        bool allowOutsideWindow = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(geometry);

        if (!target.InsideWindow && !allowOutsideWindow)
            return ClickOutcome.RejectedOutsideWindow;

        // 先映射，再点。顺序不能反 —— 没映射就点等于用错坐标系。
        var mapped = ComputerCoordinateMapper.Map(geometry, target.Clickable);
        if (!mapped.IsOk)
            return ClickOutcome.RejectedOffScreen;

        return ClickAt(mapped.Point, allowOutsideWindow);
    }

    /// <summary>在屏幕坐标处点击。逐项检查后才发事件。</summary>
    /// <param name="allowWithoutWindow">
    /// 当 <c>WindowFromPoint</c> 返回 NULL 时是否仍然点击。
    /// <para>
    /// 默认 false。只在一种情况下需要开启：目标渲染在 Win32 窗口 rect 之外的合成层里。
    /// 实测 ChatGPT 的输入框/发送键就是这种：画面可见（D3D 层高 1434），
    /// 但所属窗口只报 1155 高，因此 <c>WindowFromPoint</c> 对整块区域返回 NULL。
    /// </para>
    /// <para>开启后风险明确：点击可能穿透到桌面。调用方应该知道自己在干什么。</para>
    /// </param>
    public static ClickOutcome ClickAt(ScreenPoint point, bool allowWithoutWindow = false)
    {
        if (!IsOnScreen(point))
            return ClickOutcome.RejectedOffScreen;

        if (!HasWindowAt(point) && !allowWithoutWindow)
            return ClickOutcome.RejectedNoWindowAtPoint;

        SetCursorPosition(point);
        Pause(200);

        SendMouseButton(down: true);
        Pause(90);                    // 真实按压通常有几十毫秒，太快会被某些应用忽略
        SendMouseButton(down: false);
        Pause(300);

        return ClickOutcome.Clicked;
    }

    /// <summary>把光标移到该点（不点击）。用于悬停后再决定的场景。</summary>
    public static ClickOutcome MoveCursorTo(ScreenPoint point)
    {
        if (!IsOnScreen(point))
            return ClickOutcome.RejectedOffScreen;

        SetCursorPosition(point);
        Pause(150);
        return ClickOutcome.Clicked;
    }

    private static void SetCursorPosition(ScreenPoint point) =>
        SetCursorPos(point.X, point.Y);

    private static void SendMouseButton(bool down) =>
        mouse_event(down ? MouseEventLeftDown : MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);

    private static void Pause(int ms) => Thread.Sleep(ms);

    private static bool IsOnScreen(ScreenPoint point)
    {
        // 必须用**真实显示器**范围，而不是 SM_*VIRTUALSCREEN。
        // 实测：SM_CYVIRTUALSCREEN=1440，但副屏只有 1152 高，
        // 落在 1152..1440 的坐标会被系统静默夹到 1151 —— 点击就跑偏了。
        return WindowsMonitorEnumerator.IsOnRealMonitor(point);
    }

    private static bool HasWindowAt(ScreenPoint point)
    {
        var native = new NativePoint
        {
            X = point.X,
            Y = point.Y
        };
        return WindowFromPoint(native) != IntPtr.Zero;
    }

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern void mouse_event(
        uint flags, int dx, int dy, uint data, UIntPtr extraInfo);
}
