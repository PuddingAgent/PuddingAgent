using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Controllers;

[TestClass]
public sealed class FeishuVoiceDebugControllerTests
{
    [TestMethod]
    public async Task Send_WithoutExplicitConfirmation_DoesNotQueueMessage()
    {
        await using var scope = await TestScope.CreateAsync();

        var result = await scope.Controller.Send(
            "default",
            scope.ChannelId,
            new FeishuVoiceDebugSendRequest
            {
                Text = "这条消息不应该被发送。",
                ConfirmSend = false,
            },
            CancellationToken.None);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        Assert.IsNull(scope.MessageSystem.LastEnvelope);
    }

    [TestMethod]
    public async Task Send_UsesLatestTrustedIngressRouteAndQueuesTypedAudio()
    {
        await using var scope = await TestScope.CreateAsync();

        var result = await scope.Controller.Send(
            "default",
            scope.ChannelId,
            new FeishuVoiceDebugSendRequest
            {
                Text = "飞书语音链路调试。",
                ConfirmSend = true,
                IdempotencyKey = "debug-request-1",
            },
            CancellationToken.None);

        var accepted = Assert.IsInstanceOfType<AcceptedAtActionResult>(result);
        var response = Assert.IsInstanceOfType<FeishuVoiceDebugSendResponse>(
            accepted.Value);
        Assert.AreEqual("command-debug", response.SourceCommandId);
        Assert.AreEqual("Cherry", response.TtsVoice);
        Assert.HasCount(1, response.DeliveryIds);

        var envelope = scope.MessageSystem.LastEnvelope;
        Assert.IsNotNull(envelope);
        Assert.AreEqual(MessageContentTypes.Audio, envelope.ContentType);
        Assert.AreEqual("飞书语音链路调试。", envelope.Content);
        Assert.AreEqual("conversation-debug", envelope.ConversationId);
        Assert.AreEqual("om_debug_message_1234", envelope.ReplyToMessageId);
        Assert.AreEqual(
            $"feishu:{scope.ChannelId}",
            envelope.To.Single().Id);
        Assert.AreEqual(
            ConnectorPayloadKinds.TtsAudio,
            envelope.Metadata[ConnectorPayloadMetadata.Kind]);
        Assert.AreEqual("true", envelope.Metadata["gateway_debug_voice"]);
        Assert.AreEqual(
            "debug-request-1",
            envelope.Metadata["gateway_debug_request_id"]);
    }

    [TestMethod]
    public async Task GetStatus_ReturnsOnlyDebugVoiceDeliveryState()
    {
        await using var scope = await TestScope.CreateAsync();
        var sendResult = await scope.Controller.Send(
            "default",
            scope.ChannelId,
            new FeishuVoiceDebugSendRequest
            {
                Text = "请返回投递状态。",
                ConfirmSend = true,
                IdempotencyKey = "debug-status-1",
            },
            CancellationToken.None);
        var accepted = Assert.IsInstanceOfType<AcceptedAtActionResult>(sendResult);
        var sendResponse = Assert.IsInstanceOfType<FeishuVoiceDebugSendResponse>(
            accepted.Value);
        var delivery = await scope.Db.MessageDeliveries.SingleAsync();
        delivery.Status = MessageDeliveryStatuses.Delivered;
        delivery.AttemptCount = 1;
        delivery.AckAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await scope.Db.SaveChangesAsync();

        var result = await scope.Controller.GetStatus(
            "default",
            sendResponse.MessageId,
            CancellationToken.None);

        var ok = Assert.IsInstanceOfType<OkObjectResult>(result);
        var response = Assert.IsInstanceOfType<FeishuVoiceDebugStatusResponse>(
            ok.Value);
        Assert.AreEqual(MessageDeliveryStatuses.Delivered, response.Status);
        Assert.AreEqual("debug-status-1", response.RequestId);
        Assert.HasCount(1, response.Deliveries);
        Assert.AreEqual(
            MessageDeliveryStatuses.Delivered,
            response.Deliveries.Single().Status);
    }

    private sealed class TestScope : IAsyncDisposable
    {
        private TestScope(
            string root,
            string channelId,
            SqliteConnection connection,
            PlatformDbContext db,
            RecordingMessageSystem messageSystem,
            FeishuVoiceDebugController controller)
        {
            Root = root;
            ChannelId = channelId;
            Connection = connection;
            Db = db;
            MessageSystem = messageSystem;
            Controller = controller;
        }

        public string Root { get; }
        public string ChannelId { get; }
        public SqliteConnection Connection { get; }
        public PlatformDbContext Db { get; }
        public RecordingMessageSystem MessageSystem { get; }
        public FeishuVoiceDebugController Controller { get; }

