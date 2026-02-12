using PuddingCode.Platform;

namespace PuddingGateway.Adapters;

/// <summary>
/// 内置 Email 适配器——接收和发送邮件消息（V1 为预留骨架）。
/// </summary>
public sealed class EmailGatewayAdapter : IPuddingGatewayAdapter
{
    public GatewayAdapterDescriptor Descriptor { get; } = new()
    {
        AdapterId = "email",
        AdapterType = "email",
        Version = "1.0.0",
        Description = "Built-in Email adapter for mailbox message ingress",
        SupportedChannelTypes = ["email"]
    };

    private GatewayAdapterContext? _context;

    public Task StartAsync(GatewayAdapterContext context, CancellationToken ct = default)
    {
        _context = context;
        context.Log("[EmailAdapter] Started — email polling/webhook not yet wired.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _context = null;
        return Task.CompletedTask;
    }

    public Task PublishAsync(PuddingEgressEnvelope envelope, CancellationToken ct = default)
    {
        _context?.Log($"[EmailAdapter] Would send reply to channel {envelope.ChannelId}: {envelope.ReplyText[..Math.Min(80, envelope.ReplyText.Length)]}...");
        return Task.CompletedTask;
    }

    /// <summary>模拟收到邮件——供集成测试使用。</summary>
    public async Task SimulateIncomingEmailAsync(string from, string subject, string body, CancellationToken ct = default)
    {
        if (_context is null) return;

        var envelope = new PuddingIngressEnvelope
        {
            ChannelId = "email",
            ChannelType = "email",
            UserExternalId = from,
            MessageText = body,
            MessageType = "email",
            Metadata = new Dictionary<string, string> { ["subject"] = subject, ["from"] = from }
        };

        await _context.OnEventReceived(envelope, ct);
    }
}
