using System.Runtime.InteropServices;
using OpenCvSharp;
using Pudding.Contracts.Computer;

namespace PuddingComputer.Windows;

/// <summary>一次整屏截图：像素数据 + 它在虚拟屏幕上的原点。</summary>
public sealed class ScreenCapture : IDisposable
{
    private readonly Mat _bgra;

    internal ScreenCapture(Mat bgra, PhysicalRect bounds)
    {
        _bgra = bgra;
        Bounds = bounds;
    }

    /// <summary>截图覆盖的屏幕区域（虚拟屏坐标，副屏可为负）。</summary>
    public PhysicalRect Bounds { get; }

    /// <summary>BGRA 原始像素。调用方不应持有超过本次分析。</summary>
    public Mat Bgra => _bgra;

    /// <summary>转成 BGR（OpenCV 常用格式）。调用方负责释放。</summary>
    public Mat ToBgr()
    {
        var bgr = new Mat();
        Cv2.CvtColor(_bgra, bgr, ColorConversionCodes.BGRA2BGR);
        return bgr;
    }

    public void Dispose() => _bgra.Dispose();
}

/// <summary>
/// 从可见桌面采集整屏或物理区域。
/// <para>
/// 使用屏幕 DC 的 BitBlt，不向 Chromium 窗口请求后台渲染。
/// 截图包含实际遮挡；它不证明被遮挡窗口中的内容或控件状态。
/// </para>
/// </summary>
public static class WindowsScreenCapturer
{
    public static ScreenCapture CaptureVirtualScreen()
    {
        using var dpi = WindowsDpiEnforcer.EnterPhysicalCoordinates();
        // 所有范围在同一物理坐标上下文中取得。显示器并集的包围框仍可能有空洞，
        // 点击前必须另用 IsOnRealMonitor 检查实际显示区域。
        // 所以这里取所有**真实显示器**的并集。
        var monitors = WindowsMonitorEnumerator.Enumerate();
        if (monitors.Count == 0)
            throw new InvalidOperationException("未枚举到任何显示器。");

        int left = monitors.Min(m => m.Bounds.X);
        int top = monitors.Min(m => m.Bounds.Y);
        int right = monitors.Max(m => m.Bounds.X + m.Bounds.Width);
        int bottom = monitors.Max(m => m.Bounds.Y + m.Bounds.Height);

        return CaptureRegion(new PhysicalRect(left, top, right - left, bottom - top));
    }

    public static ScreenCapture CaptureRegion(PhysicalRect region)
    {
        using var dpi = WindowsDpiEnforcer.EnterPhysicalCoordinates();
        int w = region.Width, h = region.Height;
        if (w <= 0 || h <= 0)
            throw new ArgumentException($"截图区域无效：{region}", nameof(region));

        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            throw new InvalidOperationException("GetDC 失败。");

        IntPtr memDc = IntPtr.Zero, bitmap = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            memDc = CreateCompatibleDC(screenDc);
            bitmap = CreateCompatibleBitmap(screenDc, w, h);
            if (memDc == IntPtr.Zero || bitmap == IntPtr.Zero)
                throw new InvalidOperationException("创建兼容 DC/位图失败。");

            old = SelectObject(memDc, bitmap);

            if (!BitBlt(memDc, 0, 0, w, h, screenDc, region.X, region.Y, SrcCopy))
                throw new InvalidOperationException("BitBlt 失败。");

            var header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = w,
                Height = -h,          // 负数表示自上而下，避免图像上下翻转
                Planes = 1,
                BitCount = 32
            };

            var buffer = new byte[w * h * 4];
            int lines = GetDIBits(memDc, bitmap, 0, (uint)h, buffer, ref header, 0);

            // 这一个返回值必须检查：Python ctypes 就是因为签名不明确而静默拿到全零 buffer，
            // 表现成"截图全黑"，白白误导了一整轮排查。
            if (lines != h)
                throw new InvalidOperationException(
                    $"GetDIBits 只填充了 {lines} 行（期望 {h} 行），截图无效。");

            var mat = new Mat(h, w, MatType.CV_8UC4);
            Marshal.Copy(buffer, 0, mat.Data, buffer.Length);

            return new ScreenCapture(mat, region);
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memDc, old);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int SrcCopy = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hDc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(
        IntPtr destDc, int x, int y, int w, int h, IntPtr srcDc, int srcX, int srcY, int rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(
        IntPtr hDc, IntPtr hBitmap, uint start, uint lines, byte[] bits,
        ref BitmapInfoHeader header, uint usage);
}
