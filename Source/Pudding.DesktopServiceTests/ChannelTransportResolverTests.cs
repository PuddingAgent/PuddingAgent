using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>Core 发布的端点描述 → Desktop 传输：拒绝而不是容错，且不携带凭据。</summary>
public sealed class ChannelTransportResolverTests
{
    [Fact]
    public void Resolve_MapsEveryEndpointKind()
    {
        var pipe = DesktopChannelTransportResolver.Resolve(
            DesktopCapabilityEndpoint.NamedPipe("pudding-capability-abc", 1, "core-1"));
        Assert.True(pipe.IsSuccess);
        Assert.Equal(DesktopChannelTransportKind.NamedPipe, pipe.Value.Kind);
        Assert.Equal("pudding-capability-abc", pipe.Value.Address);

        var loopback = DesktopChannelTransportResolver.Resolve(
            DesktopCapabilityEndpoint.LoopbackHttp2(new Uri("http://127.0.0.1:5099"), 1));
        Assert.Equal(DesktopChannelTransportKind.LoopbackHttp2, loopback.Value.Kind);

        var tls = DesktopChannelTransportResolver.Resolve(
            DesktopCapabilityEndpoint.Tls(new Uri("https://core.example:5099"), 1));
        Assert.Equal(DesktopChannelTransportKind.Tls, tls.Value.Kind);
    }

    [Fact]
    public void Resolve_RejectsUnsupportedProtocolVersion()
    {
        var endpoint = DesktopCapabilityEndpoint.NamedPipe("pudding", DesktopProtocolVersion.Current + 1);

        var result = DesktopChannelTransportResolver.Resolve(endpoint);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.UnsupportedCapability, result.Error.Code);
    }

    [Fact]
    public void Resolve_RejectsMissingEndpoint()
    {
        var result = DesktopChannelTransportResolver.Resolve(null);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, result.Error.Code);
    }

    [Fact]
    public void ResolveFromText_RoundTripsThePublishedDescription()
    {
        var endpoint = DesktopCapabilityEndpoint.NamedPipe("pudding-capability-deadbeef", 1, "core-9");

        var result = DesktopChannelTransportResolver.ResolveFromText(endpoint.ToEndpointString());

        Assert.True(result.IsSuccess);
        Assert.Equal(DesktopChannelTransportKind.NamedPipe, result.Value.Kind);
        Assert.Equal(endpoint.Address, result.Value.Address);
    }

    [Fact]
    public void ResolveFromText_FailsClosedOnMalformedText()
    {
        foreach (var text in new string?[]
                 {
                     null, string.Empty, "named-pipe:pudding", "carrier-pigeon:pudding|1|",
                     "loopback-h2c:http://10.0.0.5:1|1|", "tls:http://core.example:1|1|",
                 })
        {
            var result = DesktopChannelTransportResolver.ResolveFromText(text);
            Assert.True(result.IsFailure, $"should reject '{text}'");
            Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, result.Error.Code);
        }
    }

    [Fact]
    public void ResolvedTransport_ExposesNoCredentials()
    {
        var transport = DesktopChannelTransportResolver.Resolve(
            DesktopCapabilityEndpoint.NamedPipe("pudding-capability-abc", 1, "core-1")).Value;

        Assert.DoesNotContain("core-1", transport.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("=", transport.ToString(), StringComparison.Ordinal);
        Assert.Equal("NamedPipe:pudding-capability-abc", transport.ToString());
    }
}
/// <summary>Desktop 侧能力通道设置：默认关闭、声明集合与策略表一致、传输解析不做回退。</summary>
public sealed class DesktopCapabilityChannelSettingsTests
{


    [Fact]
    public void DefaultConfiguration_KeepsTheLegacyBridgeSelected()
    {
        var settings = DesktopCapabilityChannelSettings.Disabled;

        Assert.False(settings.Enabled);
        Assert.Equal("default", settings.DesktopId);
        Assert.Equal(DesktopCapabilityChannelSettings.DefaultControlTokenHeader, settings.ControlTokenHeader);

        // 未启用时两个入口都必须明确失败，不得静默降级到能力通道。
        Assert.True(settings.CreateConnectionOptions(new DesktopProcessInstanceId("proc-1")).IsFailure);
        Assert.True(settings.ResolveTransportFromDescription("named-pipe:pudding-x|1|").IsFailure);
    }

    [Fact]
    public void EnabledConfiguration_BuildsConnectionOptions()
    {
        var settings = new DesktopCapabilityChannelSettings
        {
            Enabled = true,
            DesktopId = "desk-7",
            HandshakeTimeoutSeconds = 20,
        };

        var options = settings.CreateConnectionOptions(
            new DesktopProcessInstanceId("proc-1"), DesktopChannelAuthentication.StaticHeader("x-token", "secret"));

        Assert.True(options.IsSuccess);
        Assert.Equal(new DesktopInstanceId("desk-7"), options.Value.DesktopId);
        Assert.Equal(DesktopCapabilityChannelSettings.DeclaredCapabilities, options.Value.SupportedCapabilities);
        Assert.Equal(TimeSpan.FromSeconds(20), options.Value.HandshakeTimeout);
        Assert.NotNull(options.Value.Authentication);
    }

    [Fact]
    public void DeclaredCapabilities_AllHaveAnAdmissionRule()
    {
        // 方向很重要：**已声明的必须都有显式准入规则**——否则会出现「未受管控的能力」。
        // 反过来不成立：策略表包含预留能力（dialog/file_picker/clipboard 有准入行但尚未实现、
        // 因而不在声明集合里），这是有意的「目录 ⊇ 已实现能力」形态。
        var admitted = DesktopCapabilityPolicy.Snapshot.Keys.Aggregate(DesktopCapability.None, (all, one) => all | one);
        var ungated = DesktopCapabilityChannelSettings.DeclaredCapabilities & ~admitted;

        Assert.Equal(DesktopCapability.None, ungated);

        // 预留能力确实还没被声明（改这条断言等于宣布它们已实现，应同时补 payload 与探针断言）。
        Assert.False(DesktopCapabilityChannelSettings.DeclaredCapabilities.HasFlag(DesktopCapability.ShellDialog));
        Assert.False(DesktopCapabilityChannelSettings.DeclaredCapabilities.HasFlag(DesktopCapability.ShellClipboard));
    }

    [Fact]
    public void EnabledConfiguration_RejectsOutOfRangeTimeout()
    {
        var settings = new DesktopCapabilityChannelSettings { Enabled = true, HandshakeTimeoutSeconds = 0 };

        var options = settings.CreateConnectionOptions(new DesktopProcessInstanceId("proc-1"));

        Assert.True(options.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, options.Error.Code);
    }

    [Fact]
    public void EnabledConfiguration_ResolvesThePublishedEndpoint()
    {
        var settings = new DesktopCapabilityChannelSettings { Enabled = true };

        var namedPipe = settings.ResolveTransportFromDescription("named-pipe:pudding-capability-abc|1|core-1");
        Assert.True(namedPipe.IsSuccess);
        Assert.Equal(DesktopChannelTransportKind.NamedPipe, namedPipe.Value.Kind);

        // 描述不可解析时明确失败（不做跨传输回退）。
        Assert.True(settings.ResolveTransportFromDescription("carrier-pigeon:x|1|").IsFailure);
    }
}
