using System.Collections.Concurrent;
using System.Text.Json;
using PuddingBrowser.Protocol;

namespace PuddingDesktop.Browser;

/// <summary>
/// Dispatches incoming Bridge commands to the appropriate browser operation handler.
/// Enforces idempotency via result cache, pause/takeover gating, and deadline checks.
/// </summary>
public sealed class BrowserBridgeCommandDispatcher
{
    private readonly BrowserOperationResultCache _resultCache = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeOperations = new();
    private readonly ConcurrentQueue<AgentBrowserActivity> _activities = new();
    private const int MaxActivities = 100;

    private volatile bool _paused;
    private volatile bool _userTakeover;

    // Will be set when BrowserWorkspaceController is available (Task 5)
    private IBrowserCommandHandler? _handler;

    public bool IsPaused => _paused;
    public bool IsUserTakeover => _userTakeover;
    public IReadOnlyCollection<AgentBrowserActivity> RecentActivities => _activities.ToArray();

    public void SetHandler(IBrowserCommandHandler handler) => _handler = handler;

    /// <summary>
    /// Type-safe handler removal. Only clears if the expected handler matches.
    /// Prevents accidental null assignment (Phase 2A-1 fix: no SetHandler(null!)).
    /// </summary>
    public void ClearHandler(IBrowserCommandHandler expectedHandler)
    {
        if (ReferenceEquals(_handler, expectedHandler))
            _handler = null;
    }

    public void SetPaused(bool paused) => _paused = paused;
    public void SetUserTakeover(bool takeover) => _userTakeover = takeover;

    public async Task<BrowserBridgeCommandResult> DispatchAsync(
        BrowserBridgeCommand command,
        CancellationToken externalCt)
    {
        // Idempotency: return cached result for duplicate operationId
        if (_resultCache.TryGet(command.OperationId, out var cached))
        {
            return cached!;
        }

        // Gate: pause/takeover rejects new Agent commands
        if (_paused)
        {
            return CacheAndReturn(Error(command.OperationId,
                BrowserBridgeErrorCodes.BrowserPaused, "Browser is paused"));
        }

        if (_userTakeover)
        {
            return CacheAndReturn(Error(command.OperationId,
                BrowserBridgeErrorCodes.BrowserUserTakeover, "User has taken over browser control"));
        }

        // Validate command name
        if (!BrowserBridgeCommandNames.All.Contains(command.Name))
        {
            return CacheAndReturn(Error(command.OperationId,
                BrowserBridgeErrorCodes.BrowserOperationNotSupported,
                $"Unknown command: {command.Name}"));
        }

        // Deadline check
        if (command.DeadlineUtc <= DateTimeOffset.UtcNow)
        {
            return CacheAndReturn(Error(command.OperationId,
                BrowserBridgeErrorCodes.BrowserDeadlineExceeded, "Command deadline has already passed"));
        }

        // Execute with deadline + cancellation
        using var deadlineCts = new CancellationTokenSource();
        var delay = command.DeadlineUtc - DateTimeOffset.UtcNow;
        deadlineCts.CancelAfter(delay);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt, deadlineCts.Token);
        _activeOperations[command.OperationId] = linkedCts;

        var activity = new AgentBrowserActivity(command.Name, command.PageId ?? command.ContextId ?? "-");
        RecordActivity(activity);

        try
        {
            BrowserBridgeCommandResult result;

            if (_handler is null)
            {
                result = Error(command.OperationId,
                    BrowserBridgeErrorCodes.BrowserNotAvailable,
                    "Browser workspace not initialized");
            }
            else
            {
                result = await _handler.ExecuteAsync(command, linkedCts.Token);
            }

            activity.Complete(result.Success, result.ErrorCode);
            return CacheAndReturn(result);
        }
        catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested)
        {
            activity.Complete(false, BrowserBridgeErrorCodes.BrowserDeadlineExceeded);
            return CacheAndReturn(Error(command.OperationId,
                BrowserBridgeErrorCodes.BrowserDeadlineExceeded, "Command deadline exceeded"));
        }
        catch (OperationCanceledException)
        {
            activity.Complete(false, BrowserBridgeErrorCodes.BrowserCancelled);
            return CacheAndReturn(Error(command.OperationId,
                BrowserBridgeErrorCodes.BrowserCancelled, "Command cancelled"));
        }
        catch (Exception ex)
        {
            activity.Complete(false, BrowserBridgeErrorCodes.BrowserOperationFailed);
            return CacheAndReturn(Error(command.OperationId,
                BrowserBridgeErrorCodes.BrowserOperationFailed, ex.Message));
        }
        finally
        {
            _activeOperations.TryRemove(command.OperationId, out _);
        }
    }

    public void Cancel(Guid operationId)
    {
        if (_activeOperations.TryGetValue(operationId, out var cts))
        {
            cts.Cancel();
        }
    }

    public void FailAllPending(string errorCode, string message)
    {
        foreach (var kvp in _activeOperations)
        {
            kvp.Value.Cancel();
        }
        _activeOperations.Clear();
    }

    private BrowserBridgeCommandResult CacheAndReturn(BrowserBridgeCommandResult result)
    {
        _resultCache.Add(result.OperationId, result);
        return result;
    }

    private void RecordActivity(AgentBrowserActivity activity)
    {
        _activities.Enqueue(activity);
        while (_activities.Count > MaxActivities)
        {
            _activities.TryDequeue(out _);
        }
    }

    private static BrowserBridgeCommandResult Error(Guid operationId, string code, string message)
        => new()
        {
            OperationId = operationId,
            Success = false,
            ErrorCode = code,
            ErrorMessage = message
        };
}

/// <summary>
/// Abstraction for the actual browser operation executor (implemented by BrowserWorkspaceController in Task 5).
/// </summary>
public interface IBrowserCommandHandler
{
    Task<BrowserBridgeCommandResult> ExecuteAsync(BrowserBridgeCommand command, CancellationToken ct);
}

/// <summary>
/// Records a single agent browser activity entry.
/// </summary>
public sealed class AgentBrowserActivity
{
    public string CommandName { get; }
    public string Target { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; private set; }
    public bool? Success { get; private set; }
    public string? ErrorCode { get; private set; }

    public AgentBrowserActivity(string commandName, string target)
    {
        CommandName = commandName;
        Target = target;
    }

    public void Complete(bool success, string? errorCode)
    {
        CompletedAt = DateTimeOffset.UtcNow;
        Success = success;
        ErrorCode = errorCode;
    }
}
