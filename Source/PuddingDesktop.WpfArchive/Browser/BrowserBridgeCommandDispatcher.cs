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
///
/// 职责边界（ADR-097 D3/D4，2026-10-18）：
/// · **准入**只有一处：会话权威（经 <see cref="BrowserPageLifecycle"/> 的标量门面）；
///   owner 来自宿主注入的 Origin，首条可信命令确认交页归属，触碰 handler 前复检同一票据，
///   所有退出路径精确释放；
/// · **幂等**只有一处：原子执行账本（同键同指纹等待/复用，异载荷冲突）；
/// · **暂停/接管**只阻断副作用（只读观察继续），运行控制状态读进程内唯一权威。
///
/// 本类是 <see cref="IBrowserAutomationGate"/> 的实现之一：它的 pause/takeover 就是**执行闸门本身**。
/// 在它只是"自己的一个字段"时，Shell 的接管开关从来不写它，于是 Bridge 路径上的人工接管无效
/// （审计 2026-10-08 结论 #1）。现在它的控制状态**不再自己持有**：读写的是进程内唯一权威实例
/// （经由 <see cref="BrowserPageLifecycle"/>，与能力通道、生命周期清理点同一个对象）。
/// </summary>
public sealed class BrowserBridgeCommandDispatcher : IBrowserAutomationGate
{
    // 重放**载荷**存储（去重权威是 _ledger）：只在账本 ReplayCompleted 分支内被查询 ⇒ 不构成跨调用者/跨世代复用入口（见 BrowserOperationResultCache 的类文档）。
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

    // I2-c：执行器路由（迁移期可选；null=全部走 handler）。
    private BrowserCommandExecutorRoute? _executorRoute;

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

    /// <summary>会话控制面变化（交页待确认 → 可信会话确认归属、撤销、暂停/接管等）。</summary>
    public event EventHandler<BrowserSessionChangedEventArgs>? SessionChanged;

    private string? _lastSessionStatusKey;

    /// <summary>
    /// 把会话控制面快照推给 UI（只在可观察内容变化时推）：UI 必须能区分
    /// “等待会话确认 / 已交出 / 无目标”，而不是点了按钮就谎称已交出（ADR-097 D3）。
    /// </summary>
    private void PublishSessionStatus()
    {
        var status = _lifecycle.CaptureSessionStatus();
        var key = string.Concat(status.Describe(), "\u001f", status.Reason ?? string.Empty);
        if (string.Equals(_lastSessionStatusKey, key, StringComparison.Ordinal))
        {
            return;
        }

        _lastSessionStatusKey = key;
        try
        {
            SessionChanged?.Invoke(this, new BrowserSessionChangedEventArgs(status));
        }
        catch
        {
            // 订阅者异常不得打断命令执行。
        }
    }

    public void SetHandler(IBrowserCommandHandler handler) => _handler = handler;

    /// <summary>
    /// I2-c 接线缝：把已通过会话准入、且能映射成能力请求的命令交给**工作区执行器**执行。
    ///
    /// 返回 <c>null</c> 表示"本命令不由执行器路由"（回退到 <see cref="IBrowserCommandHandler"/>，
    /// 迁移期语义）；抛异常或返回失败结果都**不得**静默回退（那会双执行/绕过门禁）。
    /// 由 Shell 组合根在浏览器工作区就绪后设置；WpfArchive 不直接引用执行器实现。
    /// </summary>
    public void SetExecutorRoute(BrowserCommandExecutorRoute route) => _executorRoute = route ?? throw new ArgumentNullException(nameof(route));

    /// <summary>执行器路由（诊断用：是否已接线）。</summary>
    public bool HasExecutorRoute => _executorRoute is not null;

    /// <summary>窗口退出/管线释放时清除路由，避免残留执行器引用。</summary>
    public void ClearExecutorRoute() => _executorRoute = null;


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

