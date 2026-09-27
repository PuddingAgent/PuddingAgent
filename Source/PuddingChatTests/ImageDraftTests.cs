using PuddingChat;

namespace PuddingChatTests;

public sealed class ImageDraftTests
{
    private static AttachedImage Image(string id) => new(id, "image.png", "image/png", 10, 10);
    [Fact]
    public void ImageOnlySendRetriesFrozenReferencesAndKeepsNewImages()
    {
        var role = new RoleKey("w", "a"); var state = new ChatSelection(); state.Select(role);
        state.AddImage(role, Image("one"));
        var pending = state.Prepare("s"); Assert.Equal("", pending.Text);
        state.AddImage(role, Image("two"));
        Assert.Same(pending, state.Prepare("s")); Assert.Single(pending.Images!);
        state.Accept(pending); Assert.Equal("two", Assert.Single(state.Images).ArtifactId);
    }
    [Fact]
    public void LateImportAndReceiptRemainWithCapturedRole()
    {
        var a = new RoleKey("w", "a"); var b = new RoleKey("w", "b");
        var state = new ChatSelection(); state.Select(a); state.AddImage(a, Image("one")); var send = state.Prepare("s");
        state.Select(b); state.AddImage(a, Image("two")); Assert.Empty(state.Images);
        state.AddImage(b, Image("other")); state.Accept(send); Assert.Equal("other", Assert.Single(state.Images).ArtifactId);
        state.Select(a); Assert.Equal("two", Assert.Single(state.Images).ArtifactId);
        state.RemoveImage("two"); Assert.Empty(state.Images);
    }
    [Fact]
    public void CaptureBeforeSessionCreationDoesNotIncludeLaterImages()
    {
        var a = new RoleKey("w", "a"); var state = new ChatSelection(); state.Select(a);
        state.AddImage(a, Image("one")); var captured = state.Images;
        state.AddImage(a, Image("two")); var send = state.Prepare("s", "", captured);
        Assert.Equal("one", Assert.Single(send.Images!).ArtifactId);
        state.Clear(); state.Select(a); Assert.Empty(state.Images); Assert.Null(state.Pending);
    }
}
