using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingCore.Panels;

namespace PuddingBrowser.AgentTools;

public sealed record RenderPanelArgs
{
    [ToolParam("Panel id, e.g. 'main' or 'design'. Re-rendering the same id replaces that canvas instead of opening another. Letters, digits, '.', '_' and '-' only, max 64.")]
    public string? PanelId { get; init; }

    [ToolParam("Self-contained HTML fragment or full document (max 256 KB). Inline CSS is allowed; scripts run sandboxed with no network and no shell bridge. Give either Html or HtmlPath.")]
    public string? Html { get; init; }

    [ToolParam("Path to a UTF-8 file holding the HTML, resolved against the current working directory. Prefer this for anything large so the markup never has to travel through the tool arguments. Must stay inside the working directory and fit the 256 KB canvas limit.")]
    public string? HtmlPath { get; init; }

    [ToolParam("Optional canvas title.")]
    public string? Title { get; init; }
}

/// <summary>
/// 把一段 HTML 变成「工具区里的一块画布」。
///
/// <para>
/// <b>两段语义，顺序不可颠倒</b>：
/// ① <b>写</b>：内容先落到 Core 存储（<see cref="IPanelCanvasStore"/>）—— 这是权威事实；
/// ② <b>展示</b>：再尽力让桌面把它显示出来（能力通道）。
/// </para>
/// <para>
/// 为什么这样排：面板原先只有「推送」一条路，Desktop 没连上就等于内容根本不存在，
/// 既看不见也无从重放。先写存储之后，<b>通道不可用只是「没显示」，不是「没发生」</b> ——
/// 结果里把这两种状态分开报告（<c>stored</c> / <c>stored_and_shown</c>），
/// 不许把「已写入」含糊成失败，也不许把「未展示」含糊成成功。
/// </para>
/// <para>
/// 边界：本工具<b>不落盘</b>、不读文件。面板展示的是「现算现给」的媒体资源
/// （结构图、表格、可交互小部件），没有天然的文件生命周期；为它造临时文件只会引入
/// 命名与清理问题。需要给人看的<b>文档</b>用 <c>preview_document</c>（走文件路径）。
/// </para>
/// <para>
/// <b>为什么继承 <see cref="PuddingToolBase{TArgs}"/> 而不是 BrowserAgentToolBase</b>：
/// 后者在 <c>ExecuteCoreAsync</c> 里强制要求浏览器调用者身份，没有就 fail-closed。
/// 面板渲染不是浏览器操作 —— 继承它会让本工具永远失败，且编译期看不出来
/// （同 preview_document 踩过的坑）。
/// </para>
/// </summary>
[Tool(
    id: DesktopPanelToolIds.Render,
    name: "Render panel",
    description: "Store self-contained HTML as a Desktop tool-area panel canvas and try to show it now (sandboxed: no network, no shell bridge). Use for diagrams, tables and small interactive widgets. Max 256 KB. Re-rendering the same panelId replaces that canvas. Content is stored even when no Desktop is connected.",
    category: ToolCategory.FileSystem,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ConcurrencySafe)]