    /// <summary>
    /// 用户把某页设为（或清除）Agent 目标：下发到准入权威。
    /// 本类是 Bridge 闸门本身，因此它就是那个必须知道目标的执行路径。
    /// 交页后立刻推送一次会话状态：UI 在此之前只能显示“等待会话确认”。
    /// </summary>
    public void SetAgentTarget(string? contextId, string? pageId)
    {
        _lifecycle.SetAgentTarget(contextId, pageId);
        PublishSessionStatus();
    }

    public async Task<BrowserBridgeCommandResult> DispatchAsync(
        BrowserBridgeCommand command,
        CancellationToken externalCt)
    {
        // 方案 §4.2：**账本是唯一幂等来源**。键 = (进程实例, 连接世代, operationId)；
        // 指纹含调用者身份与完整语义载荷 ⇒ 换调用者/换目标/换参数一律冲突，绝不跨调用者复用结果。
        // 真实命名空间（进程实例 + 当前连接世代）——**不得用常量**：世代进键才能保证
        // "重连后的同 ID 落在新命名空间、不被旧世代记录误判为重复"（方案 §4.2）。
        var ledgerNamespace = _lifecycle.LedgerNamespace;
        var key = new BrowserOperationLedgerKey(
            ledgerNamespace.ProcessInstance, ledgerNamespace.Generation, command.OperationId.ToString("n"));
        var fingerprint = Fingerprint(command);

        // 方案 §4.1：deadline 不进指纹，但**每次接收**都要检查"这次请求是否已过期" ——
        // 包括**重放**路径（否则同一个 operationId 在期限之后重传会静默拿到历史结果，
        // 掩盖"本次请求已过期"这一事实）。检查必须位于账本裁决**之前**。
        if (command.DeadlineUtc <= DateTimeOffset.UtcNow)
        {
            return Error(
                command.OperationId,
                BrowserBridgeErrorCodes.BrowserDeadlineExceeded,
                "Command deadline has already passed");
        }

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
                    // 仅在账本已判定"同键 + 同指纹 + 已知终态"之后取载荷（跨调用者/跨世代不可能到达这里）。
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

        // Validate command name
        if (!BrowserBridgeCommandNames.All.Contains(command.Name))
        {
            return CacheAndReturn(Error(command.OperationId,
                BrowserBridgeErrorCodes.BrowserOperationNotSupported,
                $"Unknown command: {command.Name}"));
        }

        // Gate: 暂停/接管是**运行控制**，只阻断副作用；只读观察继续（ADR-097 D4 / 69 规格 §1.1）。
        // 先按命令类别判定，再决定是否拒绝——否则「接管后可读」只在文档里成立，默认 Bridge 路径见不到。
        var readOnlyCommand = ReadOnlyPageCommands.Contains(command.Name);

        if (!readOnlyCommand && IsPaused)
        {
            var pausedActivity = new AgentBrowserActivity(
                command.OperationId, command.Name,
                command.PageId ?? command.ContextId ?? "-", command.Origin);
            pausedActivity.Complete(false, BrowserBridgeErrorCodes.BrowserPaused);
            RecordActivity(pausedActivity);
            return CacheAndReturn(Error(command.OperationId,
                BrowserBridgeErrorCodes.BrowserPaused, "Browser is paused"));
        }

        if (!readOnlyCommand && IsUserTakeover)
        {
            var takeoverActivity = new AgentBrowserActivity(
                command.OperationId, command.Name,
                command.PageId ?? command.ContextId ?? "-", command.Origin);
            takeoverActivity.Complete(false, BrowserBridgeErrorCodes.BrowserUserTakeover);
            RecordActivity(takeoverActivity);
            return CacheAndReturn(Error(command.OperationId,
                BrowserBridgeErrorCodes.BrowserUserTakeover, "User has taken over browser control"));
        }

        // Deadline check
        if (command.DeadlineUtc <= DateTimeOffset.UtcNow)
        {
            return CacheAndReturn(Error(command.OperationId,
                BrowserBridgeErrorCodes.BrowserDeadlineExceeded, "Command deadline has already passed"));
        }

        // ADR-097 D3/D4：Bridge 页面/管理命令的唯一准入在**会话权威**（BrowserPageLifecycle 背后的
        // Browser Session Authority）：用户交页 + 宿主注入的可信 owner + 只读/写分级 + 写世代/唯一票据。
        // 无 PageId 的**发现类**命令不要求目标（否则会形成“看不到页面所以选不了页面”的循环）。
        var gate = AuthorizeSessionCommand(command);

        // 首条可信命令可能刚刚确认了交页归属 ⇒ 推送一次会话状态（UI 从“等待确认”切到“已交出”）。
        PublishSessionStatus();

        if (!gate.Allowed)
        {
            var deniedActivity = new AgentBrowserActivity(
                command.OperationId, command.Name,
                command.PageId ?? command.ContextId ?? "-", command.Origin);
            deniedActivity.Complete(false, gate.ErrorCode);
            RecordActivity(deniedActivity);
            return CacheAndReturn(Error(command.OperationId, gate.ErrorCode!, gate.Message!));
        }

        var permit = gate.Permit;

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
            // 触碰页面前**复检同一张票据**：入队期间可能被接管/暂停/切目标/换代/释放。
            if (permit is { } admitted)
            {
                var revalidated = _lifecycle.RevalidatePageCommand(admitted);
                if (!revalidated.Allow)
                {
                    activity.Complete(false, revalidated.ErrorCode);
                    PublishActivity(activity);
                    return CacheAndReturn(Error(
                        command.OperationId, revalidated.ErrorCode!, revalidated.Message!));
                }
            }

            BrowserBridgeCommandResult result;

            // I2-c：已接线的命令优先经执行器（会话准入/复检已在上方完成；执行器侧不再重复裁决）。
            // 路由返回 null = 该命令不在执行器覆盖面内 ⇒ 回退 handler（迁移期）。异常不回退。
            if (_executorRoute is not null
                && await _executorRoute(command, linkedCts.Token).ConfigureAwait(false) is { } routed)
            {
                result = routed;
            }
            else if (_handler is null)
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
            // 所有退出路径精确释放这张票据（取消/超时/异常都一样；重复释放是幂等的）。
            if (permit is { } admitted)
            {
                try
                {
                    _lifecycle.CompletePageCommand(admitted);
                }
                catch
                {
                    // 释放失败不得掩盖原结果；账本已按失败/未知处置。
                }
            }

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

    /// <summary>
    /// 无需先交出页面即可执行的**引导/发现类**命令：
    /// · 只读发现（context.list / context.getInfo / page.list）——启动流程要求“没目标也能看见页面”；
    /// · context.create —— 在尚未交出任何页面时创建隔离的 Agent 上下文本身就是引导步骤，
    ///   它不触碰任何既有页面；其余管理/页面命令仍必须绑定已交出的目标。
    /// </summary>
    private static readonly IReadOnlySet<string> DiscoveryCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        BrowserBridgeCommandNames.ContextList,
        BrowserBridgeCommandNames.ContextGetInfo,
        BrowserBridgeCommandNames.PageList,
        BrowserBridgeCommandNames.ContextCreate,
    };

