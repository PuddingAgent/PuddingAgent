using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using PuddingPlatform.Services.AgentChat;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 统一会话打开与分页 API（阶段 C3b；Plan §5.1–§5.3）。
/// <para>
/// 旧 <c>/api/workspaces/{ws}/agents/{agentId}/conversation</c> 仍保留到前端阶段 D 切换；
/// 本控制器只读、无事件重放、无摘要补建，失败返回稳定 code。
/// </para>
/// </summary>
[ApiController]
[Authorize]
[Route("api")]
public sealed class ConversationOpenApiController(
    AgentMainSessionResolver sessionResolver,
    ConversationOpenQueryService queryService) : ControllerBase
{
    /// <summary>Agent 主会话统一打开快照；支持 ETag/If-None-Match 304。</summary>
    [HttpGet("workspaces/{workspaceId}/agents/{agentId}/conversation-open")]
    public async Task<ActionResult<ConversationOpenSnapshot>> OpenAgentConversation(
        string workspaceId,
        string agentId,
        [FromQuery] int? messageLimit,
        CancellationToken ct)
    {
        var session = await sessionResolver.ResolveAsync(
            workspaceId, ResolveOwnerUserId(), agentId, ct);
        if (session is null)
        {
            return NotFound(new
            {
                code = "conversation_not_found",
                message = "No resolvable main conversation for this agent.",
            });
        }

        var snapshot = await queryService.OpenAsync(session.SessionId, messageLimit, ct);
        var etag = $"\"{snapshot.RepresentationTag}\"";
        Response.Headers.ETag = new StringValues(etag);

        if (Request.Headers.IfNoneMatch.Any(value => string.Equals(value, etag, StringComparison.Ordinal)))
            return StatusCode(StatusCodes.Status304NotModified);

        return Ok(snapshot);
    }

    /// <summary>历史消息键集分页；游标绑定 conversation 与稳定排序。</summary>
    [HttpGet("conversations/{conversationId}/history")]
    public async Task<ActionResult<ConversationHistoryPage>> GetHistory(
        string conversationId,
        [FromQuery] long? beforeCreatedAt,
        [FromQuery] string? beforeMessageId,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        try
        {
            var page = await queryService.GetHistoryPageAsync(
                new ConversationHistoryQuery(conversationId, beforeCreatedAt, beforeMessageId, limit),
                ct);
            return Ok(page);
        }
        catch (ConversationQueryException ex)
        {
            return BadRequest(new { code = ex.Code, message = ex.Message });
        }
    }

    /// <summary>过程明细 canonical sequence 分页；空展示页也返回可前进扫描边界。</summary>
    [HttpGet("conversations/{conversationId}/process")]
    public async Task<ActionResult<ConversationProcessPage>> GetProcess(
        string conversationId,
        [FromQuery] string? turnId,
        [FromQuery] long? beforeSequence,
        [FromQuery] long? throughSequence,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        try
        {
            var page = await queryService.GetProcessPageAsync(
                new ConversationProcessQuery(conversationId, turnId, beforeSequence, throughSequence, limit),
                ct);
            return Ok(page);
        }
        catch (ConversationQueryException ex)
        {
            return BadRequest(new { code = ex.Code, message = ex.Message });
        }
    }

    private string ResolveOwnerUserId() => User.Identity?.Name ?? "single-user";
}
