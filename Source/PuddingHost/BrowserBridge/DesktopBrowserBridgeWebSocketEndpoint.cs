using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PuddingBrowser.Protocol;
using PuddingHost.Hosting;

namespace PuddingHost.BrowserBridge;

/// <summary>
/// Clock abstraction for Core Browser Bridge heartbeat/timeout.
/// Enables FakeTimeProvider testing without real 15/45 second waits.
/// </summary>
public interface IBrowserBridgeClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>
/// Production clock using real time.
/// </summary>
public sealed class SystemBrowserBridgeClock : IBrowserBridgeClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        => Task.Delay(delay, cancellationToken);
}

/// <summary>
/// WebSocket endpoint for the Desktop Browser Bridge.
/// 
/// Phase 2A-1 final acceptance fixes:
/// - Generation obtained from IDesktopBrowserConnectionRegistry.NextGeneration() (no cast)
/// - Connection token linked to endpoint CTS so Watchdog can cancel blocked ReceiveAsync
/// - Injectable IBrowserBridgeClock for deterministic heartbeat/timeout testing
/// - TryAttach rejects if any connection exists (no zombie replacement)
/// - Single send loop invariant maintained
/// </summary>
public static class DesktopBrowserBridgeWebSocketEndpoint
{
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(2);

    public static async Task HandleAsync(
        HttpContext context,
        IDesktopBrowserConnectionRegistry registry,
        IDesktopBrowserCommandBroker broker,
        DesktopControlTokenValidator tokenValidator,
        IBrowserBridgeClock clock,
        ILogger logger)
    {
        // 1. Verify loopback
        if (!IsLoopback(context.Connection.RemoteIpAddress))
        {
            context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            return;
        }

        // 2. Verify token
        if (!await tokenValidator.ValidateAsync(context.Request.Headers))
        {
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return;
        }

        // 3. Must be WebSocket request
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync();

        // 4. Create connection with generation from interface (no cast)
        var generation = registry.NextGeneration();
        var connectionId = Guid.NewGuid();
        var connection = new DesktopBrowserConnection(connectionId, generation);

        if (!registry.TryAttach(connection))
        {
            await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                "Another Desktop is already connected", context.RequestAborted);
            await connection.DisposeAsync();
            return;
        }

        logger.LogInformation(
            "Browser Bridge connection attached: {ConnectionId} gen={Generation}",
            connectionId, generation);

