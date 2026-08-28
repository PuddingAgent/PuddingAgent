using PuddingDesktop.Bootstrap;

namespace PuddingDesktop.Tests.Bootstrap;

public sealed class DesktopBootstrapHttpRequestParserTests
{
    [Fact]
    public void TryParseStartBody_DeploymentFields_ArePreserved()
    {
        const string json = """
            {
              "token": "tok",
              "requestedBy": "agent:test",
              "yolo": true,
              "deploymentMode": "prebuilt-artifact",
              "artifactDirectory": "E:\\repo\\.tmp-build\\core",
              "artifactAssemblySha256": "abc123"
            }
            """;

        var parsed = DesktopBootstrapHttpRequestParser.TryParseStartBody(
            json,
            out var token,
            out var requestedBy,
            out var yolo,
            out var deploymentMode,
            out var artifactDirectory,
            out var artifactAssemblySha256);

        Assert.True(parsed);
        Assert.Equal("tok", token);
        Assert.Equal("agent:test", requestedBy);
        Assert.True(yolo);
        Assert.Equal("prebuilt-artifact", deploymentMode);
        Assert.Equal(@"E:\repo\.tmp-build\core", artifactDirectory);
        Assert.Equal("abc123", artifactAssemblySha256);
    }

    [Theory]
    [InlineData(null, "desktop-build")]
    [InlineData("build", "desktop-build")]
    [InlineData("prebuilt_artifact", "prebuilt-artifact")]
    [InlineData("restart", "restart-only")]
    public void NormalizeDeploymentMode_SupportedAliases_ReturnCanonical(string? value, string expected)
        => Assert.Equal(expected, DesktopBootstrapSignalParser.NormalizeDeploymentMode(value));

    [Fact]
    public void NormalizeDeploymentMode_Unknown_ReturnsNull()
        => Assert.Null(DesktopBootstrapSignalParser.NormalizeDeploymentMode("hot-swap"));
}
