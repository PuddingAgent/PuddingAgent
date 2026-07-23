using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PuddingAgent.Tools;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingPlatform.Services;

namespace PuddingWebApiTests.Tools;

[TestClass]
public sealed class ImageReaderToolTests
{
    [TestMethod]
    public async Task ExecuteAsync_Imports_Local_Image_And_Invokes_Vision_Model()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pudding-image-reader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var imagePath = Path.Combine(root, "sample.png");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3, 4]);

        var storage = new VisionArtifactStorageService(
            PuddingDataPaths.FromRoot(root),
            NullLogger<VisionArtifactStorageService>.Instance);
        var route = new ResolvedLlmRoute
        {
            ProviderId = "vision-provider",
            ModelId = "vision-model",
            Config = new LlmConfig
            {
                Endpoint = "https://vision.example/v1",
                ApiKey = "test-key",
                ModelId = "vision-model",
            },
        };
        var resolver = new Mock<ILlmResolver>();
        resolver
            .Setup(service => service.ResolveRouteAsync(
                null,
                It.Is<IReadOnlyCollection<string>>(tags => tags.Contains("vision")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(route);

        LlmInvocationRequest? captured = null;
        var invocation = new Mock<ILlmInvocationService>();
        invocation
            .Setup(service => service.InvokeAsync(
                It.IsAny<LlmInvocationRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<LlmInvocationRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new LlmInvocationResult
            {
                Success = true,
                ReplyText = "A purple pudding logo is visible.",
                ProviderId = "vision-provider",
                ModelId = "vision-model",
            });

        var tool = new ImageReaderTool(
            storage,
            storage,
            resolver.Object,
            invocation.Object,
            NullLogger<ImageReaderTool>.Instance);
        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "tool-call-1",
            ArgumentsJson = $$"""{"path":{{System.Text.Json.JsonSerializer.Serialize(imagePath)}},"prompt":"Describe it"}""",
            Context = new ToolExecutionContext
            {
                WorkspaceId = "default",
                SessionId = "session-1",
                AgentInstanceId = "agent-1",
                AgentTemplateId = "template-1",
            },
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("A purple pudding logo is visible.", result.Output);
        Assert.IsNotNull(captured);
        Assert.AreEqual("vision-provider", captured.Profile.ProviderId);
        Assert.AreEqual("vision-model", captured.Profile.ModelId);
        Assert.AreEqual(1, captured.Messages.Count);
        Assert.AreEqual(1, captured.Messages[0].VisualArtifactIds?.Count);
    }
}
