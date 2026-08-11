using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PuddingAgent.Tools;
using PuddingCode.Abstractions;
using PuddingCode.Agents;
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

        var paths = PuddingDataPaths.FromRoot(root);
        await WriteAgentManifestAsync(
            paths,
            "configuration-agent",
            imageReaderModel: "vision-provider/vision-model",
            preferredProviderId: "text-provider",
            preferredModelId: "text-model");
        var storage = new VisionArtifactStorageService(
            paths,
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
                "vision-provider/vision-model",
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
            new AgentProfileProvider(paths),
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
                AgentInstanceId = "ephemeral-agent",
                ConfigurationAgentInstanceId = "configuration-agent",
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
        resolver.Verify(service => service.ResolveRouteAsync(
            "vision-provider/vision-model",
            It.Is<IReadOnlyCollection<string>>(tags => tags.Contains("vision")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenDedicatedModelFails_FallsBackToAgentMainVisionModel()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pudding-image-reader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var imagePath = Path.Combine(root, "sample.png");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3, 4]);

        var paths = PuddingDataPaths.FromRoot(root);
        await WriteAgentManifestAsync(
            paths,
            "agent-1",
            imageReaderModel: "cheap/cheap-vision",
            preferredProviderId: "premium",
            preferredModelId: "premium-vision");
        var storage = new VisionArtifactStorageService(
            paths,
            NullLogger<VisionArtifactStorageService>.Instance);
        var resolver = new Mock<ILlmResolver>();
        resolver
            .Setup(service => service.ResolveRouteAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyCollection<string>>(tags => tags.Contains("vision")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                string? modelRoute,
                IReadOnlyCollection<string>? requiredCapabilityTags,
                CancellationToken _) =>
            {
                Assert.IsTrue(requiredCapabilityTags?.Contains("vision"));
                var parts = modelRoute!.Split('/', 2);
                return new ResolvedLlmRoute
                {
                    ProviderId = parts[0],
                    ModelId = parts[1],
                    Config = new LlmConfig { ModelId = parts[1] },
                };
            });

        var invokedModels = new List<string>();
        var invocation = new Mock<ILlmInvocationService>();
        invocation
            .Setup(service => service.InvokeAsync(
                It.IsAny<LlmInvocationRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((LlmInvocationRequest request, CancellationToken _) =>
            {
                invokedModels.Add(request.Profile.ModelId);
                return request.Profile.ModelId == "cheap-vision"
                    ? new LlmInvocationResult { Success = false, Error = "temporary failure" }
                    : new LlmInvocationResult
                    {
                        Success = true,
                        ReplyText = "Fallback model description.",
                    };
            });

        var tool = new ImageReaderTool(
            storage,
            storage,
            new AgentProfileProvider(paths),
            resolver.Object,
            invocation.Object,
            NullLogger<ImageReaderTool>.Instance);
        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "tool-call-fallback",
            ArgumentsJson = $$"""{"path":{{System.Text.Json.JsonSerializer.Serialize(imagePath)}}}""",
            Context = new ToolExecutionContext
            {
                WorkspaceId = "default",
                SessionId = "session-1",
                AgentInstanceId = "agent-1",
                AgentTemplateId = "template-1",
            },
        });

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("Fallback model description.", result.Output);
        CollectionAssert.AreEqual(
            new[] { "cheap-vision", "premium-vision" },
            invokedModels);
    }

    [TestMethod]
    public async Task ExecuteAsync_WithoutImageReaderModel_DoesNotSelectGlobalVisionModel()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pudding-image-reader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var imagePath = Path.Combine(root, "sample.png");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3, 4]);

        var paths = PuddingDataPaths.FromRoot(root);
        await WriteAgentManifestAsync(
            paths,
            "agent-1",
            imageReaderModel: null,
            preferredProviderId: "premium",
            preferredModelId: "premium-vision");
        var storage = new VisionArtifactStorageService(
            paths,
            NullLogger<VisionArtifactStorageService>.Instance);
        var resolver = new Mock<ILlmResolver>();
        var invocation = new Mock<ILlmInvocationService>();
        var tool = new ImageReaderTool(
            storage,
            storage,
            new AgentProfileProvider(paths),
            resolver.Object,
            invocation.Object,
            NullLogger<ImageReaderTool>.Instance);

        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "tool-call-missing-config",
            ArgumentsJson = $$"""{"path":{{System.Text.Json.JsonSerializer.Serialize(imagePath)}}}""",
            Context = new ToolExecutionContext
            {
                WorkspaceId = "default",
                SessionId = "session-1",
                AgentInstanceId = "agent-1",
            },
        });

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "imageReaderModel");
        resolver.Verify(service => service.ResolveRouteAsync(
            It.IsAny<string>(),
            It.IsAny<IReadOnlyCollection<string>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        invocation.Verify(service => service.InvokeAsync(
            It.IsAny<LlmInvocationRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static async Task WriteAgentManifestAsync(
        PuddingDataPaths paths,
        string agentId,
        string? imageReaderModel,
        string preferredProviderId,
        string preferredModelId)
    {
        var instanceRoot = paths.AgentInstanceRoot(agentId);
        Directory.CreateDirectory(instanceRoot);
        var manifest = new AgentInstanceManifest
        {
            AgentInstanceId = agentId,
            TemplateId = "template-1",
            WorkspaceId = "default",
            ImageReaderModel = imageReaderModel,
            PreferredProviderId = preferredProviderId,
            PreferredModelId = preferredModelId,
        };
        await File.WriteAllTextAsync(
            Path.Combine(instanceRoot, "manifest.json"),
            System.Text.Json.JsonSerializer.Serialize(manifest));
    }
}
