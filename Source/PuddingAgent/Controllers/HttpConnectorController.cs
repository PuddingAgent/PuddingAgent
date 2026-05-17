using Microsoft.AspNetCore.Mvc;
using PuddingAgent.Connectors;

namespace PuddingAgent.Controllers;

/// <summary>
/// HTTP 最小协议接入：
///  - POST /connector/http/{channelId}
///  - 只负责把请求转换为连接器事件并投递到网关/事件系统
///  - 不在控制器内直接执行 Agent
/// </summary>
[ApiController]
[Route("connector/http")]
public sealed class HttpConnectorController : ControllerBase
{
    private readonly HttpConnector _httpConnector;
    private readonly ILogger<HttpConnectorController> _logger;

    public HttpConnectorController(HttpConnector httpConnector, ILogger<HttpConnectorController> logger)
    {
        _httpConnector = httpConnector;
        _logger = logger;
    }

    [HttpPost("{channelId}")]
    public async Task<IActionResult> IngressAsync(
        [FromRoute] string channelId,
        [FromBody] HttpConnectorIngressRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "message is required" });

        var metadata = request.Metadata ?? new Dictionary<string, string>();
        if (!metadata.ContainsKey("source_type")) metadata["source_type"] = "http";
        if (!metadata.ContainsKey("source_id")) metadata["source_id"] = channelId;
        if (!metadata.ContainsKey("source_name")) metadata["source_name"] = "http";

        var sessionId = await _httpConnector.HandleIncomingAsync(
            channelId: channelId,
            userExternalId: request.UserExternalId ?? "http:anonymous",
            messageText: request.Message,
            sessionId: request.SessionId,
            messageType: request.MessageType,
            metadata: metadata,
            ct: cancellationToken);

        _logger.LogInformation("[HTTP:Ingress] Accepted channelId={ChannelId} sessionId={SessionId}", channelId, sessionId);

        return Accepted(new
        {
            accepted = true,
            channelId,
            sessionId,
            observe = new
            {
                sse = $"/platform/api/chat/sessions/{sessionId}/stream",
                history = $"/platform/api/chat/sessions/{sessionId}/history",
            }
        });
    }
}

public sealed record HttpConnectorIngressRequest
{
    public string Message { get; init; } = "";
    public string? SessionId { get; init; }
    public string? UserExternalId { get; init; }
    public string? MessageType { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}
