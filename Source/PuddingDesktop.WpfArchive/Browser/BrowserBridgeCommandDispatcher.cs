using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;
using PuddingBrowser.Protocol;

namespace PuddingDesktop.Browser;

/// <summary>
/// Dispatches incoming Bridge commands to the appropriate browser operation handler.
/// Enforces idempotency via result cache, pause/takeover gating, and deadline checks.
///
/// 本类是 <see cref="IBrowserAutomationGate"/> 的实现之一：它的 pause/takeover 就是**执行闸门本身**。
/// 在它只是"自己的一个字段"时，Shell 的接管开关从来不写它，于是 Bridge 路径上的人工接管无效
/// （审计 2026-10-08 结论 #1）。现在它的控制状态**不再自己持有**：读写的是进程内唯一权威实例
/// （经由 <see cref="BrowserPageLifecycle"/>，与能力通道、生命周期清理点同一个对象）。
/// </summary>
public sealed class BrowserBridgeCommandDispatcher : IBrowserAutomationGate
{
    private readonly BrowserOperationResultCache _resultCache = new();

    // 方案 §4.2：**唯一**幂等来源就是原子执行账本 —— 同键同指纹在途则等同一完成源、
    // 已完成则复用、指纹不同则冲突；不再另造一份"按 Guid 的 in-flight/调用者缓存"。
    // 指纹里含调用者身份 ⇒ 换调用者即冲突（不跨调用者复用他人结果）。
    private readonly BrowserOperationLedger _ledger = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeOperations = new();
    private readonly ConcurrentDictionary<Guid, (string Code, string Message)> _forcedFailures = new();
    private readonly ConcurrentQueue<AgentBrowserActivity> _activities = new();
    private readonly BrowserPageLifecycle _lifecycle;
    private const int MaxActivities = 100;

    // Will be set when BrowserWorkspaceController is available (Task 5)
    private IBrowserCommandHandler? _handler;

    /// <summary>
    /// 只接受生命周期端口：它是**唯一**权威实例的所有者。Bridge 路径是默认路径，
    /// 因此这里必须读同一份状态，否则用户在 UI 上接管后 Bridge 命令照旧执行。
    /// </summary>
    public BrowserBridgeCommandDispatcher(BrowserPageLifecycle lifecycle)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    /// <summary>本闸门读写的唯一事实源（诊断用）。</summary>
    public BrowserPageLifecycle Lifecycle => _lifecycle;

    public bool IsPaused => _lifecycle.IsPaused;
    public bool IsUserTakeover => _lifecycle.IsUserTakeover;
    public IReadOnlyCollection<AgentBrowserActivitySnapshot> RecentActivities
        => _activities.Select(activity => activity.Snapshot()).ToArray();
    public event EventHandler<AgentBrowserActivityChangedEventArgs>? ActivityChanged;
    public event EventHandler<AgentBrowserOperationStateChangedEventArgs>? OperationStateChanged;

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

    public void SetPaused(bool paused) => _lifecycle.SetPaused(paused, "bridge gate");

    public void SetUserTakeover(bool takeover) =>
        _lifecycle.SetUserTakeover(takeover, "bridge gate");

    public async Task<BrowserBridgeCommandResult> DispatchAsync(
        BrowserBridgeCommand command,
        CancellationToken externalCt)
    {
        // 方案 §4.2：**账本是唯一幂等来源**。键 = (进程实例, 连接世代, operationId)；
        // 指纹含调用者身份与完整语义载荷 ⇒ 换调用者/换目标/换参数一律冲突，绝不跨调用者复用结果。
        var key = new BrowserOperationLedgerKey("bridge", 0, command.OperationId.ToString("n"));
        var fingerprint = Fingerprint(command);

        while (true)
        {
            var decision = _ledger.TryBegin(key, fingerprint);
            switch (decision.State)
            {
                case BrowserOperationLedgerState.Conflict:
                    return Error(
                        command.OperationId,
                        "operation_id_conflict",
                        "this operation id is already bound to a different caller or payload; a result is never replayed across callers");

                case BrowserOperationLedgerState.AwaitPending:
                    if (decision.Pending is null)
                    {
                        break;
                    }

                    // 等**同一完成源**：绝不重复进 handler。
                    await decision.Pending.ConfigureAwait(false);
                    continue;

                case BrowserOperationLedgerState.ReplayCompleted:
                    if (_resultCache.TryGet(command.OperationId, out var replayed))
                    {
                        return replayed!;
                    }

                    return Error(
                        command.OperationId,
                        "operation_result_unavailable",
                        "the operation already completed but its result is no longer retained");

                case BrowserOperationLedgerState.Execute:
                    return await ExecuteOnceAsync(command, externalCt, key).ConfigureAwait(false);
            }

            break;
        }

        return Error(command.OperationId, "operation_ledger_unavailable", "the ledger returned no usable decision");
    }

