using System.Text.Json;
using PuddingDesktop.Configuration;

namespace PuddingDesktop.Tests.Debug;

public sealed class DesktopDebugSettingsJsonTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    [Fact]
    public void Deserialize_MissingDebugSection_UsesDefaults()
    {
        var settings = JsonSerializer.Deserialize<DesktopBootstrapSettings>("{}", JsonOptions);

        Assert.NotNull(settings);
        Assert.False(settings.Debug.Enabled);
        Assert.Equal(8000, settings.Debug.FrontendPort);
        Assert.Equal(80, settings.Debug.ProxyPort);
        Assert.Equal(180, settings.Debug.FrontendStartupTimeoutSeconds);
        Assert.Equal(300, settings.Debug.BackendBuildTimeoutSeconds);
        Assert.Null(settings.Debug.RepositoryRoot);
    }

    [Fact]
    public void Deserialize_ReadsDebugSection()
    {
        var settings = JsonSerializer.Deserialize<DesktopBootstrapSettings>(
            """
            {
              "dataRoot": "D:\\data",
              "debug": {
                "enabled": true,
                "repositoryRoot": "E:\\github\\AgentNetworkPlan\\PuddingAgent",
                "frontendPort": 8001,
                "proxyPort": 8081,
                "frontendWorkingDirectory": "E:\\fe",
                "backendProjectPath": "E:\\be\\PuddingAgent.csproj"
              }
            }
            """,
            JsonOptions);

        Assert.NotNull(settings);
        Assert.True(settings.Debug.Enabled);
        Assert.Equal("E:\\github\\AgentNetworkPlan\\PuddingAgent", settings.Debug.RepositoryRoot);
        Assert.Equal(8001, settings.Debug.FrontendPort);
        Assert.Equal(8081, settings.Debug.ProxyPort);
        Assert.Equal("E:\\fe", settings.Debug.FrontendWorkingDirectory);
        Assert.Equal("E:\\be\\PuddingAgent.csproj", settings.Debug.BackendProjectPath);
    }

    [Fact]
    public void Serialize_WritesCamelCaseDebugSection()
    {
        var json = JsonSerializer.Serialize(
            new DesktopBootstrapSettings
            {
                Debug = new DesktopDebugSettings { Enabled = true, ProxyPort = 80 },
            },
            JsonOptions);

        Assert.Contains("\"debug\"", json);
        Assert.Contains("\"enabled\": true", json);
        Assert.Contains("\"proxyPort\": 80", json);
        Assert.Contains("\"frontendPort\": 8000", json);
    }

    [Fact]
    public void Serialize_RoundTripsDebugSection()
    {
        var original = new DesktopBootstrapSettings
        {
            Debug = new DesktopDebugSettings
            {
                Enabled = true,
                RepositoryRoot = "E:\\repo",
                FrontendPort = 8000,
                ProxyPort = 80,
                FrontendStartupTimeoutSeconds = 240,
                BackendBuildTimeoutSeconds = 600,
            },
        };

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var parsed = JsonSerializer.Deserialize<DesktopBootstrapSettings>(json, JsonOptions);

        Assert.NotNull(parsed);
        Assert.Equal(original.Debug, parsed.Debug);
    }
}
