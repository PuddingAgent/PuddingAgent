using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Abstractions;
using PuddingCode.Platform;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 工作区通知 SSE — 所有会话的摘要事件推送。
/// 
/// 前端页面生命周期内保持连接，接收跨会话通知：
///   · 异步子代理完成
///   · Cron 作业触发新会话
///   · 连接器收到新消息
/// 
/// 关联 ADR：Docs/07架构/16会话状态层与客户端解耦ADR.md §5 ADR-016-D
/// </summary>
[Authorize]
[ApiController]
[Route("api/workspaces/{workspaceId}/notifications")]
public class WorkspaceNotificationsController : ControllerBase
{
    private readonly ISessionStateManager _ssm;
    private readonly ILogger<WorkspaceNotificationsController> _logger;

    public WorkspaceNotificationsController(
        ISessionStateManager ssm,
        ILogger<WorkspaceNotificationsController> logger)
    {
        _ssm = ssm;
        _logger = logger;
    }

    /// <summary>
    /// 订阅工作区通知（SSE）。
    /// GET /api/workspaces/{workspaceId}/notifications/stream
    /// </summary>
    [HttpGet("stream")]
    public async Task NotificationStream(string workspaceId, CancellationToken ct)
    {
        ConfigureSseResponse(Response);

        var reader = _ssm.SubscribeWorkspace(workspaceId);

        _logger.LogInformation(
            "[WorkspaceNotifications] SSE subscribed workspace={Workspace}", workspaceId);

        try
        {
            await foreach (var notification in reader.ReadAllAsync(ct))
            {
                var data = System.Text.Json.JsonSerializer.Serialize(notification);
                await WriteSseAsync(Response,
                    new ServerSentEventFrame(notification.Type, data), ct);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug(
                "[WorkspaceNotifications] SSE disconnected workspace={Workspace}", workspaceId);
        }
    }

    // ── SSE 工具方法 ───────────────────────────────────

    private static void ConfigureSseResponse(HttpResponse response)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no";
    }

    private static async Task WriteSseAsync(
        HttpResponse response,
        ServerSentEventFrame frame,
        CancellationToken ct)
    {
        await response.WriteAsync($"event: {frame.Event}\n", ct);
        await response.WriteAsync($"data: {frame.Data}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}
