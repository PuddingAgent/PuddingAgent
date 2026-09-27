using PuddingChat;
namespace PuddingChatTests;

public class RemoteImageReferenceTests
{
    [Fact]
    public void AcceptsHttpImagesWithoutEmbeddedCredentials()
    {
        Assert.Equal("example.com", RemoteImageReference.Resolve("https://example.com/image.png?size=small")!.Host);
        Assert.NotNull(RemoteImageReference.Resolve("http://example.com/image"));
    }
    [Fact]
    public void RejectsNonWebReferencesCredentialsAndOversizedInput()
    {
        foreach (var input in new[] { "file:///C:/image.png", "data:image/png;base64,a", "javascript:alert(1)", "//host/image.png",
            "https://user:password@host/image.png", "relative.png", "https://host/" + new string('a', 4096) })
            Assert.Null(RemoteImageReference.Resolve(input));
    }
}