    /// <summary>会话准入结果（标量；失败带稳定码，成功带不透明票据）。</summary>
    private readonly record struct SessionGateResult(
        bool Allowed,
        BrowserSessionPermit? Permit,
        string? ErrorCode,
        string? Message)
    {
        public static SessionGateResult Ok(BrowserSessionPermit? permit) => new(true, permit, null, null);

        public static SessionGateResult Denied(string errorCode, string message) => new(false, null, errorCode, message);
    }

    /// <summary>
    /// Bridge 路径的**唯一**会话准入（ADR-097 D3/D4）：
    /// ① 发现类无 PageId 命令放行；② 其它命令必须有宿主注入的可信 owner（Origin）；
    /// ③ 首次命令把用户交出的页面**确认归属**给该 owner；④ 之后由会话权威裁决目标/只读写/写世代/票据。
    /// 拒绝码与工具侧统一（agent_target_required / browser_owner_mismatch / browser_busy ...）。
    /// </summary>
    private SessionGateResult AuthorizeSessionCommand(BrowserBridgeCommand command)
    {
        if (string.IsNullOrEmpty(command.PageId) && DiscoveryCommands.Contains(command.Name))
        {
            return SessionGateResult.Ok(null);
        }

        // owner 来自**宿主注入的 Origin**（Core 执行上下文投影），不是模型参数。
        var owner = command.Origin?.ConversationId;
        if (string.IsNullOrWhiteSpace(owner))
        {
            owner = command.Origin?.SessionId;
        }

        if (string.IsNullOrWhiteSpace(owner) || owner.Length > BrowserSessionControlHandle.MaxLength)
        {
            return SessionGateResult.Denied(
                BrowserSessionErrorCodes.ControlHandleRequired,
                "this bridge command carries no trusted core-session owner; the user must hand the page over to a core session");
        }

        var callerKind = command.Caller?.Kind switch
        {
            "agent" => BrowserCallerKind.Agent,
            "sub_agent" => BrowserCallerKind.SubAgent,
            _ => (BrowserCallerKind?)null,
        };

        if (callerKind is null
            || string.IsNullOrWhiteSpace(command.Caller!.CallerId)
            || string.IsNullOrWhiteSpace(command.Caller.TaskId))
        {
            return SessionGateResult.Denied(
                BrowserBridgeErrorCodes.BrowserUnauthorized,
                "browser commands must carry a trusted caller identity");
        }

        var caller = new BrowserCallerIdentity(
            callerKind.Value,
            command.Caller.CallerId,
            command.Caller.TaskId,
            string.IsNullOrWhiteSpace(command.Caller.ParentTaskId) ? null : command.Caller.ParentTaskId);

        // 首次可信命令确认交页归属（已归属其它 owner 时返回 false，随后准入会给出 owner_mismatch）。
        _lifecycle.TryClaimControlOwner(owner!);

        DesktopPageTarget? target;
        if (!string.IsNullOrEmpty(command.PageId))
        {
            if (string.IsNullOrWhiteSpace(command.ContextId))
            {
                return SessionGateResult.Denied(
                    BrowserSessionErrorCodes.TargetRequired,
                    "page commands must carry an explicit context id");
            }

            try
            {
                target = new DesktopPageTarget(command.ContextId, command.PageId);
            }
            catch (ArgumentException)
            {
                return SessionGateResult.Denied(
                    BrowserBridgeErrorCodes.BrowserInvalidCommand,
                    "page command target is not a valid context/page identifier");
            }
        }
        else
        {
            // 无 PageId 的**管理类**命令（page.create / context.*）：绑定当前已确认目标及其上下文，
            // 不允许借会话权限管理别的上下文。
            target = _lifecycle.CaptureSessionTarget();
            if (target is null)
            {
                return SessionGateResult.Denied(
                    BrowserSessionErrorCodes.TargetRequired,
                    "no page has been handed to the agent yet; the user must select one in the desktop UI");
            }

            if (string.IsNullOrWhiteSpace(command.ContextId)
                || !string.Equals(command.ContextId, target.ContextId, StringComparison.Ordinal))
            {
                return SessionGateResult.Denied(
                    BrowserSessionErrorCodes.TargetRequired,
                    "management commands must target the handed-over context");
            }
        }

        var requiresWrite = !ReadOnlyPageCommands.Contains(command.Name);
        var decision = _lifecycle.AuthorizePageCommand(
            caller,
            BrowserSessionControlHandle.Require(owner!),
            target,
            requiresWrite);

        return decision.Allow
            ? SessionGateResult.Ok(decision.Permit)
            : SessionGateResult.Denied(
                decision.ErrorCode ?? BrowserSessionErrorCodes.PermitInvalid,
                decision.Message ?? "the browser session refused this command");
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

/// <summary>
/// 会话控制面变化（交页/确认/撤销/暂停/接管）。只携带 Contracts/DesktopService 标量快照。
/// </summary>
public sealed class BrowserSessionChangedEventArgs : EventArgs
{
    public BrowserSessionStatus Status { get; }

    public BrowserSessionChangedEventArgs(BrowserSessionStatus status)
        => Status = status ?? throw new ArgumentNullException(nameof(status));
}
