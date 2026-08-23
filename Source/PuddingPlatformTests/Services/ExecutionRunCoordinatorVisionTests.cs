using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingPlatform.Services.AgentChat;

namespace PuddingPlatformTests.Services;

/// <summary>
/// ADR-077 V0：附件提示合同 — 视觉模型原生看图（无第二次模型调用、无本地路径泄漏）；
/// 文本模型只收 artifact:// 占位与 image_reader 显式调用引导（自动预观察旁路已删除）。
/// </summary>
[TestClass]
public class ExecutionRunCoordinatorVisionTests
{
    private const string ArtifactId = "vision-0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void BuildMessageText_VisionModel_UsesNativeNoticeWithoutPathsOrObservation()
    {
        var text = ExecutionRunCoordinator.BuildMessageText(
            "比较两张截图",
            [ArtifactId, "vision-fedcba9876543210fedcba9876543210"],
            primaryModelSupportsVision: true);

        Contains(text, "native image parts");
        NotContains(text, "Platform-provided visual observation");
        NotContains(text, @":\");
        NotContains(text, "image_reader");
        Contains(text, "untrusted user-supplied media content");
    }

    [TestMethod]
    public void BuildMessageText_TextModel_UsesArtifactPlaceholderAndExplicitImageReaderGuidance()
    {
        var text = ExecutionRunCoordinator.BuildMessageText(
            "这两张图有什么区别",
            [ArtifactId],
            primaryModelSupportsVision: false);

        Contains(text, $"artifact://{ArtifactId}");
        Contains(text, "cannot view images natively");
        Contains(text, "image_reader");
        NotContains(text, "Platform-provided visual observation");
        NotContains(text, @":\");
    }

    [TestMethod]
    public void BuildMessageText_NoImages_ReturnsContentUnchanged()
    {
        var text = ExecutionRunCoordinator.BuildMessageText(
            "纯文本问题",
            null,
            primaryModelSupportsVision: false);
        Assert.AreEqual("纯文本问题", text);
    }

    [TestMethod]
    public void ContentValidator_AcceptsTypedImageParts_AndAllowsPureImageTurn()
    {
        var content = new List<ContentPart>
        {
            new() { Type = "text", Text = "看图" },
            new() { Type = "image", ArtifactId = ArtifactId },
            new() { Type = "image", ArtifactId = "vision-ffffffffffffffffffffffffffffffff", Detail = "low" },
        };

        Assert.IsNull(ConversationContentValidator.Validate(content));

        var parts = ConversationContentValidator.ToLlmContentParts(content, out var flattened);
        Assert.AreEqual("看图", flattened);
        Assert.AreEqual(3, parts!.Count);
        var images = parts.OfType<LlmImagePart>().ToList();
        Assert.AreEqual(2, images.Count);
        Assert.AreEqual(VisionContentPartDetails.Original, images[0].Detail);
        Assert.AreEqual(VisionContentPartDetails.Low, images[1].Detail);
    }

    [TestMethod]
    [DataRow("image", "bad-id", null)]
    [DataRow("image", "vision-ABC", null)]
    [DataRow("image", "vision-0123456789abcdef0123456789abcdef", "ultra")]
    [DataRow("file", null, null)]
    public void ContentValidator_RejectsInvalidImageParts(string type, string? artifactId, string? detail)
    {
        var content = new List<ContentPart>
        {
            new() { Type = type, ArtifactId = artifactId, Detail = detail },
        };
        Assert.IsNotNull(ConversationContentValidator.Validate(content));
    }

    [TestMethod]
    public void ContentValidator_RejectsEmptyTextPart_AndNineImages()
    {
        Assert.IsNotNull(ConversationContentValidator.Validate(
            [new ContentPart { Type = "text", Text = "  " }]));

        var nine = Enumerable.Range(0, 9)
            .Select(i => new ContentPart
            {
                Type = "image",
                ArtifactId = "vision-" + new string('0', 31) + i.ToString()[0],
            })
            .ToList();
        Assert.IsNotNull(ConversationContentValidator.Validate(nine));
    }

    private static void Contains(string text, string expected)
        => Assert.IsTrue(text.Contains(expected, StringComparison.Ordinal), $"expected to find: {expected}");

    private static void NotContains(string text, string unexpected)
        => Assert.IsFalse(text.Contains(unexpected, StringComparison.Ordinal), $"expected NOT to find: {unexpected}");
}