        public static async Task<TestScope> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "pudding-feishu-voice-debug-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            var channelService = new ChannelConfigurationFileService(
                PuddingDataPaths.FromRoot(root),
                new EmptyAgentCatalog(),
                new NoOpAgentChannelBinder(),
                NullLogger<ChannelConfigurationFileService>.Instance);
            var channel = await channelService.CreateWorkspaceChannelAsync(
                "default",
                new UpsertWorkspaceChannelRequest(
                    "飞书语音调试",
                    null,
                    ChannelProviderKinds.Feishu,
                    BoundAgentId: null,
                    AppId: "cli_voice_debug",
                    AppSecret: "test-secret",
                    StreamingRepliesEnabled: true,
                    PrivilegedUserOpenIds: [],
                    IsEnabled: true,
                    TtsRepliesEnabled: true,
                    TtsVoice: "Cherry"));

            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new PlatformDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var metadata = new Dictionary<string, string>
            {
                [MessageGatewayMetadata.IsGatewayIngress] = "true",
                [MessageGatewayMetadata.ChannelId] = channel.ChannelId,
                [MessageGatewayMetadata.ChannelType] = ChannelProviderKinds.Feishu,
                [MessageGatewayMetadata.ConnectorId] =
                    $"feishu:{channel.ChannelId}",
                [MessageGatewayMetadata.ExternalConversationId] =
                    "oc_debug_conversation_1234",
                [MessageGatewayMetadata.ExternalMessageId] =
                    "om_debug_message_1234",
            };
            db.ChatExecutionCommands.Add(new ChatExecutionCommandEntity
            {
                CommandId = "command-debug",
                BatchId = "batch-debug",
                WorkspaceId = "default",
                SessionId = "conversation-debug",
                MessageId = "message-debug",
                UserMessageId = "user-message-debug",
                TurnId = "turn-debug",
                AgentInstanceId = "agent-debug",
                Status = "succeeded",
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                MetadataJson = JsonSerializer.Serialize(metadata),
            });
            await db.SaveChangesAsync();

            var messageSystem = new RecordingMessageSystem(db);
            var controller = new FeishuVoiceDebugController(
                db,
                channelService,
                messageSystem,
                NullLogger<FeishuVoiceDebugController>.Instance);
            return new TestScope(
                root,
                channel.ChannelId,
                connection,
                db,
                messageSystem,
                controller);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class RecordingMessageSystem(PlatformDbContext db)
        : IMessageSystem
    {
        public MessageEnvelope? LastEnvelope { get; private set; }

        public async Task<MessageSendResult> SendAsync(
            MessageEnvelope envelope,
            CancellationToken ct = default)
        {
            LastEnvelope = envelope;
            var deliveryId = $"delivery-{envelope.MessageId}";
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            db.RoomMessages.Add(new RoomMessageEntity
            {
                MessageId = envelope.MessageId,
                WorkspaceId = envelope.From.WorkspaceId ?? "default",
                RoomId = envelope.RoomId ?? envelope.ConversationId ?? "debug",
                FromKind = envelope.From.Kind,
                FromId = envelope.From.Id,
                Audience = envelope.Audience,
                Visibility = envelope.Visibility,
                Content = envelope.Content,
                ConversationId = envelope.ConversationId,
                ReplyToMessageId = envelope.ReplyToMessageId,
                CorrelationId = envelope.CorrelationId,
                CausationId = envelope.CausationId,
                MetadataJson = JsonSerializer.Serialize(envelope.Metadata),
                CreatedAt = now,
            });
            db.MessageDeliveries.Add(new MessageDeliveryEntity
            {
                DeliveryId = deliveryId,
                MessageId = envelope.MessageId,
                WorkspaceId = envelope.From.WorkspaceId ?? "default",
                RoomId = envelope.RoomId,
                TargetKind = envelope.To.Single().Kind,
                TargetId = envelope.To.Single().Id,
                Status = MessageDeliveryStatuses.Queued,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync(ct);
            return new MessageSendResult
            {
                MessageId = envelope.MessageId,
                RoomId = envelope.RoomId,
                DeliveryIds = [deliveryId],
            };
        }
    }

    private sealed class EmptyAgentCatalog : IWorkspaceAgentCatalog
    {
        public Task<IReadOnlyList<WorkspaceAgentDto>> ListAgentsAsync(
            string workspaceId,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkspaceAgentDto>>([]);
    }

    private sealed class NoOpAgentChannelBinder : IAgentChannelBinder
    {
        public Task SetChannelBindingAsync(
            string workspaceId,
            string channelId,
            string? agentId,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
