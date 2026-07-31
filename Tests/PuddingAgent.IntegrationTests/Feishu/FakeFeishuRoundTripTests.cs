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
            services.AddSingleton(
                new VisionArtifactStorageService(
                    paths,
                    NullLogger<VisionArtifactStorageService>.Instance));
            services.AddDbContext<PlatformDbContext>(
                options => options.UseSqlite(connection));
            services.AddSingleton<IInternalEventBus>(eventBus);
            services.AddSingleton<IImageGenerationService, UnusedImageGenerationService>();
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
                CreateChannelService(paths, agentId, conversationId),
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
            await fakeFeishu.EmitImageAsync(
                "om_fake_image",
                externalChatId,
                "ou_fake_sender",
                "vision-0123456789abcdef0123456789abcdef");

            Assert.HasCount(2, submitHandler.Commands);
            var accepted = submitHandler.Commands[0];
            Assert.IsTrue(accepted.IsTrustedGatewayIngress);
            Assert.AreEqual(conversationId, accepted.ConversationId);
            Assert.AreEqual(inboundText, accepted.Content.Single().Text);
            Assert.AreEqual(
                "feishu",
                accepted.Metadata![MessageGatewayMetadata.ChannelType]);
            Assert.AreEqual(
                externalMessageId,
                accepted.Metadata[MessageGatewayMetadata.ExternalMessageId]);
            var acceptedImage = submitHandler.Commands[1];
            Assert.AreEqual(
                "用户从飞书发送了一张图片。",
                acceptedImage.Content.Single().Text);
            Assert.AreEqual(
                "image",
                acceptedImage.Metadata![MessageGatewayMetadata.MessageType]);
            Assert.AreEqual(
                "vision-0123456789abcdef0123456789abcdef",
                acceptedImage.Metadata["visionArtifactId"]);

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
            Assert.HasCount(3, deliveries);
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
                new[] { inboundText, "用户从飞书发送了一张图片。", replyText },
                transcript.ToArray());
            var imageMetadataJson = await verifyDb.RoomMessages
                .AsNoTracking()
                .Where(message => message.Content == "用户从飞书发送了一张图片。")
                .Select(message => message.MetadataJson)
                .SingleAsync();
            Assert.IsNotNull(imageMetadataJson);
            using var imageMetadata = JsonDocument.Parse(imageMetadataJson!);
            Assert.AreEqual(
                "vision-0123456789abcdef0123456789abcdef",
                imageMetadata.RootElement
                    .GetProperty("visionArtifactIds")
                    .GetString());

            await connectorHost.StopAllAsync();
        }
        finally
        {
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task FakeFeishuStreamingCard_ProjectsDeltasAndFinalizesThroughDurableDelivery()
    {
        const string workspaceId = "default";
        const string agentId = "fake-stream-agent";
        const string conversationId = "fake-stream-main";
        const string connectorId = "feishu:fake-stream-agent";
        const string externalMessageId = "om_fake_stream_source";
        const string externalChatId = "oc_fake_stream_chat";
        const string replyText =
            "第一段，第二段，完成。\n\n```voice\n这是追加的语音内容。\n```";
        const string projectedVoice = "这是追加的语音内容。";

        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            "pudding-fake-feishu-stream-tests",
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

            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var eventBus = new SynchronousInternalEventBus();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(
                new VisionArtifactStorageService(
                    paths,
                    NullLogger<VisionArtifactStorageService>.Instance));
            services.AddDbContext<PlatformDbContext>(
                options => options.UseSqlite(connection));
            services.AddSingleton<IInternalEventBus>(eventBus);
            services.AddSingleton<IImageGenerationService, UnusedImageGenerationService>();
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
                await SeedStreamingCommandAsync(
                    db,
                    workspaceId,
                    agentId,
                    conversationId,
                    connectorId,
                    externalMessageId,
                    externalChatId);
            }

            var connectorHost = new ConnectorHost(
                (_, _) => Task.CompletedTask,
                NullLogger<ConnectorHost>.Instance);
            var fakeFeishu = new FakeFeishuConnector(
                connectorId,
                workspaceId,
                agentId);
            connectorHost.Register(fakeFeishu);
            await connectorHost.StartAsync(connectorId);

            var connectorDispatcher = new ConnectorDeliveryDispatcher(
                eventBus,
                provider.GetRequiredService<IServiceScopeFactory>(),
                connectorHost,
                NullLogger<ConnectorDeliveryDispatcher>.Instance);
            using var connectorSubscription = await eventBus.SubscribeAsync(
                "message.deliver",
                evt => connectorDispatcher.HandleAsync(evt));
            var streamWorker = new FeishuStreamingProjectionWorker(
                provider.GetRequiredService<IServiceScopeFactory>(),
                connectorHost,
                CreateChannelService(paths, agentId, conversationId),
                NullLogger<FeishuStreamingProjectionWorker>.Instance);

            Assert.AreEqual(1, await streamWorker.ProjectBatchAsync());
            CollectionAssert.AreEqual(
                new[]
                {
                    ConnectorStreamOperations.Create,
                    ConnectorStreamOperations.Publish,
                    ConnectorStreamOperations.Update,
                },
                fakeFeishu.Operations.Select(item => item.Operation).ToArray());
            var update = fakeFeishu.Operations.Single(item =>
                item.Operation == ConnectorStreamOperations.Update);
            Assert.AreEqual(
                "第一段，第二段，",
                update.Parameters[ConnectorStreamParameters.Content]);
            Assert.AreEqual(
                "1",
                update.Parameters[ConnectorStreamParameters.Sequence]);

            await AppendStreamingDeltaAsync(
                provider,
                conversationId,
                "完成。\n\n```voice\n这是追加的语音内容。\n```");
            Assert.AreEqual(1, await streamWorker.ProjectBatchAsync());
            var voiceAwareUpdate = fakeFeishu.Operations
                .Where(item => item.Operation == ConnectorStreamOperations.Update)
                .Single(item =>
                    item.Parameters[ConnectorStreamParameters.Sequence] == "2");
            Assert.AreEqual(
                replyText,
                voiceAwareUpdate.Parameters[ConnectorStreamParameters.Content]);
            Assert.IsTrue(
                voiceAwareUpdate.Parameters[ConnectorStreamParameters.Content]
                    .Contains("```voice", StringComparison.Ordinal));

            await CompleteStreamingCommandAsync(
                provider,
                conversationId,
                replyText);
            var ordinaryProjector = new ConversationReplyProjectionWorker(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ConversationReplyProjectionWorker>.Instance);
            Assert.AreEqual(
                0,
                await ordinaryProjector.ProjectBatchAsync(),
                "An active card stream must suppress the duplicate terminal text reply.");

            Assert.AreEqual(1, await streamWorker.ProjectBatchAsync());
            Assert.HasCount(2, fakeFeishu.SentMessages);
            var final = fakeFeishu.SentMessages.Single(message =>
                !message.Metadata.ContainsKey(ConnectorPayloadMetadata.Kind));
            Assert.AreEqual(replyText, final.Content);
            Assert.AreEqual(
                ConnectorStreamMetadata.FinalizeReplyMode,
                final.Metadata[ConnectorStreamMetadata.ReplyMode]);
            Assert.AreEqual(
                "3",
                final.Metadata[ConnectorStreamMetadata.ContentSequence]);
            Assert.AreEqual(
                "4",
                final.Metadata[ConnectorStreamMetadata.FinishSequence]);
            Assert.AreEqual(externalMessageId, final.Metadata["message_id"]);
            var audio = fakeFeishu.SentMessages.Single(message =>
                message.Metadata.TryGetValue(
                    ConnectorPayloadMetadata.Kind,
                    out var kind)
                && kind == ConnectorPayloadKinds.TtsAudio);
            Assert.AreEqual(projectedVoice, audio.Content);
            Assert.AreEqual("Cherry", audio.Metadata[MessageGatewayMetadata.TtsVoice]);
            Assert.IsFalse(
                audio.Metadata.ContainsKey(ConnectorStreamMetadata.ReplyMode));
            Assert.AreNotEqual(final.Metadata["uuid"], audio.Metadata["uuid"]);
            Assert.AreEqual(0, await ordinaryProjector.ProjectBatchAsync());

            await using var verifyScope = provider.CreateAsyncScope();
            var verifyDb =
                verifyScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var projection = await verifyDb.ConnectorStreamProjections
                .AsNoTracking()
                .SingleAsync();
            Assert.AreEqual(
                ConnectorStreamProjectionStatuses.Completed,
                projection.Status);
            Assert.AreEqual(replyText, projection.Content);
            Assert.IsNull(projection.PendingEventSequence);
            Assert.IsTrue(await verifyDb.MessageDeliveries.AnyAsync(delivery =>
                delivery.TargetKind == MessageEndpointKinds.Connector
                && delivery.Status == MessageDeliveryStatuses.Delivered));

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
            ChannelIds = [agentId],
        };
        var channel = new ChannelInstanceManifest
        {
            ChannelId = agentId,
            WorkspaceId = workspaceId,
            ProviderId = ChannelProviderKinds.Feishu,
            Name = "Fake Feishu Channel",
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Feishu = new FeishuChannelSettings
            {
                AppId = "cli_fake",
                AppSecret = "fake-secret",
            },
        };
        await File.WriteAllTextAsync(
            Path.Combine(root, "manifest.json"),
            JsonSerializer.Serialize(
                manifest,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Directory.CreateDirectory(paths.ChannelRoot(agentId));
        await File.WriteAllTextAsync(
            paths.ChannelManifestFile(agentId),
            JsonSerializer.Serialize(
                channel,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static ChannelConfigurationFileService CreateChannelService(
        PuddingDataPaths paths,
        string agentId,
        string conversationId) => new(
        paths,
        new FakeWorkspaceAgentCatalog(agentId, conversationId),
        new UnexpectedAgentChannelBinder(),
        NullLogger<ChannelConfigurationFileService>.Instance);

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
            ChannelId = agentId,
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

    private static async Task SeedStreamingCommandAsync(
        PlatformDbContext db,
        string workspaceId,
        string agentId,
        string conversationId,
        string connectorId,
        string externalMessageId,
        string externalChatId)
    {
        var now = DateTimeOffset.UtcNow;
        db.ChatExecutionCommands.Add(new ChatExecutionCommandEntity
        {
            CommandId = "fake-stream-command",
            BatchId = "fake-stream-batch",
            WorkspaceId = workspaceId,
            SessionId = conversationId,
            MessageId = "fake-stream-assistant-message",
            UserMessageId = "fake-stream-user-message",
            TurnId = "fake-stream-turn",
            AgentInstanceId = agentId,
            UserId = "ou_fake_sender",
            ChannelId = "feishu",
            RunId = "fake-stream-run",
            Status = "running",
            CreatedAt = now.ToUnixTimeMilliseconds(),
            StartedAt = now.ToUnixTimeMilliseconds(),
            MetadataJson = JsonSerializer.Serialize(
                new Dictionary<string, string>
                {
                    [MessageGatewayMetadata.IsGatewayIngress] = "true",
                    [MessageGatewayMetadata.ChannelId] = agentId,
                    [MessageGatewayMetadata.ChannelType] = "feishu",
                    [MessageGatewayMetadata.ConnectorId] = connectorId,
                    [MessageGatewayMetadata.ExternalConversationId] = externalChatId,
                    [MessageGatewayMetadata.ExternalMessageId] = externalMessageId,
                    [MessageGatewayMetadata.ExternalUserId] = "ou_fake_sender",
                    [MessageGatewayMetadata.TtsRepliesEnabled] = "true",
                    [MessageGatewayMetadata.TtsVoice] = "Cherry",
                }),
        });
        db.ConversationEvents.Add(new ConversationEventEntity
        {
            ConversationId = conversationId,
            Sequence = 1,
            EventId = "fake-stream-delta",
            WorkspaceId = workspaceId,
            TurnId = "fake-stream-turn",
            CommandId = "fake-stream-command",
            RunId = "fake-stream-run",
            MessageId = "fake-stream-assistant-message",
            Type = ConversationEventTypes.MessageContentAppended,
            Payload = JsonSerializer.Serialize(new
            {
                delta = "第一段，第二段，",
            }),
            OccurredAt = now.ToString("O"),
            CommittedAt = now.ToString("O"),
        });
        await db.SaveChangesAsync();
    }

    private static async Task CompleteStreamingCommandAsync(
        IServiceProvider provider,
        string conversationId,
        string reply)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var command = await db.ChatExecutionCommands.SingleAsync();
        var now = DateTimeOffset.UtcNow;
        var terminalSequence = await db.ConversationEvents
            .Where(evt => evt.ConversationId == conversationId)
            .MaxAsync(evt => evt.Sequence) + 1;
        command.Status = "succeeded";
        command.TerminalSequence = terminalSequence;
        command.CompletedAt = now.ToUnixTimeMilliseconds();
        db.ConversationEvents.Add(new ConversationEventEntity
        {
            ConversationId = conversationId,
            Sequence = terminalSequence,
            EventId = "fake-stream-terminal",
            WorkspaceId = command.WorkspaceId,
            TurnId = command.TurnId,
            CommandId = command.CommandId,
            RunId = command.RunId,
            MessageId = command.MessageId,
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

    private static async Task AppendStreamingDeltaAsync(
        IServiceProvider provider,
        string conversationId,
        string delta)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var command = await db.ChatExecutionCommands.SingleAsync();
        var now = DateTimeOffset.UtcNow;
        var sequence = await db.ConversationEvents
            .Where(evt => evt.ConversationId == conversationId)
            .MaxAsync(evt => evt.Sequence) + 1;
        db.ConversationEvents.Add(new ConversationEventEntity
        {
            ConversationId = conversationId,
            Sequence = sequence,
            EventId = $"fake-stream-delta-{sequence}",
            WorkspaceId = command.WorkspaceId,
            TurnId = command.TurnId,
            CommandId = command.CommandId,
            RunId = command.RunId,
            MessageId = command.MessageId,
            Type = ConversationEventTypes.MessageContentAppended,
            Payload = JsonSerializer.Serialize(new { delta }),
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
            Capabilities = ["receive", "send", "stream", "audio"],
        };

        public List<ConnectorMessage> SentMessages { get; } = [];
        public List<ConnectorOperationCall> Operations { get; } = [];

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
                    ChannelId = agentId,
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

        public Task EmitImageAsync(
            string messageId,
            string chatId,
            string senderId,
            string artifactId,
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
                    ChannelId = agentId,
                    ChannelType = "feishu",
                    UserExternalId = senderId,
                    MessageText = "用户从飞书发送了一张图片。",
                    MessageType = "image",
                    ExternalConversationId = chatId,
                    ExternalMessageId = messageId,
                    CorrelationId = chatId,
                    Metadata = new Dictionary<string, string>
                    {
                        ["source"] = "fake-feishu",
                        ["inputMode"] = "image",
                        ["visionArtifactId"] = artifactId,
                        ["visionArtifactIds"] = artifactId,
                    },
                },
                ct);
        }

        public Task<ConnectorOperationResult> OperateAsync(
            string operation,
            Dictionary<string, string>? parameters = null,
            CancellationToken ct = default)
        {
            var copied = new Dictionary<string, string>(
                parameters ?? [],
                StringComparer.Ordinal);
            Operations.Add(new ConnectorOperationCall(operation, copied));
            return Task.FromResult(operation switch
            {
                ConnectorStreamOperations.Create => new ConnectorOperationResult
                {
                    Success = true,
                    Data = "card_fake_stream",
                },
                ConnectorStreamOperations.Publish => new ConnectorOperationResult
                {
                    Success = true,
                    Data = "om_fake_stream_reply",
                },
                ConnectorStreamOperations.Update
                    or ConnectorStreamOperations.Finish => new ConnectorOperationResult
                    {
                        Success = true,
                    },
                _ => new ConnectorOperationResult
                {
                    Success = false,
                    Error = "Not supported by fake Feishu.",
                },
            });
        }

        public Task<ConnectorDiagnostics> GetDiagnosticsAsync(
            CancellationToken ct = default)
            => Task.FromResult(new ConnectorDiagnostics
            {
                Status = _context is null ? "stopped" : "connected",
                MessagesSent = SentMessages.Count,
            });
    }

    private sealed record ConnectorOperationCall(
        string Operation,
        Dictionary<string, string> Parameters);

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
                    UpdatedAt: DateTimeOffset.UtcNow,
                    ChannelIds: [agentId]),
            ]);
    }

    private sealed class UnexpectedAgentChannelBinder : IAgentChannelBinder
    {
        public Task SetChannelBindingAsync(
            string workspaceId,
            string channelId,
            string? agentId,
            CancellationToken ct = default)
            => throw new AssertFailedException(
                "Runtime channel reads must not mutate Agent bindings.");
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

    private sealed class UnusedImageGenerationService : IImageGenerationService
    {
        public Task<ImageGenerationResult> GenerateAsync(
            ImageGenerationRequest request,
            CancellationToken ct = default)
            => Task.FromException<ImageGenerationResult>(
                new InvalidOperationException(
                    "Image generation is not expected in this fake Feishu test."));
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
