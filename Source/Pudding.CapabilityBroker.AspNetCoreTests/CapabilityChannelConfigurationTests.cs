using Microsoft.Extensions.Configuration;
using Pudding.CapabilityBroker.AspNetCore;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace CapabilityBrokerAspNetCoreTests;

/// <summary>
/// 配置绑定与端点描述派生：默认关闭、隔离命名、非法配置整体失败、描述不含凭据。
/// 这些是「重启窗口内只剩两行装配」的前提：所有可判定逻辑都在这里被独立验证。
/// </summary>
public sealed class CapabilityChannelConfigurationTests
{
    private static IConfiguration Configuration(params (string Key, string? Value)[] values)
    {
        var pairs = values.ToDictionary(
            entry => $"{CapabilityChannelConfiguration.SectionName}:{entry.Key}",
            entry => entry.Value!,
            StringComparer.OrdinalIgnoreCase);

        return new ConfigurationBuilder().AddInMemoryCollection(pairs).Build();
    }

    [Fact]
    public void DefaultConfiguration_IsDisabled()
    {
        var empty = CapabilityChannelConfiguration.Bind(new ConfigurationBuilder().Build());
        Assert.False(empty.Enabled);

        var absent = CapabilityChannelConfiguration.Bind(Configuration(("LoopbackPort", "5099")));
        Assert.False(absent.Enabled);
        Assert.Null(absent.Describe("user", "root"));

        var disabled = CapabilityChannelConfiguration.Bind(Configuration(("Enabled", "false")));
        Assert.False(disabled.Enabled);

        // 关闭时不允许被误用：构造选项必须显式失败。
        Assert.Throws<InvalidOperationException>(() => disabled.CreateOptions("user", "root"));
    }

    [Fact]
    public void EnabledConfiguration_BindsEveryField()
    {
        var config = CapabilityChannelConfiguration.Bind(Configuration(
            ("Enabled", "true"),
            ("Transport", "both"),
            ("DesktopId", "desk-7"),
            ("LoopbackPort", "5099"),
            ("Grantable", "webview.navigate, shell.status"),
            ("MaxInFlightPerConnection", "3"),
            ("MaxMessageBytes", "2097152"),
            ("HandshakeTimeoutSeconds", "5")));

        Assert.True(config.Enabled);
        Assert.Equal("desk-7", config.DesktopId);
        Assert.Equal(5099, config.LoopbackPort);
        Assert.Equal(3, config.MaxInFlightPerConnection);
        Assert.Equal(2097152, config.MaxMessageBytes);
        Assert.Equal(5, config.HandshakeTimeoutSeconds);

        var options = config.CreateOptions("user", "root", "core-1");
        Assert.Equal(new DesktopInstanceId("desk-7"), options.ExpectedDesktopId);
        Assert.Equal(DesktopCapability.WebViewNavigate | DesktopCapability.ShellStatus, options.Grantable);
        Assert.Equal(3, options.MaxInFlightPerConnection);
        Assert.Equal(TimeSpan.FromSeconds(5), options.HandshakeTimeout);
        Assert.Equal("core-1", options.CoreInstanceId);
    }

    [Fact]
    public void DerivedPipeName_IsScopedPerUserAndProductInstance()
    {
        var config = CapabilityChannelConfiguration.Bind(Configuration(("Enabled", "true")));

        var first = config.CreateOptions("alice", "data-root-A");
        var second = config.CreateOptions("alice", "data-root-B");

        Assert.NotNull(first.NamedPipeName);
        Assert.True(DesktopCapabilityEndpoint.IsValidPipeName(first.NamedPipeName));
        // 不同 DataRoot 不会串接。
        Assert.NotEqual(first.NamedPipeName, second.NamedPipeName);
    }

    [Fact]
    public void ExplicitPipeName_Wins()
    {
        var config = CapabilityChannelConfiguration.Bind(Configuration(
            ("Enabled", "true"), ("NamedPipeName", "pudding-capability-explicit")));

        var options = config.CreateOptions("alice", "root");
        Assert.Equal("pudding-capability-explicit", options.NamedPipeName);
        Assert.Equal(
            "pudding-capability-explicit",
            config.Describe("alice", "root", "core-1")!.Address);
    }

