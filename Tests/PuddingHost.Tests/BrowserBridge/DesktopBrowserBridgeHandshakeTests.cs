using System.Net.WebSockets;
using System.Text.Json;
using PuddingBrowser.Protocol;

namespace PuddingHost.Tests.BrowserBridge;

public sealed class DesktopBrowserBridgeHandshakeTests
{
    [Fact]
    public async Task AcceptedHello_MakesBrokerAvailableOnlyAfterAck()
    {
        await using var host = await BrowserBridgeTestHost.StartAsync();
        using var socket = await host.ConnectWebSocketAsync();

        Assert.False(host.Broker.IsDesktopConnected);
        var ack = await BrowserBridgeTestHost.CompleteHelloAsync(socket);

        Assert.True(ack.Accepted);
        Assert.True(host.Broker.IsDesktopConnected);
    }

    [Fact]
    public async Task FirstMessageOtherThanHello_IsRejected()
    {
        await using var host = await BrowserBridgeTestHost.StartAsync();
        using var socket = await host.ConnectWebSocketAsync();
        var firstMessageId = Guid.NewGuid();
        await BrowserBridgeTestHost.SendEnvelopeAsync(socket, new BrowserBridgeEnvelope
        {
            MessageId = firstMessageId,
            Kind = BrowserBridgeMessageKind.Heartbeat,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { })
        });

        var ackEnvelope = await BrowserBridgeTestHost.ReceiveEnvelopeAsync(socket);
        var ack = BrowserBridgeSerializer.DeserializePayload<BrowserBridgeHelloAck>(ackEnvelope);

        Assert.False(ack.Accepted);
        Assert.Equal(firstMessageId, ackEnvelope.CorrelationId);
        Assert.False(host.Broker.IsDesktopConnected);
    }

    [Fact]
    public async Task ProtocolMismatch_IsRejected()
    {
        await using var host = await BrowserBridgeTestHost.StartAsync();
        using var socket = await host.ConnectWebSocketAsync();
        await BrowserBridgeTestHost.SendEnvelopeAsync(
            socket,
            BrowserBridgeTestHost.HelloEnvelope(BrowserBridgeProtocol.CurrentVersion + 1));

        var ackEnvelope = await BrowserBridgeTestHost.ReceiveEnvelopeAsync(socket);
        var ack = BrowserBridgeSerializer.DeserializePayload<BrowserBridgeHelloAck>(ackEnvelope);

        Assert.False(ack.Accepted);
        Assert.Equal(BrowserBridgeErrorCodes.BrowserProtocolMismatch, ack.ErrorCode);
        Assert.False(host.Broker.IsDesktopConnected);
    }

    [Fact]
    public async Task SecondConnection_CannotReplaceAwaitingHelloConnection()
    {
        await using var host = await BrowserBridgeTestHost.StartAsync();
        using var first = await host.ConnectWebSocketAsync();
        using var second = await host.ConnectWebSocketAsync();

        var buffer = new byte[32];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var close = await second.ReceiveAsync(buffer, cts.Token);

        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, close.CloseStatus);
        Assert.NotNull(host.Registry.Current);
        Assert.False(host.Registry.IsDesktopConnected);
    }
}
