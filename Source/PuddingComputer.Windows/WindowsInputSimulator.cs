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
    public static ClickOutcome Click(AnnotatedTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!target.InsideWindow)
            return ClickOutcome.RejectedOutsideWindow;

        return ClickAt(target.Clickable);
    }

    /// <summary>在屏幕坐标处点击。逐项检查后才发事件。</summary>
    public static ClickOutcome ClickAt(ImagePoint point)
    {
        if (!IsOnScreen(point))
            return ClickOutcome.RejectedOffScreen;

        if (!HasWindowAt(point))
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
    public static ClickOutcome MoveCursorTo(ImagePoint point)
    {
        if (!IsOnScreen(point))
            return ClickOutcome.RejectedOffScreen;

        SetCursorPosition(point);
        Pause(150);
        return ClickOutcome.Clicked;
    }

    private static void SetCursorPosition(ImagePoint point) =>
        SetCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y));

    private static void SendMouseButton(bool down) =>
        mouse_event(down ? MouseEventLeftDown : MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);

    private static void Pause(int ms) => Thread.Sleep(ms);

    private static bool IsOnScreen(ImagePoint point)
    {
        int x = GetSystemMetrics(SmXVirtualScreen);
        int y = GetSystemMetrics(SmYVirtualScreen);
        int w = GetSystemMetrics(SmCxVirtualScreen);
        int h = GetSystemMetrics(SmCyVirtualScreen);

        return point.X >= x && point.X < x + w
            && point.Y >= y && point.Y < y + h;
    }

    private static bool HasWindowAt(ImagePoint point)
    {
        var native = new NativePoint
        {
            X = (int)Math.Round(point.X),
            Y = (int)Math.Round(point.Y)
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
