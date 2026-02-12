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

    /// <summary>
    /// POST /api/messageingress/stream
    /// 发送消息并以 SSE 事件流返回 Agent 回复。
    /// </summary>
    [HttpPost("stream")]
    public async Task PostStream(
        [FromBody] MessageIngressRequest request,
        CancellationToken ct)
    {
        ConfigureSseResponse(Response);

        if (string.IsNullOrWhiteSpace(request.ChannelId))
        {
            await WriteSseAsync(Response, "error", new { message = "ChannelId is required" }, ct);
            return;
        }
        if (string.IsNullOrWhiteSpace(request.UserExternalId))
        {
            await WriteSseAsync(Response, "error", new { message = "UserExternalId is required" }, ct);
            return;
        }
        if (string.IsNullOrWhiteSpace(request.MessageText))
        {
            await WriteSseAsync(Response, "error", new { message = "MessageText is required" }, ct);
            return;
        }

        var correlationId = request.CorrelationId ?? "(none)";
        _logger.LogInformation(
            "[Ingress] STREAM ws={Ws} channel={Ch} user={User} hasLlmConfig={HasConfig} correlationId={CorrId}",
            request.WorkspaceId ?? "default", request.ChannelId, request.UserExternalId,
            request.LlmConfig is not null, correlationId);

        try
        {
            await foreach (var frame in _router.RouteMessageStreamAsync(request, ct))
                await WriteRawSseAsync(Response, frame, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[Ingress] STREAM cancelled correlationId={CorrId}", correlationId);
            await WriteSseAsync(Response, "cancelled", new { correlationId }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Ingress] STREAM failed correlationId={CorrId}", correlationId);
            await WriteSseAsync(Response, "error", new { message = ex.Message }, CancellationToken.None);
        }
    }

    private static void ConfigureSseResponse(HttpResponse response)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no";
    }

    private static async Task WriteRawSseAsync(
        HttpResponse response,
        ServerSentEventFrame frame,
        CancellationToken ct)
    {
        await response.WriteAsync($"event: {frame.Event}\n", ct);
        await response.WriteAsync($"data: {frame.Data}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    private static Task WriteSseAsync(
        HttpResponse response,
        string eventName,
        object payload,
        CancellationToken ct) =>
        WriteRawSseAsync(response, ServerSentEventFrame.Json(eventName, payload), ct);
}
