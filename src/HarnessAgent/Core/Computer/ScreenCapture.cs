using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace HarnessAgent.Core.Computer;

public sealed record MonitorInfo
{
    public required IntPtr Handle { get; init; }
    public required Rectangle Bounds { get; init; }
    public required Rectangle WorkingArea { get; init; }
    public required bool IsPrimary { get; init; }
    public required string DeviceName { get; init; }
}

public sealed class ScreenCapture
{
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hDst, int x, int y, int w, int h,
        IntPtr hSrc, int x1, int y1, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObj);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hDC, IntPtr lprcClip,
        MonitorEnumProc lpfnEnum, IntPtr dwData);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor,
        ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private const uint SRCCOPY = 0x00CC0020;
    private const int MONITORINFOF_PRIMARY = 1;

    private static readonly List<MonitorInfo> _pending = new();

    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        _pending.Clear();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, EnumProc, IntPtr.Zero);
        return _pending.ToList();
    }

    private static bool EnumProc(IntPtr hMonitor, IntPtr hdcMonitor,
        ref RECT lprcMonitor, IntPtr dwData)
    {
        var mi = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
        if (GetMonitorInfo(hMonitor, ref mi))
        {
            _pending.Add(new MonitorInfo
            {
                Handle = hMonitor,
                Bounds = new Rectangle(mi.rcMonitor.Left, mi.rcMonitor.Top,
                    mi.rcMonitor.Right - mi.rcMonitor.Left,
                    mi.rcMonitor.Bottom - mi.rcMonitor.Top),
                WorkingArea = new Rectangle(mi.rcWork.Left, mi.rcWork.Top,
                    mi.rcWork.Right - mi.rcWork.Left,
                    mi.rcWork.Bottom - mi.rcWork.Top),
                IsPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0,
                DeviceName = mi.szDevice,
            });
        }
        return true;
    }

    public Bitmap CaptureAll()
    {
        var monitors = GetMonitors();
        var left = monitors.Min(m => m.Bounds.Left);
        var top = monitors.Min(m => m.Bounds.Top);
        var right = monitors.Max(m => m.Bounds.Right);
        var bottom = monitors.Max(m => m.Bounds.Bottom);
        return CaptureRegion(new Rectangle(left, top, right - left, bottom - top));
    }

    public Bitmap CaptureMonitor(int index) => CaptureRegion(GetMonitors()[index].Bounds);

    public Bitmap CaptureRegion(Rectangle r)
    {
        var hdc = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(hdc);
        var bmp = CreateCompatibleBitmap(hdc, r.Width, r.Height);
        var old = SelectObject(mem, bmp);
        BitBlt(mem, 0, 0, r.Width, r.Height, hdc, r.X, r.Y, SRCCOPY);
        var img = Image.FromHbitmap(bmp);
        SelectObject(mem, old);
        DeleteObject(bmp);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, hdc);
        return img;
    }

    public static void SavePng(Bitmap bmp, string path) => bmp.Save(path, ImageFormat.Png);
}
