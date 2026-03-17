using PuddingCode.Platform;
using PuddingGateway;

namespace PuddingController.Services;

/// <summary>
/// 出站消息回写服务——将 Runtime 的回复通过对应适配器发回给原始渠道。
/// </summary>
public sealed class GatewayEgressService
{
    private readonly GatewayAdapterHost _gatewayHost;
    private readonly ILogger<GatewayEgressService> _logger;

    public GatewayEgressService(GatewayAdapterHost gatewayHost, ILogger<GatewayEgressService> logger)
    {
        _gatewayHost = gatewayHost;
        _logger = logger;
    }

    /// <summary>向用户原始渠道发布 Agent 回复。失败时仅记录日志，不抛出异常。</summary>
    public async Task PublishReplyAsync(
        string channelId,
        string userExternalId,
        string sessionId,
        string correlationMessageId,
        string replyText,
        CancellationToken ct = default)
    {
        try
        {
            var envelope = new PuddingEgressEnvelope
            {
                ChannelId = channelId,
                SessionId = sessionId,
                CorrelationId = correlationMessageId,
                ReplyText = replyText,
            };
            await _gatewayHost.PublishAsync(envelope, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Egress] Failed to publish reply to channel={Channel} user={User}",
                channelId, userExternalId);
        }
    }
}
