using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.MessageGateway;

namespace PuddingPlatformTests.Services.MessageGateway;

[TestClass]
public sealed class ConversationReplyProjectionWorkerTests
{
    [TestMethod]
    public async Task ProjectBatchAsync_ProjectsTerminalReplyOnce_ToBoundConnector()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var sent = new RecordingMessageSystem();
        var services = new ServiceCollection();
        services.AddDbContext<PlatformDbContext>(
            options => options.UseSqlite(connection));
        services.AddSingleton<IMessageSystem>(sent);
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.ChatExecutionCommands.Add(new ChatExecutionCommandEntity
            {
                CommandId = "command-1",
                BatchId = "batch-1",
                ClientRequestId = "request-1",
                WorkspaceId = "default",
                SessionId = "conversation-1",
                MessageId = "assistant-message-1",
                UserMessageId = "gateway-message-1",
                TurnId = "turn-1",
                AgentInstanceId = "assistant",
                ChannelId = "feishu",
                Status = "succeeded",
                TerminalSequence = 2,
                CreatedAt = 100,
                CompletedAt = 200,
                MetadataJson = JsonSerializer.Serialize(
                    new Dictionary<string, string>
                    {
                        [MessageGatewayMetadata.IsGatewayIngress] = "true",
                        [MessageGatewayMetadata.ChannelId] = "feishu",
                        [MessageGatewayMetadata.ChannelType] = "feishu",
                        [MessageGatewayMetadata.ConnectorId] =
                            "feishu:assistant",
                        [MessageGatewayMetadata.ExternalConversationId] =
                            "oc_chat",
                        [MessageGatewayMetadata.ExternalMessageId] =
                            "om_external",
                    }),
            });
            db.ConversationEvents.Add(new ConversationEventEntity
            {
                ConversationId = "conversation-1",
                Sequence = 2,
                EventId = "event-2",
                WorkspaceId = "default",
                TurnId = "turn-1",
                CommandId = "command-1",
                RunId = "run-1",
                MessageId = "assistant-message-1",
                Type = ConversationEventTypes.TurnCompleted,
                Payload = """{"kind":"Completed","reply":"agent answer"}""",
                OccurredAt = "2026-07-25T00:00:00Z",
                CommittedAt = "2026-07-25T00:00:00Z",
            });
            await db.SaveChangesAsync();
        }

        var worker = new ConversationReplyProjectionWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ConversationReplyProjectionWorker>.Instance);

        Assert.AreEqual(1, await worker.ProjectBatchAsync());
        Assert.AreEqual(0, await worker.ProjectBatchAsync());
        Assert.AreEqual(1, sent.Envelopes.Count);

        var envelope = sent.Envelopes.Single();
        Assert.AreEqual("agent answer", envelope.Content);
        Assert.AreEqual("conversation-1", envelope.ConversationId);
        Assert.AreEqual("om_external", envelope.ReplyToMessageId);
        Assert.AreEqual(
            MessageEndpointKinds.Connector,
            envelope.To.Single().Kind);
        Assert.AreEqual("feishu:assistant", envelope.To.Single().Id);
        Assert.AreEqual(
            envelope.MessageId,
            envelope.Metadata[MessageGatewayMetadata.IdempotencyKey]);

        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb =
            verifyScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.IsNotNull(
            (await verifyDb.ChatExecutionCommands.SingleAsync())
            .ReplyProjectedAt);
    }

    private sealed class RecordingMessageSystem : IMessageSystem
    {
        public List<MessageEnvelope> Envelopes { get; } = [];

        public Task<MessageSendResult> SendAsync(
            MessageEnvelope envelope,
            CancellationToken ct = default)
        {
            Envelopes.Add(envelope);
            return Task.FromResult(new MessageSendResult
            {
                MessageId = envelope.MessageId,
                RoomId = envelope.RoomId,
                DeliveryIds = [$"delivery-{envelope.MessageId}"],
            });
        }
    }
}
