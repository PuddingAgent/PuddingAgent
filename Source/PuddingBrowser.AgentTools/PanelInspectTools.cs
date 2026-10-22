using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingCore.Panels;

namespace PuddingBrowser.AgentTools;

public static partial class DesktopPanelToolIds
{
    public const string Read = "read_panel";

    public const string List = "list_panels";

    public const string Clear = "clear_panel";
}

/// <summary>交互面板的**读取面**参数。</summary>
public sealed record ReadPanelArgs
{
    [ToolParam("Panel id, default 'main'.")]
    public string? PanelId { get; init; }

    [ToolParam("How many characters of the stored HTML to include (0 = metadata only, capped at 8000). Use this to confirm what is actually on the canvas without pulling 256 KB into the conversation.")]
    public int? MaxChars { get; init; }
}

/// <summary>画布状态。内容默认不回显 —— 只有显式要预览时才返回前 N 个字符。</summary>
public sealed record ReadPanelToolValue
{
    public required string PanelId { get; init; }

    /// <summary>false 表示这块画布还没有任何内容（不是错误）。</summary>
    public required bool Exists { get; init; }

    public string? Title { get; init; }

    /// <summary>单调递增的版本号；画布端靠它判断是否重绘。</summary>
    public long? Revision { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>内容的字符数（不是字节数）。</summary>
    public int? ContentLength { get; init; }

    public string? ContentPreview { get; init; }

    /// <summary>预览是否被截断（<see cref="ContentPreview"/> 短于全文）。</summary>
    public bool PreviewTruncated { get; init; }

    /// <summary>
    /// 用户在画布上画的标注原文（<c>puddingAnnotation.take()</c> 的 JSON），没有则为 null。
    /// 内含 <c>{src,width,height,naturalWidth,naturalHeight,items[]}</c>：
    /// items 的坐标是**页面显示像素**，换算见 <see cref="AnnotationsNote"/>。
    /// </summary>
    public string? Annotations { get; init; }

    /// <summary>标注快照的写入时间（Desktop 侧给出），用来判断它有多旧。</summary>
    public DateTimeOffset? AnnotationsUpdatedAt { get; init; }

    /// <summary>标注原文是否被截断（只截展示，不影响快照本身）。</summary>
    public bool AnnotationsTruncated { get; init; }

    /// <summary>换算说明：有标注时才给，避免把没用到的约定塞进每次读数。</summary>
    public string? AnnotationsNote { get; init; }
}

/// <summary>
/// 读一块画布的内容状态。
///
/// <para>
/// 为什么需要这个工具：内容归 Core 之后，<c>render_panel</c> 的返回值只说「已写入 / 已展示」，
/// 不足以回答「现在画布上到底是什么」。缺少这个读取面，每次核对都得靠外部手段（curl、
/// 翻日志），这正是把一次性手段当成常规操作。
/// </para>
/// </summary>
[Tool(
    id: DesktopPanelToolIds.Read,
    name: "Read panel",
    description: "Report what a Desktop tool-area panel canvas currently holds: existence, title, monotonically increasing revision, content length, and optionally the first N characters. Read-only. Use it to confirm a render landed and to decide whether a redraw is needed, without pulling the whole canvas into the conversation.",
    category: ToolCategory.FileSystem,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe)]
public sealed class ReadPanelTool(
    IPanelCanvasStore panelCanvases,
    IPanelAnnotationFileReader? annotationFiles = null) : PuddingToolBase<ReadPanelArgs>
{
    private const string DefaultPanelId = "main";

    /// <summary>预览上限：够看清结构，又不会把 256 KB 画布灌进上下文。</summary>
    private const int MaxPreviewChars = 8000;

    /// <summary>标注原文的展示上限：几个箭头加一段文字远低于它，而涂鸦轨迹可能很长。</summary>
    private const int MaxAnnotationChars = 4000;

    /// <summary>
    /// 坐标换算说明。**不在这里做归一化**：一套 0–999 的换算只应该有一个实现
    /// （<c>Desktop.Foundation.PanelAnnotation.Normalize</c>），在 Agent 工具里再写一遍
    /// 就变成两套约定，迟早会不一致。所以只把像素与显示尺寸原样交出，由读的人按这条式子算。
    /// </summary>
    private const string AnnotationCoordinateNote =
        "items[].x/x1/x2/y/y1/y2/points[] are DISPLAY pixels. Normalize with "
        + "round(px / width * 1000) and round(py / height * 1000), clamped to 0..999, "
        + "before using them as <point x y> / <bbox x1 y1 x2 y2> for image editing. "
        + "kinds: box(x1,y1,x2,y2), arrow(x1,y1,x2,y2), point(x,y), text(label,x,y), scribble(points[]).";

    protected override Task<ToolExecutionResult> ExecuteCoreAsync(
        ReadPanelArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var panelId = string.IsNullOrWhiteSpace(args.PanelId) ? DefaultPanelId : args.PanelId.Trim();
        var snapshot = panelCanvases.Get(panelId);
        var annotations = ReadAnnotations(panelId);

        if (snapshot is null)
        {
            // 不存在的画布不是错误：它只是还没有内容。
            // 标注仍然照报：画布内容可能已被清掉，而用户画在上面的标记还在。
            return Task.FromResult(BrowserToolResponse.Success(new ReadPanelToolValue
            {
                PanelId = panelId,
                Exists = false,
                Annotations = annotations.Json,
                AnnotationsUpdatedAt = annotations.UpdatedAt,
                AnnotationsTruncated = annotations.Truncated,
                AnnotationsNote = annotations.Json is null ? null : AnnotationCoordinateNote,
            }));
        }

        var requested = args.MaxChars ?? 0;
        var take = Math.Clamp(requested, 0, MaxPreviewChars);
        var cut = Math.Min(take, snapshot.Content.Length);

        // 不要把代理对切一半：半个 surrogate 序列化后会变成替换字符，预览里就多出一个无名乱码。
        if (cut > 0 && cut < snapshot.Content.Length && char.IsHighSurrogate(snapshot.Content[cut - 1]))
        {
            cut--;
        }

        var preview = cut == 0 ? null : snapshot.Content[..cut];

        return Task.FromResult(BrowserToolResponse.Success(new ReadPanelToolValue
        {
            PanelId = snapshot.PanelId,
            Exists = true,
            Title = snapshot.Title,
            Revision = snapshot.Revision,
            UpdatedAt = snapshot.UpdatedAt,
            ContentLength = snapshot.Content.Length,
            ContentPreview = preview,
            PreviewTruncated = preview is not null && preview.Length < snapshot.Content.Length,
            Annotations = annotations.Json,
            AnnotationsUpdatedAt = annotations.UpdatedAt,
            AnnotationsTruncated = annotations.Truncated,
            AnnotationsNote = annotations.Json is null ? null : AnnotationCoordinateNote,
        }));
    }

    /// <summary>
    /// 读出交接文件里的标注。没接读取器（旧接线 / 单测里的单参构造）、文件不存在、
    /// 文件坏了，一律当「没有标注」返回 <c>null</c> —— 读不到标注不该让读画布失败。
    /// </summary>
    private AnnotationView ReadAnnotations(string panelId)
    {
        if (annotationFiles is null)
        {
            return default;
        }

        var file = annotationFiles.Read(panelId);
        if (file is null)
        {
            return default;
        }

        var payload = file.PayloadJson;
        if (payload.Length <= MaxAnnotationChars)
        {
            return new AnnotationView(payload, file.UpdatedAt, Truncated: false);
        }

        // 同预览：只截展示，且不把代理对切成半个。
        var cut = MaxAnnotationChars;
        if (char.IsHighSurrogate(payload[cut - 1]))
        {
            cut--;
        }

        return new AnnotationView(payload[..cut], file.UpdatedAt, Truncated: true);
    }

    private readonly record struct AnnotationView(string? Json, DateTimeOffset? UpdatedAt, bool Truncated);
}

/// <summary>列出画布（元数据，不含内容）。</summary>
public sealed record ListPanelsArgs
{
}

public sealed record PanelSummaryValue
{
    public required string PanelId { get; init; }

