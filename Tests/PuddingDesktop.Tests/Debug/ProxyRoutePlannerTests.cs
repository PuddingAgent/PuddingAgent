using PuddingDesktop.Debug;

namespace PuddingDesktop.Tests.Debug;

public sealed class ProxyRoutePlannerTests
{
    [Theory]
    [InlineData("/api/workspaces")]
    [InlineData("/api")]
    [InlineData("/apifoo")]
    [InlineData("/swagger/index.html")]
    [InlineData("/swagger")]
    [InlineData("/health")]
    [InlineData("/healthz")]
    [InlineData("/health/ready")]
    [InlineData("/metrics")]
    [InlineData("/assets/agent-avatars/a.png")]
    [InlineData("/connectors/x")]
    [InlineData("/session-events/abc")]
    public void IsBackendPath_MatchesBackendPrefixes(string path)
        => Assert.True(ProxyRoutePlanner.IsBackendPath(path));

    [Theory]
    [InlineData("/admin/user/login")]
    [InlineData("/admin/")]
    [InlineData("/admin")]
    [InlineData("/")]
    [InlineData("/desktop/browser-bridge")]
    [InlineData("/internal/desktop/shutdown")]
    public void IsBackendPath_LeavesNonBackendPathsToFrontend(string path)
        => Assert.False(ProxyRoutePlanner.IsBackendPath(path));

    [Theory]
    [InlineData("/admin/user/login", "/admin/")]
    [InlineData("/admin/settings/profile", "/admin/")]
    [InlineData("/admin/user/login?redirect=1", "/admin/")]
    [InlineData("/admin/", "/admin/")]
    [InlineData("/admin", "/admin")]
    [InlineData("/admin/app.css", "/admin/app.css")]
    [InlineData("/admin/app.css?v=1", "/admin/app.css?v=1")]
    [InlineData("/admin/static/logo.png", "/admin/static/logo.png")]
    [InlineData("/", "/")]
    [InlineData("/some/other?x=1", "/some/other?x=1")]
    public void GetSpaFallbackPath_MirrorsDevUpSemantics(string pathAndQuery, string expected)
        => Assert.Equal(expected, ProxyRoutePlanner.GetSpaFallbackPath(pathAndQuery));

    [Fact]
    public void GetEffectivePath_AppliesSpaFallbackOnlyToGetAndHead()
    {
        Assert.Equal("/admin/", ProxyRoutePlanner.GetEffectivePath("GET", "/admin/user/login"));
        Assert.Equal("/admin/", ProxyRoutePlanner.GetEffectivePath("HEAD", "/admin/user/login"));
        Assert.Equal("/admin/user/login", ProxyRoutePlanner.GetEffectivePath("POST", "/admin/user/login"));
        Assert.Equal("/admin/user/login", ProxyRoutePlanner.GetEffectivePath("PUT", "/admin/user/login"));
    }
}
