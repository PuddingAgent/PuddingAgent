using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Agent-facing message sender backed by the message fabric.
/// </summary>
[Tool(
    id: "send_message",
    name: "发送消息",
    description: "通过消息系统发送消息。支持发送给 user:id、agent:id、room:id、connector:id，或使用 @all/all 广播到当前聊天室。",
    category: ToolCategory.Messaging,
    permission: ToolPermissionLevel.Low)]
public sealed class SendMessageTool : PuddingToolBase<SendMessageArgs>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IServiceScopeFactory? _scopeFactory;

    public SendMessageTool(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SendMessageArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        var content = args.Content;
        var rawTo = args.To ?? "";
        var roomId = args.RoomId;

        if (string.IsNullOrWhiteSpace(content))
            return ToolExecutionResult.Fail("content is required.");

        var targets = ParseTargets(rawTo, context.WorkspaceId, roomId);
        if (targets.Count == 0)
            return ToolExecutionResult.Fail("to is required. Use an address like user:owner, agent:assistant, room:default, or @all.");

        var audience = ResolveAudience(args.Audience, targets);
        var envelope = new MessageEnvelope
        {
            From = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = context.AgentInstanceId, WorkspaceId = context.WorkspaceId },
            To = targets,
            RoomId = roomId,
            ConversationId = context.SessionId,
            ReplyToMessageId = args.ReplyToMessageId,
            Audience = audience,
            Visibility = args.Visibility ?? MessageVisibilities.Public,
            Content = content,
            Priority = args.Priority ?? 0,
            Metadata = new Dictionary<string, string>
            {
                ["source"] = "agent_tool", ["tool"] = "send_message", ["intent"] = "inform",
            },
        };

        try
        {
            var result = await SendAsync(envelope, ct);
            return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
            {
                status = "ok", result.MessageId, result.RoomId, result.DeliveryIds,
            }, JsonOptions));
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Fail(ex.Message);
        }
    }

    private async Task<MessageSendResult> SendAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        if (_scopeFactory is null)
            throw new InvalidOperationException("Message system is not configured.");

        using var scope = _scopeFactory.CreateScope();
        var messageSystem = scope.ServiceProvider.GetRequiredService<IMessageSystem>();
        return await messageSystem.SendAsync(envelope, ct);
    }

    private static string ResolveAudience(string? explicitAudience, IReadOnlyList<MessageAddress> targets)
    {
        if (!string.IsNullOrWhiteSpace(explicitAudience)) return explicitAudience!;
        return targets.Any(t => t.Kind == MessageEndpointKinds.Room) ? MessageAudiences.Broadcast : MessageAudiences.Direct;
    }

    private static IReadOnlyList<MessageAddress> ParseTargets(string? raw, string workspaceId, string? roomId)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var tokens = raw.Split([',', ';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var result = new List<MessageAddress>();
        foreach (var token in tokens)
        {
            if (token.Equals("@all", StringComparison.OrdinalIgnoreCase) || token.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new MessageAddress { Kind = MessageEndpointKinds.Room, Id = string.IsNullOrWhiteSpace(roomId) ? "default" : roomId!, WorkspaceId = workspaceId });
                continue;
            }
            var parts = token.Split(':', 2, StringSplitOptions.TrimEntries);
            result.Add(parts.Length == 2
                ? new MessageAddress { Kind = parts[0], Id = parts[1], WorkspaceId = workspaceId }
                : new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = token, WorkspaceId = workspaceId });
        }
        return result;
    }
}

public sealed record SendMessageArgs
{
    [ToolParam("Message content to send.")]
    public string? Content { get; init; }
    [ToolParam("Message target address list. Examples: user:owner, agent:assistant, room:default, @all.")]
    public string? To { get; init; }
    [ToolParam("Optional audience: direct / broadcast / room.")]
    public string? Audience { get; init; }
    [ToolParam("Optional visibility: private / public / system.")]
    public string? Visibility { get; init; }
    [ToolParam("Optional room id for room transcript and @all broadcasts.")]
    public string? RoomId { get; init; }
    [ToolParam("Optional numeric priority. 5 maps to important, 10 maps to urgent.")]
    public int? Priority { get; init; }
    [ToolParam("Optional message id this message replies to.")]
    public string? ReplyToMessageId { get; init; }
}
