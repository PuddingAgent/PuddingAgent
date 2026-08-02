using System.Collections.Concurrent;
using System.Text.Json;
using PuddingBrowser.Protocol;

namespace PuddingHost.BrowserBridge;

/// <summary>
/// Command broker that routes commands to the connected Desktop via its per-connection
/// outbound channel. Tracks pending operations with connection generation to prevent
/// stale results from completing new operations.
/// </summary>
public sealed class DesktopBrowserCommandBroker : IDesktopBrowserCommandBroker, IAsyncDisposable
{
    private readonly IDesktopBrowserConnectionRegistry _registry;
    private readonly ConcurrentDictionary<Guid, PendingBrowserOperation> _pendingOperations = new();

    public bool IsDesktopConnected => _registry.IsDesktopConnected;

    public DesktopBrowserCommandBroker(IDesktopBrowserConnectionRegistry registry)
    {
        _registry = registry;
    }

    public async Task<BrowserBridgeCommandResult> ExecuteAsync(
        BrowserBridgeCommand command,
        CancellationToken cancellationToken)
    {
        var connection = _registry.Current;
        if (connection is not { IsHandshakeAccepted: true })
        {
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserNotAvailable,
                "No authenticated Desktop connected");
        }

        if (!BrowserBridgeCommandNames.All.Contains(command.Name))
        {
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserOperationNotSupported,
                $"Unknown command: {command.Name}");
        }

        if (command.DeadlineUtc <= DateTimeOffset.UtcNow)
        {
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserDeadlineExceeded,
                "Command deadline has already passed");
        }

        // Duplicate operation id: do not overwrite existing TCS
        if (_pendingOperations.ContainsKey(command.OperationId))
        {
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserInvalidCommand,
                "Duplicate operation id");
        }

        var tcs = new TaskCompletionSource<BrowserBridgeCommandResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var pending = new PendingBrowserOperation(
            command.OperationId,
            connection.ConnectionId,
            connection.Generation,
            tcs);

        if (!_pendingOperations.TryAdd(command.OperationId, pending))
        {
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserInvalidCommand,
                "Duplicate operation id");
        }

        // Build envelope and enqueue to the connection's outbound channel
        var envelope = new BrowserBridgeEnvelope
        {
            MessageId = Guid.NewGuid(),
            CorrelationId = command.OperationId,
            Kind = BrowserBridgeMessageKind.Command,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(command,
                BrowserBridgeSerializerOptions.Default)
        };

        try
        {
            await connection.EnqueueAsync(envelope, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _pendingOperations.TryRemove(command.OperationId, out _);
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserCancelled,
                "Cancelled before send");
        }
        catch
        {
            _pendingOperations.TryRemove(command.OperationId, out _);
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserBridgeDisconnected,
                "Failed to enqueue command");
        }

        // Set up deadline + caller cancellation
        var delay = command.DeadlineUtc - DateTimeOffset.UtcNow;
        using var deadlineCts = new CancellationTokenSource(
            delay > TimeSpan.Zero ? delay : TimeSpan.Zero);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, deadlineCts.Token);

        using var registration = linkedCts.Token.Register(() =>
        {
            if (_pendingOperations.TryRemove(command.OperationId, out var removed))
            {
                if (deadlineCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    removed.Completion.TrySetResult(Error(command.OperationId,
                        BrowserBridgeErrorCodes.BrowserDeadlineExceeded, "Command deadline exceeded"));
                    // Send cancel to Desktop
                    _ = SendCancelAsync(connection, command.OperationId);
                }
                else
                {
                    removed.Completion.TrySetResult(Error(command.OperationId,
                        BrowserBridgeErrorCodes.BrowserCancelled, "Cancelled by caller"));
                    _ = SendCancelAsync(connection, command.OperationId);
                }
            }
        });

        try
        {
            return await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserCancelled, "Cancelled");
        }
        finally
        {
            _pendingOperations.TryRemove(command.OperationId, out _);
        }
    }

    public async Task CancelAsync(Guid operationId, CancellationToken cancellationToken)
    {
        if (_pendingOperations.TryRemove(operationId, out var pending))
        {
            pending.Completion.TrySetResult(Error(operationId,
                BrowserBridgeErrorCodes.BrowserCancelled, "Cancelled by broker"));

            var connection = _registry.Current;
            if (connection is { IsHandshakeAccepted: true }
                && connection.ConnectionId == pending.ConnectionId
                && connection.Generation == pending.ConnectionGeneration)
            {
                await SendCancelAsync(connection, operationId);
            }
        }
    }

    public void HandleResult(Guid connectionId, long generation, BrowserBridgeCommandResult result)
    {
        if (_pendingOperations.TryGetValue(result.OperationId, out var pending))
        {
            // Only complete if the result comes from the same connection generation
            if (pending.ConnectionId == connectionId && pending.ConnectionGeneration == generation)
            {
                _pendingOperations.TryRemove(result.OperationId, out _);
                pending.Completion.TrySetResult(result);
            }
            // Otherwise: stale result from old connection — ignore
        }
    }

    public void FailPendingForConnection(Guid connectionId, long generation, string errorCode, string message)
    {
        foreach (var kvp in _pendingOperations)
        {
            var pending = kvp.Value;
            if (pending.ConnectionId == connectionId && pending.ConnectionGeneration == generation)
            {
                if (_pendingOperations.TryRemove(kvp.Key, out _))
                {
                    pending.Completion.TrySetResult(Error(kvp.Key, errorCode, message));
                }
            }
        }
    }

    private async Task SendCancelAsync(DesktopBrowserConnection connection, Guid operationId)
    {
        try
        {
            var cancelEnvelope = new BrowserBridgeEnvelope
            {
                MessageId = Guid.NewGuid(),
                CorrelationId = operationId,
                Kind = BrowserBridgeMessageKind.Cancel,
                CreatedAt = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.SerializeToElement(new BrowserBridgeCancel
                {
                    OperationId = operationId
                }, BrowserBridgeSerializerOptions.Default)
            };
            connection.TryEnqueue(cancelEnvelope);
        }
        catch { /* best effort */ }
        await Task.CompletedTask;
    }

    private static BrowserBridgeCommandResult Error(Guid operationId, string code, string msg)
        => new()
        {
            OperationId = operationId,
            Success = false,
            ErrorCode = code,
            ErrorMessage = msg
        };

    public ValueTask DisposeAsync()
    {
        foreach (var kvp in _pendingOperations)
        {
            if (_pendingOperations.TryRemove(kvp.Key, out var pending))
            {
                pending.Completion.TrySetResult(Error(kvp.Key,
                    BrowserBridgeErrorCodes.BrowserBridgeDisconnected, "Broker disposed"));
            }
        }
        return ValueTask.CompletedTask;
    }

    private sealed record PendingBrowserOperation(
        Guid OperationId,
        Guid ConnectionId,
        long ConnectionGeneration,
        TaskCompletionSource<BrowserBridgeCommandResult> Completion);
}

/// <summary>
/// Shared serializer options for the Browser Bridge.
/// </summary>
internal static class BrowserBridgeSerializerOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
