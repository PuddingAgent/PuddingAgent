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

    [Fact]
    public void TryParseFrontendBody_ArtifactFields_ArePreserved()
    {
        const string json = """
            {
              "token": "tok",
              "artifactDirectory": "E:\\repo\\Source\\PuddingPlatformAdmin\\dist",
              "artifactIndexSha256": "def456"
            }
            """;

        var parsed = DesktopBootstrapHttpRequestParser.TryParseFrontendBody(
            json,
            out var token,
            out var artifactDirectory,
            out var artifactIndexSha256);

        Assert.True(parsed);
        Assert.Equal("tok", token);
        Assert.Equal(@"E:\repo\Source\PuddingPlatformAdmin\dist", artifactDirectory);
        Assert.Equal("def456", artifactIndexSha256);
    }

    [Fact]
    public void ControlRoutePaths_AreStableForAutomationClients()
    {
        Assert.Equal("/desktop/bootstrap/core/restart", DesktopBootstrapHttpEndpoint.CoreRestartPath);
        Assert.Equal(
            "/desktop/bootstrap/core/deploy-restart",
            DesktopBootstrapHttpEndpoint.CoreDeployRestartPath);
        Assert.Equal(
            "/desktop/bootstrap/frontend/build-deploy",
            DesktopBootstrapHttpEndpoint.FrontendBuildDeployPath);
        Assert.Equal("/desktop/bootstrap/frontend/load", DesktopBootstrapHttpEndpoint.FrontendLoadPath);
        Assert.Equal("/desktop/bootstrap/diagnostics", DesktopBootstrapHttpEndpoint.DiagnosticsPath);
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
