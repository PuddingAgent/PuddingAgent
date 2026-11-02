using System.Runtime.InteropServices;
using OpenCvSharp;
using Pudding.Contracts.Computer;

namespace PuddingComputer.Windows;

/// <summary>一个显示器的物理范围。</summary>
public sealed record MonitorBounds(PhysicalRect Bounds, bool IsPrimary);

/// <summary>
/// 枚举真实显示器范围。
/// <para>
/// **存在的理由**（2026-11-02 实测）：<c>SM_CYVIRTUALSCREEN</c> 报 1440，
/// 但副屏实际只有 1152 高。按虚拟屏尺寸截图会多截 288px 的**无效区域**，
/// 那片区域是 BitBlt 留下的残影 —— 看起来像真的控件，实际不存在。
/// </para>
/// <para>
/// 后果实测：`SetCursorPos(3000,1200)` 被系统夹到 `(3000,1151)`。
/// 也就是说任何落在无效区域的坐标，点击都会跑到屏幕边缘。
/// </para>
/// </summary>
public static class WindowsMonitorEnumerator
{
    public static IReadOnlyList<MonitorBounds> Enumerate()
    {
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
