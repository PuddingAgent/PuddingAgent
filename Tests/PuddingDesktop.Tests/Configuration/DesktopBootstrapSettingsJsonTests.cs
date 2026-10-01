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
    public void ToolWorkspace_IsUnconfiguredUntilTheUserChangesIt()
    {
        var settings = new DesktopBootstrapSettings();

        // 无 toolWorkspace 节 = 用户从未配置过（区别于「保存了默认值」）。
        // 未配置时的初始化比例由 Foundation 决定（§13.3：首次且只有工具首页 → 0.32），
        // 因此这里只钉住「未配置」这个语义本身。
        Assert.Null(settings.ToolWorkspace);
        // 已配置但未指定比例时，类型自身默认仍是通用 0.45
        Assert.Equal(0.45, new DesktopToolWorkspaceSettings().WidthRatio);
        Assert.False(new DesktopToolWorkspaceSettings().AutoExpandOnActivity);
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
        Assert.NotNull(restored.ToolWorkspace);
        Assert.Equal(0.62, restored.ToolWorkspace!.WidthRatio, 3);
        Assert.True(restored.ToolWorkspace.AutoExpandOnActivity);
        Assert.Equal(@"D:\data", restored.DataRoot);
    }

    [Fact]
    public void ToolWorkspace_MissingSectionMeansUnconfigured()
    {
        var settings = JsonSerializer.Deserialize<DesktopBootstrapSettings>(
            """{"dataRoot":"D:\\data"}""",
            JsonOptions);

        Assert.NotNull(settings);
        // 缺节不能伪装成「用户保存了 0.45」—— 否则初始化默认永远生效不了
        Assert.Null(settings.ToolWorkspace);
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
