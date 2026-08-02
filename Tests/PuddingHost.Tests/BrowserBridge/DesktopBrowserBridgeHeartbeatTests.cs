using System.Net.WebSockets;
using System.Text.Json;
using PuddingBrowser.Protocol;

namespace PuddingHost.Tests.BrowserBridge;

public sealed class DesktopBrowserBridgeHeartbeatTests
{
    [Fact]
    public async Task DesktopHeartbeat_ReceivesCorrelatedAck()
    {
        await using var host = await BrowserBridgeTestHost.StartAsync();
        using var socket = await host.ConnectWebSocketAsync();
        Assert.True((await BrowserBridgeTestHost.CompleteHelloAsync(socket)).Accepted);

        var heartbeatId = Guid.NewGuid();
        await BrowserBridgeTestHost.SendEnvelopeAsync(socket, new BrowserBridgeEnvelope
        {
            MessageId = heartbeatId,
            Kind = BrowserBridgeMessageKind.Heartbeat,
            CreatedAt = host.Clock.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { })
        });

        var ack = await BrowserBridgeTestHost.ReceiveEnvelopeAsync(socket);
        Assert.Equal(BrowserBridgeMessageKind.HeartbeatAck, ack.Kind);
        Assert.Equal(heartbeatId, ack.CorrelationId);
    }

    [Fact]
    public async Task WatchdogTimeout_CancelsBlockedReceiveAndDetachesConnection()
    {
        await using var host = await BrowserBridgeTestHost.StartAsync();
        using var socket = await host.ConnectWebSocketAsync();
        Assert.True((await BrowserBridgeTestHost.CompleteHelloAsync(socket)).Accepted);
        await BrowserBridgeTestHost.WaitUntilAsync(() => host.Registry.IsDesktopConnected);

        host.Clock.Advance(TimeSpan.FromSeconds(46));

        await BrowserBridgeTestHost.WaitUntilAsync(() => host.Registry.Current is null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var buffer = new byte[BrowserBridgeProtocol.MaxMessageBytes];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cts.Token);
        } while (result.MessageType != WebSocketMessageType.Close);

        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.False(host.Broker.IsDesktopConnected);
    }
}
