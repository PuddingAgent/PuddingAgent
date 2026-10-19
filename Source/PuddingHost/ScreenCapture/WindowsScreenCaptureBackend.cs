using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PuddingHost.ScreenCapture;

/// <summary>
/// Windows GDI 采集后端。全部调用都是**同步**的，且每个入口都在同一线程内完成
/// 「切 DPI 上下文 → 抓图 → 还原上下文」——因为 <c>SetThreadDpiAwarenessContext</c>
/// 是**线程级**状态，中间一旦 await 就可能落到别的线程上，导致坐标按缩放后的虚拟值计算。
///
/// <para>坐标口径：一律**物理像素**（虚拟桌面坐标系）。位于 150% 缩放的副屏上时，
/// 拿到的是真实的 3840×2160，而不是被系统虚拟化后的 2560×1440。</para>
/// </summary>
public sealed class WindowsScreenCaptureBackend : IScreenCaptureBackend
{
    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
    private static readonly nint PerMonitorAwareV2 = -4;

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int Srccopy = 0x00CC0020;
    private const int Captureblt = 0x40000000; // 关掉它会把分层窗口（含浏览器）抓成黑块
    private const uint MonitorinfofPrimary = 0x00000001;
    private const uint PwRenderfullcontent = 0x00000002;
    private const uint DibRgbColors = 0;
    private const int BiRgb = 0;

