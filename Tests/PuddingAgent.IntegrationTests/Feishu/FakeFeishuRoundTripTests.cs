using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingAgent.Services;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingController.Services;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.MessageFabric;
using PuddingPlatform.Services.MessageGateway;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Messaging;

namespace PuddingAgent.IntegrationTests.Feishu;

[TestClass]
public sealed class FakeFeishuRoundTripTests
{
    [TestMethod]
    public async Task FakeFeishuMessage_RunsDurableIngressAndReplyDeliveryChain()
    {
        const string workspaceId = "default";
        const string agentId = "fake-feishu-agent";
        const string conversationId = "fake-agent-main";
        const string connectorId = "feishu:fake-feishu-agent";
        const string externalMessageId = "om_fake_message";
        const string externalChatId = "oc_fake_chat";
        const string inboundText = "fake feishu says hello";
        const string replyText = "fake agent replies hello";

        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            "pudding-fake-feishu-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);

        try
        {
            var paths = PuddingDataPaths.FromRoot(dataRoot);
            await WriteManifestAsync(
                paths,
                workspaceId,
                agentId,
                conversationId);

            var sessions = new InMemorySessionRepository(paths);
            await sessions.CreateAsync(new SessionRecord
            {
                SessionId = conversationId,
                WorkspaceId = workspaceId,
                AgentTemplateId = "general-assistant",
                AgentInstanceId = agentId,
                ChannelId = "admin",
                OwnerUserId = "admin",
                SessionType = SessionType.ServiceSession,
                SessionRole = SessionRole.Main,
                PrincipalKind = "agent",
                PrincipalId = agentId,
                Status = SessionStatus.Active,
            });

            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var eventBus = new SynchronousInternalEventBus();
            var submitHandler = new RecordingSubmitTurnHandler();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<PlatformDbContext>(
                options => options.UseSqlite(connection));
            services.AddSingleton<IInternalEventBus>(eventBus);
            services.AddSingleton<ISubmitTurnHandler>(submitHandler);
            services.AddSingleton<IRuntimeAgentDispatcher, UnusedRuntimeAgentDispatcher>();
            services.AddSingleton<IWorkspaceAgentCatalog>(
                new FakeWorkspaceAgentCatalog(agentId, conversationId));
            services.AddScoped<IMessageRouter, MessageRouter>();
            services.AddScoped<MessageFabricStore>();
            services.AddScoped<IMessageInbox>(
                provider => provider.GetRequiredService<MessageFabricStore>());
            services.AddScoped<WorkspaceRoomParticipantProvider>();
            services.AddScoped<IMessageSystem, MessageSystem>();
            await using var provider = services.BuildServiceProvider();

            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                await db.Database.EnsureCreatedAsync();
            }

            var gateway = new MessageGatewayIngress(
                new AgentManifestCatalog(paths),
                sessions,
                new UnexpectedMainSessionBinder(),
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<MessageGatewayIngress>.Instance);
            var connectorHost = new ConnectorHost(
                async (envelope, ct) =>
                {
                    await gateway.AcceptAsync(envelope, ct);
                },
                NullLogger<ConnectorHost>.Instance);
            var fakeFeishu = new FakeFeishuConnector(
                connectorId,
                workspaceId,
                agentId);
            connectorHost.Register(fakeFeishu);
            await connectorHost.StartAsync(connectorId);

            var agentDispatcher = new MessageDeliveryDispatcher(
                eventBus,
                provider.GetRequiredService<IServiceScopeFactory>(),
                new AgentWakeQueue(NullLogger<AgentWakeQueue>.Instance),
                NullLogger<MessageDeliveryDispatcher>.Instance);
            var connectorDispatcher = new ConnectorDeliveryDispatcher(
                eventBus,
                provider.GetRequiredService<IServiceScopeFactory>(),
                connectorHost,
                NullLogger<ConnectorDeliveryDispatcher>.Instance);
            using var agentSubscription = await eventBus.SubscribeAsync(
                "message.deliver",
                evt => agentDispatcher.HandleAsync(evt));
            using var connectorSubscription = await eventBus.SubscribeAsync(
                "message.deliver",
                evt => connectorDispatcher.HandleAsync(evt));

            await fakeFeishu.EmitTextAsync(
                externalMessageId,
                externalChatId,
                "ou_fake_sender",
                inboundText);

            Assert.HasCount(1, submitHandler.Commands);
            var accepted = submitHandler.Commands.Single();
            Assert.IsTrue(accepted.IsTrustedGatewayIngress);
            Assert.AreEqual(conversationId, accepted.ConversationId);
            Assert.AreEqual(inboundText, accepted.Content.Single().Text);
            Assert.AreEqual(
                "feishu",
                accepted.Metadata![MessageGatewayMetadata.ChannelType]);
            Assert.AreEqual(
                externalMessageId,
                accepted.Metadata[MessageGatewayMetadata.ExternalMessageId]);

            await CommitFakeAgentReplyAsync(
                provider,
                accepted,
                agentId,
                replyText);
            var replyProjector = new ConversationReplyProjectionWorker(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ConversationReplyProjectionWorker>.Instance);

            Assert.AreEqual(1, await replyProjector.ProjectBatchAsync());
            Assert.AreEqual(0, await replyProjector.ProjectBatchAsync());
            Assert.HasCount(1, fakeFeishu.SentMessages);

            var sent = fakeFeishu.SentMessages.Single();
            Assert.AreEqual(externalChatId, sent.Target);
            Assert.AreEqual(replyText, sent.Content);
            Assert.AreEqual(externalMessageId, sent.Metadata["message_id"]);
            Assert.AreEqual(externalChatId, sent.Metadata["chat_id"]);
            Assert.IsFalse(string.IsNullOrWhiteSpace(sent.Metadata["uuid"]));

            await using var verifyScope = provider.CreateAsyncScope();
            var verifyDb =
                verifyScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var deliveries = await verifyDb.MessageDeliveries
                .AsNoTracking()
                .OrderBy(delivery => delivery.Id)
                .ToListAsync();
            Assert.HasCount(2, deliveries);
            Assert.IsTrue(deliveries.All(delivery =>
                delivery.Status == MessageDeliveryStatuses.Delivered));
            Assert.IsTrue(deliveries.Any(delivery =>
                delivery.TargetKind == MessageEndpointKinds.Agent));
            Assert.IsTrue(deliveries.Any(delivery =>
                delivery.TargetKind == MessageEndpointKinds.Connector));

            var transcript = await verifyDb.RoomMessages
                .AsNoTracking()
                .OrderBy(message => message.Id)
                .Select(message => message.Content)
                .ToListAsync();
            CollectionAssert.AreEqual(
                new[] { inboundText, replyText },
                transcript.ToArray());

            await connectorHost.StopAllAsync();
        }
        finally
        {
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }

