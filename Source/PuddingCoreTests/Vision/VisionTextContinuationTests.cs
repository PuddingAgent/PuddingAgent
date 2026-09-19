using PuddingCode.Core;
using PuddingCode.Models;

namespace PuddingCoreTests;

[TestClass]
public sealed class VisionTextContinuationTests
{
    [TestMethod]
    public void HistoricalOverflowProjectsReferencesAndPreservesCurrentAndOriginals()
    {
        var old = new ChatMessage(ChatRole.Tool, "result", ToolCallId: "call-1",
            ContentParts: Enumerable.Range(0, 601).Select(i => (LlmContentPart)new LlmImagePart($"image-{i}")).ToArray());
        var current = new ChatMessage(ChatRole.User, "continue in text") { SourceContentHash = "current" };
        var recovery = new VisionTextContinuation("current", 600);
        var prepared = recovery.Prepare([old, current]);
        Assert.IsTrue(recovery.Active);
        Assert.HasCount(601, ChatMessageMultimodalNormalizer.GetImageParts(old));
        Assert.IsEmpty(ChatMessageMultimodalNormalizer.GetImageParts(prepared[0]));
        StringAssert.Contains(prepared[0].Content!, "未发送像素");
        Assert.AreEqual("call-1", prepared[0].ToolCallId);
        Assert.AreSame(current, prepared[1]);
        var newTool = new ChatMessage(ChatRole.Tool, "new", ToolCallId: "call-2", VisualArtifactIds: ["new-image"]);
        var next = recovery.Prepare([old, current, newTool]);
        Assert.AreSame(newTool, next[2]);
        Assert.HasCount(1, ChatMessageMultimodalNormalizer.GetImageParts(next[2]));
    }

    [TestMethod]
    public void CurrentImagesOrMissingIdentityNeverActivateRecovery()
    {
        var old = new ChatMessage(ChatRole.User, "old", VisualArtifactIds: ["old"]);
        var current = new ChatMessage(ChatRole.User, "inspect", VisualArtifactIds: ["new"]) { SourceContentHash = "current" };
        Assert.IsFalse(new VisionTextContinuation("current", 1).TryActivate([old, current]));
        Assert.IsFalse(new VisionTextContinuation(null, 1).TryActivate([old, current]));
        var text = current with { VisualArtifactIds = null };
        Assert.IsFalse(new VisionTextContinuation("current", 1).TryActivate([old, text, current with { Role = ChatRole.Tool }]));
    }

    [TestMethod]
    public void HistoricalPreparationFailureCanRetryTextOnce()
    {
        var messages = new[] {
            new ChatMessage(ChatRole.User, "old", VisualArtifactIds: ["missing"]),
            new ChatMessage(ChatRole.User, "continue") { SourceContentHash = "current" }
        };
        var recovery = new VisionTextContinuation("current", 600);
        Assert.IsFalse(recovery.Active);
        Assert.IsTrue(recovery.TryActivate(messages));
        Assert.IsFalse(recovery.TryActivate(messages));
        Assert.IsEmpty(ChatMessageMultimodalNormalizer.GetImageParts(recovery.Prepare(messages)[0]));
    }
}
