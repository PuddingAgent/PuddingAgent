using Pudding.CapabilityBroker;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace CapabilityBrokerTests;

/// <summary>端点命名与描述：按用户与产品实例隔离、可复现、不泄漏明文作用域。</summary>
public sealed class EndpointNamingTests
{
    [Fact]
    public void NamedPipeName_IsDeterministicAndScopedPerUserAndProductInstance()
    {
        var baseline = CapabilityEndpointNaming.NamedPipeName(@"DESKTOP-1\alice", "data-D--data");

        Assert.Equal(baseline, CapabilityEndpointNaming.NamedPipeName(@"DESKTOP-1\alice", "data-D--data"));
        Assert.NotEqual(baseline, CapabilityEndpointNaming.NamedPipeName(@"DESKTOP-1\bob", "data-D--data"));
        Assert.NotEqual(baseline, CapabilityEndpointNaming.NamedPipeName(@"DESKTOP-1\alice", "data-D--other"));

        // 不同 DataRoot 不能串接：名字必须不同。
        Assert.NotEqual(
            CapabilityEndpointNaming.NamedPipeName("alice", "root-A"),
            CapabilityEndpointNaming.NamedPipeName("alice", "root-B"));
    }

    [Fact]
    public void NamedPipeName_IsAValidPipeNameAndDoesNotLeakTheScope()
    {
        var name = CapabilityEndpointNaming.NamedPipeName(@"DESKTOP-1\alice", "D:\\data");

        Assert.True(DesktopCapabilityEndpoint.IsValidPipeName(name));
        Assert.StartsWith(CapabilityEndpointNaming.ProductPrefix, name, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data", name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\\', name);
        Assert.Equal(
            CapabilityEndpointNaming.ProductPrefix.Length + 1 + CapabilityEndpointNaming.ScopeHashLength,
            name.Length);
    }

    [Fact]
    public void ScopeHash_RequiresBothParts()
    {
        Assert.Throws<ArgumentException>(() => CapabilityEndpointNaming.ComputeScopeHash("  ", "root"));
        Assert.Throws<ArgumentException>(() => CapabilityEndpointNaming.ComputeScopeHash("alice", " "));
    }

    [Fact]
    public void NamedPipeEndpoint_PublishesTheDerivedNameAndCoreInstance()
    {
        var endpoint = CapabilityEndpointNaming.NamedPipeEndpoint("alice", "root-A", "core-42");

        Assert.Equal(DesktopCapabilityEndpointKind.NamedPipe, endpoint.Kind);
        Assert.Equal(CapabilityEndpointNaming.NamedPipeName("alice", "root-A"), endpoint.Address);
        Assert.Equal("core-42", endpoint.ServerInstanceId);
        Assert.Equal(DesktopProtocolVersion.Current, endpoint.ProtocolVersion);

        // 发布形式可被 Desktop 侧严格解析回来。
        Assert.True(DesktopCapabilityEndpoint.TryParse(endpoint.ToEndpointString(), out var parsed));
        Assert.Equal(endpoint, parsed);
    }

    [Fact]
    public void LoopbackAndTlsEndpoints_AreShapedAsExpected()
    {
        var loopback = CapabilityEndpointNaming.LoopbackEndpoint(5099, "core-1");
        Assert.Equal("http://127.0.0.1:5099", loopback.Address);

        var tls = CapabilityEndpointNaming.TlsEndpoint("core.example", 5099, "core-1");
        Assert.Equal("https://core.example:5099", tls.Address);

        Assert.Throws<ArgumentOutOfRangeException>(() => CapabilityEndpointNaming.LoopbackEndpoint(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CapabilityEndpointNaming.LoopbackEndpoint(70000));
        Assert.Throws<ArgumentException>(() => CapabilityEndpointNaming.TlsEndpoint("  ", 5099));
    }
}
