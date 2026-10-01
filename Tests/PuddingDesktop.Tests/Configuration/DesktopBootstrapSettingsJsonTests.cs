using System.Text.Json;
using PuddingDesktop.Configuration;
using PuddingDesktop.Runtime;

namespace PuddingDesktop.Tests.Configuration;

public sealed class DesktopBootstrapSettingsJsonTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    [Fact]
    public void Deserialize_AcceptsNamedCloseBehavior()
    {
        var settings = JsonSerializer.Deserialize<DesktopBootstrapSettings>(
            """{"closeBehavior":"ExitAndStopCore"}""",
            JsonOptions);

        Assert.NotNull(settings);
        Assert.Equal(DesktopCloseBehavior.ExitAndStopCore, settings.CloseBehavior);
    }

    [Fact]
    public void Serialize_WritesNamedCloseBehavior()
    {
        var json = JsonSerializer.Serialize(
            new DesktopBootstrapSettings
            {
                CloseBehavior = DesktopCloseBehavior.MinimizeToTray,
            },
            JsonOptions);

        Assert.Contains("\"closeBehavior\": \"MinimizeToTray\"", json);
    }

    [Fact]
    public void ToolWorkspace_DefaultsToCollapsedFortyFivePercent()
    {
        var settings = new DesktopBootstrapSettings();
        var workspace = settings.ToolWorkspace;

        Assert.Equal(0.45, workspace.WidthRatio);
        Assert.False(workspace.AutoExpandOnActivity);
        // The workspace never starts expanded, so no expansion flag is persisted.
        Assert.DoesNotContain("IsExpanded", JsonSerializer.Serialize(settings, JsonOptions));
    }

    [Fact]
    public void ToolWorkspace_RoundTripsThroughJson()
    {
        var json = JsonSerializer.Serialize(
            new DesktopBootstrapSettings
            {
                DataRoot = @"D:\data",
                ToolWorkspace = new DesktopToolWorkspaceSettings { WidthRatio = 0.62, AutoExpandOnActivity = true },
            },
            JsonOptions);

        var restored = JsonSerializer.Deserialize<DesktopBootstrapSettings>(json, JsonOptions);

        Assert.NotNull(restored);
        Assert.Equal(0.62, restored.ToolWorkspace.WidthRatio, 3);
        Assert.True(restored.ToolWorkspace.AutoExpandOnActivity);
        Assert.Equal(@"D:\data", restored.DataRoot);
    }

    [Fact]
    public void ToolWorkspace_MissingSectionUsesDefaults()
    {
        var settings = JsonSerializer.Deserialize<DesktopBootstrapSettings>(
            """{"dataRoot":"D:\\data"}""",
            JsonOptions);

        Assert.NotNull(settings);
        Assert.Equal(0.45, settings.ToolWorkspace.WidthRatio);
    }

    [Theory]
    [InlineData(0, 0.1)]
    [InlineData(9, 0.9)]
    [InlineData(double.NaN, 0.45)]
    public void ToolWorkspace_InvalidRatioIsNormalized(double stored, double expected)
    {
        Assert.Equal(expected, new DesktopToolWorkspaceSettings { WidthRatio = stored }.Normalize().WidthRatio);
    }
}
