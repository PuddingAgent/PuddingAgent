using Pudding.Contracts.Computer;

namespace PuddingComputer.Automation;

/// <summary>检测器候选：屏幕物理矩形及来源信息，不证明是可交互控件。</summary>
public readonly record struct RawDetection(PhysicalRect Rect, string Kind,
    string Source = "unknown", string? Label = null, double? Confidence = null);

/// <summary>
/// 一个视觉候选：带编号、三套坐标、以及中心是否在窗口范围内的几何判定。
/// <para>
/// 程序提供坐标映射；模型选 ID，或参考本图纠正点击点。
/// </para>
/// </summary>
public sealed record AnnotatedTarget(
    int Id,
    string Kind,
    PhysicalRect Global,
    PhysicalRect WindowLocal,
    ImagePoint Clickable,
    bool InsideWindow)
{
    public string Source { get; init; } = "unknown";
    public string? Label { get; init; }
    public double? Confidence { get; init; }
}

/// <summary>
/// 把原始检测结果转成"带参考坐标的目标表"（用户 2026-11-02 明确要求：
/// 坐标必须由 PuddingAgent 附加，而不是让模型或临时脚本自己算）。
/// <para>
/// 三套坐标各有用途，缺一会导致不同层的错误：
/// </para>
/// <list type="bullet">
/// <item><see cref="AnnotatedTarget.Global"/>：屏幕绝对坐标，用于 <c>SetCursorPos</c>/<c>SendInput</c>；</item>
/// <item><see cref="AnnotatedTarget.WindowLocal"/>：相对窗口左上角，用于日志、比对与跨屏迁移；</item>
/// <item><see cref="AnnotatedTarget.Clickable"/>：候选中心在输出图中的坐标，不证明可交互性。</item>
/// </list>
/// <para>
/// <see cref="AnnotatedTarget.InsideWindow"/> 只描述中心与参考窗口的几何关系。
/// 实际命中窗口、焦点与应用效果必须在输入和后续观察路径分别检查。
/// </para>
/// </summary>
public static class ComputerSnapshotAnnotator
{
    /// <summary>
    /// 标注目标。
    /// </summary>
    /// <param name="detections">检测器输出（顺序不重要，编号会重排）。</param>
    /// <param name="windowRect">目标窗口在屏幕上的矩形（全局坐标）。</param>
    /// <param name="minSize">小于此尺寸的检测结果视为噪声丢弃。</param>
    public static IReadOnlyList<AnnotatedTarget> Annotate(
        IReadOnlyList<RawDetection> detections,
        PhysicalRect windowRect,
        ComputerImageGeometry geometry,
        int minSize = 8)
    {
        ArgumentNullException.ThrowIfNull(detections);
        ArgumentNullException.ThrowIfNull(geometry);
        if (geometry.SourceRect.Width <= 0 || geometry.SourceRect.Height <= 0 ||
            geometry.ImageWidth <= 0 || geometry.ImageHeight <= 0)
            throw new ArgumentException("Image geometry must have positive dimensions.", nameof(geometry));

        var result = new List<AnnotatedTarget>(detections.Count);
        var id = 0;

        foreach (var d in detections)
        {
            var r = d.Rect;
            if (r.Width < minSize || r.Height < minSize)
                continue;

            id++;

            var local = new PhysicalRect(
                r.X - windowRect.X,
                r.Y - windowRect.Y,
                r.Width,
                r.Height);

            // 中心点用整数除法，避免 0.5 造成相邻两次标注结果不一致
            var centerX = r.X + r.Width / 2;
            var centerY = r.Y + r.Height / 2;
            var clickable = new ImagePoint(
                (centerX - geometry.SourceRect.X) * (double)geometry.ImageWidth / geometry.SourceRect.Width,
                (centerY - geometry.SourceRect.Y) * (double)geometry.ImageHeight / geometry.SourceRect.Height);

            // 窗口内外判定：按**点击落点**（中心）判定，而不是整块矩形。
            //
            // 2026-11-03 实测修正：点击只作用在中心点上，整块是否完全在窗口内
            // 不决定可点性。当时一个 232x20 的目标因左边界超出窗口 100px 被判为
            // “窗口外”，而它的中心点 (3959,712) 稳稳落在窗口内、WindowFromPoint
            // 也确实命中该窗口 —— 结果 Click 因这个标记直接拒绝，端到端跑不通。
            //
            // 中心点用半开区间 [windowRect.X, windowRect.X + Width)，与
            // ComputerCoordinateMapper 的图内边界判定保持一致。
            var inside = centerX >= windowRect.X
                && centerY >= windowRect.Y
                && centerX < windowRect.X + windowRect.Width
                && centerY < windowRect.Y + windowRect.Height;

            result.Add(new AnnotatedTarget(id, d.Kind, r, local, clickable, inside)
            { Source = d.Source, Label = d.Label, Confidence = d.Confidence });
        }

        return result;
    }

    /// <summary>
    /// 渲染成模型可直接读的文本表。**只含编号、类别、坐标，不含任何推测性描述。**
    /// </summary>
    public static string Render(IReadOnlyList<AnnotatedTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("ID   KIND        GLOBAL(x,y,w,h)        WINDOW-LOCAL      CLICK    IN-WINDOW");
        foreach (var t in targets)
        {
            sb.Append(t.Id.ToString().PadLeft(3)).Append("  ");
            sb.Append((t.Kind.Length <= 10 ? t.Kind.PadRight(10) : t.Kind[..10] + "…")).Append("  ");
            sb.Append($"({t.Global.X},{t.Global.Y},{t.Global.Width},{t.Global.Height})".PadRight(22)).Append("  ");
            sb.Append($"({t.WindowLocal.X},{t.WindowLocal.Y})".PadRight(17)).Append("  ");
            sb.Append($"({t.Clickable.X},{t.Clickable.Y})".PadRight(8)).Append("  ");
            sb.AppendLine(t.InsideWindow ? "yes" : "NO(unclickable)");
        }
        return sb.ToString();
    }
}