    [Fact]
    public void Describe_ProducesAParsablePublishableDescriptionWithoutCredentials()
    {
        var config = CapabilityChannelConfiguration.Bind(Configuration(("Enabled", "true")));

        var endpoint = config.Describe("alice", @"D:\data", "core-42");

        Assert.NotNull(endpoint);
        Assert.Equal(DesktopCapabilityEndpointKind.NamedPipe, endpoint!.Kind);
        Assert.Equal("core-42", endpoint.ServerInstanceId);
        Assert.True(DesktopCapabilityEndpoint.TryParse(endpoint.ToEndpointString(), out var parsed));
        Assert.Equal(endpoint, parsed);

        // 描述里不得出现用户作用域或 DataRoot 的任何片段。
        Assert.DoesNotContain("alice", endpoint.ToEndpointString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data", endpoint.ToEndpointString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Transport_DeterminesWhatIsListenedToAndPublished()
    {
        // 缺省 named-pipe：只监听管道，描述发布管道。
        var pipe = CapabilityChannelConfiguration.Bind(Configuration(("Enabled", "true")));
        var pipeOptions = pipe.CreateOptions("u", "r");
        Assert.NotNull(pipeOptions.NamedPipeName);
        Assert.Null(pipeOptions.LoopbackPort);
        Assert.Equal(DesktopCapabilityEndpointKind.NamedPipe, pipe.Describe("u", "r")!.Kind);

        // loopback-h2c：只监听回环，描述必须发布回环（否则 Desktop 会去连未监听的管道）。
        var loopback = CapabilityChannelConfiguration.Bind(Configuration(
            ("Enabled", "true"), ("Transport", "loopback-h2c"), ("LoopbackPort", "5099")));
        var loopbackOptions = loopback.CreateOptions("u", "r");
        Assert.Null(loopbackOptions.NamedPipeName);
        Assert.Equal(5099, loopbackOptions.LoopbackPort);
        var description = loopback.Describe("u", "r");
        Assert.Equal(DesktopCapabilityEndpointKind.LoopbackHttp2, description!.Kind);
        Assert.Equal("http://127.0.0.1:5099", description.Address);

        // both：两者都监听，描述发布产品默认的管道。
        var both = CapabilityChannelConfiguration.Bind(Configuration(
            ("Enabled", "true"), ("Transport", "both"), ("LoopbackPort", "5099")));
        var bothOptions = both.CreateOptions("u", "r");
        Assert.NotNull(bothOptions.NamedPipeName);
        Assert.Equal(5099, bothOptions.LoopbackPort);
        Assert.Equal(DesktopCapabilityEndpointKind.NamedPipe, both.Describe("u", "r")!.Kind);
    }

    [Fact]
    public void Transport_AndEndpointSettingsMustAgree()
    {
        // named-pipe 不接受 LoopbackPort。
        Assert.Throws<InvalidOperationException>(() => CapabilityChannelConfiguration
            .Bind(Configuration(("Enabled", "true"), ("LoopbackPort", "5099")))
            .CreateOptions("u", "r"));

        // loopback-h2c 必须给端口，且不接受 NamedPipeName。
        Assert.Throws<InvalidOperationException>(() => CapabilityChannelConfiguration
            .Bind(Configuration(("Enabled", "true"), ("Transport", "loopback-h2c")))
            .CreateOptions("u", "r"));
        Assert.Throws<InvalidOperationException>(() => CapabilityChannelConfiguration
            .Bind(Configuration(("Enabled", "true"), ("Transport", "loopback-h2c"), ("NamedPipeName", "pudding-x")))
            .CreateOptions("u", "r"));

        // 未知传输形态不静默回退。
        Assert.Throws<InvalidOperationException>(
            () => CapabilityChannelConfiguration.ParseTransport("carrier-pigeon"));
    }

    [Fact]
    public void InvalidConfiguration_FailsAsAWhole()
    {
        // 未登记能力线名：不静默忽略。
        Assert.Throws<InvalidOperationException>(
            () => CapabilityChannelConfiguration.ParseGrantable("webview.navigate,shell.telepathy"));

        // 非法管道名。
        var badPipe = CapabilityChannelConfiguration.Bind(Configuration(
            ("Enabled", "true"), ("NamedPipeName", @"\\.\pipe\bad")));
        Assert.Throws<InvalidOperationException>(() => badPipe.CreateOptions("u", "r"));

        // 越界消息上限。
        var badLimit = CapabilityChannelConfiguration.Bind(Configuration(
            ("Enabled", "true"), ("MaxMessageBytes", "1024")));
        Assert.Throws<ArgumentOutOfRangeException>(() => badLimit.CreateOptions("u", "r"));

        // 空的 Grantable。
        var emptyGrantable = CapabilityChannelConfiguration.Bind(Configuration(
            ("Enabled", "true"), ("Grantable", " , ")));
        Assert.Throws<InvalidOperationException>(() => emptyGrantable.CreateOptions("u", "r"));
    }

    [Fact]
    public void DefaultGrantable_CoversExactlyTheImplementedCapabilities()
    {
        var granted = CapabilityChannelConfiguration.ParseGrantable(null);

        Assert.Equal(CapabilityChannelConfiguration.DefaultGrantable, granted);

        // 目录里已登记但尚无 payload 的能力（dialog/picker/clipboard）不得被缺省授予。
        foreach (var descriptor in DesktopCapabilities.All)
        {
            var implemented = descriptor.Capability is
                DesktopCapability.WebViewNavigate or
                DesktopCapability.WebViewExecuteJavascript or
                DesktopCapability.WebViewPageState or
                DesktopCapability.ShellNotification or
                DesktopCapability.ShellStatus or
                DesktopCapability.BrowserSnapshot or
                DesktopCapability.BrowserLocate or
                DesktopCapability.BrowserInteract or
                DesktopCapability.BrowserWaitFor or
                DesktopCapability.BrowserContexts or
                DesktopCapability.BrowserTabs or
                DesktopCapability.ShellClipboard;

            Assert.Equal(implemented, granted.HasFlag(descriptor.Capability));
        }
    }
}
