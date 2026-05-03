using Microsoft.AspNetCore.Mvc;
using PuddingAgent.P2P;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingRuntime.Services;

namespace PuddingAgent.Controllers;

/// <summary>
/// P2P 直连 API：
/// - /api/p2p/ping：节点探活；
/// - /api/p2p/peers：查看当前发现到的对等节点；
/// - /api/p2p/message：接收对等节点消息并转发到本机 Runtime 执行链路。
/// </summary>
[ApiController]
[Route("api/p2p")]
public sealed class P2pController(
    IP2pDiscoveryService discoveryService,
    AgentExecutionService executionService,
    IConfiguration configuration,
    ILogger<P2pController> logger) : ControllerBase
{
    /// <summary>
    /// P2P 健康检查与节点元信息。
    /// </summary>
    [HttpGet("ping")]
    public ActionResult<PeerNode> Ping()
    {
        var nodeId = P2pNodeIdentity.ResolveNodeId(configuration);
        var displayName = P2pNodeIdentity.ResolveDisplayName(configuration);
        var port = Request.Host.Port ?? P2pNodeIdentity.ResolvePort(configuration);
        var host = string.IsNullOrWhiteSpace(Request.Host.Host)
            ? P2pNodeIdentity.ResolveHostAddress()
            : Request.Host.Host;

        return Ok(new PeerNode(nodeId, displayName, host, port, DateTime.UtcNow));
    }

    /// <summary>
    /// 获取当前已发现的在线节点列表。
    /// </summary>
    [HttpGet("peers")]
    public ActionResult<IReadOnlyList<PeerNode>> GetPeers()
        => Ok(discoveryService.GetPeers());

    /// <summary>
    /// 对等节点消息入口：将消息转发给本机 Runtime（AgentExecutionService）。
    /// </summary>
    [HttpPost("message")]
    public async Task<ActionResult<P2pDirectMessageResponse>> ForwardMessage(
        [FromBody] P2pDirectMessageRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.MessageText))
            return BadRequest(new { error = "MessageText is required." });

        var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? $"p2p-{Guid.NewGuid():N}"[..20]
            : request.SessionId;

        var dispatch = new RuntimeDispatchRequest
        {
            SessionId = sessionId,
            WorkspaceId = string.IsNullOrWhiteSpace(request.WorkspaceId) ? "default" : request.WorkspaceId,
            AgentTemplateId = string.IsNullOrWhiteSpace(request.AgentTemplateId)
                ? "workspace-service-agent"
                : request.AgentTemplateId,
            MessageText = request.MessageText,
        };

        var result = await executionService.ExecuteAsync(dispatch, ct);

        logger.LogInformation(
            "[P2P] DirectMessage handled. from={SourceNodeId} session={SessionId} success={Success}",
            request.SourceNodeId,
            sessionId,
            result.IsSuccess);

        var response = new P2pDirectMessageResponse
        {
            SessionId = sessionId,
            SourceNodeId = request.SourceNodeId,
            CorrelationId = request.CorrelationId,
            IsSuccess = result.IsSuccess,
            ReplyText = result.ReplyText,
            ErrorMessage = result.ErrorMessage,
        };

        return result.IsSuccess ? Ok(response) : StatusCode(StatusCodes.Status502BadGateway, response);
    }
}

/// <summary>
/// 对等节点直连消息请求。
/// </summary>
public sealed record P2pDirectMessageRequest
{
    public string? SourceNodeId { get; init; }
    public string? CorrelationId { get; init; }
    public string MessageText { get; init; } = string.Empty;
    public string? SessionId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? AgentTemplateId { get; init; }
}

/// <summary>
/// 对等节点直连消息响应。
/// </summary>
public sealed record P2pDirectMessageResponse
{
    public string SessionId { get; init; } = string.Empty;
    public string? SourceNodeId { get; init; }
    public string? CorrelationId { get; init; }
    public bool IsSuccess { get; init; }
    public string? ReplyText { get; init; }
    public string? ErrorMessage { get; init; }
}