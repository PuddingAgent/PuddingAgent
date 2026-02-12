using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Agent-facing pull inbox reader for the message fabric.
/// </summary>
[Tool(
    id: "receive_messages",
    name: "接收消息",
    description: "读取当前 Agent 在消息系统中的待处理收件箱消息，可选择读取后确认 ack。",
    category: ToolCategory.Messaging,
    permission: ToolPermissionLevel.Low)]
public sealed class ReceiveMessagesTool : PuddingToolBase<ReceiveMessagesArgs>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IMessageInbox? _inbox;
    private readonly IServiceScopeFactory? _scopeFactory;

    public ReceiveMessagesTool(IMessageInbox inbox) => _inbox = inbox;

    [ActivatorUtilitiesConstructor]
    public ReceiveMessagesTool(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        ReceiveMessagesArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        var query = new MessageInboxQuery
        {
            Endpoint = new MessageAddress
            {
                Kind = args.EndpointKind ?? MessageEndpointKinds.Agent,
                Id = args.EndpointId ?? context.AgentInstanceId,
                WorkspaceId = context.WorkspaceId,
            },
            WorkspaceId = context.WorkspaceId,
            RoomId = args.RoomId,
            Limit = args.Limit,
            IncludeDelivered = args.IncludeDelivered ?? false,
        };

        try
        {
            var messages = await ListAsync(query, ct);
            if (args.Ack == true)
            {
                foreach (var m in messages)
                    await AckAsync(m.DeliveryId, ct);
            }

            return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
            {
                status = "ok",
                count = messages.Count,
                acked = args.Ack == true,
                messages,
            }, JsonOptions));
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Fail(ex.Message);
        }
    }

    private async Task<IReadOnlyList<MessageInboxItem>> ListAsync(MessageInboxQuery query, CancellationToken ct)
    {
        if (_inbox is not null)
            return await _inbox.ListAsync(query, ct);

        if (_scopeFactory is null)
            throw new InvalidOperationException("Message inbox is not configured.");

        using var scope = _scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageInbox>().ListAsync(query, ct);
    }

    private async Task AckAsync(string deliveryId, CancellationToken ct)
    {
        if (_inbox is not null) { await _inbox.AckAsync(deliveryId, ct); return; }

        if (_scopeFactory is null)
            throw new InvalidOperationException("Message inbox is not configured.");

        using var scope = _scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMessageInbox>().AckAsync(deliveryId, ct);
    }
}

public sealed record ReceiveMessagesArgs
{
    [ToolParam("Optional endpoint id. Defaults to the current agent instance id.")]
    public string? EndpointId { get; init; }
    [ToolParam("Optional endpoint kind. Defaults to agent.")]
    public string? EndpointKind { get; init; }
    [ToolParam("Optional room id filter.")]
    public string? RoomId { get; init; }
    [ToolParam("Maximum messages to return, from 1 to 100. Defaults to 20.")]
    public int Limit { get; init; } = 20;
    [ToolParam("true to include already delivered messages. Defaults to false.")]
    public bool? IncludeDelivered { get; init; }
    [ToolParam("true to acknowledge returned deliveries after reading. Defaults to false.")]
    public bool? Ack { get; init; }
}
