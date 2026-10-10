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

        // **状态屏障**（不是 sleep）：先确认服务端已经把**第一条**连接登记成"等待 Hello"的那一条。
        // 少了这道屏障，两条连接会竞争 `Registry.Current`：第二条若抢先登记，
        // 服务端拒绝的就是**第一条**，本用例等的那次关闭永远不来 —— 这正是它在负载下
        // 以 `OperationCanceledException`(ReceiveAsync 超时) 形式登记为 flake 的机制。
        await BrowserBridgeTestHost.WaitUntilAsync(
            () => host.Registry.Current is not null,
            timeoutMs: 30_000);

        using var second = await host.ConnectWebSocketAsync();

        var buffer = new byte[32];
        // 判据仍然是"第二条连接收到服务端关闭"，但等待本身是异步的：这个 30 秒只是 **watchdog**，
        // 用于在真挂住时给出可诊断失败，而不是"2 秒内必须到达"的速度判据。
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var close = await second.ReceiveAsync(buffer, cts.Token);

        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, close.CloseStatus);
        Assert.NotNull(host.Registry.Current);
        Assert.False(host.Registry.IsDesktopConnected);
    }
}
