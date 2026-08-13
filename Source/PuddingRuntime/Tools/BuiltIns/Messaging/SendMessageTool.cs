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
/// agent:* 目标走原有织物投递；user:* / connector:* 目标（以及 @all / room:*
/// 广播请求）在当前飞书回合中解析为受信任连接器的文字回信。
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

        // 分支 B：任何非 agent 目标（user / room / connector / @all）都通过
        // 当前飞书命令路由投递，避免 user/room/connector 投递落入无人认领的
        // MessageDeliveryDispatcher（只认领 agent）或缺少 ExternalConversationId
        // 而死信的 ConnectorDeliveryDispatcher。
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

    /// <summary>
    /// 飞书回信路由：从当前执行 CommandId 解析受信任连接器路由，构造
    /// Kind=Connector 的 MessageEnvelope（ContentType=Text）交给
    /// ConnectorDeliveryDispatcher 认领。失败一律写入 tool_result，不抛死循环。
    /// </summary>
    private async Task<ToolExecutionResult> SendViaFeishuRouteAsync(
        SendMessageArgs args,
        IReadOnlyList<MessageAddress> targets,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        // 广播目标没有单点 ExternalConversationId：当前仅支持 1:1 私聊回信。
        if (targets.Any(t => string.Equals(
                t.Kind,
                MessageEndpointKinds.Room,
                StringComparison.OrdinalIgnoreCase)))
        {
            return ToolExecutionResult.Fail(
                "广播目标暂不支持：@all / room:* 没有单点 ExternalConversationId，当前仅支持 1:1 私聊回信。");
        }

        if (_scopeFactory is null)
            return ToolExecutionResult.Fail("Message system is not configured.");

        using var scope = _scopeFactory.CreateScope();
        var routeReader = scope.ServiceProvider.GetService<IGatewayCommandRouteReader>();
        if (routeReader is null)
        {
            return ToolExecutionResult.Fail(
                "无飞书会话上下文，无法投递：当前运行时未配置飞书路由解析器。");
        }

        var identity = context.ExecutionIdentity;
        var commandId = identity?.CommandId;

        GatewayCommandRoute route;
        if (!string.IsNullOrWhiteSpace(commandId))
        {
            // 飞书 ingress 回合：按 CommandId 解析受信任回信路由。
            // 主会话 Turn 与子代理执行均可解析到飞书命令路由：
            // 子代理身份由编排层继承父 CommandId，命令行仍指向网关入口命令。
            var isMainTurn = identity!.Kind == RuntimeExecutionKind.ConversationTurn;
            var isDelegatedSubAgent = identity.Kind == RuntimeExecutionKind.SubAgent
                && identity.ParentRunId is { Length: > 0 };
            if (!isMainTurn && !isDelegatedSubAgent)
            {
                return ToolExecutionResult.Fail(
                    "send_message 飞书回信仅在主会话 Turn 或委派子代理执行中可用。");
            }

            var resolved = await routeReader.GetAsync(commandId, ct);
            if (resolved is null)
            {
                return ToolExecutionResult.Fail(
                    $"无飞书会话上下文，无法投递：CommandId={commandId} 对应的执行命令不存在。");
            }
            route = resolved;

            // 归属校验：与 SendImageTool 一致，防止跨工作区/跨会话/跨 Agent 误投。
            if (!string.Equals(route.WorkspaceId, context.WorkspaceId, StringComparison.Ordinal)
                || !string.Equals(route.ConversationId, identity.ConversationId, StringComparison.Ordinal)
                || !MatchesExecutionAgent(route, context, isMainTurn))
            {
                return ToolExecutionResult.Fail(
                    "飞书回信路由校验失败：执行命令不属于当前工作区/会话/Agent。");
            }

            if (!route.IsGatewayIngress
                || !string.Equals(route.ChannelType, "feishu", StringComparison.OrdinalIgnoreCase))
            {
                return ToolExecutionResult.Fail(
                    "send_message 飞书回信仅支持飞书来源回合（IsGatewayIngress=true 且 ChannelType=feishu）。");
            }
        }
        else
        {
            // 主动发送场景（无 CommandId，如心跳/网页端）：回退到该 Agent 最近一次
            // 飞书会话的受信任连接器路由，投递到用户最近对话的单聊 chat_id。
            var resolved = await routeReader.FindRecentFeishuRouteAsync(
                context.AgentInstanceId,
                context.WorkspaceId,
                ct);
            if (resolved is null)
            {
                return ToolExecutionResult.Fail(
                    "无最近飞书会话，无法主动投递：请先在飞书向该 Agent 发过消息，之后才能从心跳/网页端主动推送。");
            }
            route = resolved;
        }

        var connectorId = route.ConnectorId;
        var externalConversationId = route.ExternalConversationId;
        if (string.IsNullOrWhiteSpace(connectorId)
            || string.IsNullOrWhiteSpace(externalConversationId))
        {
            return ToolExecutionResult.Fail(
                "飞书回信路由不完整：缺少 ConnectorId 或 ExternalConversationId，无法投递。");
        }

        // connector 目标显式点名时，必须与当前回合的受信任连接器一致。
        var explicitConnectorId = targets
            .FirstOrDefault(t => string.Equals(
                t.Kind,
                MessageEndpointKinds.Connector,
                StringComparison.OrdinalIgnoreCase))
            ?.Id;
        if (!string.IsNullOrWhiteSpace(explicitConnectorId)
            && !string.Equals(explicitConnectorId, connectorId, StringComparison.Ordinal))
        {
            return ToolExecutionResult.Fail(
                $"无法投递到 connector:{explicitConnectorId}：当前回合的受信任飞书连接器是 connector:{connectorId}。");
        }

        // ExternalMessageId 有则作为回复锚点，无则新发。
        var externalMessageId = route.ExternalMessageId;
        var messageId = StableId(
            "send-message",
            commandId,
            connectorId,
            externalMessageId ?? externalConversationId);

        var metadata = new Dictionary<string, string>(
            route.Metadata,
            StringComparer.Ordinal)
        {
            [MessageGatewayMetadata.ConnectorId] = connectorId,
            [MessageGatewayMetadata.IdempotencyKey] = messageId,
            [MessageGatewayMetadata.IsProjection] = "true",
        };

        try
        {
            var messageSystem = scope.ServiceProvider.GetRequiredService<IMessageSystem>();
            var result = await messageSystem.SendAsync(
                new MessageEnvelope
                {
                    MessageId = messageId,
                    From = new MessageAddress
                    {
                        Kind = MessageEndpointKinds.Agent,
                        Id = route.AgentInstanceId,
                        WorkspaceId = route.WorkspaceId,
                    },
                    To =
                    [
                        new MessageAddress
                        {
                            Kind = MessageEndpointKinds.Connector,
                            Id = connectorId,
                            WorkspaceId = route.WorkspaceId,
                        },
                    ],
                    RoomId = route.ConversationId,
                    ConversationId = route.ConversationId,
                    ReplyToMessageId = externalMessageId,
                    CorrelationId = route.ConversationId,
                    CausationId = route.TurnId,
                    Audience = MessageAudiences.Direct,
                    Visibility = MessageVisibilities.Private,
                    ContentType = MessageContentTypes.Text,
                    Content = args.Content ?? string.Empty,
                    Metadata = metadata,
                },
                ct);
            return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
            {
                status = "ok",
                route = "feishu",
                target = $"connector:{connectorId}",
                result.MessageId,
                result.RoomId,
                result.DeliveryIds,
                externalMessageId,
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
