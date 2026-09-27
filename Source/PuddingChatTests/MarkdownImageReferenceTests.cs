namespace PuddingChatTests;

public sealed class MarkdownImageReferenceTests
{
    private const string Id = "vision-0123456789abcdef0123456789abcdef";
    [Fact]
    public void MapsWebGeneratedImageFormsToCanonicalId()
    {
        foreach (var source in new[] { Id, " " + Id.ToUpperInvariant() + " ", Id + ".png", @"D:\artifacts\" + Id + ".webp", "/artifacts/" + Id + ".jpeg" })
            Assert.Equal(Id, PuddingChat.MarkdownImageReference.Resolve(source, "team"));
    }
    [Fact]
    public void ApiPresentationReferencesMustMatchCurrentWorkspaceExactly()
    {
        Assert.Equal(Id, PuddingChat.MarkdownImageReference.Resolve("/api/workspaces/team%20one/vision-artifacts/" + Id, "team one"));
        Assert.Null(PuddingChat.MarkdownImageReference.Resolve("/api/workspaces/other/vision-artifacts/" + Id, "team"));
    }
    [Fact]
    public void DoesNotTreatRemoteLinksOrArbitraryFilesAsArtifactIds()
    {
        foreach (var source in new[] { "https://host/" + Id + ".png", "file:///" + Id + ".png", "//host/" + Id + ".png", "data:image/png;base64,123", @"D:\secret.png", Id + "\n" + Id, Id + ".png?key=value", "vision-not-an-id", Id + ".svg" })
            Assert.Null(PuddingChat.MarkdownImageReference.Resolve(source, "team"));
    }
}
