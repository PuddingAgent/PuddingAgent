using OpenCvSharp;
using Pudding.Contracts.Computer;
using PuddingComputer.Automation;

namespace PuddingComputer.Windows;

/// <summary>一次屏幕分析的完整结果：截图、候选目标表。</summary>
public sealed class ScreenAnalysis : IDisposable
{
    private readonly Mat _bgr;

    internal ScreenAnalysis(Mat bgr, PhysicalRect screenBounds, IReadOnlyList<AnnotatedTarget> targets)
    {
        _bgr = bgr;
        ScreenBounds = screenBounds;
        Targets = targets;
    }

    /// <summary>截图覆盖的屏幕区域。</summary>
    public PhysicalRect ScreenBounds { get; }

    /// <summary>带编号与三套坐标的目标表。模型只应看到 Id 与 Kind。</summary>
    public IReadOnlyList<AnnotatedTarget> Targets { get; }

    /// <summary>BGR 图像，供画标注图或局部裁剪用。</summary>
    public Mat Bgr => _bgr;

    /// <summary>渲染成模型可读的文本表。</summary>
    public string RenderTable() => ComputerSnapshotAnnotator.Render(Targets);

    /// <summary>画一张带编号的标注图并保存。</summary>
    public void SaveAnnotated(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var marked = _bgr.Clone();
        foreach (var t in Targets)
        {
            var color = t.InsideWindow
                ? new Scalar(0, 0, 255)
                : new Scalar(128, 128, 128);   // 不可点目标用灰色，视觉上与可点目标区分

            Cv2.Rectangle(marked,
                new Rect(t.Global.X - ScreenBounds.X, t.Global.Y - ScreenBounds.Y,
                         t.Global.Width, t.Global.Height),
                color, 2);

            Cv2.PutText(marked, t.Id.ToString(),
                new Point(t.Global.X - ScreenBounds.X + 2,
                          Math.Max(12, t.Global.Y - ScreenBounds.Y - 4)),
                HersheyFonts.HersheySimplex, 0.42, color, 1);
        }

        Cv2.ImWrite(path, marked);
    }

    public void Dispose() => _bgr.Dispose();
}

/// <summary>
/// 一次调用完成"截屏 → 检测 → 附加坐标"，输出可直接给模型读的目标表。
/// <para>
/// 这是 Computer Use 感知层的**唯一入口**：调用方不需要知道 GDI、OpenCV、编号规则，
/// 也不允许自己算坐标（用户 2026-11-02 明确要求）。
/// </para>
/// </summary>
public static class ComputerScreenAnalyzer
{
    /// <summary>
    /// 截整屏并分析。
    /// </summary>
    /// <param name="referenceWindow">
    /// 参考窗口矩形。传入后，落在该窗口之外的检测结果会被标为
    /// <see cref="AnnotatedTarget.InsideWindow"/> = false（这类目标点击会被穿透）。
    /// 省略时视为整屏都是有效区域。
    /// </param>
    public static ScreenAnalysis Analyze(PhysicalRect? referenceWindow = null)
    {
        using var capture = WindowsScreenCapturer.CaptureVirtualScreen();
        return Analyze(capture, referenceWindow);
    }

    public static ScreenAnalysis Analyze(ScreenCapture capture, PhysicalRect? referenceWindow = null)
    {
        ArgumentNullException.ThrowIfNull(capture);

        var bgr = capture.ToBgr();
        try
        {
            var detector = new OpenCvElementDetector();
            var detections = detector.Detect(bgr, capture.Bounds);

            var effectiveWindow = referenceWindow ?? capture.Bounds;
            var targets = ComputerSnapshotAnnotator.Annotate(detections, effectiveWindow);

            return new ScreenAnalysis(bgr, capture.Bounds, targets);
        }
        catch
        {
            bgr.Dispose();
            throw;
        }
    }
}
