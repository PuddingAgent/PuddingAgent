using System.Net.WebSockets;
using Microsoft.AspNetCore.Mvc;
using PuddingAgent.Connectors;

namespace PuddingAgent.Controllers;

/// <summary>
/// WebSocket 连接端点。
/// 路由：ws://host:5000/ws/connect
/// 连接后由 GatewayAuthService 完成鉴权（SM2 签名或白名单），
/// 然后交给 WebSocketConnector.AcceptAsync 管理。
/// </summary>
[ApiController]
public class WebSocketController : ControllerBase
{
    private readonly WebSocketConnector _wsConnector;
    private readonly ILogger<WebSocketController> _logger;

    public WebSocketController(WebSocketConnector wsConnector, ILogger<WebSocketController> logger)
    {
        _wsConnector = wsConnector;
        _logger = logger;
    }

    [Route("/ws/connect")]
    public async Task Connect()
    {
        _logger.LogWarning("[WS:Endpoint] Request received, IsWebSocket={IsWs}", HttpContext.WebSockets.IsWebSocketRequest);

        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            _logger.LogWarning("[WS:Endpoint] Not a WebSocket request, rejecting");
            HttpContext.Response.StatusCode = 400;
            return;
        }

        var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        var connectionId = $"ws-{Guid.NewGuid():N}"[..16];
        var authenticatedUser = HttpContext.Items["AuthUser"] as string;

        _logger.LogWarning("[WS:Endpoint] Connection accepted id={Id} user={User}", connectionId, authenticatedUser ?? "(anon)");

        await _wsConnector.AcceptAsync(socket, connectionId, authenticatedUser, HttpContext.RequestAborted);

        _logger.LogWarning("[WS:Endpoint] Connection ended id={Id}", connectionId);
    }
}
