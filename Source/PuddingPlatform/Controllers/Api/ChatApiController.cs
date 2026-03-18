using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>管理员 Chat 代理 API — 将消息转发至 Controller 服务的 MessageIngress 端点。</summary>
[Authorize]
[ApiController]
[Route("api/workspaces/{workspaceId}/chat")]
public class ChatApiController(PlatformDbContext db, PlatformApiClient apiClient) : ControllerBase
{
    // POST /api/workspaces/{workspaceId}/chat/message
    [HttpPost("message")]
    public async Task<ActionResult<AdminChatResponse>> SendMessage(
        string workspaceId, [FromBody] AdminChatRequest req, CancellationToken ct)
    {
        // 验证 workspace 存在
        var ws = await db.Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);
        if (ws is null)
            return NotFound(new { message = $"Workspace '{workspaceId}' 不存在" });

        if (string.IsNullOrWhiteSpace(req.MessageText))
            return BadRequest(new { message = "消息内容不能为空" });

        // 使用 web-chat 内置渠道 ID（已在 SeedDefaults 中注册）
        // 会话隔离通过 sessionId 实现，无需在 channelId 中区分 Agent
        var channelId = $"web-chat-{workspaceId}";

        // 将当前登录用户作为外部用户 ID（或使用固定 admin 标识）
        var userExternalId = User.Identity?.Name ?? "admin";

        var result = await apiClient.SendMessageAsync(
            channelId:      channelId,
            userExternalId: userExternalId,
            messageText:    req.MessageText,
            workspaceId:    workspaceId,
            sessionId:      req.SessionId,
            ct:             ct);

        if (result is null)
            return StatusCode(502, new { message = "Controller 服务未响应，请确认服务运行状态" });

        return Ok(new AdminChatResponse(
            result.MessageId,
            result.SessionId,
            result.Reply,
            result.IsSuccess,
            result.ErrorMessage));
    }
}