        // Link endpoint CTS to both RequestAborted AND connection.ConnectionToken
        // so Watchdog can cancel blocked ReceiveAsync via connection.Complete()
        using var endpointCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, connection.ConnectionToken);
        var ct = endpointCts.Token;

        Task? sendTask = null;
        Task? heartbeatTask = null;
        Task? watchdogTask = null;

        try
        {
            // 5. Await Hello (must be first message within 5s)
            var helloOk = await AwaitHelloAsync(ws, connection, clock, ct);
            if (!helloOk)
            {
                logger.LogWarning("Browser Bridge Hello failed: {ConnectionId}", connectionId);
                return;
            }

            // 6. Start single Send Loop (only consumer of outbound channel)
            sendTask = SendLoopAsync(ws, connection, ct);

            // 7. Start Heartbeat Loop (enqueues heartbeats to outbound channel)
            heartbeatTask = HeartbeatLoopAsync(connection, clock, ct);

            // 8. Start Watchdog (independent timeout check that can cancel Receive)
            watchdogTask = WatchdogLoopAsync(connection, clock, logger, ct);

            // 9. Receive Loop (processes incoming messages)
            await ReceiveLoopAsync(ws, connection, broker, clock, logger, ct);
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (WebSocketException ex)
        {
            logger.LogDebug("Browser Bridge WebSocket error: {ConnectionId} {Message}",
                connectionId, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Browser Bridge unexpected error: {ConnectionId}", connectionId);
        }
        finally
        {
            // 10. Shutdown: cancel all loops, complete connection
            connection.Complete();
            endpointCts.Cancel();

            // Fail only THIS generation's pending operations
            broker.FailPendingForConnection(connectionId, generation,
                BrowserBridgeErrorCodes.BrowserBridgeDisconnected, "Desktop disconnected");

            // Detach only if still current (generation-safe)
            registry.Detach(connectionId, generation);

            // Best-effort close handshake
            try
            {
                if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", closeCts.Token);
                }
            }
            catch { /* best effort */ }

            // Await tasks to prevent orphaned continuations
            if (sendTask is not null)
                await sendTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (heartbeatTask is not null)
                await heartbeatTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (watchdogTask is not null)
                await watchdogTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            await connection.DisposeAsync();

            logger.LogInformation(
                "Browser Bridge connection closed: {ConnectionId} gen={Generation}",
                connectionId, generation);
        }
    }

    /// <summary>
    /// Waits for the first message which must be a Hello within 5 seconds.
    /// Sends HelloAck directly (only writer at this point before send loop starts).
    /// </summary>
    private static async Task<bool> AwaitHelloAsync(
        WebSocket ws, DesktopBrowserConnection connection,
        IBrowserBridgeClock clock, CancellationToken ct)
    {
        using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        helloCts.CancelAfter(HelloTimeout);

        try
        {
            var buffer = new byte[BrowserBridgeProtocol.MaxMessageBytes];
            var messageBuffer = new MemoryStream();

            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), helloCts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    return false;
                if (result.MessageType == WebSocketMessageType.Binary)
                    return false;
                messageBuffer.Write(buffer, 0, result.Count);
                if (messageBuffer.Length > BrowserBridgeProtocol.MaxMessageBytes)
                    return false;
            } while (!result.EndOfMessage);

            var span = new ReadOnlySpan<byte>(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
            var envelope = BrowserBridgeSerializer.Deserialize(span);

            if (envelope.Kind != BrowserBridgeMessageKind.Hello)
            {
                var errorAck = BuildHelloAckEnvelope(envelope.MessageId, false,
                    BrowserBridgeErrorCodes.BrowserInvalidCommand, "First message must be Hello");
                var errorBytes = BrowserBridgeSerializer.Serialize(errorAck);
                await ws.SendAsync(errorBytes, WebSocketMessageType.Text, true, ct);
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Expected Hello", ct);
                return false;
            }

            var hello = BrowserBridgeSerializer.DeserializePayload<BrowserBridgeHello>(envelope);
            var accepted = connection.TryAcceptHello(hello, out var ack);

            var ackEnvelope = BuildHelloAckEnvelope(envelope.MessageId, ack.Accepted,
                ack.ErrorCode, ack.ErrorMessage);
            var ackBytes = BrowserBridgeSerializer.Serialize(ackEnvelope);
            await ws.SendAsync(ackBytes, WebSocketMessageType.Text, true, ct);

            if (!accepted)
            {
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                    ack.ErrorMessage ?? "Hello rejected", ct);
            }

            connection.MarkReceived(clock.UtcNow);
            return accepted;
        }
        catch (OperationCanceledException)
        {
            try
            {
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Hello timeout", CancellationToken.None);
            }
            catch { }
            return false;
        }
        catch (BrowserBridgeProtocolException)
        {
            return false;
        }
    }

    /// <summary>
    /// Single send loop: the ONLY code path that calls ws.SendAsync after Hello.
    /// </summary>
    private static async Task SendLoopAsync(
        WebSocket ws, DesktopBrowserConnection connection, CancellationToken ct)
    {
        try
        {
            await foreach (var envelope in connection.Outbound.ReadAllAsync(ct))
            {
                if (ws.State != WebSocketState.Open) break;
                var bytes = BrowserBridgeSerializer.Serialize(envelope);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
        }
        catch (OperationCanceledException) { /* normal */ }
        catch (WebSocketException) { /* connection lost */ }
    }

    /// <summary>
    /// Enqueues a Heartbeat envelope every 15 seconds using injectable clock.
    /// </summary>
    private static async Task HeartbeatLoopAsync(
        DesktopBrowserConnection connection, IBrowserBridgeClock clock, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await clock.DelayAsync(BrowserBridgeProtocol.DefaultHeartbeatInterval, ct);

                var heartbeat = new BrowserBridgeEnvelope
                {
                    MessageId = Guid.NewGuid(),
                    Kind = BrowserBridgeMessageKind.Heartbeat,
                    CreatedAt = clock.UtcNow,
                    Payload = JsonSerializer.SerializeToElement(new { },
                        BrowserBridgeSerializerOptions.Default)
                };

                connection.TryEnqueue(heartbeat);
            }
        }
        catch (OperationCanceledException) { /* normal */ }
    }

    /// <summary>
    /// Independent Watchdog: checks LastReceivedAt every 2 seconds.
    /// If exceeded DefaultHeartbeatTimeout, calls connection.Complete() which
    /// cancels the ConnectionToken, unblocking the ReceiveAsync in ReceiveLoop.
    /// </summary>
    private static async Task WatchdogLoopAsync(
        DesktopBrowserConnection connection, IBrowserBridgeClock clock,
        ILogger logger, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await clock.DelayAsync(WatchdogInterval, ct);

                var elapsed = clock.UtcNow - connection.LastReceivedAt;
                if (elapsed > BrowserBridgeProtocol.DefaultHeartbeatTimeout)
                {
                    logger.LogWarning(
                        "Browser Bridge watchdog timeout: {ConnectionId} elapsed={Elapsed}s",
                        connection.ConnectionId, elapsed.TotalSeconds);
                    connection.Complete(); // cancels ConnectionToken → unblocks ReceiveAsync
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* normal */ }
    }

    /// <summary>
    /// Receive loop: processes incoming messages after Hello is accepted.
    /// Timeout is handled by the independent Watchdog, not by pre-check here.
    /// </summary>
    private static async Task ReceiveLoopAsync(
        WebSocket ws,
        DesktopBrowserConnection connection,
        IDesktopBrowserCommandBroker broker,
        IBrowserBridgeClock clock,
        ILogger logger,
        CancellationToken ct)
    {
        var buffer = new byte[BrowserBridgeProtocol.MaxMessageBytes];
        var messageBuffer = new MemoryStream();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            messageBuffer.SetLength(0);
            WebSocketReceiveResult result;

            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    logger.LogWarning("Browser Bridge binary message rejected: {ConnectionId}",
                        connection.ConnectionId);
                    return;
                }

                messageBuffer.Write(buffer, 0, result.Count);
                if (messageBuffer.Length > BrowserBridgeProtocol.MaxMessageBytes)
                {
                    logger.LogWarning("Browser Bridge message too large: {ConnectionId}",
                        connection.ConnectionId);
                    return;
                }
            } while (!result.EndOfMessage);

            connection.MarkReceived(clock.UtcNow);

            try
            {
                var span = new ReadOnlySpan<byte>(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                var envelope = BrowserBridgeSerializer.Deserialize(span);
                await HandleMessageAsync(connection, envelope, broker, clock, ct);
            }
            catch (BrowserBridgeProtocolException ex)
            {
                logger.LogWarning("Browser Bridge protocol error: {ConnectionId} {Code} {Message}",
                    connection.ConnectionId, ex.ErrorCode, ex.Message);
                return;
            }
        }
    }

    private static async Task HandleMessageAsync(
        DesktopBrowserConnection connection,
        BrowserBridgeEnvelope envelope,
        IDesktopBrowserCommandBroker broker,
        IBrowserBridgeClock clock,
        CancellationToken ct)
    {
        switch (envelope.Kind)
        {
            case BrowserBridgeMessageKind.CommandResult:
                var result = BrowserBridgeSerializer.DeserializePayload<BrowserBridgeCommandResult>(envelope);
                broker.HandleResult(connection.ConnectionId, connection.Generation, result);
                break;

            case BrowserBridgeMessageKind.HeartbeatAck:
                // Desktop acknowledged our heartbeat — LastReceivedAt already updated
                break;

            case BrowserBridgeMessageKind.Heartbeat:
                // Desktop sent us a heartbeat — enqueue ack
                var ack = new BrowserBridgeEnvelope
                {
                    MessageId = Guid.NewGuid(),
                    CorrelationId = envelope.MessageId,
                    Kind = BrowserBridgeMessageKind.HeartbeatAck,
                    CreatedAt = clock.UtcNow,
                    Payload = JsonSerializer.SerializeToElement(new { },
                        BrowserBridgeSerializerOptions.Default)
                };
                connection.TryEnqueue(ack);
                break;

            case BrowserBridgeMessageKind.Hello:
                // Duplicate Hello after handshake — ignore
                break;

            default:
                break;
        }

        await Task.CompletedTask;
    }

    private static BrowserBridgeEnvelope BuildHelloAckEnvelope(
        Guid correlationId, bool accepted, string? errorCode, string? errorMessage)
    {
        return new BrowserBridgeEnvelope
        {
            MessageId = Guid.NewGuid(),
            CorrelationId = correlationId,
            Kind = BrowserBridgeMessageKind.HelloAck,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new BrowserBridgeHelloAck
            {
                ProtocolVersion = BrowserBridgeProtocol.CurrentVersion,
                Accepted = accepted,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage
            }, BrowserBridgeSerializerOptions.Default)
        };
    }

    private static bool IsLoopback(IPAddress? address)
    {
        return address is not null && (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6Loopback));
    }
}
