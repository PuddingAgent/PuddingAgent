using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingAgent.Tools;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingRuntime.Services.Tools;

namespace PuddingAgent.IntegrationTests.Feishu;

[TestClass]
public sealed class SendImageToolTests
{
    [TestMethod]
    public void GenerateImage_IsTemplateGrantedControlledNetworkOperation()
    {
        var tool = new GenerateImageTool(
            new StubImageGenerationService(),
            NullLogger<GenerateImageTool>.Instance);
        var decision =
            new ToolPermissionPolicyService().Classify(tool.Descriptor);

        Assert.AreEqual(
            ToolPermissionLevel.Medium,
            tool.Descriptor.PermissionLevel);
        Assert.IsTrue(
            tool.Descriptor.Safety.HasFlag(
                ToolSafetyFlags.RequiresNetwork));
        Assert.IsFalse(
            tool.Descriptor.Safety.HasFlag(
                ToolSafetyFlags.RequiresFileWrite));
        Assert.AreEqual(
            ToolPermissionTier.TemplateGranted,
            decision.Tier);
        Assert.IsFalse(decision.RequiresRuntimeAuthorization);
    }

    [TestMethod]
    public async Task ExecuteAsync_QueuesArtifactToCurrentTrustedFeishuRoute()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"pudding-send-image-{Guid.NewGuid():N}");
        await using var connection =
            new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        try
        {
            var sent = new RecordingMessageSystem();
            var artifacts = new VisionArtifactStorageService(
                PuddingDataPaths.FromRoot(root),
                NullLogger<VisionArtifactStorageService>.Instance);
            await using var image =
                new MemoryStream([0x89, 0x50, 0x4E, 0x47]);
            var artifact = await artifacts.SaveAsync(
                "default",
                image,
                "image/png");

            var services = new ServiceCollection();
            services.AddDbContext<PlatformDbContext>(
                options => options.UseSqlite(connection));
            services.AddSingleton<IMessageSystem>(sent);
            await using var provider = services.BuildServiceProvider();

            await using (var scope = provider.CreateAsyncScope())
            {
                var db =
                    scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                await db.Database.EnsureCreatedAsync();
                db.ChatExecutionCommands.Add(CreateCommand());
                await db.SaveChangesAsync();
            }

            var tool = new SendImageTool(
                provider.GetRequiredService<IServiceScopeFactory>(),
                artifacts,
                NullLogger<SendImageTool>.Instance);
            var result = await tool.ExecuteAsync(new ToolExecutionRequest
            {
                ToolCallId = "tool-image-1",
                ArgumentsJson = JsonSerializer.Serialize(
                    new { artifactId = artifact.ArtifactId }),
                Context = CreateContext(),
            });

            Assert.IsTrue(result.Success, result.Error);
            var queued = sent.Envelopes.Single();
            Assert.AreEqual(MessageContentTypes.Image, queued.ContentType);
            Assert.AreEqual(artifact.ArtifactId, queued.Content);
            Assert.AreEqual("om_image", queued.ReplyToMessageId);
            Assert.AreEqual(
                ConnectorPayloadKinds.VisionImage,
                queued.Metadata[ConnectorPayloadMetadata.Kind]);
            Assert.AreEqual(
                artifact.ArtifactId,
                queued.Metadata[ConnectorPayloadMetadata.ArtifactId]);
            await using var verifyScope = provider.CreateAsyncScope();
            var verifyDb =
                verifyScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var command = await verifyDb.ChatExecutionCommands.SingleAsync();
            var commandMetadata =
                JsonSerializer.Deserialize<Dictionary<string, string>>(
                    command.MetadataJson!);
            Assert.AreEqual(
                "true",
                commandMetadata![
                    MessageGatewayMetadata.ImageToolSuppressDirective]);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static ChatExecutionCommandEntity CreateCommand()
        => new()
        {
            CommandId = "command-image",
            BatchId = "batch-image",
            WorkspaceId = "default",
            SessionId = "conversation-image",
            MessageId = "assistant-image",
            UserMessageId = "gateway-image",
            TurnId = "turn-image",
            AgentInstanceId = "assistant",
            ChannelId = "feishu",
            Status = "running",
            CreatedAt = 100,
            StartedAt = 101,
            MetadataJson = JsonSerializer.Serialize(
                new Dictionary<string, string>
                {
                    [MessageGatewayMetadata.IsGatewayIngress] = "true",
                    [MessageGatewayMetadata.ChannelId] =
                        "feishu-channel",
                    [MessageGatewayMetadata.ChannelType] = "feishu",
                    [MessageGatewayMetadata.ConnectorId] =
                        "feishu:assistant",
                    [MessageGatewayMetadata.ExternalConversationId] =
                        "oc_image",
                    [MessageGatewayMetadata.ExternalMessageId] =
                        "om_image",
                }),
        };

    private static ToolExecutionContext CreateContext()
        => new()
        {
            WorkspaceId = "default",
            SessionId = "conversation-image",
            AgentInstanceId = "assistant",
            ExecutionIdentity = new RuntimeExecutionIdentity
            {
                Kind = RuntimeExecutionKind.ConversationTurn,
                ConversationId = "conversation-image",
                TurnId = "turn-image",
                CommandId = "command-image",
                RunId = "run-image",
                ToolCallId = "tool-image-1",
                TraceId = null,
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

    private sealed class StubImageGenerationService
        : IImageGenerationService
    {
        public Task<ImageGenerationResult> GenerateAsync(
            ImageGenerationRequest request,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
