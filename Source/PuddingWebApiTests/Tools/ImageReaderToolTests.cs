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

/// <summary>
/// ADR-077 V2：image_reader 新合同 — path 唯一必填（URL/绝对路径/artifact://）；
/// auto 优先 native（typed 图片工具结果，零辅助 invocation），文本调用模型才 delegate；
/// 失败走稳定错误码，输出不含绝对路径。
/// </summary>
[TestClass]
public sealed class ImageReaderToolTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static string CreateImageFile(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), $"pudding-image-reader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var imagePath = Path.Combine(root, "sample.png");
        File.WriteAllBytes(imagePath, Png);
        return imagePath;
    }

    private static async Task<(PuddingDataPaths Paths, VisionArtifactStorageService Storage)> CreateStorageAsync(
        string root,
        string? visionHelperModel = null)
    {
        var paths = PuddingDataPaths.FromRoot(root);
        await WriteAgentManifestAsync(
            paths,
            "configuration-agent",
            visionHelperModel,
            "text-provider",
            "text-model");
        var storage = new VisionArtifactStorageService(
            paths,
            NullLogger<VisionArtifactStorageService>.Instance);
        return (paths, storage);
    }

    [TestMethod]
    public async Task Auto_VisionCaller_ReturnsNativeImageToolPartsWithoutSecondInvocation()
    {
        var imagePath = CreateImageFile(out var root);
        var (_, storage) = await CreateStorageAsync(root);

        var tool = CreateTool(storage, root, new Mock<ILlmResolver>(), new Mock<ILlmInvocationService>());
        var result = await tool.ExecuteAsync(Request(
            imagePath,
            context: Context(callerSnapshot: Snapshot(vision: true, protocol: "responses"))));

        Assert.IsTrue(result.Error is null, result.Error);
        Assert.IsTrue(result.Success);
        Assert.IsNotNull(result.ToolContentParts);
        Assert.AreEqual(1, result.ToolContentParts.Count);
        var image = (LlmImagePart)result.ToolContentParts[0];
        StringAssert.StartsWith(image.ArtifactId, "vision-");
        // native 模式零辅助 LLM invocation（ADR-077 §9.2）
        StringAssert.Contains(result.Output, "native image part");
        Assert.IsFalse(result.Output.Contains(root, StringComparison.Ordinal), "output must not leak the host path");
        // 原文件不被移动或修改
        Assert.IsTrue(File.Exists(imagePath));
        Assert.AreEqual(Png.Length, new FileInfo(imagePath).Length);
    }

    [TestMethod]
    public async Task Auto_TextCaller_DelegatesToConfiguredHelper_WithProvenance()
    {
        var imagePath = CreateImageFile(out var root);
        var (_, storage) = await CreateStorageAsync(root, visionHelperModel: "vision-provider/vision-model");

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
            .Setup(service => service.InvokeAsync(It.IsAny<LlmInvocationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<LlmInvocationRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(new LlmInvocationResult
            {
                Success = true,
                ReplyText = "A purple pudding logo.",
                ProviderId = "vision-provider",
                ModelId = "vision-model",
            });

        var tool = CreateTool(storage, root, resolver, invocation);
        var result = await tool.ExecuteAsync(Request(
            imagePath,
            context: Context(callerSnapshot: Snapshot(vision: false, protocol: "openai"))));

        Assert.IsTrue(result.Success, result.Error);
        // 精确一次可归因 helper invocation
        invocation.Verify(service => service.InvokeAsync(
            It.IsAny<LlmInvocationRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.IsNotNull(captured);
        Assert.AreEqual("vision-provider", captured.Profile.ProviderId);
        // helper 收到的消息携带 canonical 图片部件
        Assert.AreEqual(1, captured.Messages[0].ContentParts!.OfType<LlmImagePart>().Count());
        // provenance 可见
        StringAssert.Contains(result.Output, "helper=vision-provider/vision-model");
        StringAssert.Contains(result.Output, "artifact=vision-");
    }

    [TestMethod]
    public async Task Delegate_WithoutHelper_ReturnsStableErrorAndNeverInvokes()
    {
        var imagePath = CreateImageFile(out var root);
        var (_, storage) = await CreateStorageAsync(root, visionHelperModel: null);

        var invocation = new Mock<ILlmInvocationService>();
        var tool = CreateTool(storage, root, new Mock<ILlmResolver>(), invocation);
        var result = await tool.ExecuteAsync(Request(
            imagePath,
            arguments: $$"""{"path":{{System.Text.Json.JsonSerializer.Serialize(imagePath)}},"mode":"delegate"}""",
            context: Context(callerSnapshot: Snapshot(vision: false, protocol: "openai"))));

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "vision_helper_model_required");
        invocation.Verify(service => service.InvokeAsync(
            It.IsAny<LlmInvocationRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Native_OnNonResponsesProtocol_ReturnsToolOutputNotSupported()
    {
        var imagePath = CreateImageFile(out var root);
        var (_, storage) = await CreateStorageAsync(root);

        var tool = CreateTool(storage, root, new Mock<ILlmResolver>(), new Mock<ILlmInvocationService>());
        var result = await tool.ExecuteAsync(Request(
            imagePath,
            arguments: $$"""{"path":{{System.Text.Json.JsonSerializer.Serialize(imagePath)}},"mode":"native"}""",
            context: Context(callerSnapshot: Snapshot(vision: true, protocol: "openai"))));

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "vision_tool_output_not_supported");
    }

    [TestMethod]
    public async Task ArtifactReference_ReusesWorkspaceArtifactByContentHash()
    {
        var imagePath = CreateImageFile(out var root);
        var (_, storage) = await CreateStorageAsync(root);

        await using var first = new MemoryStream(Png);
        var imported = await storage.SaveAsync("default", first, "image/png");

        var tool = CreateTool(storage, root, new Mock<ILlmResolver>(), new Mock<ILlmInvocationService>());
        var result = await tool.ExecuteAsync(Request(
            $"artifact://{imported.ArtifactId}",
            context: Context(callerSnapshot: Snapshot(vision: true, protocol: "responses"))));

        Assert.IsTrue(result.Success, result.Error);
        var image = (LlmImagePart)result.ToolContentParts![0];
        // 内容哈希稳定 id：与首次导入一致
        Assert.AreEqual(imported.ArtifactId, image.ArtifactId);
    }

    [TestMethod]
    public async Task RelativePath_RejectedWithStableError()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pudding-image-reader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var (_, storage) = await CreateStorageAsync(root);

        var tool = CreateTool(storage, root, new Mock<ILlmResolver>(), new Mock<ILlmInvocationService>());
        var result = await tool.ExecuteAsync(Request(
            "relative/sample.png",
            context: Context(callerSnapshot: Snapshot(vision: true, protocol: "responses"))));

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "vision_source_invalid");
    }

    [TestMethod]
    public async Task UrlLoopback_RejectedAsNonPublicNetwork()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pudding-image-reader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var (_, storage) = await CreateStorageAsync(root);

        var tool = CreateTool(storage, root, new Mock<ILlmResolver>(), new Mock<ILlmInvocationService>());
        var result = await tool.ExecuteAsync(Request(
            "http://127.0.0.1:9/img.png",
            context: Context(callerSnapshot: Snapshot(vision: true, protocol: "responses"))));

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "vision_source_access_denied");
    }

    private static ImageReaderTool CreateTool(
        VisionArtifactStorageService storage,
        string root,
        Mock<ILlmResolver> resolver,
        Mock<ILlmInvocationService> invocation)
    {
        var paths = PuddingDataPaths.FromRoot(root);
        var sourceResolver = new ImageReaderSourceResolver(
            storage,
            null,
            NullLogger<ImageReaderSourceResolver>.Instance);
        return new ImageReaderTool(
            sourceResolver,
            storage,
            new AgentProfileProvider(paths),
            resolver.Object,
            invocation.Object,
            NullLogger<ImageReaderTool>.Instance);
    }

    private static ToolExecutionRequest Request(
        string path,
        ToolExecutionContext context,
        string? arguments = null)
        => new()
        {
            ToolCallId = "tool-call-1",
            ArgumentsJson = arguments
                ?? $$"""{"path":{{System.Text.Json.JsonSerializer.Serialize(path)}}}""",
            Context = context,
        };

    private static ToolExecutionContext Context(PuddingCode.Platform.LlmRouteSnapshot? callerSnapshot = null)
        => new()
        {
            WorkspaceId = "default",
            SessionId = "session-1",
            AgentInstanceId = "ephemeral-agent",
            ConfigurationAgentInstanceId = "configuration-agent",
            AgentTemplateId = "template-1",
            CallerLlmSnapshot = callerSnapshot,
        };

    private static PuddingCode.Platform.LlmRouteSnapshot Snapshot(bool vision, string protocol)
        => new(
            "text-provider",
            "text-model",
            protocol,
            vision ? ["vision"] : []);

    private static async Task WriteAgentManifestAsync(
        PuddingDataPaths paths,
        string agentId,
        string? visionHelperModel,
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
            VisionHelperModel = visionHelperModel,
            PreferredProviderId = preferredProviderId,
            PreferredModelId = preferredModelId,
        };
        await File.WriteAllTextAsync(
            Path.Combine(instanceRoot, "manifest.json"),
            System.Text.Json.JsonSerializer.Serialize(manifest));
    }
}
