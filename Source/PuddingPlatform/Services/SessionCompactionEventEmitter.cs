using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Runtime;

namespace PuddingPlatform.Services;

/// <summary>
/// ISessionCompactionEventEmitter 实现：通过 ISessionStateManager.AppendAsync 推送压缩 SSE 事件。
/// 注册为 Singleton，在 ContextWindowManager.TryAutoCompactAsync 中调用。
/// </summary>
public sealed class SessionCompactionEventEmitter : ISessionCompactionEventEmitter
{
    private readonly ISessionStateManager _ssm;
    private readonly ILogger<SessionCompactionEventEmitter> _logger;

    public SessionCompactionEventEmitter(
        ISessionStateManager ssm,
        ILogger<SessionCompactionEventEmitter> logger)
    {
        _ssm = ssm;
        _logger = logger;
    }

    public async Task EmitAsync(
        string sessionId,
        string workspaceId,
        string eventType,
        object payload,
        CancellationToken ct = default)
    {
        try
        {
            var frame = ServerSentEventFrame.Json(eventType, payload);
            await _ssm.AppendAsync(sessionId, workspaceId, frame, ct);
        }
        catch (Exception ex)
        {
            // SSE 推送失败不影响压缩主流程
            _logger.LogWarning(ex,
                "[CompactionEmitter] Failed to emit SSE {EventType} session={Session}",
                eventType, sessionId);
        }
    }
}