public sealed class RenderPanelTool(
    IPanelCanvasStore panelCanvases,
    IDesktopPanelCapabilitySurface surface,
    IDesktopCapabilityCallContextFactory callContexts) : PuddingToolBase<RenderPanelArgs>
{
    /// <summary>默认画布：与 Shell 侧「交互面板」单例 tab 对齐（ToolTabIdentity.Panel("main")）。</summary>
    private const string DefaultPanelId = "main";

    /// <summary>内容来源的解析结果。用结果对象而不是抛异常：这类失败是调用方给错了参数，不是意外。</summary>
    private readonly record struct HtmlResolution(bool Success, string? Content, string? ErrorCode, string? ErrorMessage)
    {
        public static HtmlResolution Ok(string content) => new(true, content, null, null);

        public static HtmlResolution Fail(string code, string message) => new(false, null, code, message);
    }

    /// <summary>
    /// 把 <c>Html</c> 或 <c>HtmlPath</c> 解析成待发布的内容。
    ///
    /// <para>
    /// 边界（<b>不是</b>安全边界的全部，但是它该负责的那一段）：路径必须落在本次执行的
    /// <see cref="ToolExecutionContext.WorkingDirectory"/> 之内，且不超过画布上限。
    /// 内容上限的真正真源仍是 <c>PanelCanvasLimits</c> —— 这里只提前挡，不重新定义。
    /// </para>
    /// <para>
    /// 两个都给 ⇒ 拒绝，而不是静默选一个：那会让「我以为发布的是文件」变成真事故。
    /// </para>
    /// <para>
    /// 没有本次执行根（<see cref="ToolExecutionContext.WorkingDirectory"/> 为空）时，
    /// **绝对路径仍然接受**：内联 <c>html</c> 本来就能发布任意内容，放开绝对路径没有扩大
    /// 能力边界，只是省掉一次复制粘贴。相对路径则无法解析，只能拒绝并说明原因。
    /// </para>
    /// </summary>
    private static async Task<HtmlResolution> ResolveHtmlAsync(
        RenderPanelArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        var hasInline = !string.IsNullOrWhiteSpace(args.Html);
        var hasPath = !string.IsNullOrWhiteSpace(args.HtmlPath);

        if (hasInline && hasPath)
        {
            return HtmlResolution.Fail("invalid_request", "give either 'html' or 'htmlPath', not both.");
        }

        if (hasInline)
        {
            return HtmlResolution.Ok(args.Html!);
        }

        if (!hasPath)
        {
            return HtmlResolution.Fail("invalid_request", "either 'html' or 'htmlPath' is required.");
        }

        // rootFull 为 null 表示「本次执行没有工作目录」——此时不做根内校验，只接受绝对路径。
        // 旧实现在这里直接拒绝，导致 htmlPath 在普通对话轮完全不可用（实测踩到）。
        var root = context.WorkingDirectory;
        string? rootFull = null;
        string fullPath;
        try
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                if (!Path.IsPathFullyQualified(args.HtmlPath!))
                {
                    return HtmlResolution.Fail(
                        "invalid_request",
                        "htmlPath needs an absolute path when this execution has no working directory; "
                        + "or pass the markup through 'html'.");
                }

                fullPath = Path.GetFullPath(args.HtmlPath!);
            }
            else
            {
                rootFull = Path.GetFullPath(root);
                fullPath = Path.GetFullPath(Path.Combine(rootFull, args.HtmlPath!), rootFull);
            }
        }
        catch (Exception ex)
        {
            return HtmlResolution.Fail("invalid_request", $"htmlPath is not a usable path: {ex.Message}");
        }

        if (rootFull is not null && !IsInside(rootFull, fullPath))
        {
            return HtmlResolution.Fail(
                "invalid_request", $"htmlPath must stay inside the working directory ({rootFull}).");
        }

        if (!File.Exists(fullPath))
        {
            return HtmlResolution.Fail("file_not_found", $"htmlPath does not exist: {args.HtmlPath}");
        }

        // 先按字节粗挡（UTF-8 最坏 4 字节/字符），避免为一个必然越界的文件先把内存吃掉。
        var length = new FileInfo(fullPath).Length;
        if (length > (long)PanelCanvasLimits.MaxContentChars * 4)
        {
            return HtmlResolution.Fail(
                "invalid_request",
                $"htmlPath is far beyond the {PanelCanvasLimits.MaxContentChars}-character canvas limit.");
        }

        var text = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return HtmlResolution.Fail("invalid_request", $"htmlPath is empty: {args.HtmlPath}");
        }

        if (text.Length > PanelCanvasLimits.MaxContentChars)
        {
            return HtmlResolution.Fail(
                "invalid_request",
                $"htmlPath exceeds the {PanelCanvasLimits.MaxContentChars}-character canvas limit.");
        }

        return HtmlResolution.Ok(text);
    }

    /// <summary>路径是否落在根内。Windows 大小写不敏感，其它平台逐字符比较。</summary>
    private static bool IsInside(string rootFull, string path)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(path, rootFull, comparison))
        {
            return true;
        }

        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison);
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        RenderPanelArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var panelId = string.IsNullOrWhiteSpace(args.PanelId) ? DefaultPanelId : args.PanelId;

        // 内容来源先定下来：内联字符串，或工作区里的一个 UTF-8 文件。
        // 后者是为「大段标记不该穿过工具参数」准备的 —— 生成的文件直接发布，正文不进上下文。
        var html = await ResolveHtmlAsync(args, context, ct).ConfigureAwait(false);
        if (!html.Success)
        {
            return BrowserToolResponse.Failure(html.ErrorCode!, html.ErrorMessage!);
        }

        // 形状校验交给契约（字符集/长度/内容上限/contentType 白名单）。
        // 这里不重复实现一份，否则两处上限会漂移；异常转成工具错误而不是冒泡。
        DesktopPanelRenderRequest request;
        try
        {
            request = new DesktopPanelRenderRequest(panelId, html.Content!, title: args.Title);
        }
        catch (ArgumentException ex)
        {
            return BrowserToolResponse.Failure("invalid_request", ex.Message);
        }

        // ① 权威写入：这一步失败才是真失败。
        PanelCanvasSnapshot snapshot;
        try
        {
            snapshot = panelCanvases.Publish(request.PanelId, request.Content, request.Title);
        }
        catch (ArgumentException ex)
        {
            return BrowserToolResponse.Failure("invalid_request", ex.Message);
        }

        // ② 可选展示：通道没连上、或桌面拒绝了，都只影响「显示」，不影响「已写入」。
        var shown = false;
        string? desktopStatus = null;
        string? desktopNote = null;

        if (callContexts.TryCreate() is { } call)
        {
            var result = await surface.RenderPanelAsync(request, call, ct);

            if (result.IsFailure)
            {
                desktopNote = $"{result.Error!.WireCode}: {result.Error.Message}";
            }
            else
            {
                shown = true;
                desktopStatus = DesktopPanelRenderStatusWire.NameOf(result.Value.Status);
            }
        }
        else
        {
            desktopNote = "no Desktop is connected (capability channel not established)";
        }

        // 只回画布身份、版本与阶段：内容不回显（它可能很长，也可能含用户数据）。
        return BrowserToolResponse.Success(new RenderPanelToolValue
        {
            PanelId = snapshot.PanelId,
            Revision = snapshot.Revision,
            Status = shown ? RenderPanelToolStatus.StoredAndShown : RenderPanelToolStatus.Stored,
            DesktopStatus = desktopStatus,
            DesktopNote = desktopNote,
        });
    }
}

/// <summary>工具结果：画布身份 + 版本 + 两段状态。刻意不含内容。</summary>
public sealed record RenderPanelToolValue
{
    public required string PanelId { get; init; }

    /// <summary>单调递增的内容版本；面板端靠它判断要不要重新渲染。</summary>
    public required long Revision { get; init; }

    /// <summary><c>stored</c>（已写入，未显示）或 <c>stored_and_shown</c>（已写入且桌面受理）。</summary>
    public required string Status { get; init; }

    /// <summary>桌面受理时的阶段线名；未展示则为 null。</summary>
    public string? DesktopStatus { get; init; }

    /// <summary>未展示时的如实原因（通道未建立 / 能力失败的码与消息）。</summary>
    public string? DesktopNote { get; init; }
}

/// <summary>结果状态线名。用常量而非散落字符串，避免与测试/前端各写一份。</summary>
public static class RenderPanelToolStatus
{
    public const string Stored = "stored";

    public const string StoredAndShown = "stored_and_shown";
}

public static partial class DesktopPanelToolIds
{
    public const string Render = "render_panel";
}