    private async Task<BrowserBridgeCommandResult> ExecuteOnceAsync(
        BrowserBridgeCommand command,
        CancellationToken externalCt,
        BrowserOperationLedgerKey key)
    {
        try
        {
            var result = await DispatchCoreAsync(command, externalCt).ConfigureAwait(false);
            _ledger.Complete(key, result.Success ? "completed" : "failed");
            return result;
        }
        catch
        {
            // 结果未知：账本按 fail-closed 处置（不复用、也不当安全重试）。
            _ledger.Abandon(key, "outcome_unknown: dispatch threw");
            throw;
        }
    }

    /// <summary>完整语义指纹（方案 §4.1）：调用者身份 + 能力名 + 目标 + 完整载荷（deadline 等非语义元数据不入）。</summary>
    private static string Fingerprint(BrowserBridgeCommand command)
    {
        var canonical = string.Concat(
            CallerKey(command), "\u001e",
            command.Name ?? string.Empty, "\u001e",
            command.ContextId ?? string.Empty, "\u001e",
            command.PageId ?? string.Empty, "\u001e",
            command.Arguments.ValueKind == JsonValueKind.Undefined ? "null" : command.Arguments.GetRawText());

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private async Task<BrowserBridgeCommandResult> DispatchCoreAsync(
        BrowserBridgeCommand command,
        CancellationToken externalCt)
    {

        // Gate: pause/takeover rejects new Agent commands.
        // Record a failed activity so Agent and user understand the rejection (doc 6.1).
        if (IsPaused)
        {
            var pausedActivity = new AgentBrowserActivity(
                command.OperationId, command.Name,
                command.PageId ?? command.ContextId ?? "-", command.Origin);
            pausedActivity.Complete(false, BrowserBridgeErrorCodes.BrowserPaused);
            RecordActivity(pausedActivity);
            return CacheAndReturn(Error(command.OperationId,
                BrowserBridgeErrorCodes.BrowserPaused, "Browser is paused"));
        }

        if (IsUserTakeover)
        {
            var takeoverActivity = new AgentBrowserActivity(
                command.OperationId, command.Name,
                command.PageId ?? command.ContextId ?? "-", command.Origin);
            takeoverActivity.Complete(false, BrowserBridgeErrorCodes.BrowserUserTakeover);
            RecordActivity(takeoverActivity);
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

        // 方案 §5.2/§5.3：Bridge 页面命令必须先过**页面授权**闸门 —— **无授权不得触碰 handler**
        // （外部复核 P0：Bridge 无任务授权仍能执行页面操作）。fail closed：未登记的只读命令按写处理。
        if (TryAuthorizePageCommand(command, out var authorizationReason))
        {
            var deniedActivity = new AgentBrowserActivity(
                command.OperationId, command.Name,
                command.PageId ?? command.ContextId ?? "-", command.Origin);
            deniedActivity.Complete(false, PageGrantRequired);
            RecordActivity(deniedActivity);
            return CacheAndReturn(Error(command.OperationId, PageGrantRequired, authorizationReason!));
        }

        // Execute with deadline + cancellation
        using var deadlineCts = new CancellationTokenSource();
        var delay = command.DeadlineUtc - DateTimeOffset.UtcNow;
        deadlineCts.CancelAfter(delay);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt, deadlineCts.Token);
        _activeOperations[command.OperationId] = linkedCts;

        // Publish operation state after command passes gates and enters active set (doc 6.1)
        PublishOperationState(command.Origin);

        var activity = new AgentBrowserActivity(
            command.OperationId,
            command.Name,
            command.PageId ?? command.ContextId ?? "-",
            command.Origin);
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

            // Publish operation state after command completes / is removed (doc 6.1)
            PublishOperationState(null);
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
    {
        try
        {
            ActivityChanged?.Invoke(
                this,
                new AgentBrowserActivityChangedEventArgs(activity.Snapshot()));
        }
        catch
        {
            // Subscriber exception must not break command execution (doc 6.1)
        }
    }

    private void PublishOperationState(BrowserBridgeCommandOrigin? mostRecentOrigin)
    {
        try
        {
            OperationStateChanged?.Invoke(
                this,
                new AgentBrowserOperationStateChangedEventArgs(
                    new AgentBrowserOperationStateSnapshot
                    {
                        ActiveOperationCount = _activeOperations.Count,
                        MostRecentOrigin = mostRecentOrigin
                    }));
        }
        catch
        {
            // Subscriber exception must not break command execution (doc 6.1)
        }
    }

    /// <summary>方案 §5.3：**只读**命令的最小准入是目标页 Read；其余（含未登记者）一律要 Write（fail closed）。</summary>
    private static readonly IReadOnlySet<string> ReadOnlyPageCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        BrowserBridgeCommandNames.PageSnapshot,
        BrowserBridgeCommandNames.PageLocate,
        BrowserBridgeCommandNames.PageWaitFor,
        BrowserBridgeCommandNames.PageScreenshot,
        BrowserBridgeCommandNames.PageGetInfo,
        BrowserBridgeCommandNames.PageList,
        BrowserBridgeCommandNames.ContextList,
        BrowserBridgeCommandNames.ContextGetInfo,
    };

    private const string PageGrantRequired = "page_grant_required";

    /// <summary>
    /// 页面授权闸门（方案 §5.2/§5.3）：要求调用者在该目标页上持有满足最小范围的**有效页面授权**。
    /// 返回 <c>true</c> 表示**拒绝**（并给出原因）；无页面目标的命令不在此闸门范围。
    /// </summary>
    private bool TryAuthorizePageCommand(BrowserBridgeCommand command, out string? reason)
    {
        reason = null;

        if (string.IsNullOrEmpty(command.ContextId) || string.IsNullOrEmpty(command.PageId))
        {
            return false;
        }

        var wire = command.Caller;
        var kind = wire?.Kind switch
        {
            "agent" => BrowserCallerKind.Agent,
            "sub_agent" => BrowserCallerKind.SubAgent,
            _ => (BrowserCallerKind?)null,
        };

        if (kind is null || string.IsNullOrWhiteSpace(wire!.CallerId) || string.IsNullOrWhiteSpace(wire.TaskId))
        {
            reason = "browser page commands must carry a trusted caller identity";
            return true;
        }

        var caller = new BrowserCallerIdentity(
            kind.Value,
            wire.CallerId,
            wire.TaskId,
            string.IsNullOrWhiteSpace(wire.ParentTaskId) ? null : wire.ParentTaskId);

        var required = ReadOnlyPageCommands.Contains(command.Name)
            ? BrowserPageGrantScope.Read
            : BrowserPageGrantScope.Write;
        var target = new DesktopPageTarget(command.ContextId, command.PageId);
        var now = DateTimeOffset.UtcNow;

        var granted = _lifecycle.Authority.ActiveGrants.Any(grant =>
            grant.Target == target
            && grant.Grantee.IsSameTaskScope(caller)
            && (grant.Scope & required) == required
            && (grant.ExpiresAtUtc is null || grant.ExpiresAtUtc > now));

        if (!granted)
        {
            reason = $"no {(required == BrowserPageGrantScope.Read ? "read" : "write")} page grant for this caller on {target.Key}";
            return true;
        }

        return false;
    }

    /// <summary>调用者身份键（方案 §4.2）：幂等缓存与去重都按"**谁发起**"隔离，不跨调用者复用。</summary>
    private static string CallerKey(BrowserBridgeCommand command)
    {
        var caller = command.Caller;
        return string.Concat(
            caller?.Kind ?? string.Empty, "\u001f",
            caller?.CallerId ?? string.Empty, "\u001f",
            caller?.TaskId ?? string.Empty, "\u001f",
            caller?.ParentTaskId ?? string.Empty);
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
    public Guid OperationId { get; }
    public string CommandName { get; }
    public string Target { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; private set; }
    public bool? Success { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? AgentInstanceId { get; }
    public string? SessionId { get; }
    public string? RunId { get; }
    public string? ToolCallId { get; }
    public string? ToolName { get; }

    public AgentBrowserActivity(Guid operationId, string commandName, string target, BrowserBridgeCommandOrigin? origin = null)
    {
        OperationId = operationId;
        CommandName = commandName;
        Target = target;
        AgentInstanceId = origin?.AgentInstanceId;
        SessionId = origin?.SessionId;
        RunId = origin?.RunId;
        ToolCallId = origin?.ToolCallId;
        ToolName = origin?.ToolName;
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
        ErrorCode = ErrorCode,
        AgentInstanceId = AgentInstanceId,
        SessionId = SessionId,
        RunId = RunId,
        ToolCallId = ToolCallId,
        ToolName = ToolName
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
    public string? AgentInstanceId { get; init; }
    public string? SessionId { get; init; }
    public string? RunId { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public bool IsCompleted => CompletedAt.HasValue;
}

public sealed class AgentBrowserActivityChangedEventArgs : EventArgs
{
    public AgentBrowserActivitySnapshot Snapshot { get; }

    public AgentBrowserActivityChangedEventArgs(AgentBrowserActivitySnapshot snapshot)
        => Snapshot = snapshot;
}

/// <summary>
/// Snapshot of current operation state for automatic control state management (doc 6.2).
/// </summary>
public sealed record AgentBrowserOperationStateSnapshot
{
    public required int ActiveOperationCount { get; init; }
    public BrowserBridgeCommandOrigin? MostRecentOrigin { get; init; }
}

/// <summary>
/// Event args for OperationStateChanged, carrying the current operation state snapshot.
/// </summary>
public sealed class AgentBrowserOperationStateChangedEventArgs : EventArgs
{
    public AgentBrowserOperationStateSnapshot Snapshot { get; }

    public AgentBrowserOperationStateChangedEventArgs(AgentBrowserOperationStateSnapshot snapshot)
        => Snapshot = snapshot;
}
