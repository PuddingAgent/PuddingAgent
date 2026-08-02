using System.Net.WebSockets;
using System.Text.Json;
using PuddingBrowser.Protocol;

namespace PuddingHost.Tests.BrowserBridge;

public sealed class DesktopBrowserBridgeDisconnectTests
{
    [Fact]
    public async Task ClientClose_DetachesRegistry()
    {
        await using var host = await BrowserBridgeTestHost.StartAsync();
        using var socket = await host.ConnectWebSocketAsync();
        Assert.True((await BrowserBridgeTestHost.CompleteHelloAsync(socket)).Accepted);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test", CancellationToken.None);

        await BrowserBridgeTestHost.WaitUntilAsync(() => host.Registry.Current is null);
        Assert.False(host.Broker.IsDesktopConnected);
    }

    [Fact]
    public async Task Disconnect_FailsPendingCommandForThatGeneration()
    {
        await using var host = await BrowserBridgeTestHost.StartAsync();
        using var socket = await host.ConnectWebSocketAsync();
        Assert.True((await BrowserBridgeTestHost.CompleteHelloAsync(socket)).Accepted);

        var command = new BrowserBridgeCommand
        {
            OperationId = Guid.NewGuid(),
            Name = BrowserBridgeCommandNames.PageGoto,
            DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1),
            Arguments = JsonSerializer.SerializeToElement(new { url = "https://example.com" })
        };
        var pending = host.Broker.ExecuteAsync(command, CancellationToken.None);
        var sent = await BrowserBridgeTestHost.ReceiveEnvelopeAsync(socket);
        Assert.Equal(BrowserBridgeMessageKind.Command, sent.Kind);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "disconnect", CancellationToken.None);
        var result = await pending;

        Assert.False(result.Success);
        Assert.Equal(BrowserBridgeErrorCodes.BrowserBridgeDisconnected, result.ErrorCode);
    }

    [Fact]
    public async Task Reconnect_DoesNotReplayOldPendingCommand()
    {
        await using var host = await BrowserBridgeTestHost.StartAsync();
        using (var first = await host.ConnectWebSocketAsync())
        {
            Assert.True((await BrowserBridgeTestHost.CompleteHelloAsync(first)).Accepted);
            var command = new BrowserBridgeCommand
            {
                OperationId = Guid.NewGuid(),
                Name = BrowserBridgeCommandNames.PageGoto,
                DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1),
                Arguments = JsonSerializer.SerializeToElement(new { url = "https://example.com" })
            };
            var pending = host.Broker.ExecuteAsync(command, CancellationToken.None);
            Assert.Equal(BrowserBridgeMessageKind.Command,
                (await BrowserBridgeTestHost.ReceiveEnvelopeAsync(first)).Kind);
            await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "disconnect", CancellationToken.None);
            Assert.Equal(BrowserBridgeErrorCodes.BrowserBridgeDisconnected, (await pending).ErrorCode);
        }

        await BrowserBridgeTestHost.WaitUntilAsync(() => host.Registry.Current is null);
        using var second = await host.ConnectWebSocketAsync();
        Assert.True((await BrowserBridgeTestHost.CompleteHelloAsync(second)).Accepted);

        using var receiveCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var buffer = new byte[1024];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await second.ReceiveAsync(buffer, receiveCts.Token));
    }
}
