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

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var correlationId = request.CorrelationId ?? "(none)";
        _logger.LogInformation(
            "[Ingress] REQUEST ws={Ws} channel={Ch} user={User} hasLlmConfig={HasConfig} correlationId={CorrId}",
            request.WorkspaceId ?? "default", request.ChannelId, request.UserExternalId,
            request.LlmConfig is not null, correlationId);

        var response = await _router.RouteMessageAsync(request, ct);
        sw.Stop();

        if (response.IsSuccess)
            _logger.LogInformation(
                "[Ingress] OK msgId={MsgId} session={Session} elapsed={Elapsed}ms correlationId={CorrId}",
                response.MessageId, response.SessionId, sw.ElapsedMilliseconds, correlationId);
        else
            _logger.LogWarning(
                "[Ingress] FAILED msgId={MsgId} elapsed={Elapsed}ms error={Error} correlationId={CorrId}",
                response.MessageId, sw.ElapsedMilliseconds, response.ErrorMessage, correlationId);

        return Ok(response);
    }
}
