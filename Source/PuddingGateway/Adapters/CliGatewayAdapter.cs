using PuddingCode.Platform;

namespace PuddingGateway.Adapters;

/// <summary>
/// 内置 CLI 适配器——为 CLI 客户端提供 HTTP API 接入。
/// CLI 通过 Controller HTTP API 发送消息，此适配器仅做标记和出站回写占位。
/// </summary>
public sealed class CliGatewayAdapter : IPuddingGatewayAdapter
{
    public GatewayAdapterDescriptor Descriptor { get; } = new()
    {
        AdapterId = "cli",
        AdapterType = "cli",
        Version = "1.0.0",
        Description = "Built-in CLI adapter for HTTP API message ingress",
        SupportedChannelTypes = ["cli"]
    };

    public Task StartAsync(GatewayAdapterContext context, CancellationToken ct = default)
    {
        context.Log("[CliAdapter] Started — CLI messages arrive via Controller HTTP API.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task PublishAsync(PuddingEgressEnvelope envelope, CancellationToken ct = default)
    {
        // CLI 回复通过 HTTP response 返回，无需 push。
        return Task.CompletedTask;
    }
}
