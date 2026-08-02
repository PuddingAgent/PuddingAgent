using System.Net.WebSockets;

namespace PuddingDesktop.Browser;

/// <summary>
/// Abstraction over WebSocket transport for deterministic testing.
/// Production implementation wraps ClientWebSocket; test fakes can script
/// HelloAck, Command, Close, silence, and record Send calls.
/// Token must only be passed via SetRequestHeader — never logged or stored in state.
/// </summary>
public interface IDesktopBrowserWebSocket : IAsyncDisposable
{
    WebSocketState State { get; }

    void SetRequestHeader(string name, string value);

    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    ValueTask SendAsync(
        ReadOnlyMemory<byte> payload,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken);

    ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken);

    Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken);
}

/// <summary>
/// Factory for creating WebSocket transport instances. Allows test substitution.
/// </summary>
public interface IDesktopBrowserWebSocketFactory
{
    IDesktopBrowserWebSocket Create();
}

/// <summary>
/// Production WebSocket transport wrapping ClientWebSocket.
/// </summary>
public sealed class ClientWebSocketTransport : IDesktopBrowserWebSocket
{
    private readonly ClientWebSocket _ws = new();

    public WebSocketState State => _ws.State;

    public void SetRequestHeader(string name, string value)
        => _ws.Options.SetRequestHeader(name, value);

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        => _ws.ConnectAsync(uri, cancellationToken);

    public ValueTask SendAsync(
        ReadOnlyMemory<byte> payload,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken)
        => _ws.SendAsync(payload, messageType, endOfMessage, cancellationToken);

    public ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken)
        => _ws.ReceiveAsync(buffer, cancellationToken);

    public Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken)
        => _ws.CloseAsync(closeStatus, statusDescription, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _ws.Dispose();
        await Task.CompletedTask;
    }
}

/// <summary>
/// Default production factory.
/// </summary>
public sealed class DefaultDesktopBrowserWebSocketFactory : IDesktopBrowserWebSocketFactory
{
    public IDesktopBrowserWebSocket Create() => new ClientWebSocketTransport();
}

/// <summary>
/// Clock abstraction for deterministic testing of heartbeat/timeout logic.
/// Tests use FakeClock to advance 45 seconds instantly without real waiting.
/// </summary>
public interface IBrowserBridgeClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>
/// Default production clock using real time.
/// </summary>
public sealed class SystemBrowserBridgeClock : IBrowserBridgeClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        => Task.Delay(delay, cancellationToken);
}
