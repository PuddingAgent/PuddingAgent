using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Platform;
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
        var channelId = $"web-chat-{workspaceId}";

        // 将当前登录用户作为外部用户 ID
        var userExternalId = User.Identity?.Name ?? "admin";

        // 解析 Agent 绑定的 LLM Provider 配置，随请求下发给 Controller
        LlmConfig? llmConfig = null;
        if (!string.IsNullOrEmpty(req.AgentId))
        {
            var agent = await db.WorkspaceAgents.AsNoTracking()
                .FirstOrDefaultAsync(a => a.AgentId == req.AgentId && a.IsEnabled, ct);
            if (agent?.PreferredProviderId is not null)
            {
                var provider = await db.LlmProviders.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.ProviderId == agent.PreferredProviderId && p.IsEnabled, ct);
                if (provider is not null)
                {
                    llmConfig = new LlmConfig
                    {
                        Endpoint = provider.BaseUrl,
                        ApiKey = provider.ApiKey,
                        ModelId = agent.PreferredModelId,
                    };
                }
            }
        }

        var result = await apiClient.SendMessageAsync(
            channelId:      channelId,
            userExternalId: userExternalId,
            messageText:    req.MessageText,
            workspaceId:    workspaceId,
            sessionId:      req.SessionId,
            llmConfig:      llmConfig,
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
