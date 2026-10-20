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
public sealed class ReadPanelTool(IPanelCanvasStore panelCanvases) : PuddingToolBase<ReadPanelArgs>
{
    private const string DefaultPanelId = "main";

    /// <summary>预览上限：够看清结构，又不会把 256 KB 画布灌进上下文。</summary>
    private const int MaxPreviewChars = 8000;

    protected override Task<ToolExecutionResult> ExecuteCoreAsync(
        ReadPanelArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var panelId = string.IsNullOrWhiteSpace(args.PanelId) ? DefaultPanelId : args.PanelId.Trim();
        var snapshot = panelCanvases.Get(panelId);

        if (snapshot is null)
        {
            // 不存在的画布不是错误：它只是还没有内容。
            return Task.FromResult(BrowserToolResponse.Success(new ReadPanelToolValue
            {
                PanelId = panelId,
                Exists = false,
            }));
        }

        var requested = args.MaxChars ?? 0;
        var take = Math.Clamp(requested, 0, MaxPreviewChars);
        var preview = take == 0 ? null : snapshot.Content[..Math.Min(take, snapshot.Content.Length)];

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
        }));
    }
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
