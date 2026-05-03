using Microsoft.AspNetCore.Mvc;
using PuddingAgent.Connectors;
using PuddingCode.Platform;
using PuddingRuntime.Services;

namespace PuddingAgent.Controllers;

/// <summary>
/// Webhook 接收端点 — 接收外部系统的 HTTP POST 事件，
/// 验证 HMAC-SHA256 签名后路由到 Agent Runtime 执行。
/// </summary>
[ApiController]
[Route("webhook")]
public sealed class WebhookController(
    WebhookConnector webhookConnector,
    AgentExecutionService executionService,
    ILogger<WebhookController> logger) : ControllerBase
{
    /// <summary>
    /// 接收外部 webhook 事件。
    /// POST /webhook/{channelId}
    /// 请求头 X-Webhook-Signature: sha256=...
    /// </summary>
    [HttpPost("{channelId}")]
    public async Task<IActionResult> ReceiveWebhook(
        string channelId,
        CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);

        if (string.IsNullOrWhiteSpace(body))
            return BadRequest(new { error = "Empty body" });

        // 提取签名头
        var signatureHeader = Request.Headers["X-Webhook-Signature"].FirstOrDefault()
            ?? Request.Headers["X-Hub-Signature-256"].FirstOrDefault();

        // 提取请求头作为元数据
        var metadata = new Dictionary<string, string>();
        foreach (var header in Request.Headers)
        {
            if (header.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
                metadata[header.Key] = header.Value.FirstOrDefault() ?? "";
        }

        // 通过 WebhookConnector 验证签名并投递事件
        var accepted = await webhookConnector.HandleIncomingAsync(
            channelId, body, signatureHeader, metadata, ct);

        if (!accepted)
        {
            return Unauthorized(new { error = "Signature verification failed" });
        }

        // 构造 RuntimeDispatchRequest 发送给 AgentExecutionService
        var sessionId = $"webhook-{channelId}-{Guid.NewGuid():N}"[..20];
        var dispatchRequest = new RuntimeDispatchRequest
        {
            SessionId = sessionId,
            WorkspaceId = "default",
            AgentTemplateId = "workspace-service-agent",
            MessageText = body,
        };

        var result = await executionService.ExecuteAsync(dispatchRequest, ct);

        logger.LogInformation(
            "[Webhook] channel={ChannelId} session={SessionId} success={Success}",
            channelId, sessionId, result.IsSuccess);

        return Ok(new
        {
            accepted = true,
            channelId,
            sessionId = result.SessionId,
            reply = result.ReplyText ?? result.ErrorMessage ?? "(empty)",
            isSuccess = result.IsSuccess,
        });
    }
}
