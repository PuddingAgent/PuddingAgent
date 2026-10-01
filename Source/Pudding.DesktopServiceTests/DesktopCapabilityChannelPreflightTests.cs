using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>Desktop 侧"要不要启动能力通道"：默认关闭、描述不可用即不启动、绝不跨传输回退。</summary>
public sealed class DesktopCapabilityChannelPreflightTests
{
    private static readonly DesktopProcessInstanceId Process = new("desktop-process-1");

    [Fact]
    public void DisabledSettingsKeepTheLegacyBridge()
    {
        var report = DesktopCapabilityChannelPreflight.Evaluate(
            DesktopCapabilityChannelSettings.Disabled, Process, authentication: null, endpointDescription: null);

        Assert.False(report.ShouldStart);
        Assert.Contains("Enabled is false", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingOrUnusableDescriptionMeansNoStartAndNoFallback()
    {
        var settings = new DesktopCapabilityChannelSettings { Enabled = true };

        var missing = DesktopCapabilityChannelPreflight.Evaluate(settings, Process, null, endpointDescription: null);
        Assert.False(missing.ShouldStart);
        Assert.Contains("missing or not usable", missing.Summary, StringComparison.Ordinal);

        var unusable = DesktopCapabilityChannelPreflight.Evaluate(settings, Process, null, endpointDescription: "carrier-pigeon:x|1|");
        Assert.False(unusable.ShouldStart);
    }

    [Fact]
    public void EnabledSettingsWithAGoodDescriptionStartOnThatTransport()
    {
        var settings = new DesktopCapabilityChannelSettings { Enabled = true };

        var report = DesktopCapabilityChannelPreflight.Evaluate(
            settings, Process, authentication: null, endpointDescription: "named-pipe:pudding-capability-abc|1|core-42");

        Assert.True(report.ShouldStart);
        Assert.Contains("declared capabilities:", string.Join(";", report.Notes), StringComparison.Ordinal);
    }

    [Fact]
    public void NullSettingsAreTreatedAsDisabled()
    {
        var report = DesktopCapabilityChannelPreflight.Evaluate(null, Process, null, "named-pipe:x|1|core-1");

        Assert.False(report.ShouldStart);
    }
}