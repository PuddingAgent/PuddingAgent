using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Agent-facing message sender backed by the message fabric.
/// agent:* 目标走消息织物；飞书显式收件人来自配置目录，当前回合回复来自可信命令。
/// </summary>
[Tool(
    id: "send_message",
    name: "发送消息",
    description: "通过消息系统发送消息。Agent 目标默认 intent=inform（只通知、不唤醒模型、不自动回复）；需要对方执行时使用 ask/request_review/delegate，平台最多投影一次被动回复。飞书主动发送先用 list_feishu_recipients 查询，再用 to=user:ou_xxx 和可选 channel_id；不依赖历史消息。user:owner 在飞书回合回复当前用户，否则选唯一白名单收件人；多位候选必须明确选择。飞书每次仅一个目标，不支持广播。",
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

        var intent = string.IsNullOrWhiteSpace(args.Intent)
            ? MessageIntents.Inform
            : args.Intent.Trim().ToLowerInvariant();
        if (!MessageIntents.AgentToolAllowed.Contains(intent))
        {
            return ToolExecutionResult.Fail(
                "intent must be one of: inform, ask, request_review, delegate, report_result.");
        }
        var requiresResponse = args.RequiresResponse
            ?? (intent is MessageIntents.Ask
                or MessageIntents.RequestReview
                or MessageIntents.Delegate);

        // 飞书单点发送与 Agent 消息织物使用各自的目标解析边界。
        if (targets.Any(t => !string.Equals(
                t.Kind,
                MessageEndpointKinds.Agent,
                StringComparison.OrdinalIgnoreCase)))
        {
            return await SendViaFeishuRouteAsync(args, targets, context, ct);
        }

        // 分支 A：纯 agent 目标保持原有 Message Fabric 投递。
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
                ["source"] = "agent_tool",
                ["tool"] = "send_message",
                [MessageDeliveryPolicy.IntentMetadataKey] = intent,
                [MessageDeliveryPolicy.RequiresResponseMetadataKey] =
                    requiresResponse ? "true" : "false",
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

    private async Task<ToolExecutionResult> SendViaFeishuRouteAsync(
        SendMessageArgs args,
        IReadOnlyList<MessageAddress> targets,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (targets.Any(t => t.Kind.Equals(MessageEndpointKinds.Room, StringComparison.OrdinalIgnoreCase)))
            return ToolExecutionResult.Fail("广播目标暂不支持：飞书发送请逐个指定收件人。");
        if (targets.Count != 1)
            return ToolExecutionResult.Fail("飞书发送每次只支持一个目标；不能混合 Agent 与用户或多个收件人。");

        var target = targets[0];
        var isUser = target.Kind.Equals(MessageEndpointKinds.User, StringComparison.OrdinalIgnoreCase);
        var isConnector = target.Kind.Equals(MessageEndpointKinds.Connector, StringComparison.OrdinalIgnoreCase);
        if ((!isUser && !isConnector) || string.IsNullOrWhiteSpace(target.Id))
            return ToolExecutionResult.Fail("不支持的飞书目标。请用 list_feishu_recipients 查询，再指定 user:ou_xxx。");
        if (_scopeFactory is null)
            return ToolExecutionResult.Fail("Message system is not configured.");

        using var scope = _scopeFactory.CreateScope();
        var identity = context.ExecutionIdentity;
        var channelId = string.IsNullOrWhiteSpace(args.ChannelId) ? null : args.ChannelId.Trim();
        // 只有别名/当前连接器允许回复当前回合；显式用户 ID 永不被入站路由覆盖。
        var isDefaultUser = isUser && target.Id.Equals("owner", StringComparison.OrdinalIgnoreCase);
        GatewayCommandRoute? route = null;
        if ((isDefaultUser || isConnector) && identity?.CommandId is { Length: > 0 } commandId)
        {
            var isMainTurn = identity.Kind == RuntimeExecutionKind.ConversationTurn;
            var isDelegated = identity.Kind == RuntimeExecutionKind.SubAgent
                && identity.ParentRunId is { Length: > 0 };
            var reader = scope.ServiceProvider.GetService<IGatewayCommandRouteReader>();
            if (reader is not null && (isMainTurn || isDelegated))
            {
                var resolved = await reader.GetAsync(commandId, ct);
                if (resolved is not null
                    && resolved.WorkspaceId == context.WorkspaceId
                    && resolved.ConversationId == identity.ConversationId
                    && MatchesExecutionAgent(resolved, context, isMainTurn)
                    && resolved.IsGatewayIngress
                    && string.Equals(resolved.ChannelType, "feishu", StringComparison.OrdinalIgnoreCase)
                    && (channelId is null || resolved.ConnectorId == FeishuConnectorIds.ForChannel(channelId)))
                    route = resolved;
            }
        }

        string connectorId;
        string agentId;
        string conversationId;
        string? turnId;
        string? recipientAddress = null;
        Dictionary<string, string> metadata;
        if (route is not null)
        {
            if (string.IsNullOrWhiteSpace(route.ConnectorId)
                || string.IsNullOrWhiteSpace(route.ExternalConversationId))
                return ToolExecutionResult.Fail("飞书回信路由不完整：缺少 ConnectorId 或 ExternalConversationId。");
            if (isConnector && target.Id != route.ConnectorId)
                return ToolExecutionResult.Fail("目标与当前回合的受信任飞书连接器不一致。");
            connectorId = route.ConnectorId;
            agentId = route.AgentInstanceId;
            conversationId = route.ConversationId;
            turnId = route.TurnId;
            metadata = new Dictionary<string, string>(route.Metadata, StringComparer.Ordinal)
            {
                [MessageGatewayMetadata.ExternalConversationId] = route.ExternalConversationId,
            };
            // 回复只消费回合路由，不接受混入的主动收件人或底层协议别名。
            metadata.Remove(MessageGatewayMetadata.ReceiveId);
            metadata.Remove(MessageGatewayMetadata.ReceiveIdType);
            metadata.Remove("open_id");
            metadata.Remove("message_id");
            metadata.Remove(MessageGatewayMetadata.ExternalMessageId);
            if (!string.IsNullOrWhiteSpace(route.ExternalMessageId))
                metadata[MessageGatewayMetadata.ExternalMessageId] = route.ExternalMessageId;
        }
        else
        {
            if (isConnector)
                return ToolExecutionResult.Fail("无当前飞书回信上下文。主动发送请查询 list_feishu_recipients 并指定 user:ou_xxx。");
            var directory = scope.ServiceProvider.GetService<IFeishuRecipientDirectory>();
            if (directory is null)
                return ToolExecutionResult.Fail("飞书收件人目录未配置，无法主动发送。");
            agentId = context.ConfigurationAgentInstanceId ?? context.AgentInstanceId;
            var candidates = (await directory.ListAsync(context.WorkspaceId, agentId, ct))
                .Where(item => (channelId is null || item.ChannelId == channelId)
                    && (isDefaultUser || item.OpenId == target.Id)).ToList();
            if (candidates.Count == 0)
                return ToolExecutionResult.Fail("未找到已绑定且启用的飞书白名单收件人。请检查渠道绑定及特权用户 Open ID，或用 list_feishu_recipients 查询。");
            if (candidates.Count != 1)
                return ToolExecutionResult.Fail("存在多个飞书收件人或渠道，请用 list_feishu_recipients 查询并明确指定 user:OpenID 与 channel_id。");
            var recipient = candidates[0];
            connectorId = recipient.ConnectorId;
            recipientAddress = recipient.Address;
            conversationId = context.SessionId;
            turnId = identity?.TurnId;
            // 主动发送只使用新配置，不继承任何历史会话、回复锚点或发件人元数据。
            metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MessageGatewayMetadata.ChannelId] = recipient.ChannelId,
                [MessageGatewayMetadata.ChannelType] = "feishu",
                [MessageGatewayMetadata.ReceiveId] = recipient.OpenId,
                [MessageGatewayMetadata.ReceiveIdType] = "open_id",
            };
        }

        var isReply = route is not null;
        var messageId = isReply
            ? StableId("send-message", identity!.CommandId!, connectorId,
                route!.ExternalMessageId ?? route.ExternalConversationId!)
            : Guid.NewGuid().ToString("N");
        metadata[MessageGatewayMetadata.ConnectorId] = connectorId;
        metadata[MessageGatewayMetadata.IdempotencyKey] = messageId;
        metadata[MessageGatewayMetadata.IsProjection] = "true";

        try
        {
            var messageSystem = scope.ServiceProvider.GetRequiredService<IMessageSystem>();
            var result = await messageSystem.SendAsync(new MessageEnvelope
            {
                MessageId = messageId,
                From = new MessageAddress
                {
                    Kind = MessageEndpointKinds.Agent, Id = agentId, WorkspaceId = context.WorkspaceId,
                },
                To = [new MessageAddress
                {
                    Kind = MessageEndpointKinds.Connector, Id = connectorId, WorkspaceId = context.WorkspaceId,
                }],
                RoomId = conversationId,
                ConversationId = conversationId,
                ReplyToMessageId = route?.ExternalMessageId,
                CorrelationId = conversationId,
                CausationId = turnId,
                Audience = MessageAudiences.Direct,
                Visibility = MessageVisibilities.Private,
                ContentType = MessageContentTypes.Text,
                Content = args.Content ?? string.Empty,
                Metadata = metadata,
            }, ct);
            return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
            {
                status = "ok",
                deliveryStatus = "queued",
                route = "feishu",
                target = $"connector:{connectorId}",
                recipient = recipientAddress,
                isReply,
                result.MessageId,
                result.RoomId,
                result.DeliveryIds,
                externalMessageId = route?.ExternalMessageId,
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

    /// <summary>
    /// 校验命令归属的执行主体。主会话要求命令属于当前 AgentInstanceId；
    /// 子代理运行使用临时 sub-session 身份（AgentInstanceId），
    /// 但命令归属根 Agent，因此按 ConfigurationAgentInstanceId（根 Agent）校验。
    /// </summary>
    private static bool MatchesExecutionAgent(
        GatewayCommandRoute route,
        ToolExecutionContext context,
        bool isMainTurn)
    {
        if (isMainTurn)
        {
            return string.Equals(
                route.AgentInstanceId,
                context.AgentInstanceId,
                StringComparison.Ordinal);
        }

        return context.ConfigurationAgentInstanceId is { Length: > 0 }
               && string.Equals(
                   route.AgentInstanceId,
                   context.ConfigurationAgentInstanceId,
                   StringComparison.Ordinal);
    }

    private static string StableId(params string[] parts)
    {
        var raw = string.Join('\n', parts);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
            .ToLowerInvariant()[..32];
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
    [ToolParam("Agent address list, or one Feishu recipient from list_feishu_recipients (user:ou_xxx). user:owner replies to the current Feishu turn, otherwise selects the sole configured recipient. Feishu broadcasts/mixed targets are unsupported.")]
    public string? To { get; init; }
    [ToolParam("Optional Feishu channel id from list_feishu_recipients. Required when the same Open ID occurs in multiple channels.")]
    public string? ChannelId { get; init; }
    [ToolParam("Optional audience: direct / broadcast / room.")]
    public string? Audience { get; init; }
    [ToolParam("Optional visibility: private / public / system.")]
    public string? Visibility { get; init; }
    [ToolParam("Optional room id for room transcript and @all broadcasts.")]
    public string? RoomId { get; init; }
    [ToolParam("Optional numeric priority. 5 maps to important, 10 maps to urgent.")]
    public int? Priority { get; init; }
    [ToolParam("Agent message intent. inform/report_result are passive notifications; ask/request_review/delegate create one executable request. Defaults to inform.")]
    public string? Intent { get; init; }
    [ToolParam("Optional response contract. Defaults true for ask/request_review/delegate and false for inform/report_result. Replies never wake the receiving model.")]
    public bool? RequiresResponse { get; init; }
    [ToolParam("Optional message id this message replies to.")]
    public string? ReplyToMessageId { get; init; }
}
