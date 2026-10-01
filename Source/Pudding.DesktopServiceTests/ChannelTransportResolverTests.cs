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
