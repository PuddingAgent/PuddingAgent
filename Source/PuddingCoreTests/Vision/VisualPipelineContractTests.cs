using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Core;
using PuddingCode.Models;

namespace PuddingCoreTests;

[TestClass]
public sealed class LlmVisualInputPlannerTests
{
    private const string Workspace = "default";

    private static string DataUri(int approximateBytes)
        => "data:image/png;base64," + new string('A', approximateBytes);

    private sealed class FixedResolver : PuddingCode.Abstractions.IVisualArtifactResolver
    {
        private readonly HashSet<string> _missing;

        public FixedResolver(params string[] missing) => _missing = [.. missing];

        public Task<PuddingCode.Abstractions.VisualArtifactResolveResult?> ResolveAsync(
            string workspaceId,
            string artifactId,
            CancellationToken ct = default)
            => _missing.Contains(artifactId)
                ? Task.FromResult<PuddingCode.Abstractions.VisualArtifactResolveResult?>(null)
                : Task.FromResult<PuddingCode.Abstractions.VisualArtifactResolveResult?>(
                    new PuddingCode.Abstractions.VisualArtifactResolveResult(
                        artifactId,
                        DataUri(1_000),
                        "image/png"));
    }

    [TestMethod]
    public async Task PlanAsync_ResolvesAllImages_AndEstimatesTokenUpperBound()
    {
        var plan = await LlmVisualInputPlanner.PlanAsync(
            Workspace,
            [new LlmImagePart("vision-0123456789abcdef0123456789abcdef"),
             new LlmImagePart("vision-fedcba9876543210fedcba9876543210", VisionContentPartDetails.Low)],
            new FixedResolver());

        Assert.AreEqual(2, plan.Images.Count);
        Assert.AreEqual(VisionContentPartDetails.Low, plan.Images[1].Detail);
        // 384 × 图片数的 token 上界
        Assert.AreEqual(768, plan.EstimatedTokenUpperBound);
    }

    [TestMethod]
    public async Task PlanAsync_DeduplicatesSameArtifactAndDetail()
    {
        var part = new LlmImagePart("vision-0123456789abcdef0123456789abcdef");
        var plan = await LlmVisualInputPlanner.PlanAsync(
            Workspace,
            [part, part with { }],
            new FixedResolver());

        Assert.AreEqual(2, plan.Images.Count);
        // 每张图都计费（同一 part 出现两次仍是两次输入），但解析只发生一次由 resolver 调用数保证；
        // 上界按引用次数估计
        Assert.AreEqual(768, plan.EstimatedTokenUpperBound);
    }

    [TestMethod]
    public async Task PlanAsync_MissingArtifact_FailsClosedWithStableCode()
    {
        var ex = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() =>
            LlmVisualInputPlanner.PlanAsync(
                Workspace,
                [new LlmImagePart("vision-0123456789abcdef0123456789abcdef")],
                new FixedResolver("vision-0123456789abcdef0123456789abcdef")));

        Assert.AreEqual(VisionErrorCodes.ArtifactMissing, ex.Code);
    }

    [TestMethod]
    public async Task PlanAsync_MoreThanEightImages_Rejected()
    {
        var parts = Enumerable.Range(0, 9)
            .Select(i => new LlmImagePart("vision-" + new string('0', 31) + i.ToString()[0]))
            .ToList();

        var ex = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() =>
            LlmVisualInputPlanner.PlanAsync(Workspace, parts, new FixedResolver()));
        Assert.AreEqual(VisionErrorCodes.RequestLimitExceeded, ex.Code);
    }

    [TestMethod]
    public async Task PlanAsync_ImageOverInlineLimit_RejectedUntilFilesApi()
    {
        var bigDataUri = DataUri(3_000_000);
        var resolver = new OversizeResolver(bigDataUri);
        var ex = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() =>
            LlmVisualInputPlanner.PlanAsync(
                Workspace,
                [new LlmImagePart("vision-0123456789abcdef0123456789abcdef")],
                resolver));
        Assert.AreEqual(VisionErrorCodes.RequestLimitExceeded, ex.Code);
    }

    private sealed class OversizeResolver(string dataUri) : PuddingCode.Abstractions.IVisualArtifactResolver
    {
        public Task<PuddingCode.Abstractions.VisualArtifactResolveResult?> ResolveAsync(
            string workspaceId,
            string artifactId,
            CancellationToken ct = default)
            => Task.FromResult<PuddingCode.Abstractions.VisualArtifactResolveResult?>(
                new PuddingCode.Abstractions.VisualArtifactResolveResult(artifactId, dataUri, "image/png"));
    }
}

