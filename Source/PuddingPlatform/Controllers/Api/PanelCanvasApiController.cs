using Microsoft.AspNetCore.Mvc;
using PuddingCore.Panels;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 面板画布内容的读写入口。
///
/// <para>
/// **为什么要有这条 HTTP 通道**：面板内容原先只能经 Desktop 能力通道推送，结果
/// 「Desktop 没连上」就等于「面板无法控制」。把内容归到 Core 之后，读（面板视图）
/// 和写（Agent / 运维脚本）都能走普通 HTTP —— 传输坏了不影响内容本身。
/// </para>
/// <para>
/// 边界：这里是<b>内容</b>接口，不负责开标签、不管焦点。要让人真的看到某块画布，
/// 仍然由 Desktop 决定（拉取后渲染，或经能力通道收到 refresh 信号）。
/// </para>
/// <para>
/// 返回的 <see cref="PanelCanvasDto.Content"/> 是 Agent 提供的 HTML 原文，**按 JSON 字符串**
/// 返回（不做转义前渲染）。渲染端必须放进沙箱，不得直接注入到特权页面。
/// </para>
/// </summary>
[ApiController]
[Route("api/panels")]
public sealed class PanelCanvasApiController(IPanelCanvasStore panelCanvases) : ControllerBase
{
    /// <summary>列出所有画布（只含元数据，不含内容）。用于「有哪些画布」的概览。</summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<PanelCanvasSummaryDto>> List()
        => Ok(panelCanvases.List().Select(ToSummary).ToArray());

    /// <summary>取某块画布的当前快照（含内容）。不存在返回 404，而不是空内容 —— 两者要能区分。</summary>
    [HttpGet("{panelId}")]
    public ActionResult<PanelCanvasDto> Get(string panelId)
        => panelCanvases.Get(panelId) is { } snapshot
            ? Ok(ToDto(snapshot))
            : NotFound(new { error = $"Panel canvas '{panelId}' has no content yet." });

    /// <summary>发布（新建或整幅替换）一块画布。同一 id 反复发布会递增 revision。</summary>
    [HttpPut("{panelId}")]
    public ActionResult<PanelCanvasDto> Publish(string panelId, [FromBody] PublishPanelCanvasRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return Ok(ToDto(panelCanvases.Publish(panelId, request.Content, request.Title)));
        }
        catch (ArgumentException ex)
        {
            // 形态非法（id 字符集/长度、内容为 null 或越界）是可判定的调用方错误，不是服务端故障。
            // ArgumentNullException 也是 ArgumentException 的子类，一并在这里被接住。
            return BadRequest(new { error = ex.Message });
        }
    }

    private static PanelCanvasDto ToDto(PanelCanvasSnapshot snapshot)
        => new(snapshot.PanelId, snapshot.Title, snapshot.Revision, snapshot.UpdatedAt, snapshot.Content);

    private static PanelCanvasSummaryDto ToSummary(PanelCanvasSnapshot snapshot)
        => new(snapshot.PanelId, snapshot.Title, snapshot.Revision, snapshot.UpdatedAt);
}

/// <summary>画布快照（含内容）。</summary>
public sealed record PanelCanvasDto(
    string PanelId,
    string? Title,
    long Revision,
    DateTimeOffset UpdatedAt,
    string Content);

/// <summary>画布元数据（不含内容）：列表用。</summary>
public sealed record PanelCanvasSummaryDto(
    string PanelId,
    string? Title,
    long Revision,
    DateTimeOffset UpdatedAt);

/// <summary>发布请求体。</summary>
public sealed record PublishPanelCanvasRequest(string Content, string? Title);
