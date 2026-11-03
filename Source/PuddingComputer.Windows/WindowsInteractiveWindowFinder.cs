using System.Runtime.InteropServices;
using OpenCvSharp;
using Pudding.Contracts.Computer;

namespace PuddingComputer.Windows;

/// <summary>一个候选分析窗口及其视觉评分；评分不证明输入可达性。</summary>
public sealed record WindowCandidate(
    IntPtr Handle,
    int ProcessId,
    string Title,
    string ClassName,
    PhysicalRect Bounds,
    double MeanLuminance,
    int DistinctColorBuckets,
    double Score);

/// <summary>
/// 从同进程的可见顶层窗口中，按截图内容选择用于分析的候选窗口。
/// <para>
/// 评分使用亮度、颜色种类与面积。它是视觉启发式，不证明窗口身份、遮挡状态或点击效果。
/// </para>
/// <para>
/// 分析窗口与输入接收窗口可以不同。同进程覆盖层承接中心点命中，不作为评分惩罚；
/// 实际点击落点的授权检查由输入执行器负责。
/// </para>
/// </summary>
public static class WindowsInteractiveWindowFinder
{
    /// <summary>
    /// 在指定进程的所有可见顶层窗口里，选出最可能是"真实界面"的那个。
    /// </summary>
    /// <param name="processId">目标进程。</param>
    /// <param name="capture">已采集的整屏截图，用于算宽度/亮度。</param>
    /// <param name="minimumSize">最小尺寸过滤，默认 300x300。</param>
    /// <returns>评分最高的候选；没有任何候选时为 null。</returns>
    public static WindowCandidate? FindBest(int processId, ScreenCapture capture, int minimumSize = 300)
    {
        using var dpi = WindowsDpiEnforcer.EnterPhysicalCoordinates();
        ArgumentNullException.ThrowIfNull(capture);

        using var bgr = capture.ToBgr();
        var candidates = new List<WindowCandidate>();

        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != processId || !IsWindowVisible(hwnd))
                return true;

            if (!TryGetBounds(hwnd, out var bounds))
                return true;

            if (bounds.Width < minimumSize || bounds.Height < minimumSize)
                return true;

            // 夹到截图范围内再统计
            int x = Math.Max(0, bounds.X);
            int y = Math.Max(0, bounds.Y);
            int right = Math.Min(capture.Bounds.Width, bounds.X + bounds.Width);
            int bottom = Math.Min(capture.Bounds.Height, bounds.Y + bounds.Height);
            if (right - x < minimumSize || bottom - y < minimumSize)
                return true;

            var roi = new Rect(x, y, right - x, bottom - y);
            using var crop = new Mat(bgr, roi);
            var stats = Measure(crop);

            // 打分：界面底色通常亮度很高或很低，且颜色种类少（UI 灰阶为主）
            // 壁纸混入会拉低亮度、拉高颜色种类
            double score = ScoreFor(stats.MeanLuminance, stats.DistinctColorBuckets, bounds.Width, bounds.Height);

            // 同进程覆盖窗口可能承接中心点命中；中心点命中归属不参与窗口评分。

            var title = new System.Text.StringBuilder(256);
            GetWindowTextW(hwnd, title, 256);
            var cls = new System.Text.StringBuilder(256);
            GetClassNameW(hwnd, cls, 256);

            candidates.Add(new WindowCandidate(
                hwnd, (int)pid, title.ToString(), cls.ToString(), bounds,
                stats.MeanLuminance, stats.DistinctColorBuckets, score));

            return true;
        }, IntPtr.Zero);

        return candidates.OrderByDescending(c => c.Score).FirstOrDefault();
    }

    /// <summary>
    /// 分析窗口的视觉评分（纯函数，可单测）。
    /// <para>
    /// 启发式偏好接近纯白或纯深色、颜色种类少的区域，并给予有限的面积奖励。
    /// 分数仅用于候选排序，不是对窗口可交互性的判定。
    /// </para>
    /// </summary>
    /// <param name="meanLuminance">窗口区域的平均亮度 0..255。</param>
    /// <param name="colorBuckets">量化后的颜色种类数（32 级 / 通道）。</param>
    /// <param name="width">窗口宽。</param>
    /// <param name="height">窗口高。</param>
    /// <returns>0..1 的分数，越高越可能是真实界面。</returns>
    internal static double ScoreFor(double meanLuminance, int colorBuckets, int width, int height)
    {
        // 亮度分：偏亮则越白越高，偏暗则越黑越高（中间灰的界面很少见）
        double luminanceScore = Math.Abs(meanLuminance - 250) < Math.Abs(meanLuminance - 128)
            ? meanLuminance / 255.0
            : 1.0 - meanLuminance / 255.0;

        // 颜色罚：壁纸混入会拉高颜色种类
        double colorPenalty = Math.Clamp(colorBuckets / 200.0, 0.0, 1.0);

        // 面积奖：大窗口优先（但不能干过前两项）
        double areaBonus = Math.Min(1.0, (width * (double)height) / (1920.0 * 1080.0));

        return luminanceScore * (1.0 - colorPenalty) * (0.7 + 0.3 * areaBonus);
    }

    private static (double MeanLuminance, int DistinctColorBuckets) Measure(Mat bgr)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        double mean = Cv2.Mean(gray).Val0;

        // 把 BGR 各通道压到 32 级，统计出现了多少种颜色桶
        using var small = new Mat();
        Cv2.Resize(bgr, small, new Size(Math.Min(bgr.Width, 160), Math.Min(bgr.Height, 160)), 0, 0, InterpolationFlags.Area);
        var buckets = new HashSet<int>();
        for (int y = 0; y < small.Rows; y += 2)
        {
            for (int x = 0; x < small.Cols; x += 2)
            {
                var p = small.At<Vec3b>(y, x);
                int key = (p.Item2 / 32) << 10 | (p.Item1 / 32) << 5 | (p.Item0 / 32);
                buckets.Add(key);
            }
        }

        return (mean, buckets.Count);
    }

    private static bool TryGetBounds(IntPtr hwnd, out PhysicalRect bounds)
    {
        bounds = default;

        // 优先 DWM 扩展框：不受 DPI 虚拟化影响，也不含不可见的窗口阴影
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var dwmRect, Marshal.SizeOf<NativeRect>()) == 0
            && dwmRect.Right > dwmRect.Left && dwmRect.Bottom > dwmRect.Top)
        {
            bounds = new PhysicalRect(dwmRect.Left, dwmRect.Top,
                dwmRect.Right - dwmRect.Left, dwmRect.Bottom - dwmRect.Top);
            return true;
        }

        if (!GetWindowRect(hwnd, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            return false;

        bounds = new PhysicalRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return true;
    }

    private const int DwmwaExtendedFrameBounds = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, System.Text.StringBuilder buffer, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, System.Text.StringBuilder buffer, int maxCount);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);

}