    public IReadOnlyList<ScreenDisplay> GetDisplays()
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            var found = new List<(PixelRect Bounds, bool Primary, string Device)>();
            EnumDisplayMonitors(0, 0, (nint monitor, nint hdc, ref Rect rect, nint data) =>
            {
                var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    var b = new PixelRect(
                        info.rcMonitor.Left,
                        info.rcMonitor.Top,
                        info.rcMonitor.Right - info.rcMonitor.Left,
                        info.rcMonitor.Bottom - info.rcMonitor.Top);
                    found.Add((b, (info.dwFlags & MonitorinfofPrimary) != 0, info.szDevice ?? string.Empty));
                }

                return true;
            }, 0);

            return found
                .Select((m, i) => new ScreenDisplay(i, string.IsNullOrWhiteSpace(m.Device) ? $"display-{i}" : m.Device, m.Bounds, m.Primary))
                .ToList();
        }
        finally
        {
            Restore(previous);
        }
    }

    public IReadOnlyList<ScreenWindow> GetWindows(int maxWindows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWindows);

        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            var handles = new List<nint>();
            EnumWindows((hwnd, _) =>
            {
                if (handles.Count >= maxWindows * 4)
                {
                    return false;
                }

                handles.Add(hwnd);
                return true;
            }, 0);

            var windows = new List<ScreenWindow>(handles.Count);
            foreach (var hwnd in handles)
            {
                var length = GetWindowTextLength(hwnd);
                if (length <= 0 || !GetWindowRect(hwnd, out var rect))
                {
                    continue; // 无标题（工具窗口/隐藏宿主）不参与匹配
                }

                var title = new StringBuilder(length + 1);
                if (GetWindowText(hwnd, title, title.Capacity) <= 0)
                {
                    continue;
                }

                var bounds = new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                if (bounds.IsEmpty)
                {
                    continue;
                }

                windows.Add(new ScreenWindow(
                    hwnd,
                    title.ToString(),
                    ResolveProcessName(hwnd),
                    bounds,
                    IsWindowVisible(hwnd)));
            }

            return windows
                .OrderByDescending(w => w.IsVisible)
                .ThenByDescending(w => w.Bounds.Area)
                .Take(maxWindows)
                .ToList();
        }
        finally
        {
            Restore(previous);
        }
    }

    public CapturedScreenImage Capture(PixelRect region)
    {
        if (region.IsEmpty)
        {
            throw new ArgumentException("capture region must have positive width and height", nameof(region));
        }

        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            return CaptureFromScreen(region);
        }
        finally
        {
            Restore(previous);
        }
    }

    public CapturedScreenImage CaptureWindow(nint handle, PixelRect bounds)
    {
        if (handle == 0)
        {
            throw new ArgumentException("window handle must not be zero", nameof(handle));
        }

        if (bounds.IsEmpty)
        {
            throw new ArgumentException("window bounds must have positive width and height", nameof(bounds));
        }

        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            var width = bounds.Width;
            var height = bounds.Height;
            var screenDc = GetDC(0);
            var memDc = CreateCompatibleDC(screenDc);
            var bitmap = CreateCompatibleBitmap(screenDc, width, height);
            var previousObject = SelectObject(memDc, bitmap);

            try
            {
                // PrintWindow 能抓到被遮挡的窗口；失败时退回屏幕位图（遮挡内容会失真，
                // 但至少能拿到窗口矩形内的可见部分）。两条路径都如实经 CaptureMode 回报。
                if (!PrintWindow(handle, memDc, PwRenderfullcontent))
                {
                    BitBlt(memDc, 0, 0, width, height, screenDc, bounds.X, bounds.Y, Srccopy | Captureblt);
                }

                return ReadPixels(memDc, bitmap, width, height);
            }
            finally
            {
                SelectObject(memDc, previousObject);
                DeleteObject(bitmap);
                DeleteDC(memDc);
                ReleaseDC(0, screenDc);
            }
        }
        finally
        {
            Restore(previous);
        }
    }

    private static CapturedScreenImage CaptureFromScreen(PixelRect region)
    {
        var screenDc = GetDC(0);
        var memDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, region.Width, region.Height);
        var previousObject = SelectObject(memDc, bitmap);

        try
        {
            if (!BitBlt(memDc, 0, 0, region.Width, region.Height, screenDc, region.X, region.Y, Srccopy | Captureblt))
            {
                throw new InvalidOperationException(
                    $"BitBlt failed for region {region} (Win32 error {Marshal.GetLastWin32Error()})");
            }

            return ReadPixels(memDc, bitmap, region.Width, region.Height);
        }
        finally
        {
            SelectObject(memDc, previousObject);
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(0, screenDc);
        }
    }

    /// <summary>把 DDB 读成 top-down 的 32bpp BGRA（负高度 = 自上而下，避免整幅上下翻转）。</summary>
    private static CapturedScreenImage ReadPixels(nint memDc, nint bitmap, int width, int height)
    {
        var stride = width * 4;
        var bytes = new byte[(long)stride * height];
        var info = new BitmapInfo
        {
            bmiHeader = new BitmapInfoHeader
            {
                biSize = Marshal.SizeOf<BitmapInfoHeader>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BiRgb,
            },
        };

        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var scanned = GetDIBits(memDc, bitmap, 0, (uint)height, handle.AddrOfPinnedObject(), ref info, DibRgbColors);
            if (scanned == 0)
            {
                throw new InvalidOperationException(
                    $"GetDIBits returned no scan lines (Win32 error {Marshal.GetLastWin32Error()})");
            }
        }
        finally
        {
            handle.Free();
        }

        return new CapturedScreenImage(width, height, bytes, stride);
    }

    private static string ResolveProcessName(nint hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            return pid == 0 ? string.Empty : Process.GetProcessById((int)pid).ProcessName;
        }
        catch
        {
            return string.Empty; // 进程可能已退出；进程名只是诊断信息，不值得让整次采集失败
        }
    }

    private static void Restore(nint previous)
    {
        if (previous != 0)
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }

    private delegate bool MonitorEnumProc(nint hMonitor, nint hdc, ref Rect rect, nint data);

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader bmiHeader;
        public uint bmiColors;
    }

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint dpiContext);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint hwnd, nint hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint hObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(nint hdcDest, int xDest, int yDest, int width, int height, nint hdcSrc, int xSrc, int ySrc, int rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint hdc, nint hbm, uint start, uint cLines, nint lpvBits, ref BitmapInfo lpbmi, uint usage);
}
