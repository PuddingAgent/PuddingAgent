using OpenCvSharp;
using Pudding.Contracts.Computer;
using PuddingComputer.Automation;

namespace PuddingComputer.Windows;

/// <summary>一次屏幕分析的完整结果：截图、候选目标表。</summary>
public sealed class ScreenAnalysis : IDisposable
{
    private readonly Mat _bgr;

    internal ScreenAnalysis(Mat bgr, PhysicalRect screenBounds, IReadOnlyList<AnnotatedTarget> targets,
        WindowCandidate? resolvedWindow = null)
    {
        _bgr = bgr;
        ScreenBounds = screenBounds;
        Targets = targets;
        ResolvedWindow = resolvedWindow;
    }

    /// <summary>截图覆盖的屏幕区域。</summary>
    public PhysicalRect ScreenBounds { get; }

    /// <summary>
    /// 本次分析实际选中的参考窗口。
    /// <para>
    /// 仅当走 <see cref="ComputerScreenAnalyzer.AnalyzeForProcess"/> 这条路径时有值；
    /// 调用方自己传 <c>referenceWindow</c> 时为 null。
    /// 把它暴露出来是因为“选了哪个窗口”本身就是个需要核对的事实：
    /// 同一进程可能有多个同名顶层窗口，选错会导致所有 <c>InsideWindow</c> 判定失真。
    /// </para>
    /// </summary>
    public WindowCandidate? ResolvedWindow { get; }

    /// <summary>
    /// 本次分析选中的窗口句柄，用作点击时的归属校验根。
    /// <para>
    /// 将它传给 <see cref="WindowsInputSimulator.Click"/> 的 <c>authorizedRoot</c> 参数后，
    /// 落在其他窗口上的点击会被拒绝（<see cref="ClickOutcome.RejectedUnexpectedWindowAtPoint"/>）。
    /// 不传的话归属检查不生效。
    /// </para>
    /// <para>走“调用方自己指定窗口”的路径时为 null，此时无从校验，应保持 null。</para>
    /// </summary>
    public IntPtr? AuthorizedRoot => ResolvedWindow?.Handle;

    /// <summary>
    /// 构造与本次分析产物配套的图像几何。
    /// <para>
    /// 为什么由分析器提供而不是让调用方自己拼：图内尺寸必须与**这一张**截图一致，
    /// 采集方式必须是 <see cref="ComputerCaptureMode.ScreenPixels" />。
    /// 让调用方手写这两项，就有写错的机会，而坐标写错不会报错。
    /// </para>
    /// </summary>
    public ComputerImageGeometry CreateGeometry() =>
        new(ScreenBounds, _bgr.Width, _bgr.Height,
            ComputerCaptureMode.ScreenPixels,
            CoordinateInputAllowed: true);

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
    public static ScreenAnalysis Analyze(PhysicalRect? referenceWindow = null,
        OpenCvElementDetector? detector = null)
    {
        using var capture = WindowsScreenCapturer.CaptureVirtualScreen();
        return Analyze(capture, referenceWindow, detector);
    }

    public static ScreenAnalysis Analyze(ScreenCapture capture, PhysicalRect? referenceWindow = null,
        OpenCvElementDetector? detector = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return AnalyzeCore(capture, referenceWindow ?? capture.Bounds, resolvedWindow: null, detector);
    }

