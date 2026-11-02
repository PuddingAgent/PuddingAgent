using Pudding.Contracts.Computer;

namespace PuddingComputer.Automation;

/// <summary>检测器给出的原始结果：只有一个矩形和类别，没有任何坐标解释。</summary>
public readonly record struct RawDetection(PhysicalRect Rect, string Kind);

/// <summary>
/// 一个可点击目标：带编号、三套坐标、以及"能不能点"的判定。
/// <para>
/// 模型只应该看到 <see cref="Id"/> 和 <see cref="Kind"/>；坐标由程序提供，模型不参与计算。
/// </para>
/// </summary>
public sealed record AnnotatedTarget(
    int Id,
    string Kind,
    PhysicalRect Global,
    PhysicalRect WindowLocal,
    ImagePoint Clickable,
    bool InsideWindow);

/// <summary>
/// 把原始检测结果转成"带参考坐标的目标表"（用户 2026-11-02 明确要求：
/// 坐标必须由 PuddingAgent 附加，而不是让模型或临时脚本自己算）。
/// <para>
/// 三套坐标各有用途，缺一会导致不同层的错误：
/// </para>
/// <list type="bullet">
/// <item><see cref="AnnotatedTarget.Global"/>：屏幕绝对坐标，用于 <c>SetCursorPos</c>/<c>SendInput</c>；</item>
/// <item><see cref="AnnotatedTarget.WindowLocal"/>：相对窗口左上角，用于日志、比对与跨屏迁移；</item>
/// <item><see cref="AnnotatedTarget.Clickable"/>：元素中心，且**已用窗口几何校验过**。</item>
/// </list>
/// <para>
/// <see cref="AnnotatedTarget.InsideWindow"/> 不是装饰：实测发现 Chromium 会把内容渲染到
/// 窗口 rect 之外的合成层，那部分不参与 Win32 命中测试，点下去会被穿透到桌面。
/// 把这种目标标出来，比让调用方点了没反应要好。
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
        int minSize = 8)
    {
        ArgumentNullException.ThrowIfNull(detections);

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
            var clickable = new ImagePoint(r.X + r.Width / 2, r.Y + r.Height / 2);

            // 窗口内外判定：整块矩形必须落在窗口内才算"可安全点击"
            var inside = r.X >= windowRect.X
                && r.Y >= windowRect.Y
                && r.X + r.Width <= windowRect.X + windowRect.Width
                && r.Y + r.Height <= windowRect.Y + windowRect.Height;

            result.Add(new AnnotatedTarget(id, d.Kind, r, local, clickable, inside));
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
