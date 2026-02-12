using PuddingCode.Platform;

namespace PuddingGateway.Adapters;

/// <summary>
/// 内置 Web Chat 适配器——为嵌入式网页聊天界面提供接入。
/// 渠道 ID 格式：web-chat-{workspaceId}，例如 web-chat-default。
/// 前端通过 Controller HTTP API 发送消息，回复直接由 HTTP response 返回，无需 push。
/// </summary>
public sealed class WebChatGatewayAdapter : IPuddingGatewayAdapter
{
    public GatewayAdapterDescriptor Descriptor { get; } = new()
    {
        AdapterId = "web-chat",
        AdapterType = "web-chat",
        Version = "1.0.0",
        Description = "Built-in Web Chat adapter for browser-based chat UI ingress",
        SupportedChannelTypes = ["web-chat"]
    };

    private GatewayAdapterContext? _context;

    public Task StartAsync(GatewayAdapterContext context, CancellationToken ct = default)
    {
        _context = context;
        context.Log("[WebChatAdapter] Started — web-chat messages arrive via Controller HTTP API.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _context = null;
        return Task.CompletedTask;
    }

    public Task PublishAsync(PuddingEgressEnvelope envelope, CancellationToken ct = default)
    {
        // Web Chat 回复通过 HTTP response 同步返回，无需主动 push。
        _context?.Log($"[WebChatAdapter] Reply for channel {envelope.ChannelId} delivered via HTTP response.");
        return Task.CompletedTask;
    }
}
