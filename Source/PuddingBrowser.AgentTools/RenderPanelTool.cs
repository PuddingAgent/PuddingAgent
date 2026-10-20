using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingBrowser.AgentTools;

public sealed record RenderPanelArgs
{
    [ToolParam("Panel id, e.g. 'main' or 'design'. Re-rendering the same id replaces that canvas instead of opening another. Letters, digits, '.', '_' and '-' only, max 64.")]
    public string? PanelId { get; init; }

    [ToolParam("Self-contained HTML fragment or full document (max 256 KB). Inline CSS is allowed; scripts run sandboxed with no network and no shell bridge.")]
    public required string Html { get; init; }

    [ToolParam("Optional canvas title.")]
    public string? Title { get; init; }
}

/// <summary>
/// 把一段 HTML 推到桌面工具区的「交互面板」画布。
///
/// 边界：本工具**不落盘**、不读文件。面板展示的是「现算现给」的媒体资源
/// （结构图、表格、可交互小部件），没有天然的文件生命周期；为它造临时文件只会引入
/// 命名与清理问题。需要给人看的**文档**用 <c>preview_document</c>（走文件路径）。
///
/// **为什么继承 <see cref="PuddingToolBase{TArgs}"/> 而不是 BrowserAgentToolBase**：
/// 后者在 `ExecuteCoreAsync` 里强制要求浏览器调用者身份，没有就 fail-closed。
/// 面板渲染不是浏览器操作 —— 继承它会让本工具永远失败，且编译期看不出来
/// （同 preview_document 踩过的坑）。
/// </summary>
[Tool(
    id: DesktopPanelToolIds.Render,
    name: "Render panel",
    description: "Render self-contained HTML into the Desktop tool-area panel canvas (sandboxed: no network, no shell bridge). Use for diagrams, tables and small interactive widgets. Max 256 KB. Re-rendering the same panelId replaces that canvas.",
    category: ToolCategory.FileSystem,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ConcurrencySafe)]
public sealed class RenderPanelTool(
    IDesktopPanelCapabilitySurface surface,
    IDesktopCapabilityCallContextFactory callContexts) : PuddingToolBase<RenderPanelArgs>
{
    /// <summary>默认画布：与 Shell 侧「交互面板」单例 tab 对齐（ToolTabIdentity.Panel("main")）。</summary>
    private const string DefaultPanelId = "main";

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        RenderPanelArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var panelId = string.IsNullOrWhiteSpace(args.PanelId) ? DefaultPanelId : args.PanelId;

        // 形状校验交给契约（字符集/长度/内容上限/contentType 白名单）。
        // 这里不重复实现一份，否则两处上限会漂移；异常转成工具错误而不是冒泡。
        DesktopPanelRenderRequest request;
        try
        {
            request = new DesktopPanelRenderRequest(panelId, args.Html, title: args.Title);
        }
        catch (ArgumentException ex)
        {
            return BrowserToolResponse.Failure("invalid_request", ex.Message);
        }

        if (callContexts.TryCreate() is not { } call)
        {
            return BrowserToolResponse.Failure(
                "desktop_not_connected", "No Desktop is connected for panel rendering");
        }

        var result = await surface.RenderPanelAsync(request, call, ct);

        if (result.IsFailure)
        {
            return BrowserToolResponse.Failure(result.Error!.WireCode, result.Error.Message);
        }

        // 只回画布身份与阶段：内容不回显（它可能很长，也可能含用户数据）。
        return BrowserToolResponse.Success(new RenderPanelToolValue
        {
            PanelId = result.Value.PanelId,
            Status = DesktopPanelRenderStatusWire.NameOf(result.Value.Status),
        });
    }
}

/// <summary>工具结果：画布身份 + 阶段线名。刻意不含内容。</summary>
public sealed record RenderPanelToolValue
{
    public required string PanelId { get; init; }

    public required string Status { get; init; }
}

public static class DesktopPanelToolIds
{
    public const string Render = "render_panel";
}
