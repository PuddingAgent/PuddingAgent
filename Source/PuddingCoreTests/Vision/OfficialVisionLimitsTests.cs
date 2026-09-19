using PuddingCode.Abstractions;
using PuddingCode.Core;
using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingCoreTests;

[TestClass]
public sealed class OfficialVisionLimitsTests
{
    [TestMethod]
    public void DefaultsUseDocumentedDeepSeekLimits()
    {
        var p = VisionRequestPolicy.Default;
        Assert.AreEqual(600, p.MaxImagesPerRequest);
        Assert.AreEqual(32L * 1024 * 1024, p.InlineMaxBytesPerImage);
        Assert.AreEqual(64L * 1024 * 1024, p.InlineMaxTotalBytes);
        Assert.AreEqual(48L * 1024 * 1024, p.InlineMaxTotalWireBytes);
        Assert.AreEqual(600, ConversationContentValidator.MaxImagesPerTurn);
    }

    [TestMethod]
    public async Task SixHundredImagesAcrossMessagesPass_TheNextImageIsRejected()
    {
        var budget = new VisualInputRequestBudget();
        var resolver = new TinyResolver();
        for (var batch = 0; batch < 6; batch++)
            await LlmVisualInputPlanner.PlanAsync("default",
                Enumerable.Range(0, 100).Select(i => new LlmImagePart($"image-{batch}-{i}")).ToArray(),
                resolver, budget: budget);
        Assert.AreEqual(600, budget.ImageCount);
        var error = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() =>
            LlmVisualInputPlanner.PlanAsync("default", [new LlmImagePart("image-601")], resolver, budget: budget));
        Assert.AreEqual(VisionErrorCodes.RequestLimitExceeded, error.Code);
    }

    [TestMethod]
    public async Task ImageAboveFormerTwoMegabyteThresholdDoesNotRequireFilesApi()
    {
        var plan = await LlmVisualInputPlanner.PlanAsync("default", [new LlmImagePart("image")], new LargeResolver());
        Assert.IsNotNull(plan.Images.Single().DataUri);
        Assert.IsNull(plan.Images.Single().FileId);
    }

    private sealed class TinyResolver : IVisualArtifactResolver
    {
        public Task<VisualArtifactResolveResult?> ResolveAsync(string workspaceId, string artifactId,
            CancellationToken ct = default, string detail = "original")
            => Task.FromResult<VisualArtifactResolveResult?>(new(artifactId, "data:image/png;base64,AAAA", "image/png"));
    }

    private sealed class LargeResolver : IVisualArtifactResolver
    {
        public Task<VisualArtifactResolveResult?> ResolveAsync(string workspaceId, string artifactId,
            CancellationToken ct = default, string detail = "original")
            => Task.FromResult<VisualArtifactResolveResult?>(new(artifactId, "data:image/png;base64," + new string('A', 4_000_000), "image/png"));
    }
}
