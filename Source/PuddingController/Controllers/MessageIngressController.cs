using Microsoft.AspNetCore.Mvc;
using PuddingCode.Platform;
using PuddingController.Services;

namespace PuddingController.Controllers;

/// <summary>
/// 消息入站 API——CLI / 外部客户端通过此端点发送消息。
/// 链路: HTTP -> Controller -> Session Router -> Runtime Agent -> LLM Response
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class MessageIngressController : ControllerBase
{
    private readonly SessionRouter _router;
    private readonly ILogger<MessageIngressController> _logger;

    public MessageIngressController(SessionRouter router, ILogger<MessageIngressController> logger)
    {
        _router = router;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/messageingress
    /// 发送消息到指定 Workspace，获取 Agent 回复。
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<MessageIngressResponse>> Post(
        [FromBody] MessageIngressRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ChannelId))
            return BadRequest(new { error = "ChannelId is required" });
        if (string.IsNullOrWhiteSpace(request.UserExternalId))
            return BadRequest(new { error = "UserExternalId is required" });
        if (string.IsNullOrWhiteSpace(request.MessageText))
            return BadRequest(new { error = "MessageText is required" });

        _logger.LogInformation("[Ingress] workspace={Ws} channel={Ch} user={User}",
            request.WorkspaceId ?? "default", request.ChannelId, request.UserExternalId);

        var response = await _router.RouteMessageAsync(request, ct);
        return Ok(response);
    }
}
