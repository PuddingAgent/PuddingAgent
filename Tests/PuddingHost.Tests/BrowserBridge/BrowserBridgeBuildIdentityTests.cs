using System.Text.Json;
using PuddingBrowser.Protocol;

namespace PuddingHost.Tests.BrowserBridge;

/// <summary>
/// D9 build identity: handshake payload carries diagnosable identity; it never grants authority.
/// Wire round-trip + identity construction are covered here; per-process identities appear in handshake logs.
/// </summary>
public sealed class BrowserBridgeBuildIdentityTests
{
    [Fact]
    public void FromAssembly_ReportsNameAndMvidWithoutThrowing()
    {
        var identity = BrowserBridgeBuildIdentity.FromAssembly(typeof(BrowserBridgeProtocol).Assembly);

        Assert.False(string.IsNullOrWhiteSpace(identity.BuildId));
        Assert.Equal(typeof(BrowserBridgeProtocol).Assembly.GetName().Name, identity.ModuleName);
        Assert.True(Guid.TryParse(identity.ModuleVersionId, out _));
        Assert.Contains("mvid=", identity.Describe(), StringComparison.Ordinal);
        // Build did not stamp a source revision: report unknown instead of guessing.
        Assert.Null(identity.Dirty);
    }

    [Fact]
    public void HelloAndAckPayloads_RoundTripThroughTheWireSerializer()
    {
        var identity = new BrowserBridgeBuildIdentity
        {
            BuildId = "1.2.3+abc1234",
            SourceRevision = "abc1234",
            InformationalVersion = "1.2.3+abc1234",
            ModuleName = "PuddingAgent",
            ModuleVersionId = Guid.NewGuid().ToString("D"),
            ModulePath = @"C:\build\PuddingAgent.exe",
        };

        var hello = new BrowserBridgeHello
        {
            ProtocolVersion = BrowserBridgeProtocol.CurrentVersion,
            DesktopInstanceId = "desktop-1",
            Capabilities = ["context", "page"],
            Build = identity,
        };
        var ack = new BrowserBridgeHelloAck
        {
            ProtocolVersion = BrowserBridgeProtocol.CurrentVersion,
            Accepted = true,
            Build = identity,
        };

        var helloRoundTrip = RoundTrip(hello);
        Assert.Equal("abc1234", helloRoundTrip.Build?.SourceRevision);
        Assert.Equal("1.2.3+abc1234", helloRoundTrip.Build?.BuildId);

        var ackRoundTrip = RoundTrip(ack);
        Assert.True(ackRoundTrip.Accepted);
        Assert.Equal(identity.ModuleVersionId, ackRoundTrip.Build?.ModuleVersionId);
    }

    [Fact]
    public void DirtyRevision_IsParsedOnlyWhenTheBuildStampedIt()
    {
        var clean = BrowserBridgeBuildIdentity.FromInformationalVersion("1.2.3+abc1234", "PuddingAgent");
        Assert.Equal("abc1234", clean.SourceRevision);
        Assert.Null(clean.Dirty);
        Assert.Equal("PuddingAgent", clean.ModuleName);

        var dirty = BrowserBridgeBuildIdentity.FromInformationalVersion("1.2.3+abc1234.dirty", "PuddingAgent");
        Assert.Equal("abc1234", dirty.SourceRevision);
        Assert.True(dirty.Dirty);

        // Unstamped build: revision/dirty stay unknown (null), never inferred.
        var unstamped = BrowserBridgeBuildIdentity.FromInformationalVersion("1.2.3");
        Assert.Null(unstamped.SourceRevision);
        Assert.Null(unstamped.Dirty);
        Assert.Equal("unknown", BrowserBridgeBuildIdentity.FromInformationalVersion(null).BuildId);
    }

    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    private static BrowserBridgeHello RoundTrip(BrowserBridgeHello hello)
    {
        var envelope = new BrowserBridgeEnvelope
        {
            MessageId = Guid.NewGuid(),
            Kind = BrowserBridgeMessageKind.Hello,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(hello, WireJson),
        };
        var bytes = BrowserBridgeSerializer.Serialize(envelope);
        var decoded = BrowserBridgeSerializer.Deserialize(bytes.Span);
        return BrowserBridgeSerializer.DeserializePayload<BrowserBridgeHello>(decoded);
    }

    private static BrowserBridgeHelloAck RoundTrip(BrowserBridgeHelloAck ack)
    {
        var envelope = new BrowserBridgeEnvelope
        {
            MessageId = Guid.NewGuid(),
            Kind = BrowserBridgeMessageKind.HelloAck,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(ack, WireJson),
        };
        var bytes = BrowserBridgeSerializer.Serialize(envelope);
        var decoded = BrowserBridgeSerializer.Deserialize(bytes.Span);
        return BrowserBridgeSerializer.DeserializePayload<BrowserBridgeHelloAck>(decoded);
    }
}