    private static async Task WriteManifestAsync(
        PuddingDataPaths paths,
        string workspaceId,
        string agentId,
        string conversationId)
    {
        var root = paths.AgentInstanceRoot(agentId);
        Directory.CreateDirectory(root);
        var manifest = new AgentInstanceManifest
        {
            AgentInstanceId = agentId,
            TemplateId = "general-assistant",
            WorkspaceId = workspaceId,
            DisplayName = "Fake Feishu Agent",
            MainSessionId = conversationId,
            IsEnabled = true,
            Feishu = new AgentFeishuBotConfig
            {
                Enabled = true,
                AppId = "cli_fake",
                AppSecret = "fake-secret",
            },
        };
        await File.WriteAllTextAsync(
            Path.Combine(root, "manifest.json"),
            JsonSerializer.Serialize(
                manifest,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static async Task CommitFakeAgentReplyAsync(
        IServiceProvider provider,
        SubmitTurnCommand accepted,
        string agentId,
        string reply)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var now = DateTimeOffset.UtcNow;
        var metadataJson = JsonSerializer.Serialize(accepted.Metadata);
        db.ChatExecutionCommands.Add(new ChatExecutionCommandEntity
        {
            CommandId = "fake-command",
            BatchId = "fake-batch",
            ClientRequestId = accepted.ClientRequestId,
            WorkspaceId = accepted.WorkspaceId,
            SessionId = accepted.ConversationId,
            MessageId = "fake-assistant-message",
            UserMessageId = accepted.ClientMessageId,
            TurnId = "fake-turn",
            AgentInstanceId = agentId,
            UserId = accepted.UserId,
            ChannelId = "feishu",
            RunId = "fake-run",
            TerminalSequence = 2,
            Status = "succeeded",
            CreatedAt = now.AddSeconds(-1).ToUnixTimeMilliseconds(),
            CompletedAt = now.ToUnixTimeMilliseconds(),
            MetadataJson = metadataJson,
        });
        db.ConversationEvents.Add(new ConversationEventEntity
        {
            ConversationId = accepted.ConversationId,
            Sequence = 2,
            EventId = "fake-terminal-event",
            WorkspaceId = accepted.WorkspaceId,
            TurnId = "fake-turn",
            CommandId = "fake-command",
            RunId = "fake-run",
            MessageId = "fake-assistant-message",
            Type = ConversationEventTypes.TurnCompleted,
            Payload = JsonSerializer.Serialize(new
            {
                kind = "Completed",
                reply,
            }),
            OccurredAt = now.ToString("O"),
            CommittedAt = now.ToString("O"),
        });
        await db.SaveChangesAsync();
    }

    private sealed class FakeFeishuConnector(
        string connectorId,
        string workspaceId,
        string agentId) : IPuddingConnector
    {
        private ConnectorContext? _context;

        public ConnectorDescriptor Descriptor { get; } = new()
        {
            ConnectorId = connectorId,
            ConnectorType = "feishu",
            Protocol = "fake-feishu",
            Capabilities = ["receive", "send"],
        };

        public List<ConnectorMessage> SentMessages { get; } = [];

        public Task StartAsync(
            ConnectorContext context,
            CancellationToken ct = default)
        {
            _context = context;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct = default)
        {
            _context = null;
            return Task.CompletedTask;
        }

        public Task SendAsync(
            ConnectorMessage message,
            CancellationToken ct = default)
        {
            SentMessages.Add(message);
            return Task.CompletedTask;
        }

        public Task EmitTextAsync(
            string messageId,
            string chatId,
            string senderId,
            string text,
            CancellationToken ct = default)
        {
            var context = _context
                ?? throw new InvalidOperationException("Fake Feishu is not started.");
            return context.OnEventReceived(
                new PuddingIngressEnvelope
                {
                    ConnectorId = connectorId,
                    WorkspaceId = workspaceId,
                    AgentId = agentId,
                    ChannelId = "feishu",
                    ChannelType = "feishu",
                    UserExternalId = senderId,
                    MessageText = text,
                    MessageType = "chat",
                    ExternalConversationId = chatId,
                    ExternalMessageId = messageId,
                    CorrelationId = chatId,
                    Metadata = new Dictionary<string, string>
                    {
                        ["source"] = "fake-feishu",
                    },
                },
                ct);
        }

        public Task<ConnectorOperationResult> OperateAsync(
            string operation,
            Dictionary<string, string>? parameters = null,
            CancellationToken ct = default)
            => Task.FromResult(new ConnectorOperationResult
            {
                Success = false,
                Error = "Not supported by fake Feishu.",
            });

        public Task<ConnectorDiagnostics> GetDiagnosticsAsync(
            CancellationToken ct = default)
            => Task.FromResult(new ConnectorDiagnostics
            {
                Status = _context is null ? "stopped" : "connected",
                MessagesSent = SentMessages.Count,
            });
    }

    private sealed class RecordingSubmitTurnHandler : ISubmitTurnHandler
    {
        public List<SubmitTurnCommand> Commands { get; } = [];

        public Task<AcceptanceResult> HandleAsync(
            SubmitTurnCommand command,
            CancellationToken ct)
        {
            Commands.Add(command);
            return Task.FromResult(new AcceptanceResult
            {
                ConversationId = command.ConversationId,
                MessageId = command.ClientMessageId,
                TurnIds = ["fake-turn"],
                CommandIds = ["fake-command"],
                AcceptedSequence = 1,
            });
        }
    }

    private sealed class FakeWorkspaceAgentCatalog(
        string agentId,
        string mainSessionId) : IWorkspaceAgentCatalog
    {
        public Task<IReadOnlyList<WorkspaceAgentDto>> ListAgentsAsync(
            string workspaceId,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkspaceAgentDto>>
            ([
                new WorkspaceAgentDto(
                    AgentId: agentId,
                    Name: agentId,
                    Description: null,
                    DisplayName: "Fake Feishu Agent",
                    AvatarId: null,
                    AvatarUrl: null,
                    SourceTemplateId: "general-assistant",
                    MainSessionId: mainSessionId,
                    SystemPromptOverride: null,
                    PreferredProviderId: null,
                    PreferredModelId: null,
                    IsEnabled: true,
                    IsFrozen: false,
                    CreatedAt: DateTimeOffset.UtcNow,
                    UpdatedAt: DateTimeOffset.UtcNow),
            ]);
    }

    private sealed class UnexpectedMainSessionBinder : IAgentMainSessionBinder
    {
        public Task<WorkspaceAgentDto> SetAgentMainSessionAsync(
            string workspaceId,
            string agentId,
            string mainSessionId,
            CancellationToken ct = default)
            => throw new AssertFailedException(
                "The preconfigured main session should have been reused.");
    }

    private sealed class UnusedRuntimeAgentDispatcher : IRuntimeAgentDispatcher
    {
        public Task<RuntimeDispatchResult> DispatchAsync(
            RuntimeDispatchRequest request,
            CancellationToken ct = default)
            => throw new AssertFailedException(
                "Gateway ingress must use canonical SubmitTurn, not direct Runtime dispatch.");

        public IAsyncEnumerable<ServerSentEventFrame> DispatchStreamAsync(
            RuntimeDispatchRequest request,
            CancellationToken ct = default)
            => throw new AssertFailedException(
                "Gateway ingress must use canonical SubmitTurn, not direct Runtime dispatch.");
    }

    private sealed class SynchronousInternalEventBus : IInternalEventBus
    {
        private readonly object _gate = new();
        private readonly List<Subscription> _subscriptions = [];

        public async Task PublishAsync(
            InternalEvent evt,
            CancellationToken ct = default)
        {
            List<Subscription> matches;
            lock (_gate)
            {
                matches = _subscriptions
                    .Where(item => item.IsActive)
                    .Where(item => string.Equals(
                        item.EventTypePattern,
                        evt.Type,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            foreach (var match in matches)
            {
                ct.ThrowIfCancellationRequested();
                await match.Handler(evt);
            }
        }

        public Task<IEventSubscriptionHandle> SubscribeAsync(
            string eventTypePattern,
            Func<InternalEvent, Task> handler,
            CancellationToken ct = default)
        {
            var subscription = new Subscription(
                eventTypePattern,
                handler,
                Remove);
            lock (_gate)
                _subscriptions.Add(subscription);
            return Task.FromResult<IEventSubscriptionHandle>(subscription);
        }

        public Task UnsubscribeAsync(IEventSubscriptionHandle handle)
        {
            handle.Dispose();
            return Task.CompletedTask;
        }

        private void Remove(Subscription subscription)
        {
            lock (_gate)
                _subscriptions.Remove(subscription);
        }

        private sealed class Subscription(
            string eventTypePattern,
            Func<InternalEvent, Task> handler,
            Action<Subscription> onDispose) : IEventSubscriptionHandle
        {
            public string SubscriptionId { get; } = Guid.NewGuid().ToString("N");
            public string EventTypePattern { get; } = eventTypePattern;
            public bool IsActive { get; private set; } = true;
            public Func<InternalEvent, Task> Handler { get; } = handler;

            public void Dispose()
            {
                if (!IsActive)
                    return;
                IsActive = false;
                onDispose(this);
            }
        }
    }
}
