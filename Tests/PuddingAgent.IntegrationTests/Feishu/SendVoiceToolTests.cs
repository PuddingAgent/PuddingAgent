using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingAgent.Tools;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingAgent.IntegrationTests.Feishu;

[TestClass]
public sealed class SendVoiceToolTests
{
    [TestMethod]
    public async Task ExecuteAsync_QueuesCurrentFeishuVoiceAndSuppressesFinalText()
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
            db.ChatExecutionCommands.Add(CreateCommand(ttsEnabled: true));
            await db.SaveChangesAsync();
        }

        var tool = new SendVoiceTool(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SendVoiceTool>.Instance);
        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "tool-call-1",
            ArgumentsJson = """{"text":"今天天气真好"}""",
            Context = CreateContext(),
        });

        Assert.IsTrue(result.Success, result.Error);
        var audio = sent.Envelopes.Single();
        Assert.AreEqual(MessageContentTypes.Audio, audio.ContentType);
        Assert.AreEqual("今天天气真好", audio.Content);
        Assert.AreEqual("om_tool", audio.ReplyToMessageId);
        Assert.AreEqual(
            ConnectorPayloadKinds.TtsAudio,
            audio.Metadata[ConnectorPayloadMetadata.Kind]);

        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb =
            verifyScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var command = await verifyDb.ChatExecutionCommands.SingleAsync();
        var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(
            command.MetadataJson!);
        Assert.AreEqual(
            "true",
            metadata![MessageGatewayMetadata.VoiceToolSuppressFinalText]);
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsWhenChannelVoiceCapabilityIsDisabled()
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
            db.ChatExecutionCommands.Add(CreateCommand(ttsEnabled: false));
            await db.SaveChangesAsync();
        }

        var tool = new SendVoiceTool(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SendVoiceTool>.Instance);
        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "tool-call-2",
            ArgumentsJson = """{"text":"不会发送"}""",
            Context = CreateContext() with
            {
                ExecutionIdentity = CreateContext().ExecutionIdentity! with
                {
                    ToolCallId = "tool-call-2",
                },
            },
        });

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error!, "disabled");
        Assert.AreEqual(0, sent.Envelopes.Count);
    }

    private static ChatExecutionCommandEntity CreateCommand(bool ttsEnabled)
        => new()
        {
            CommandId = "command-tool",
            BatchId = "batch-tool",
            WorkspaceId = "default",
            SessionId = "conversation-tool",
            MessageId = "assistant-tool",
            UserMessageId = "gateway-tool",
            TurnId = "turn-tool",
            AgentInstanceId = "assistant",
            ChannelId = "feishu",
            Status = "running",
            CreatedAt = 100,
            StartedAt = 101,
            MetadataJson = JsonSerializer.Serialize(
                new Dictionary<string, string>
                {
                    [MessageGatewayMetadata.IsGatewayIngress] = "true",
                    [MessageGatewayMetadata.ChannelId] = "feishu-channel",
                    [MessageGatewayMetadata.ChannelType] = "feishu",
                    [MessageGatewayMetadata.ConnectorId] = "feishu:assistant",
                    [MessageGatewayMetadata.ExternalConversationId] = "oc_tool",
                    [MessageGatewayMetadata.ExternalMessageId] = "om_tool",
                    [MessageGatewayMetadata.TtsRepliesEnabled] =
                        ttsEnabled ? "true" : "false",
                    [MessageGatewayMetadata.TtsVoice] = "Cherry",
                }),
        };

    private static ToolExecutionContext CreateContext()
        => new()
        {
            WorkspaceId = "default",
            SessionId = "conversation-tool",
            AgentInstanceId = "assistant",
            ExecutionIdentity = new RuntimeExecutionIdentity
            {
                Kind = RuntimeExecutionKind.ConversationTurn,
                ConversationId = "conversation-tool",
                TurnId = "turn-tool",
                CommandId = "command-tool",
                RunId = "run-tool",
                ToolCallId = "tool-call-1",
            },
        };

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
