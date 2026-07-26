using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingController.Services;
using PuddingPlatform.Services;

namespace PuddingAgent.Services;

/// <summary>
/// V1 Connector ingress: validates the Agent-owned Feishu binding, resolves the
/// Agent main Conversation and durably submits one Message Fabric delivery.
/// The Agent dispatcher then accepts that delivery through ADR-059.
/// </summary>
public sealed class MessageGatewayIngress(
    AgentManifestCatalog manifests,
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
        ValidateBinding(envelope, manifest);

        var conversationId = await EnsureMainConversationAsync(manifest, ct);
        var externalMessageId = envelope.ExternalMessageId ?? envelope.EnvelopeId;
        var messageId = StableId(
            "gateway-message",
            envelope.ConnectorId!,
            externalMessageId);
        var clientRequestId = StableId(
            "gateway-request",
            envelope.ConnectorId!,
            externalMessageId);

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
            [MessageGatewayMetadata.ClientRequestId] = clientRequestId,
        };

        using var scope = scopeFactory.CreateScope();
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

        if (!string.Equals(envelope.ChannelType, "feishu", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                $"V1 Message Gateway only accepts Feishu chat ingress, not '{envelope.ChannelType}'.");
    }

    private static void ValidateBinding(
        PuddingIngressEnvelope envelope,
        AgentInstanceManifest manifest)
    {
        if (!manifest.IsEnabled || manifest.IsFrozen)
            throw new InvalidOperationException(
                $"Bound Agent '{manifest.AgentInstanceId}' is disabled or frozen.");
        if (manifest.Feishu is not { Enabled: true }
            || string.IsNullOrWhiteSpace(manifest.Feishu.AppId)
            || string.IsNullOrWhiteSpace(manifest.Feishu.AppSecret))
        {
            throw new InvalidOperationException(
                $"Agent '{manifest.AgentInstanceId}' has no enabled Feishu binding.");
        }
        if (!string.Equals(
                manifest.WorkspaceId,
                envelope.WorkspaceId,
                StringComparison.Ordinal)
            || !string.Equals(
                manifest.AgentInstanceId,
                envelope.AgentId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Connector binding does not match the Agent manifest.");
        }

        var expectedConnectorId = FeishuConnectorIdentity.ForAgent(
            manifest.AgentInstanceId);
        if (!string.Equals(
                expectedConnectorId,
                envelope.ConnectorId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Connector identity does not match the Agent-owned Feishu binding.");
        }
    }

    private static string StableId(params string[] parts)
    {
        var raw = string.Join('\n', parts);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
            .ToLowerInvariant()[..32];
    }
}
