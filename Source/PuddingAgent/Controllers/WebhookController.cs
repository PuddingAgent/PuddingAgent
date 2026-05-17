using Microsoft.AspNetCore.Mvc;
using PuddingAgent.Connectors;

namespace PuddingAgent.Controllers;

/// <summary>
/// Webhook 接收端点 — 接收外部系统的 HTTP POST 事件，
/// 验证 HMAC-SHA256 签名后投递到连接器网关事件链路。
/// </summary>
[ApiController]
[Route("webhook")]
public sealed class WebhookController(
    WebhookConnector webhookConnector,
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

        var sessionHint = metadata.TryGetValue("X-Session-Id", out var sid) && !string.IsNullOrWhiteSpace(sid)
            ? sid
            : $"webhook-{channelId}-{Guid.NewGuid():N}"[..20];

        logger.LogInformation(
            "[Webhook] accepted channel={ChannelId} sessionHint={SessionId}",
            channelId, sessionHint);

        return Accepted(new
        {
            accepted = true,
            channelId,
            sessionId = sessionHint,
            observe = new
            {
                sse = $"/platform/api/chat/sessions/{sessionHint}/stream",
                history = $"/platform/api/chat/sessions/{sessionHint}/history",
            }
        });
    }
}
