using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingController.Services;
using PuddingPlatform.Services;

namespace PuddingAgent.Services;

/// <summary>
/// Connector ingress: validates the channel-owned binding and the
/// Agent channel reference, then durably submits one Message Fabric delivery.
/// The Agent dispatcher then accepts that delivery through ADR-059.
/// </summary>
public sealed class MessageGatewayIngress(
    AgentManifestCatalog manifests,
    ChannelConfigurationFileService channels,
    InMemorySessionRepository sessions,
    IAgentMainSessionBinder workspaceAgents,
    IServiceScopeFactory scopeFactory,
    ILogger<MessageGatewayIngress> logger) : IMessageGatewayIngress
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();

    public async Task<MessageGatewayIngressResult> AcceptAsync(
        PuddingIngressEnvelope envelope,
        CancellationToken ct = default)
    {
        ValidateEnvelope(envelope);

        var manifest = await manifests.GetAsync(envelope.AgentId!, ct)
            ?? throw new InvalidOperationException(
                $"Bound Agent '{envelope.AgentId}' does not exist.");
        var channel = await channels.GetChannelAsync(envelope.ChannelId, ct)
            ?? throw new InvalidOperationException(
                $"Bound channel '{envelope.ChannelId}' does not exist.");
        ValidateBinding(envelope, manifest, channel);

        var externalMessageId = envelope.ExternalMessageId ?? envelope.EnvelopeId;
        var messageId = StableId(
            "gateway-message",
            envelope.ConnectorId!,
            externalMessageId);
        var clientRequestId = StableId(
            "gateway-request",
            envelope.ConnectorId!,
            externalMessageId);

        using var scope = scopeFactory.CreateScope();
        ChannelSender? sender = envelope.ChannelType switch
        {
            "feishu" when channel.Feishu != null && envelope.Metadata.GetValueOrDefault("feishu_verified_private_user") == "true" =>
                new(channel.ChannelId, channel.Feishu.AppId, envelope.Metadata.GetValueOrDefault("feishu_tenant") ?? "", envelope.UserExternalId, false),
            "slack" when channel.Slack != null && envelope.Metadata.GetValueOrDefault(SlackChannelMetadata.IsBot) == "false" &&
                !channel.Slack.AllowedBotUserIds.Contains(envelope.Metadata[SlackChannelMetadata.UserId], StringComparer.Ordinal) =>
                new(channel.ChannelId, channel.Slack.AppId, channel.Slack.TeamId, envelope.Metadata[SlackChannelMetadata.UserId],
                    envelope.Metadata[SlackChannelMetadata.IsDirectMessage] != "true"),
            _ => null
        };
        var bound = scope.ServiceProvider.GetService<BoundChannelSessions>();
        var development = bound == null ? null : await bound.ResolveAsync(manifest, sender,
            envelope.Metadata.GetValueOrDefault(SlackChannelMetadata.ConversationKey) ?? envelope.ExternalConversationId!,
            messageId, MessagePrincipalIdentity.FromSender(MessageEndpointKinds.User, envelope.UserExternalId), ct);
        var conversationId = development?.Session ?? (envelope.ChannelType == ChannelProviderKinds.Slack || bound != null
            ? await EnsureExternalConversationAsync(manifest, envelope, ct)
            : await EnsureMainConversationAsync(manifest, ct));

        var metadata = new Dictionary<string, string>(
            envelope.Metadata,
            StringComparer.Ordinal)
        {
            [MessageGatewayMetadata.IsGatewayIngress] = "true",
            [MessageGatewayMetadata.ChannelId] = envelope.ChannelId,
            [MessageGatewayMetadata.ChannelType] = envelope.ChannelType,
            [MessageGatewayMetadata.ConnectorId] = envelope.ConnectorId!,
            [MessageGatewayMetadata.ExternalConversationId] =
                envelope.ExternalConversationId!,
            [MessageGatewayMetadata.ExternalMessageId] = externalMessageId,
            [MessageGatewayMetadata.ExternalUserId] = envelope.UserExternalId,
            [MessageGatewayMetadata.MessageType] = envelope.MessageType ?? "chat",
            [MessageGatewayMetadata.ClientRequestId] = clientRequestId,
        };

        metadata.Remove(ChannelDevelopmentTrust.Receipt);
        if (development != null)
            metadata[ChannelDevelopmentTrust.Receipt] = scope.ServiceProvider.GetRequiredService<ChannelDevelopmentTrust>().Issue(development);
        var commandResult = await TryHandleCommandAsync(
            scope.ServiceProvider,
            envelope,
            manifest,
            channel,
            conversationId,
            externalMessageId,
            messageId,
            clientRequestId,
            metadata,
            development,
            ct);
        if (commandResult is not null)
            return commandResult;

        var messageSystem = scope.ServiceProvider.GetRequiredService<IMessageSystem>();
        var result = await messageSystem.SendAsync(
            new MessageEnvelope
            {
                MessageId = messageId,
                From = new MessageAddress
                {
                    Kind = MessageEndpointKinds.User,
                    Id = envelope.UserExternalId,
                    WorkspaceId = manifest.WorkspaceId,
                },
                To =
                [
                    new MessageAddress
                    {
                        Kind = MessageEndpointKinds.Agent,
                        Id = manifest.AgentInstanceId,
                        WorkspaceId = manifest.WorkspaceId,
                        DisplayName = manifest.DisplayName,
                    },
                ],
                RoomId = conversationId,
                ConversationId = conversationId,
                CorrelationId = envelope.CorrelationId
                    ?? envelope.ExternalConversationId,
                CausationId = externalMessageId,
                Audience = MessageAudiences.Direct,
                Visibility = MessageVisibilities.Private,
                ContentType = MessageContentTypes.Text,
                Content = envelope.MessageText,
                CreatedAt = envelope.Timestamp.ToUnixTimeMilliseconds(),
                Metadata = metadata,
            },
            ct);

        logger.LogInformation(
            "[MessageGateway] Ingress accepted connector={ConnectorId} channel={ChannelType} agent={AgentId} conversation={ConversationId} message={MessageId}",
            envelope.ConnectorId,
            envelope.ChannelType,
            manifest.AgentInstanceId,
            conversationId,
            messageId);

        return new MessageGatewayIngressResult
        {
            MessageId = result.MessageId,
            ConversationId = conversationId,
            DeliveryIds = result.DeliveryIds,
        };
    }

    /// <summary>
    /// Slash commands are Pudding control messages, not Agent prompts. The
    /// gateway records and replies to every command itself. A command reaches an
    /// Agent only when its system handler explicitly returns ForwardToAgent.
    /// </summary>
    private async Task<MessageGatewayIngressResult?> TryHandleCommandAsync(
        IServiceProvider serviceProvider,
        PuddingIngressEnvelope envelope,
        AgentInstanceManifest manifest,
        ChannelInstanceManifest channel,
        string conversationId,
        string externalMessageId,
        string ingressMessageId,
        string clientRequestId,
        Dictionary<string, string> metadata,
        DevelopmentTurn? development,
        CancellationToken ct)
    {
        var commandText = envelope.MessageText.Trim();
        if (!commandText.StartsWith("/", StringComparison.Ordinal))
            return null;

        var parsed = SystemCommandParser.TryParse(commandText, out var command);
        var requiresPrivilege = parsed
                                && SystemCommandParser.RequiresPrivilege(command);
        var isPrivilegedUser = envelope.ChannelType == ChannelProviderKinds.Feishu &&
            envelope.Metadata.GetValueOrDefault("feishu_verified_private_user") == "true" &&
            development != null && IsPrivilegedFeishuUser(
            channel.Feishu,
            envelope.UserExternalId);
        var responseMessageId = StableId(
            "gateway-command-response",
            envelope.ConnectorId!,
            externalMessageId);
        var handler = serviceProvider.GetRequiredService<ISystemCommandHandler>();
        var result = await handler.HandleAsync(
            new SystemCommandRequest(
                ConversationId: conversationId,
                WorkspaceId: manifest.WorkspaceId,
                AgentId: manifest.AgentInstanceId,
                UserId: MessagePrincipalIdentity.FromSender(MessageEndpointKinds.User, envelope.UserExternalId),
                ClientRequestId: clientRequestId,
                ClientMessageId: ingressMessageId,
                ResponseMessageId: responseMessageId,
                CommandText: commandText,
                IsPrivilegedUser: isPrivilegedUser,
                SourceChannel: envelope.ChannelType,
                ExternalUserId: envelope.UserExternalId),
            ct);

        var commandMetadata = new Dictionary<string, string>(
            metadata,
            StringComparer.Ordinal)
        {
            [MessageGatewayMetadata.IsGatewayCommand] = "true",
            [MessageGatewayMetadata.GatewayCommand] = commandText,
        };
        var messageSystem = serviceProvider.GetRequiredService<IMessageSystem>();

        if (result.ForwardToAgent)
        {
            var forwardedMessageId = StableId(
                "gateway-command-agent-message",
                envelope.ConnectorId!,
                externalMessageId);
            var forwardedRequestId = StableId(
                "gateway-command-agent-request",
                envelope.ConnectorId!,
                externalMessageId);
            commandMetadata[MessageGatewayMetadata.ClientRequestId] =
                forwardedRequestId;
            if (development != null)
                commandMetadata[ChannelDevelopmentTrust.Receipt] = serviceProvider.GetRequiredService<ChannelDevelopmentTrust>()
                    .Issue(development with { Message = forwardedMessageId,
                        Actor = MessagePrincipalIdentity.FromSender(MessageEndpointKinds.System, "pudding") });

            var forwarded = await messageSystem.SendAsync(
                new MessageEnvelope
                {
                    MessageId = forwardedMessageId,
                    From = new MessageAddress
                    {
                        Kind = MessageEndpointKinds.System,
                        Id = "pudding",
                        WorkspaceId = manifest.WorkspaceId,
                        DisplayName = "Pudding",
                    },
                    To =
                    [
                        new MessageAddress
                        {
                            Kind = MessageEndpointKinds.Agent,
                            Id = manifest.AgentInstanceId,
                            WorkspaceId = manifest.WorkspaceId,
                            DisplayName = manifest.DisplayName,
                        },
                    ],
                    RoomId = conversationId,
                    ConversationId = conversationId,
                    CorrelationId = envelope.CorrelationId
                        ?? envelope.ExternalConversationId,
                    CausationId = externalMessageId,
                    Audience = MessageAudiences.Direct,
                    Visibility = MessageVisibilities.Private,
                    ContentType = MessageContentTypes.Text,
                    Content = string.IsNullOrWhiteSpace(result.AgentMessage)
                        ? result.Message
                        : result.AgentMessage,
                    CreatedAt = envelope.Timestamp.ToUnixTimeMilliseconds(),
                    Metadata = commandMetadata,
                },
                ct);

            logger.LogInformation(
                "[MessageGateway] Command forwarded after Pudding handling command={Command} connector={ConnectorId} agent={AgentId} message={MessageId}",
                result.Command,
                envelope.ConnectorId,
                manifest.AgentInstanceId,
                forwarded.MessageId);
            return new MessageGatewayIngressResult
            {
                MessageId = ingressMessageId,
                ConversationId = conversationId,
                DeliveryIds = forwarded.DeliveryIds,
            };
        }

        var replyMessageId = StableId(
            "gateway-command-reply",
            envelope.ConnectorId!,
            externalMessageId);
        commandMetadata[MessageGatewayMetadata.ReplyProjectedMessageId] =
            replyMessageId;
        commandMetadata[MessageGatewayMetadata.IdempotencyKey] =
            replyMessageId;
        var reply = await messageSystem.SendAsync(
            new MessageEnvelope
            {
                MessageId = replyMessageId,
                From = new MessageAddress
                {
                    Kind = MessageEndpointKinds.System,
                    Id = "pudding",
                    WorkspaceId = manifest.WorkspaceId,
                    DisplayName = "Pudding",
                },
                To =
                [
                    new MessageAddress
                    {
                        Kind = MessageEndpointKinds.Connector,
                        Id = envelope.ConnectorId!,
                        WorkspaceId = manifest.WorkspaceId,
                    },
                ],
                RoomId = conversationId,
                ConversationId = conversationId,
                ReplyToMessageId = externalMessageId,
                CorrelationId = envelope.CorrelationId
                    ?? envelope.ExternalConversationId,
                CausationId = externalMessageId,
                Audience = MessageAudiences.Direct,
                Visibility = MessageVisibilities.Private,
                ContentType = MessageContentTypes.Text,
                Content = development?.Source.Group == true ? "命令已处理，详情请在工作台核对。" : result.Message,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Metadata = commandMetadata,
            },
            ct);

        logger.LogInformation(
            "[MessageGateway] Command intercepted command={Command} parsed={Parsed} privileged={Privileged} whitelisted={Whitelisted} connector={ConnectorId} message={MessageId}",
            result.Command,
            parsed,
            requiresPrivilege,
            isPrivilegedUser,
            envelope.ConnectorId,
            reply.MessageId);
        return new MessageGatewayIngressResult
        {
            MessageId = ingressMessageId,
            ConversationId = conversationId,
            DeliveryIds = reply.DeliveryIds,
        };
    }

    private async Task<string> EnsureMainConversationAsync(
        AgentInstanceManifest manifest,
        CancellationToken ct)
    {
        var gate = _sessionLocks.GetOrAdd(
            $"{manifest.WorkspaceId}:{manifest.AgentInstanceId}",
            _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(manifest.MainSessionId))
            {
                var configured = await sessions.GetAsync(manifest.MainSessionId, ct);
                if (configured is not null
                    && string.Equals(
                        configured.WorkspaceId,
                        manifest.WorkspaceId,
                        StringComparison.Ordinal)
                    && string.Equals(
                        configured.AgentInstanceId,
                        manifest.AgentInstanceId,
                        StringComparison.Ordinal)
                    && configured.SessionRole == SessionRole.Main)
                {
                    return configured.SessionId;
                }
            }

            var existing = await sessions.FindMainAsync(
                manifest.WorkspaceId,
                "agent",
                manifest.AgentInstanceId,
                ct);
            if (existing is not null)
            {
                await workspaceAgents.SetAgentMainSessionAsync(
                    manifest.WorkspaceId,
                    manifest.AgentInstanceId,
                    existing.SessionId,
                    ct);
                return existing.SessionId;
            }

            var created = new SessionRecord
            {
                SessionId = Guid.NewGuid().ToString("N"),
                WorkspaceId = manifest.WorkspaceId,
                AgentTemplateId = manifest.TemplateId,
                AgentInstanceId = manifest.AgentInstanceId,
                ChannelId = "admin",
                OwnerUserId = "admin",
                SessionType = SessionType.ServiceSession,
                SessionRole = SessionRole.Main,
                PrincipalKind = "agent",
                PrincipalId = manifest.AgentInstanceId,
                Status = SessionStatus.Active,
                Title = string.IsNullOrWhiteSpace(manifest.DisplayName)
                    ? "主线"
                    : $"{manifest.DisplayName} 主线",
            };
            await sessions.CreateAsync(created, ct);
            await workspaceAgents.SetAgentMainSessionAsync(
                manifest.WorkspaceId,
                manifest.AgentInstanceId,
                created.SessionId,
                ct);
            return created.SessionId;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> EnsureExternalConversationAsync(AgentInstanceManifest manifest, PuddingIngressEnvelope envelope, CancellationToken ct)
    {
        var slack = envelope.ChannelType == ChannelProviderKinds.Slack;
        var route = slack ? envelope.Metadata[SlackChannelMetadata.ConversationKey] : envelope.ExternalConversationId!;
        var sessionId = StableId(envelope.ChannelType + "-session", manifest.WorkspaceId, manifest.AgentInstanceId, envelope.ConnectorId!, route);
        var gate = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var existing = await sessions.GetAsync(sessionId, ct);
            if (existing is not null)
            {
                if (existing.WorkspaceId != manifest.WorkspaceId || existing.AgentInstanceId != manifest.AgentInstanceId || existing.ChannelId != envelope.ChannelId)
                    throw new InvalidOperationException("External conversation identity conflict.");
                return sessionId;
            }
            await sessions.CreateAsync(new SessionRecord
            {
                SessionId = sessionId, WorkspaceId = manifest.WorkspaceId, AgentTemplateId = manifest.TemplateId,
                AgentInstanceId = manifest.AgentInstanceId, ChannelId = envelope.ChannelId, OwnerUserId = envelope.UserExternalId,
                SessionType = SessionType.ServiceSession, SessionRole = SessionRole.Branch, PrincipalKind = "channel",
                PrincipalId = route, Status = SessionStatus.Active,
                Title = $"{(slack ? envelope.Metadata[SlackChannelMetadata.IsDirectMessage] == "true" ? "Slack 私聊" : "Slack 频道线程" : "飞书会话")} · {envelope.MessageText.Replace('\r', ' ').Replace('\n', ' ')[..Math.Min(50, envelope.MessageText.Length)]}"
            }, ct);
            return sessionId;
        }
        finally { gate.Release(); }
    }

    private static bool IsPrivilegedFeishuUser(
        FeishuChannelSettings? binding,
        string externalUserId)
        => binding?.PrivilegedUserOpenIds.Any(
               allowed => string.Equals(
                   allowed,
                   externalUserId,
                   StringComparison.OrdinalIgnoreCase))
           == true;

    private static void ValidateEnvelope(PuddingIngressEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(envelope.ConnectorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(envelope.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(envelope.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(envelope.ChannelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(envelope.ChannelType);
        ArgumentException.ThrowIfNullOrWhiteSpace(envelope.UserExternalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(envelope.MessageText);
        ArgumentException.ThrowIfNullOrWhiteSpace(envelope.ExternalConversationId);

        if (envelope.ChannelType is not (ChannelProviderKinds.Feishu or ChannelProviderKinds.Slack))
            throw new NotSupportedException(
                $"Message Gateway does not accept '{envelope.ChannelType}'.");
    }

    private static void ValidateBinding(
        PuddingIngressEnvelope envelope,
        AgentInstanceManifest manifest,
        ChannelInstanceManifest channel)
    {
        if (!manifest.IsEnabled || manifest.IsFrozen)
            throw new InvalidOperationException(
                $"Bound Agent '{manifest.AgentInstanceId}' is disabled or frozen.");
        if (!channel.IsEnabled) throw new InvalidOperationException("Channel is disabled.");
        if (envelope.ChannelType == ChannelProviderKinds.Slack)
        {
            var slack = channel.Slack ?? throw new InvalidOperationException("Slack channel settings missing.");
            var user = envelope.Metadata.GetValueOrDefault(SlackChannelMetadata.UserId);
            var team = envelope.Metadata.GetValueOrDefault(SlackChannelMetadata.TeamId);
            var direct = envelope.Metadata.GetValueOrDefault(SlackChannelMetadata.IsDirectMessage) == "true";
            var thread = envelope.Metadata.GetValueOrDefault(SlackChannelMetadata.ThreadTs);
            var bot = envelope.Metadata.GetValueOrDefault(SlackChannelMetadata.IsBot) == "true"
                || (user is not null && slack.AllowedBotUserIds.Contains(user, StringComparer.Ordinal));
            var expectedRoute = $"{team}/{envelope.ExternalConversationId}/{(direct ? "dm" : thread)}";
            if (team != slack.TeamId || user is null
                || (bot ? direct || !slack.AllowedBotUserIds.Contains(user, StringComparer.Ordinal) : !slack.AllowedUserIds.Contains(user, StringComparer.Ordinal))
                || envelope.UserExternalId != SlackChannelMetadata.Principal(team, user)
                || (!direct && !slack.AllowedChannelIds.Contains(envelope.ExternalConversationId!, StringComparer.Ordinal))
                || (direct && !envelope.ExternalConversationId!.StartsWith("D", StringComparison.Ordinal))
                || (!direct && string.IsNullOrWhiteSpace(thread))
                || envelope.Metadata.GetValueOrDefault(SlackChannelMetadata.ConversationKey) != expectedRoute)
                throw new InvalidOperationException("Slack ingress admission or routing mismatch.");
        }
        else if (channel.Feishu is not { } settings
            || string.IsNullOrWhiteSpace(settings.AppId)
            || string.IsNullOrWhiteSpace(settings.AppSecret))
        {
            throw new InvalidOperationException(
                $"Channel '{channel.ChannelId}' has no enabled Feishu configuration.");
        }
        if (!string.Equals(
                manifest.WorkspaceId,
                envelope.WorkspaceId,
                StringComparison.Ordinal)
            || !string.Equals(
                channel.WorkspaceId,
                envelope.WorkspaceId,
                StringComparison.Ordinal)
            || !string.Equals(
                manifest.AgentInstanceId,
                envelope.AgentId,
                StringComparison.Ordinal)
            || !string.Equals(
                channel.ChannelId,
                envelope.ChannelId,
                StringComparison.Ordinal)
            || !manifest.ChannelIds.Contains(
                channel.ChannelId,
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Connector channel does not match the Agent channel reference.");
        }

        var expectedConnectorId = envelope.ChannelType == ChannelProviderKinds.Slack
            ? SlackChannelMetadata.ConnectorId(channel.ChannelId)
            : FeishuConnectorIdentity.ForChannel(channel.ChannelId);
        if (!string.Equals(
                expectedConnectorId,
                envelope.ConnectorId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Connector identity does not match the channel-owned binding.");
        }
    }

    private static string StableId(params string[] parts)
    {
        var raw = string.Join('\n', parts);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
            .ToLowerInvariant()[..32];
    }
}