    public string? Title { get; init; }

    public required long Revision { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public required int ContentLength { get; init; }
}

public sealed record ListPanelsToolValue
{
    public required int Count { get; init; }

    public required IReadOnlyList<PanelSummaryValue> Canvases { get; init; }
}

/// <summary>
/// 列出所有画布。用来回答「我手上现在有哪些画布」，而不是靠记忆猜。
/// </summary>
[Tool(
    id: DesktopPanelToolIds.List,
    name: "List panels",
    description: "List every Desktop panel canvas that currently holds content, with panel id, title, revision, update time and content length (no content). Read-only.",
    category: ToolCategory.FileSystem,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe)]
public sealed class ListPanelsTool(IPanelCanvasStore panelCanvases) : PuddingToolBase<ListPanelsArgs>
{
    protected override Task<ToolExecutionResult> ExecuteCoreAsync(
        ListPanelsArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var canvases = panelCanvases.List()
            .Select(snapshot => new PanelSummaryValue
            {
                PanelId = snapshot.PanelId,
                Title = snapshot.Title,
                Revision = snapshot.Revision,
                UpdatedAt = snapshot.UpdatedAt,
                ContentLength = snapshot.Content.Length,
            })
            .ToArray();

        return Task.FromResult(BrowserToolResponse.Success(new ListPanelsToolValue
        {
            Count = canvases.Length,
            Canvases = canvases,
        }));
    }
}

/// <summary>移除一块画布。</summary>
public sealed record ClearPanelArgs
{
    [ToolParam("Panel id to remove, default 'main'. The canvas is deleted, not blanked: the next render starts again at revision 1.")]
    public string? PanelId { get; init; }
}

public sealed record ClearPanelToolValue
{
    public required string PanelId { get; init; }

    /// <summary>false 表示本来就没有这块画布（幂等，不是错误）。</summary>
    public required bool Removed { get; init; }
}

/// <summary>
/// 删除一块画布。
///
/// <para>
/// 刻意是「删除」而不是「清空成空白」：空内容会让 <c>read_panel</c> 与面板视图都要处理
/// 「存在但没内容」的第三种状态，而真正需要的语义只有「这块画布没有了」。
/// </para>
/// </summary>
[Tool(
    id: DesktopPanelToolIds.Clear,
    name: "Clear panel",
    description: "Remove a Desktop panel canvas entirely (idempotent: removing a canvas that does not exist reports removed=false, not an error). The next render of that id starts again at revision 1.",
    category: ToolCategory.FileSystem,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.Destructive)]
public sealed class ClearPanelTool(IPanelCanvasStore panelCanvases) : PuddingToolBase<ClearPanelArgs>
{
    private const string DefaultPanelId = "main";

    protected override Task<ToolExecutionResult> ExecuteCoreAsync(
        ClearPanelArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var panelId = string.IsNullOrWhiteSpace(args.PanelId) ? DefaultPanelId : args.PanelId.Trim();
        var removed = panelCanvases.Remove(panelId);

        return Task.FromResult(BrowserToolResponse.Success(new ClearPanelToolValue
        {
            PanelId = panelId,
            Removed = removed,
        }));
    }
}
