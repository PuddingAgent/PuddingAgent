using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
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
        services.AddSingleton<IImageGenerationService>(
            new StubImageGenerationService());
        services.AddSingleton(CreateVisionStorage());
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
                        [MessageGatewayMetadata.TtsRepliesEnabled] = "true",
                        [MessageGatewayMetadata.TtsVoice] = "Cherry",
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

        var envelope = sent.Envelopes.Single(item =>
            item.ContentType == MessageContentTypes.Text);
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

    [TestMethod]
    public async Task ProjectBatchAsync_VoiceOnlyDirective_PreservesRawTextThenProjectsAudio()
    {
        const string reply = "```voice\n今天天气真好\n```";
        var sent = await ProjectSucceededReplyAsync(
            reply);

        Assert.AreEqual(2, sent.Envelopes.Count);
        var text = sent.Envelopes.Single(item =>
            item.ContentType == MessageContentTypes.Text);
        var audio = AssertHasSingleAudio(sent);
        Assert.AreEqual(reply, text.Content);
        Assert.AreEqual("今天天气真好", audio.Content);
    }

    [TestMethod]
    public async Task ProjectBatchAsync_MixedVoiceDirective_ProjectsTextThenAudio()
    {
        const string reply =
            "文字说明。\n\n```voice\n语音说明。\n```\n\n补充文字。";
        var sent = await ProjectSucceededReplyAsync(
            reply);

        Assert.AreEqual(2, sent.Envelopes.Count);
        var text = sent.Envelopes.Single(item =>
            item.ContentType == MessageContentTypes.Text);
        var audio = AssertHasSingleAudio(sent);
        Assert.AreEqual(reply, text.Content);
        Assert.AreEqual("语音说明。", audio.Content);
    }

    [TestMethod]
    public async Task ProjectBatchAsync_ImageGenerationFences_StripsFencesThenAppendImages()
    {
        const string reply =
            "生成两张图。\n\n```ImageGeneration\n第一张布丁猫。\n```\n\n```ImageGeneration\n第二张布丁猫。\n```";
        var sent = await ProjectSucceededReplyAsync(reply);

        Assert.AreEqual(3, sent.Envelopes.Count);
        var text = sent.Envelopes.Single(item =>
            item.ContentType == MessageContentTypes.Text);
        Assert.AreEqual("生成两张图。", text.Content);
        Assert.IsFalse(
            text.Content.Contains("ImageGeneration", StringComparison.Ordinal));
        Assert.HasCount(
            2,
            sent.Envelopes.Where(item =>
                item.ContentType == MessageContentTypes.Image));
        foreach (var image in sent.Envelopes.Where(item =>
                     item.ContentType == MessageContentTypes.Image))
        {
            Assert.AreEqual(
                ConnectorPayloadKinds.VisionImage,
                image.Metadata[ConnectorPayloadMetadata.Kind]);
            Assert.AreEqual(
                "vision-0123456789abcdef0123456789abcdef",
                image.Metadata[ConnectorPayloadMetadata.ArtifactId]);
        }
    }

    [TestMethod]
    public async Task ProjectBatchAsync_PureImageGenerationFence_ProjectsOnlyImages()
    {
        const string reply = "```ImageGeneration\n一只布丁猫。\n```";
        var sent = await ProjectSucceededReplyAsync(reply);

        Assert.AreEqual(1, sent.Envelopes.Count);
        var image = sent.Envelopes.Single();
        Assert.AreEqual(MessageContentTypes.Image, image.ContentType);
        Assert.AreEqual(
            ConnectorPayloadKinds.VisionImage,
            image.Metadata[ConnectorPayloadMetadata.Kind]);
        Assert.AreEqual(
            "vision-0123456789abcdef0123456789abcdef",
            image.Metadata[ConnectorPayloadMetadata.ArtifactId]);
    }

    [TestMethod]
    public async Task ProjectBatchAsync_ImageToolSuppression_PreservesFenceWithoutRegeneration()
    {
        const string reply = "```ImageGeneration\n一只布丁猫。\n```";
        var sent = await ProjectSucceededReplyAsync(
            reply,
            suppressImageDirective: true);

        Assert.AreEqual(1, sent.Envelopes.Count);
        Assert.AreEqual(MessageContentTypes.Text, sent.Envelopes[0].ContentType);
        Assert.AreEqual(reply, sent.Envelopes[0].Content);
    }

    [TestMethod]
    public async Task ProjectBatchAsync_PureImageFence_ProjectsOnlyImage()
    {
        var sent = await ProjectSucceededReplyAsync(
            "```image\n{{IMAGE_PATH}}\n```");

        Assert.AreEqual(1, sent.Envelopes.Count);
        var image = sent.Envelopes.Single();
        Assert.AreEqual(MessageContentTypes.Image, image.ContentType);
        Assert.AreEqual(
            "vision-0123456789abcdef0123456789abcdef",
            image.Metadata[ConnectorPayloadMetadata.ArtifactId]);
    }

    [TestMethod]
    public async Task ProjectBatchAsync_MixedImageFence_RemovesPathAndAppendsImage()
    {
        var sent = await ProjectSucceededReplyAsync(
            "说明文字。\n\n```image\n{{IMAGE_PATH}}\n```\n\n补充文字。");

        Assert.AreEqual(2, sent.Envelopes.Count);
        var text = sent.Envelopes.Single(item =>
            item.ContentType == MessageContentTypes.Text);
        Assert.AreEqual("说明文字。\n\n\n补充文字。", text.Content);
        Assert.IsFalse(text.Content.Contains("vision-", StringComparison.Ordinal));
        Assert.AreEqual(
            MessageContentTypes.Image,
            sent.Envelopes.Single(item =>
                item.ContentType == MessageContentTypes.Image).ContentType);
    }

    [TestMethod]
    public async Task ProjectBatchAsync_ImageFenceOutsideWorkspace_RemainsText()
    {
        const string reply =
            "```image\nD:\\data\\workspaces\\other\\vision-artifacts\\vision-0123456789abcdef0123456789abcdef.png\n```";
        var sent = await ProjectSucceededReplyAsync(reply);

        Assert.AreEqual(1, sent.Envelopes.Count);
        Assert.AreEqual(MessageContentTypes.Text, sent.Envelopes[0].ContentType);
        Assert.AreEqual(reply, sent.Envelopes[0].Content);
    }

    [TestMethod]
    public async Task ProjectBatchAsync_VoiceToolSuppression_DropsTerminalText()
    {
        var sent = await ProjectSucceededReplyAsync(
            "工具已发送语音。",
            suppressFinalText: true);

        Assert.AreEqual(0, sent.Envelopes.Count);
    }

    [TestMethod]
    public async Task ProjectBatchAsync_ProjectsFailedTerminalAsClearConnectorError()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var sent = new RecordingMessageSystem();
        var services = new ServiceCollection();
        services.AddDbContext<PlatformDbContext>(
            options => options.UseSqlite(connection));
        services.AddSingleton<IMessageSystem>(sent);
        services.AddSingleton<IImageGenerationService>(
            new StubImageGenerationService());
        services.AddSingleton(CreateVisionStorage());
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.ChatExecutionCommands.Add(new ChatExecutionCommandEntity
            {
                CommandId = "command-failed",
                BatchId = "batch-failed",
                ClientRequestId = "request-failed",
                WorkspaceId = "default",
                SessionId = "conversation-failed",
                MessageId = "assistant-message-failed",
                UserMessageId = "gateway-message-failed",
                TurnId = "turn-failed",
                AgentInstanceId = "assistant",
                ChannelId = "feishu",
                Status = "failed",
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
                            "oc_failed",
                        [MessageGatewayMetadata.ExternalMessageId] =
                            "om_failed",
                    }),
            });
            db.ConversationEvents.Add(new ConversationEventEntity
            {
                ConversationId = "conversation-failed",
                Sequence = 2,
                EventId = "event-failed",
                WorkspaceId = "default",
                TurnId = "turn-failed",
                CommandId = "command-failed",
                RunId = "run-failed",
                MessageId = "assistant-message-failed",
                Type = ConversationEventTypes.TurnFailed,
                Payload =
                    """{"kind":"Failed","errorCode":"agent_configuration_invalid","errorMessage":"preferredModelId is invalid","reply":null}""",
                OccurredAt = "2026-07-29T00:00:00Z",
                CommittedAt = "2026-07-29T00:00:00Z",
            });
            await db.SaveChangesAsync();
        }

        var worker = new ConversationReplyProjectionWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ConversationReplyProjectionWorker>.Instance);

        Assert.AreEqual(1, await worker.ProjectBatchAsync());
        Assert.AreEqual(0, await worker.ProjectBatchAsync());
        var envelope = sent.Envelopes.Single();
        StringAssert.Contains(envelope.Content, "请求失败");
        StringAssert.Contains(envelope.Content, "agent_configuration_invalid");
        StringAssert.Contains(envelope.Content, "preferredModelId is invalid");
        Assert.AreEqual("om_failed", envelope.ReplyToMessageId);
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

    private static MessageEnvelope AssertHasSingleAudio(
        RecordingMessageSystem sent)
    {
        var audio = sent.Envelopes.Single(item =>
            item.ContentType == MessageContentTypes.Audio);
        Assert.AreEqual(MessageVisibilities.System, audio.Visibility);
        Assert.AreEqual(
            ConnectorPayloadKinds.TtsAudio,
            audio.Metadata[ConnectorPayloadMetadata.Kind]);
        Assert.AreEqual(
            "Cherry",
            audio.Metadata[MessageGatewayMetadata.TtsVoice]);
        return audio;
    }

    private static async Task<RecordingMessageSystem> ProjectSucceededReplyAsync(
        string reply,
        bool suppressFinalText = false,
        bool suppressImageDirective = false)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var sent = new RecordingMessageSystem();
        var services = new ServiceCollection();
        services.AddDbContext<PlatformDbContext>(
            options => options.UseSqlite(connection));
        services.AddSingleton<IMessageSystem>(sent);
        services.AddSingleton<IImageGenerationService>(
            new StubImageGenerationService());
        var visionStorage = CreateVisionStorage();
        services.AddSingleton(visionStorage);
        const string artifactId =
            "vision-0123456789abcdef0123456789abcdef";
        await using (var image = new MemoryStream(
                         Services.VisionTestImages.PngHeader(24, 24)))
        {
            await visionStorage.SaveIdempotentAsync(
                "default",
                artifactId,
                image,
                "image/png");
        }
        var localImage = await visionStorage.ResolveLocalFileAsync(
            "default",
            artifactId);
        Assert.IsNotNull(localImage);
        reply = reply.Replace(
            "{{IMAGE_PATH}}",
            localImage.Path,
            StringComparison.Ordinal);
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await db.Database.EnsureCreatedAsync();
            var metadata = new Dictionary<string, string>
            {
                [MessageGatewayMetadata.IsGatewayIngress] = "true",
                [MessageGatewayMetadata.ChannelId] = "feishu",
                [MessageGatewayMetadata.ChannelType] = "feishu",
                [MessageGatewayMetadata.ConnectorId] = "feishu:assistant",
                [MessageGatewayMetadata.ExternalConversationId] = "oc_voice",
                [MessageGatewayMetadata.ExternalMessageId] = "om_voice",
                [MessageGatewayMetadata.TtsRepliesEnabled] = "true",
                [MessageGatewayMetadata.TtsVoice] = "Cherry",
            };
            if (suppressFinalText)
            {
                metadata[MessageGatewayMetadata.VoiceToolSuppressFinalText] =
                    "true";
            }
            if (suppressImageDirective)
            {
                metadata[MessageGatewayMetadata.ImageToolSuppressDirective] =
                    "true";
            }

            db.ChatExecutionCommands.Add(new ChatExecutionCommandEntity
            {
                CommandId = "command-voice",
                BatchId = "batch-voice",
                ClientRequestId = "request-voice",
                WorkspaceId = "default",
                SessionId = "conversation-voice",
                MessageId = "assistant-message-voice",
                UserMessageId = "gateway-message-voice",
                TurnId = "turn-voice",
                AgentInstanceId = "assistant",
                ChannelId = "feishu",
                Status = "succeeded",
                TerminalSequence = 2,
                CreatedAt = 100,
                CompletedAt = 200,
                MetadataJson = JsonSerializer.Serialize(metadata),
            });
            db.ConversationEvents.Add(new ConversationEventEntity
            {
                ConversationId = "conversation-voice",
                Sequence = 2,
                EventId = "event-voice",
                WorkspaceId = "default",
                TurnId = "turn-voice",
                CommandId = "command-voice",
                RunId = "run-voice",
                MessageId = "assistant-message-voice",
                Type = ConversationEventTypes.TurnCompleted,
                Payload = JsonSerializer.Serialize(new
                {
                    kind = "Completed",
                    reply,
                }),
                OccurredAt = "2026-07-31T00:00:00Z",
                CommittedAt = "2026-07-31T00:00:00Z",
            });
            await db.SaveChangesAsync();
        }

        var worker = new ConversationReplyProjectionWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ConversationReplyProjectionWorker>.Instance);
        Assert.AreEqual(1, await worker.ProjectBatchAsync());
        Assert.AreEqual(0, await worker.ProjectBatchAsync());
        return sent;
    }

    private sealed class StubImageGenerationService : IImageGenerationService
    {
        public Task<ImageGenerationResult> GenerateAsync(
            ImageGenerationRequest request,
            CancellationToken ct = default)
            => Task.FromResult(new ImageGenerationResult
            {
                ProviderId = "test",
                ModelId = "test-image",
                Artifacts =
                [
                    new ImageGenerationArtifact
                    {
                        ArtifactId =
                            "vision-0123456789abcdef0123456789abcdef",
                        MimeType = "image/png",
                    },
                ],
            });
    }

    private static VisionArtifactStorageService CreateVisionStorage()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"pudding-projection-vision-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new VisionArtifactStorageService(
            PuddingDataPaths.FromRoot(root),
            NullLogger<VisionArtifactStorageService>.Instance);
    }
}