    /// <summary>
    /// 截整屏，并按进程**自动定位真窗口**，用它作为参考矩形进行分析。
    /// <para>
    /// 为什么不要求调用方自己传 <c>referenceWindow</c>：实测同一进程会同时暴露多个同名顶层窗口，
    /// 其中“阴影 + 桌面壁纸”的混合矩形面积更大但内容为空
    /// （详见 <see cref="WindowsInteractiveWindowFinder"/>）。传错窗口会让
    /// 所有 <c>InsideWindow</c> 判定失真，而判别依据要看像素，只能由本层来选。
    /// </para>
    /// <para>
    /// 找不到可用窗口时**抛异常而不是退回整屏**：退回整屏会让所有
    /// <c>InsideWindow</c> 恒为 true，等于默默丢掉合成层检测 —— 那正是
    /// “点了没反应”这类故障的源头。
    /// </para>
    /// </summary>
    /// <param name="processId">目标进程 ID。</param>
    /// <param name="detector">自定义检测器；省略时用默认 OpenCV 检测器。</param>
    /// <param name="minimumWindowSize">候选窗口的最小边长，默认 300。</param>
    public static ScreenAnalysis AnalyzeForProcess(
        int processId,
        OpenCvElementDetector? detector = null,
        int minimumWindowSize = 300)
    {
        using var capture = WindowsScreenCapturer.CaptureVirtualScreen();
        return AnalyzeForProcess(capture, processId, detector, minimumWindowSize);
    }

    /// <inheritdoc cref="AnalyzeForProcess(int, OpenCvElementDetector?, int)"/>
    public static ScreenAnalysis AnalyzeForProcess(
        ScreenCapture capture,
        int processId,
        OpenCvElementDetector? detector = null,
        int minimumWindowSize = 300)
    {
        ArgumentNullException.ThrowIfNull(capture);

        var window = WindowsInteractiveWindowFinder.FindBest(processId, capture, minimumWindowSize);
        if (window is null)
        {
            throw new InvalidOperationException(
                $"进程 {processId} 没有可用于坐标参考的可见窗口（最小边长 {minimumWindowSize}）。" +
                "拒绝分析而不是退回整屏 —— 退回整屏会让 InsideWindow 恒为 true，" +
                "等于丢掉合成层检测，故障会以\"点了没反应\"的形式显现。");
        }

        return AnalyzeCore(capture, window.Bounds, window, detector, restrictToWindow: true);
    }

    private static ScreenAnalysis AnalyzeCore(
        ScreenCapture capture,
        PhysicalRect effectiveWindow,
        WindowCandidate? resolvedWindow,
        OpenCvElementDetector? detector,
        bool restrictToWindow = false)
    {
        var bgr = capture.ToBgr();
        try
        {
            var effectiveDetector = detector ?? new OpenCvElementDetector();

            // 只在「按进程分析」时传关心区域：它会额外启用两个修复（List 轮廓 + 相对区域的尺寸上限）。
            // Analyze（调用方自己指定窗口）保持旧行为，避免未验证的噪声涌进整屏分析。
            var detections = effectiveDetector.Detect(bgr, capture.Bounds,
                restrictToWindow ? effectiveWindow : null);

            // 按进程分析时只保留窗口内的目标。
            // 不过滤的话，整屏检测会把其它应用（实测：同一桌面上 169 个 PuddingDesktop 元素）
            // 全带回来，而这些目标即使标了 InsideWindow=false 也只会淹没真正的目标。
            if (restrictToWindow)
            {
                detections = detections
                    .Where(d => IsCenterInside(d.Rect, effectiveWindow))
                    .ToList();
            }

            var targets = ComputerSnapshotAnnotator.Annotate(detections, effectiveWindow);

            return new ScreenAnalysis(bgr, capture.Bounds, targets, resolvedWindow);
        }
        catch
        {
            bgr.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 判断检测框**中心**是否落在窗口内。
    /// <para>
    /// 用中心而不是“相交”：半截身子在窗口外的元素，其真实可点位置不可预测，
    /// 按它算出来的中心往往已经在窗口外，点了大概率偏。
    /// </para>
    /// </summary>
    internal static bool IsCenterInside(PhysicalRect rect, PhysicalRect window)
    {
        var cx = rect.X + rect.Width / 2;
        var cy = rect.Y + rect.Height / 2;
        return cx >= window.X && cx < window.X + window.Width
            && cy >= window.Y && cy < window.Y + window.Height;
    }
}