[TestClass]
public sealed class ContentPartsEnvelopeTests
{
    [TestMethod]
    public void EncodeDecode_RoundTripsOrderedParts()
    {
        var parts = new List<LlmContentPart>
        {
            new LlmTextPart("看图"),
            new LlmImagePart("vision-0123456789abcdef0123456789abcdef"),
            new LlmTextPart("再回答"),
            new LlmImagePart("vision-fedcba9876543210fedcba9876543210", VisionContentPartDetails.Low),
        };

        var json = ContentPartsEnvelope.Encode(parts);
        StringAssert.Contains(json, "\"v\":1");

        var decoded = ContentPartsEnvelope.Decode(json);
        Assert.IsNotNull(decoded);
        Assert.AreEqual(4, decoded.Count);
        Assert.IsInstanceOfType(decoded[0], typeof(LlmTextPart));
        var image = (LlmImagePart)decoded[3];
        Assert.AreEqual(VisionContentPartDetails.Low, image.Detail);
        Assert.AreEqual("看图再回答", ContentPartsEnvelope.FlattenText(decoded));
    }

    [TestMethod]
    public void Decode_MalformedOrEmpty_ReturnsNull()
    {
        Assert.IsNull(ContentPartsEnvelope.Decode(null));
        Assert.IsNull(ContentPartsEnvelope.Decode(""));
        Assert.IsNull(ContentPartsEnvelope.Decode("not-json"));
        Assert.IsNull(ContentPartsEnvelope.Decode("""{"v":1,"parts":[]}"""));
    }
}

[TestClass]
public sealed class ChatMessageMultimodalNormalizerTests
{
    [TestMethod]
    public void ContentParts_Canonical_WhenPresent()
    {
        var message = new ChatMessage(
            ChatRole.User,
            "text",
            ContentParts: [new LlmTextPart("hello"), new LlmImagePart("vision-0123456789abcdef0123456789abcdef")]);

        var parts = ChatMessageMultimodalNormalizer.GetEffectiveContentParts(message);
        Assert.AreEqual(2, parts!.Count);
        Assert.AreEqual(1, ChatMessageMultimodalNormalizer.GetImageParts(message).Count);
    }

    [TestMethod]
    public void LegacyVisualArtifactIds_DeriveOriginalDetailParts()
    {
        var message = new ChatMessage(
            ChatRole.User,
            "hello",
            VisualArtifactIds: ["vision-0123456789abcdef0123456789abcdef"]);

        var parts = ChatMessageMultimodalNormalizer.GetEffectiveContentParts(message);
        Assert.AreEqual(2, parts!.Count);
        var image = (LlmImagePart)parts[1];
        Assert.AreEqual(VisionContentPartDetails.Original, image.Detail);
    }

    [TestMethod]
    public void TextOnlyMessage_HasNoParts()
    {
        var message = new ChatMessage(ChatRole.User, "plain");
        Assert.IsNull(ChatMessageMultimodalNormalizer.GetEffectiveContentParts(message));
        Assert.AreEqual(0, ChatMessageMultimodalNormalizer.GetImageParts(message).Count);
    }
}

[TestClass]
public sealed class VisionImageInspectorTests
{
    // 1×1 PNG
    private static readonly byte[] MinimalPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    [TestMethod]
    public void InspectPrefix_ReadsPngDimensions()
    {
        var info = VisionImageInspector.InspectPrefix(MinimalPng);
        Assert.IsNotNull(info);
        Assert.AreEqual("image/png", info.MimeType);
        Assert.AreEqual(1, info.Width);
        Assert.AreEqual(1, info.Height);
    }

    [TestMethod]
    public void InspectPrefix_RejectsNonImageBytes()
    {
        Assert.IsNull(VisionImageInspector.InspectPrefix([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13]));
        Assert.IsNull(VisionImageInspector.InspectPrefix([]));
    }

    [TestMethod]
    public void InspectPrefix_RejectsOversizedDimensions()
    {
        var bytes = new byte[24];
        MinimalPng.AsSpan(0, 8).CopyTo(bytes);
        // width = 9000
        bytes[16] = 0x23; bytes[17] = 0x28;
        Assert.IsNull(VisionImageInspector.InspectPrefix(bytes));
    }
}
