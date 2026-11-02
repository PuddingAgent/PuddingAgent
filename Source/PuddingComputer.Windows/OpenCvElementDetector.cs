using OpenCvSharp;
using Pudding.Contracts.Computer;
using PuddingComputer.Automation;

namespace PuddingComputer.Windows;

/// <summary>
/// 基于 OpenCV 的 UI 元素检测。
/// <para>
/// 两条通道取并集，因为它们各自漏不同的东西：
/// </para>
/// <list type="bullet">
/// <item><b>背景差异</b>：擅长抓"与周围底色不同"的块（文本行、浅色卡片）；</item>
/// <item><b>Canny 边缘</b>：擅长抓高对比的实心块（黑底按钮），而这类块常被前者的
/// 形态学闭运算粘连后筛掉——早期版本正是这样漏掉了「继续」按钮。</item>
/// </list>
/// <para>
/// 本类只负责"找出候选矩形"，**不解释坐标**。编号与三套坐标由
/// <see cref="ComputerSnapshotAnnotator"/> 附加，那是纯规则、可单测的部分。
/// </para>
/// </summary>
public sealed class OpenCvElementDetector
{
    private readonly int _minWidth;
    private readonly int _minHeight;
    private readonly int _minArea;
    private readonly double _minRectangularity;
    private readonly int _diffThreshold;

    public OpenCvElementDetector(
        int minWidth = 14,
        int minHeight = 10,
        int minArea = 400,
        double minRectangularity = 0.30,
        int diffThreshold = 30)
    {
        _minWidth = minWidth;
        _minHeight = minHeight;
        _minArea = minArea;
        _minRectangularity = minRectangularity;
        _diffThreshold = diffThreshold;
    }

    /// <summary>在整屏图像上检测候选元素（坐标已是屏幕绝对坐标）。</summary>
    public IReadOnlyList<RawDetection> Detect(Mat bgr, PhysicalRect screenBounds)
    {
        ArgumentNullException.ThrowIfNull(bgr);

        int w = bgr.Width, h = bgr.Height;

        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);

        // 背景色取左上角。整屏截图的左上角是桌面区域，比"窗口内的白色"更稳定。
        var background = bgr.At<Vec3b>(0, 0);

        var candidates = new List<RawDetection>();
        candidates.AddRange(DetectByBackgroundDiff(bgr, background, w, h, screenBounds));
        candidates.AddRange(DetectByCanny(gray, w, h, screenBounds));

        return SuppressOverlaps(candidates);
    }

    private IEnumerable<RawDetection> DetectByBackgroundDiff(
        Mat bgr, Vec3b background, int w, int h, PhysicalRect screenBounds)
    {
        using var diff = new Mat();
        using var abs = new Mat();
        Cv2.Absdiff(bgr, new Scalar(background.Item0, background.Item1, background.Item2), abs);
        using var diffGray = new Mat();
        Cv2.CvtColor(abs, diffGray, ColorConversionCodes.BGR2GRAY);
        Cv2.Threshold(diffGray, diff, _diffThreshold, 255, ThresholdTypes.Binary);

        using (var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 3)))
            Cv2.MorphologyEx(diff, diff, MorphTypes.Close, kernel);

        return ExtractRects(diff, w, h, screenBounds, "diff");
    }

    private IEnumerable<RawDetection> DetectByCanny(Mat gray, int w, int h, PhysicalRect screenBounds)
    {
        using var edges = new Mat();
        Cv2.Canny(gray, edges, 40, 120);
        using (var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5)))
            Cv2.MorphologyEx(edges, edges, MorphTypes.Close, kernel, null, 2);

        return ExtractRects(edges, w, h, screenBounds, "edge");
    }

    private List<RawDetection> ExtractRects(Mat mask, int w, int h, PhysicalRect screenBounds, string kind)
    {
        var list = new List<RawDetection>();

        Cv2.FindContours(mask, out Point[][] contours, out _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        foreach (var contour in contours)
        {
            var rect = Cv2.BoundingRect(contour);

            if (rect.Width < _minWidth || rect.Height < _minHeight)
                continue;
            if (rect.Width * rect.Height < _minArea)
                continue;
            if (rect.Width > w * 0.95 || rect.Height > h * 0.95)
                continue;

            double area = Cv2.ContourArea(contour);
            double rectangularity = area / (rect.Width * (double)rect.Height);
            if (rectangularity < _minRectangularity)
                continue;

            // 检测坐标是图像坐标；加上屏幕原点后才是全局坐标。
            // 这一步必须在这里做，不能让调用方自己加——否则漏一处就整体偏移。
            list.Add(new RawDetection(
                new PhysicalRect(
                    screenBounds.X + rect.X,
                    screenBounds.Y + rect.Y,
                    rect.Width,
                    rect.Height),
                kind));
        }

        return list;
    }

    /// <summary>按面积降序保留，抑制 IoU &gt;= 0.45 的重复框。</summary>
    private static IReadOnlyList<RawDetection> SuppressOverlaps(List<RawDetection> candidates)
    {
        candidates.Sort((a, b) => (b.Rect.Width * b.Rect.Height).CompareTo(a.Rect.Width * a.Rect.Height));

        var kept = new List<RawDetection>();
        foreach (var candidate in candidates)
        {
            if (kept.All(k => IntersectionOverUnion(candidate.Rect, k.Rect) < 0.45))
                kept.Add(candidate);
        }
        return kept;
    }

    private static double IntersectionOverUnion(PhysicalRect a, PhysicalRect b)
    {
        int x1 = Math.Max(a.X, b.X);
        int y1 = Math.Max(a.Y, b.Y);
        int x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        int y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

        if (x2 <= x1 || y2 <= y1) return 0;

        double intersection = (x2 - x1) * (double)(y2 - y1);
        double union = a.Width * (double)a.Height + b.Width * (double)b.Height - intersection;
        return union <= 0 ? 0 : intersection / union;
    }
}
