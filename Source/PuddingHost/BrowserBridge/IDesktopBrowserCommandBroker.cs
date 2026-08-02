using PuddingBrowser.Protocol;

namespace PuddingHost.BrowserBridge;

public interface IDesktopBrowserCommandBroker
{
    /// <summary>
    /// True only when a handshake-accepted Desktop connection exists.
    /// </summary>
    bool IsDesktopConnected { get; }

    /// <summary>
    /// Executes a command against the connected Desktop. Returns a stable error result
    /// (never throws) for not-available, deadline, cancellation, or duplicate scenarios.
    /// </summary>
    Task<BrowserBridgeCommandResult> ExecuteAsync(
        BrowserBridgeCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sends a Cancel envelope to the Desktop for the given operation.
    /// </summary>
    Task CancelAsync(Guid operationId, CancellationToken cancellationToken);

    /// <summary>
    /// Called by the endpoint when a CommandResult arrives from the Desktop.
    /// Only completes the pending operation if the connection generation matches.
    /// </summary>
    void HandleResult(Guid connectionId, long generation, BrowserBridgeCommandResult result);

    /// <summary>
    /// Fails all pending operations for a specific connection generation.
    /// Does NOT affect pending operations from a newer connection.
    /// </summary>
    void FailPendingForConnection(Guid connectionId, long generation, string errorCode, string message);
}
