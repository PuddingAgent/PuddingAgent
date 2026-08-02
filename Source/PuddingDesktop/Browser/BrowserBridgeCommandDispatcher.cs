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
    private readonly ConcurrentDictionary<Guid, (string Code, string Message)> _forcedFailures = new();
    private readonly ConcurrentQueue<AgentBrowserActivity> _activities = new();
    private const int MaxActivities = 100;

    private volatile bool _paused;
    private volatile bool _userTakeover;

    // Will be set when BrowserWorkspaceController is available (Task 5)
    private IBrowserCommandHandler? _handler;

    public bool IsPaused => _paused;
    public bool IsUserTakeover => _userTakeover;
    public IReadOnlyCollection<AgentBrowserActivitySnapshot> RecentActivities
        => _activities.Select(activity => activity.Snapshot()).ToArray();
    public event EventHandler<AgentBrowserActivityChangedEventArgs>? ActivityChanged;

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

        var activity = new AgentBrowserActivity(
            command.OperationId,
            command.Name,
            command.PageId ?? command.ContextId ?? "-");
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

            if (_forcedFailures.TryRemove(command.OperationId, out var forcedFailure))
            {
                result = Error(command.OperationId, forcedFailure.Code, forcedFailure.Message);
            }

            activity.Complete(result.Success, result.ErrorCode);
            PublishActivity(activity);
            return CacheAndReturn(result);
        }
        catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested)
        {
            activity.Complete(false, BrowserBridgeErrorCodes.BrowserDeadlineExceeded);
            PublishActivity(activity);
            return CacheAndReturn(Error(command.OperationId,
                BrowserBridgeErrorCodes.BrowserDeadlineExceeded, "Command deadline exceeded"));
        }
        catch (OperationCanceledException)
        {
            if (_forcedFailures.TryRemove(command.OperationId, out var forcedFailure))
            {
                activity.Complete(false, forcedFailure.Code);
                PublishActivity(activity);
                return CacheAndReturn(Error(command.OperationId, forcedFailure.Code, forcedFailure.Message));
            }
            activity.Complete(false, BrowserBridgeErrorCodes.BrowserCancelled);
            PublishActivity(activity);
            return CacheAndReturn(Error(command.OperationId,
                BrowserBridgeErrorCodes.BrowserCancelled, "Command cancelled"));
        }
        catch (Exception ex)
        {
            activity.Complete(false, BrowserBridgeErrorCodes.BrowserOperationFailed);
            PublishActivity(activity);
            return CacheAndReturn(Error(command.OperationId,
                BrowserBridgeErrorCodes.BrowserOperationFailed, ex.Message));
        }
        finally
        {
            _activeOperations.TryRemove(command.OperationId, out _);
            _forcedFailures.TryRemove(command.OperationId, out _);
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
            _forcedFailures[kvp.Key] = (errorCode, message);
            kvp.Value.Cancel();
        }
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
        PublishActivity(activity);
    }

    private void PublishActivity(AgentBrowserActivity activity)
        => ActivityChanged?.Invoke(
            this,
            new AgentBrowserActivityChangedEventArgs(activity.Snapshot()));

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
    public Guid OperationId { get; }
    public string CommandName { get; }
    public string Target { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; private set; }
    public bool? Success { get; private set; }
    public string? ErrorCode { get; private set; }

    public AgentBrowserActivity(Guid operationId, string commandName, string target)
    {
        OperationId = operationId;
        CommandName = commandName;
        Target = target;
    }

    public void Complete(bool success, string? errorCode)
    {
        CompletedAt = DateTimeOffset.UtcNow;
        Success = success;
        ErrorCode = errorCode;
    }

    public AgentBrowserActivitySnapshot Snapshot() => new()
    {
        OperationId = OperationId,
        CommandName = CommandName,
        Target = Target,
        StartedAt = StartedAt,
        CompletedAt = CompletedAt,
        Success = Success,
        ErrorCode = ErrorCode
    };
}

public sealed record AgentBrowserActivitySnapshot
{
    public required Guid OperationId { get; init; }
    public required string CommandName { get; init; }
    public required string Target { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public bool? Success { get; init; }
    public string? ErrorCode { get; init; }
    public bool IsCompleted => CompletedAt.HasValue;
}

public sealed class AgentBrowserActivityChangedEventArgs : EventArgs
{
    public AgentBrowserActivitySnapshot Snapshot { get; }

    public AgentBrowserActivityChangedEventArgs(AgentBrowserActivitySnapshot snapshot)
        => Snapshot = snapshot;
}
