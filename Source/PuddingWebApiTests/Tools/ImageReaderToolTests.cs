using Microsoft.Extensions.Logging.Abstractions;
using PuddingAgent.Tools;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingPlatform.Services;
using SkiaSharp;

namespace PuddingWebApiTests.Tools;

/// <summary>
/// ADR-077 V2：image_reader 新合同 — path 唯一必填（URL/绝对路径/artifact://）；
/// 始终返回 native typed 图片工具结果，零辅助 invocation；不支持视觉时明确报错；
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
    public async Task VisionCaller_ReturnsNativeImageToolParts()
    {
        var imagePath = CreateImageFile(out var root);
        var (_, storage) = await CreateStorageAsync(root);

        var tool = CreateTool(storage);
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
    public async Task TextCaller_WithHelperConfigured_StillFailsWithoutDelegation()
    {
        var imagePath = CreateImageFile(out var root);
        var (_, storage) = await CreateStorageAsync(root, visionHelperModel: "vision-provider/vision-model");
        var tool = CreateTool(storage);
        var result = await tool.ExecuteAsync(Request(imagePath,
            context: Context(callerSnapshot: Snapshot(vision: false, protocol: "responses"))));
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "vision_model_capability_mismatch");
    }

    [TestMethod]
    public async Task ToolSchema_DoesNotOfferModelDelegationModes()
    {
        var imagePath = CreateImageFile(out var root);
        var (_, storage) = await CreateStorageAsync(root);
        var tool = CreateTool(storage);
        Assert.IsNull(typeof(ImageReaderArgs).GetProperty("Mode"));
        CollectionAssert.AreEqual(new[] { "path" }, tool.Descriptor.Parameters.Required.ToArray());
        Assert.IsFalse(tool.Descriptor.Description.Contains("mode=delegate", StringComparison.Ordinal));
        var result = await tool.ExecuteAsync(Request(imagePath, context: Context()));
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "vision_model_capability_mismatch");
    }

    [TestMethod]
    public async Task Native_OnNonResponsesProtocol_ReturnsToolOutputNotSupported()
    {
        var imagePath = CreateImageFile(out var root);
        var (_, storage) = await CreateStorageAsync(root);

        var tool = CreateTool(storage);
        var result = await tool.ExecuteAsync(Request(
            imagePath,
            arguments: $$"""{"path":{{System.Text.Json.JsonSerializer.Serialize(imagePath)}}}""",
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

        var tool = CreateTool(storage);
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

        var tool = CreateTool(storage);
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

        var tool = CreateTool(storage);
        var result = await tool.ExecuteAsync(Request(
            "http://127.0.0.1:9/img.png",
            context: Context(callerSnapshot: Snapshot(vision: true, protocol: "responses"))));

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "vision_source_access_denied");
    }

    [TestMethod]
    public async Task Transform_ZoomIn_ProducesDerivedArtifactAndKeepsSourceArtifactIntact()
    {
        var imagePath = CreateTestImage(20, 10, SKEncodedImageFormat.Png, SKColors.Red, SKColors.Blue);
        var root = Path.GetDirectoryName(imagePath)!;
        var originalBytes = File.ReadAllBytes(imagePath);
        var (_, storage) = await CreateStorageAsync(root);

        var tool = CreateTool(storage);
        var native = Context(callerSnapshot: Snapshot(vision: true, protocol: "responses"));

        // 先导入一次，取得源 artifact 身份
        var import = await tool.ExecuteAsync(Request(imagePath, context: native));
        Assert.IsTrue(import.Success, import.Error);
        var sourceArtifactId = ExtractArtifactId(import.Output);

        var result = await tool.ExecuteAsync(Request(
            imagePath,
            context: native,
            arguments: $$"""{"path":{{System.Text.Json.JsonSerializer.Serialize(imagePath)}},"transform":"zoom_in","scale":2.0}"""));

        Assert.IsTrue(result.Success, result.Error);
        var derivedArtifactId = ExtractArtifactId(result.Output);
        Assert.AreNotEqual(sourceArtifactId, derivedArtifactId);
        StringAssert.Contains(result.Output, "40x20");
        StringAssert.Contains(result.Output, "the source artifact is unchanged");

        var derived = await storage.ResolveLocalFileAsync("default", derivedArtifactId, CancellationToken.None);
        Assert.IsNotNull(derived);
        Assert.AreEqual(40, derived.Width);
        Assert.AreEqual(20, derived.Height);
        var sourceAfter = await storage.ResolveLocalFileAsync("default", sourceArtifactId, CancellationToken.None);
        Assert.IsNotNull(sourceAfter);
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(sourceAfter.Path));
    }

    [TestMethod]
    public async Task Transform_ZoomOut_ScalesDownToDerivedArtifact()
    {
        var imagePath = CreateTestImage(40, 20, SKEncodedImageFormat.Png, SKColors.Red, SKColors.Blue);
        var root = Path.GetDirectoryName(imagePath)!;
        var (_, storage) = await CreateStorageAsync(root);

        var tool = CreateTool(storage);
        var result = await tool.ExecuteAsync(Request(
            imagePath,
            context: Context(callerSnapshot: Snapshot(vision: true, protocol: "responses")),
            arguments: $$"""{"path":{{System.Text.Json.JsonSerializer.Serialize(imagePath)}},"transform":"zoom_out","scale":0.5}"""));

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "20x10");
    }

    [TestMethod]
    public async Task Transform_Crop_ExtractsExactRectangleWithContent()
    {
        // 40x10 左红右蓝：裁剪右半（20,0,20,10）后必须只剩蓝色 → 像素级内容证明
        var imagePath = CreateTestImage(40, 10, SKEncodedImageFormat.Png, SKColors.Red, SKColors.Blue);
        var root = Path.GetDirectoryName(imagePath)!;
        var (_, storage) = await CreateStorageAsync(root);

        var tool = CreateTool(storage);
        var result = await tool.ExecuteAsync(Request(
            imagePath,
            context: Context(callerSnapshot: Snapshot(vision: true, protocol: "responses")),
            arguments: $$"""{"path":{{System.Text.Json.JsonSerializer.Serialize(imagePath)}},"transform":"crop","cropX":20,"cropY":0,"cropWidth":20,"cropHeight":10}"""));

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "20x10");
        var artifactId = ExtractArtifactId(result.Output);
        var local = await storage.ResolveLocalFileAsync("default", artifactId, CancellationToken.None);
        Assert.IsNotNull(local);
        using var bitmap = SKBitmap.Decode(File.ReadAllBytes(local.Path));
        Assert.AreEqual(20, bitmap.Width);
        Assert.AreEqual(10, bitmap.Height);
        Assert.IsTrue(SKColors.Blue == bitmap.GetPixel(10, 5));
    }

    [TestMethod]
    public async Task Transform_Rotate90_SwapsDimensionsInDerivedArtifact()
    {
        var imagePath = CreateTestImage(40, 10, SKEncodedImageFormat.Png, SKColors.Red, SKColors.Blue);
        var root = Path.GetDirectoryName(imagePath)!;
        var (_, storage) = await CreateStorageAsync(root);

        var tool = CreateTool(storage);
        var result = await tool.ExecuteAsync(Request(
            imagePath,
            context: Context(callerSnapshot: Snapshot(vision: true, protocol: "responses")),
            arguments: $$"""{"path":{{System.Text.Json.JsonSerializer.Serialize(imagePath)}},"transform":"rotate","rotation":90}"""));

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "10x40");
        var artifactId = ExtractArtifactId(result.Output);
        var local = await storage.ResolveLocalFileAsync("default", artifactId, CancellationToken.None);
        Assert.IsNotNull(local);
        using var bitmap = SKBitmap.Decode(File.ReadAllBytes(local.Path));
        Assert.AreEqual(10, bitmap.Width);
        Assert.AreEqual(40, bitmap.Height);
    }

    [TestMethod]
    public async Task Transform_WebPSource_ProducesNativeDerivedImageAndPreservesOriginal()
    {
        var imagePath = CreateTestImage(20, 10, SKEncodedImageFormat.Webp, SKColors.Red, SKColors.Blue);
        var root = Path.GetDirectoryName(imagePath)!;
        var originalBytes = File.ReadAllBytes(imagePath);
        var (_, storage) = await CreateStorageAsync(root);

        var tool = CreateTool(storage);
        var result = await tool.ExecuteAsync(Request(
            imagePath,
            context: Context(callerSnapshot: Snapshot(vision: true, protocol: "responses")),
            arguments: $$"""{"path":{{System.Text.Json.JsonSerializer.Serialize(imagePath)}},"transform":"zoom_in","scale":2.0}"""));

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsInstanceOfType<LlmImagePart>(result.ToolContentParts![0]);
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(imagePath));
        Assert.IsTrue(File.Exists(imagePath));
    }

    [TestMethod]
    public async Task Transform_InvalidParams_ReturnStableErrorCodesWithoutPathLeak()
    {
        var imagePath = CreateTestImage(20, 10, SKEncodedImageFormat.Png, SKColors.Red, SKColors.Blue);
        var root = Path.GetDirectoryName(imagePath)!;
        var (_, storage) = await CreateStorageAsync(root);

        var tool = CreateTool(storage);
        var context = Context(callerSnapshot: Snapshot(vision: true, protocol: "responses"));
        var pathJson = System.Text.Json.JsonSerializer.Serialize(imagePath);
        string[] payloads =
        [
            $$"""{"path":{{pathJson}},"transform":"flip"}""",
            $$"""{"path":{{pathJson}},"transform":"zoom_in"}""",
            $$"""{"path":{{pathJson}},"transform":"rotate","rotation":45}""",
            $$"""{"path":{{pathJson}},"transform":"zoom_in","scale":2.0,"rotation":90}""",
            $$"""{"path":{{pathJson}},"transform":"crop","cropX":25,"cropY":0,"cropWidth":20,"cropHeight":10}""",
        ];

        foreach (var payload in payloads)
        {
            var result = await tool.ExecuteAsync(Request(imagePath, context, payload));
            Assert.IsFalse(result.Success, payload);
            StringAssert.Contains(result.Error, "vision_source_invalid");
            Assert.IsFalse(result.Error!.Contains(root, StringComparison.Ordinal), "error must not leak the host path");
        }
    }

    private static string CreateTestImage(
        int width,
        int height,
        SKEncodedImageFormat format,
        SKColor leftColor,
        SKColor rightColor)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        using (var left = new SKPaint { Color = leftColor })
            canvas.DrawRect(0, 0, width / 2f, height, left);
        using (var right = new SKPaint { Color = rightColor })
            canvas.DrawRect(width / 2f, 0, width - width / 2f, height, right);
        using var image = surface.Snapshot();
        using var encoded = image.Encode(format, 90);
        var root = Path.Combine(Path.GetTempPath(), $"pudding-image-reader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var extension = format switch
        {
            SKEncodedImageFormat.Jpeg => "jpg",
            SKEncodedImageFormat.Webp => "webp",
            _ => "png",
        };
        var imagePath = Path.Combine(root, $"sample-{width}x{height}.{extension}");
        File.WriteAllBytes(imagePath, encoded.ToArray());
        return imagePath;
    }

    private static string ExtractArtifactId(string output)
    {
        const string marker = "(artifact:";
        var start = output.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = output.IndexOf(',', start);
        return output[start..end].Trim();
    }

    private static ImageReaderTool CreateTool(
        VisionArtifactStorageService storage)
    {
        var sourceResolver = new ImageReaderSourceResolver(
            storage,
            null,
            NullLogger<ImageReaderSourceResolver>.Instance);
        return new ImageReaderTool(
            sourceResolver,
            storage,
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
        var manifest = new
        {
            AgentInstanceId = agentId,
            TemplateId = "template-1",
            WorkspaceId = "default",
            visionHelperModel,
            PreferredProviderId = preferredProviderId,
            PreferredModelId = preferredModelId,
        };
        await File.WriteAllTextAsync(
            Path.Combine(instanceRoot, "manifest.json"),
            System.Text.Json.JsonSerializer.Serialize(manifest));
    }

    [TestMethod]
    public void ImageReader_Is_AutoAllowed_After_Low_Reclassification()
    {
        // 2026-08-28 裁定：image_reader 纯只读（ReadOnly 标注），无写/删用户数据，由 High 降为 Low ⇒ AutoAllowed 免审直通。
        var root = Path.Combine(Path.GetTempPath(), $"pudding-image-reader-perm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var (_, storage) = CreateStorageAsync(root).GetAwaiter().GetResult();
        var tool = CreateTool(storage);

        var descriptor = tool.Descriptor;
        var decision = new PuddingRuntime.Services.Tools.ToolPermissionPolicyService().Classify(descriptor);

        Assert.AreEqual(PuddingCode.Models.ToolPermissionLevel.Low, descriptor.PermissionLevel);
        Assert.IsTrue(descriptor.Safety.HasFlag(PuddingCode.Tools.ToolSafetyFlags.ReadOnly));
        Assert.AreEqual(PuddingCode.Tools.ToolPermissionTier.AutoAllowed, decision.Tier);
        Assert.IsFalse(decision.RequiresRuntimeAuthorization);
    }
}
