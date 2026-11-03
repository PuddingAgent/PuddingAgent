using System.Runtime.InteropServices;
using OpenCvSharp;
using Pudding.Contracts.Computer;

namespace PuddingComputer.Windows;

/// <summary>一个显示器的物理范围。</summary>
public sealed record MonitorBounds(PhysicalRect Bounds, bool IsPrimary);

/// <summary>
/// 枚举真实显示器范围。
/// <para>
/// 在 Per-Monitor V2 线程上下文中枚举，避免把 DPI 虚拟化坐标当作物理像素。
/// </para>
/// <para>
/// 多屏包围框可能包含无显示器区域；落点必须在至少一个显示器内，
/// 且移动后读回光标，防止系统夹取导致误点。
/// </para>
/// </summary>
public static class WindowsMonitorEnumerator
{
    public static IReadOnlyList<MonitorBounds> Enumerate()
    {
        using var dpi = WindowsDpiEnforcer.EnterPhysicalCoordinates();
        var list = new List<MonitorBounds>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref NativeRect rect, IntPtr data) =>
        {
            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            bool primary = GetMonitorInfo(hMon, ref info) && (info.dwFlags & MonitorInfoPrimary) != 0;

            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            if (w > 0 && h > 0)
                list.Add(new MonitorBounds(new PhysicalRect(rect.Left, rect.Top, w, h), primary));

            return true;
        }, IntPtr.Zero);

        return list;
    }

    /// <summary>屏幕坐标是否落在某个真实显示器内（用于点击前的最终校验）。</summary>
    public static bool IsOnRealMonitor(ScreenPoint point, IReadOnlyList<MonitorBounds>? monitors = null)
    {
        var list = monitors ?? Enumerate();
        int x = point.X;
        int y = point.Y;

        return list.Any(m =>
            x >= m.Bounds.X && x < m.Bounds.X + m.Bounds.Width &&
            y >= m.Bounds.Y && y < m.Bounds.Y + m.Bounds.Height);
    }

    private const uint MonitorInfoPrimary = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref NativeRect rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);
}
