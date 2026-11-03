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
public sealed class OpenCvElementDetector : IComputerElementDetector
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
    /// <param name="bgr">整屏图像。</param>
    /// <param name="screenBounds">该图像对应的屏幕矩形，用于把图内坐标换算成全局坐标。</param>
    /// <param name="focusRect">
    /// 关心区域（屏幕坐标）。传入后会改变两件事：
    /// <list type="number">
    /// <item>轮廓提取从 <c>RetrievalModes.External</c> 改为 <c>List</c>；</item>
    /// <item>尺寸上限从“相对整屏”改为“相对该区域”。</item>
    /// </list>
    /// <para>
    /// **为什么必须两者配合**（2026-11-03 实测）：Canny + 形态学闭运算会把整个屏幕的
    /// 边缘连成**一个 5120×1440 的连通体**，<c>External</c> 只返回这一个外轮廓；
    /// 副屏所有元素都被裹在它内部，因此没有任何独立轮廓 —— 实测副屏窗口内
    /// 轮廓数为 0。改用 <c>List</c> 后同参数下变成 116（edge）/ 656（diff）。
    /// </para>
    /// <para>
    /// 但只用 <c>List</c> 不够：那个“整个窗口”的大块仍会作为候选留下，
    /// 因为旧的尺寸上限按整屏算（4864×1368），1600×1038 的窗口远小于它。
    /// 把上限改成相对关心区域后，这类“包住全部内容的壳”才会被排除。
    /// </para>
    /// <para>省略该参数时保持旧行为（External + 相对整屏），供整屏分析使用。</para>
    /// </param>
    public IReadOnlyList<RawDetection> Detect(Mat bgr, PhysicalRect screenBounds,
        PhysicalRect? focusRect = null)
    {
        ArgumentNullException.ThrowIfNull(bgr);

        int w = bgr.Width, h = bgr.Height;

        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);

        // 背景色取左上角。整屏截图的左上角是桌面区域，比"窗口内的白色"更稳定。
        var background = bgr.At<Vec3b>(0, 0);

        var candidates = new List<RawDetection>();
        candidates.AddRange(DetectByBackgroundDiff(bgr, background, w, h, screenBounds, focusRect));
        candidates.AddRange(DetectByCanny(gray, w, h, screenBounds, focusRect));

        return SuppressOverlaps(candidates);
    }

    private IEnumerable<RawDetection> DetectByBackgroundDiff(
        Mat bgr, Vec3b background, int w, int h, PhysicalRect screenBounds, PhysicalRect? focusRect)
    {
        using var diff = new Mat();
        using var abs = new Mat();
        Cv2.Absdiff(bgr, new Scalar(background.Item0, background.Item1, background.Item2), abs);
        using var diffGray = new Mat();
        Cv2.CvtColor(abs, diffGray, ColorConversionCodes.BGR2GRAY);
        Cv2.Threshold(diffGray, diff, _diffThreshold, 255, ThresholdTypes.Binary);

        using (var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 3)))
            Cv2.MorphologyEx(diff, diff, MorphTypes.Close, kernel);

        return ExtractRects(diff, w, h, screenBounds, focusRect, "diff");
    }

    private IEnumerable<RawDetection> DetectByCanny(Mat gray, int w, int h,
        PhysicalRect screenBounds, PhysicalRect? focusRect)
    {
        using var edges = new Mat();
        Cv2.Canny(gray, edges, 40, 120);
        using (var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5)))
            Cv2.MorphologyEx(edges, edges, MorphTypes.Close, kernel, null, 2);

        return ExtractRects(edges, w, h, screenBounds, focusRect, "edge");
    }

    private List<RawDetection> ExtractRects(Mat mask, int w, int h,
        PhysicalRect screenBounds, PhysicalRect? focusRect, string kind)
    {
        var list = new List<RawDetection>();

        // 给了关心区域时用 List：External 只返回连通体的最外层，
        // 当闭运算把整屏连成一块时，区域内的所有元素都会“被包进去”而消失。
        var mode = focusRect is null ? RetrievalModes.External : RetrievalModes.List;

        Cv2.FindContours(mask, out Point[][] contours, out _,
            mode, ContourApproximationModes.ApproxSimple);

        // 尺寸上限的基准：有关心区域就用它的尺寸，否则用整屏。
        // 按整屏算时，“包住全部内容的外壳”永远在限内，于是永远过不了滤也没意义。
        int capW = focusRect?.Width ?? w;
        int capH = focusRect?.Height ?? h;
        const double capRatio = 0.95;

        foreach (var contour in contours)
        {
            var rect = Cv2.BoundingRect(contour);

            if (rect.Width < _minWidth || rect.Height < _minHeight)
                continue;
            if (rect.Width * rect.Height < _minArea)
                continue;
            if (rect.Width > capW * capRatio || rect.Height > capH * capRatio)
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
                kind, Source: "opencv"));
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
