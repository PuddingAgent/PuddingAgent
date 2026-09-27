using PuddingChat;
using Xunit;

public class WorkspaceSetupTests
{
    [Theory]
    [InlineData("../outside")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("-first")]
    [InlineData("")]
    public void InvalidWorkspaceIdIsRejected(string id) =>
        Assert.Throws<ArgumentException>(() => new WorkspaceSetupRequest(id, "Workspace", "Coder", null).Normalize());
    [Fact]
    public void NormalizePreservesExplicitModelAndTrimsNames()
    {
        var model = new ModelChoice("provider", "model", "Model");
        var request = new WorkspaceSetupRequest(" My-Code ", " Workspace ", " Coder ", model).Normalize();
        Assert.Equal("my-code", request.WorkspaceId); Assert.Equal("Workspace", request.WorkspaceName);
        Assert.Equal("Coder", request.RoleName); Assert.Same(model, request.Model);
    }
}
