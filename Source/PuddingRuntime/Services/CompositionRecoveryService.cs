using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

/// <summary>
/// C01-A：Composition 恢复状态。用于替代「恢复失败静默降级为空集合即成功」的隐式语义。
/// </summary>
public enum CompositionRecoveryStatus
{
    /// <summary>没有任何可恢复来源（composition store 与持久化版本登记表均未注册）。</summary>
    Skipped = 0,

    /// <summary>恢复来源可用，但该 session 无已持久化记录（合法空态，不是失败）。</summary>
    NoRecord = 1,

    /// <summary>至少恢复出内容（版本预热 / 工具集合水合），且无失败。</summary>
    Recovered = 2,

    /// <summary>存在失败（store busy / 读取异常 / 版本恢复失败等），原因见 <see cref="CompositionRecoveryResult.FailureReason"/>。</summary>
    Failed = 3,
}

/// <summary>
/// C01-A：Composition 恢复结果（明确状态，可被调用方观察）。
/// </summary>
public sealed record CompositionRecoveryResult(
    CompositionRecoveryStatus Status,
    bool VersionsRecovered,
    bool ToolsHydrated,
    int HydratedToolCount,
    string? FailureReason)
{
    /// <summary>是否失败（<see cref="CompositionRecoveryStatus.Failed"/>）。</summary>
    public bool IsFailure => Status == CompositionRecoveryStatus.Failed;

    /// <summary>无恢复来源。</summary>
    public static CompositionRecoveryResult Skipped(string reason) =>
        new(CompositionRecoveryStatus.Skipped, false, false, 0, reason);

    /// <summary>无记录（合法空态）。</summary>
    public static CompositionRecoveryResult NoRecord(string reason) =>
        new(CompositionRecoveryStatus.NoRecord, false, false, 0, reason);

    /// <summary>恢复出内容且无失败。</summary>
    public static CompositionRecoveryResult Recovered(
        bool versionsRecovered,
        bool toolsHydrated,
        int hydratedToolCount) =>
        new(CompositionRecoveryStatus.Recovered, versionsRecovered, toolsHydrated, hydratedToolCount, null);

    /// <summary>恢复失败（部分成功也如实记录在 VersionsRecovered / ToolsHydrated / HydratedToolCount）。</summary>
    public static CompositionRecoveryResult Failure(
        bool versionsRecovered,
        bool toolsHydrated,
        int hydratedToolCount,
        string reason) =>
        new(CompositionRecoveryStatus.Failed, versionsRecovered, toolsHydrated, hydratedToolCount, reason);
}

/// <summary>
/// P0-5 步骤 5 / C01-A：Composition 恢复服务。
///
/// 在 Agent 执行开跑时从 <see cref="ICompositionStore"/> 读取 session 最新 Composition
/// 记录的 <see cref="SessionCompositionRecord.ToolIds"/>，追加水合到
/// <see cref="AgentSessionManager"/> 的进程内 append-only 工具集合，实现跨 1h 超时清理
/// / Core 重启后的工具集合恢复（append-only，不收缩）。
///
/// C01-A 语义（相比早期实现）：
/// - <b>single-flight</b>：同一 session 的并发恢复只执行一次，后续调用复用首个在飞结果，
///   不再各自全量加载该 session 全部记录；
/// - <b>取消传播</b>：恢复路径使用调用方 ct，调用方取消时抛 <see cref="OperationCanceledException"/>，
///   不伪装成成功；
/// - <b>失败有明确状态</b>：读取/恢复失败返回 <see cref="CompositionRecoveryStatus.Failed"/> 与失败原因，
///   不再静默降级为空集合即成功（失败仍不阻断执行，由调用方决定是否忽略）。
/// </summary>
public sealed class CompositionRecoveryService
{
    private readonly ICompositionStore? _compositionStore;
    private readonly AgentSessionManager _sessionManager;
    private readonly ILogger<CompositionRecoveryService>? _logger;
    private readonly PersistentCompositionVersionRegistry? _persistentRegistry;

    // C01-A single-flight：同一 session 的并发恢复复用首个在飞任务。
    // 用 Lazy(ExecutionAndPublication) 保证 GetOrAdd 的工厂即使被并发执行多次，也只有一个主体真正跑恢复。
    private readonly ConcurrentDictionary<string, Lazy<Task<CompositionRecoveryResult>>> _inFlight =
        new(StringComparer.Ordinal);

    public CompositionRecoveryService(
        AgentSessionManager sessionManager,
        ICompositionStore? compositionStore = null,
        ILogger<CompositionRecoveryService>? logger = null,
        PersistentCompositionVersionRegistry? persistentRegistry = null)
    {
        _sessionManager = sessionManager;
        _compositionStore = compositionStore;
        _logger = logger;
        _persistentRegistry = persistentRegistry;
    }

    /// <summary>
    /// 从持久化数据恢复 session 状态：先恢复 composition 版本（P0-5 缺陷修复，写穿继续单调递增），
    /// 再水合工具集合（append-only）。
    ///
    /// 并发去重：同一 session 同时在飞的恢复只执行一次（single-flight），等待方复用首个结果。
    /// 取消：调用方 ct 全程传播；调用方取消时抛 <see cref="OperationCanceledException"/>。
    /// 失败：返回 <see cref="CompositionRecoveryStatus.Failed"/>（含原因），不抛异常、不阻断执行。
    /// </summary>
    public Task<CompositionRecoveryResult> RecoverAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Task.FromResult(CompositionRecoveryResult.Skipped("session id is empty"));

        var lazy = _inFlight.GetOrAdd(
            sessionId,
            key => new Lazy<Task<CompositionRecoveryResult>>(
                () => RecoverCoreAsync(key, ct),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return AwaitSingleFlightAsync(sessionId, lazy, ct);
    }

    /// <summary>
    /// 等待 single-flight 结果：等待方自身的取消通过 <see cref="Task.WaitAsync(CancellationToken)"/> 生效
    /// （不吞掉取消、也不把取消返回成成功状态）。
    /// </summary>
    private async Task<CompositionRecoveryResult> AwaitSingleFlightAsync(
        string sessionId,
        Lazy<Task<CompositionRecoveryResult>> lazy,
        CancellationToken ct)
    {
        try
        {
            return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // 完成后出队：后续调用视为新一轮恢复（该 session 可能已有更新的持久化记录）。
            if (lazy.IsValueCreated
                && lazy.Value.IsCompleted
                && _inFlight.TryGetValue(sessionId, out var current)
                && ReferenceEquals(current, lazy))
            {
                _inFlight.TryRemove(sessionId, out _);
            }
        }
    }

    /// <summary>
    /// 真正执行一次恢复（single-flight 主体）。取消向上传播；读取/恢复失败转为显式失败结果。
    /// </summary>
    private async Task<CompositionRecoveryResult> RecoverCoreAsync(string sessionId, CancellationToken ct)
    {
        if (_persistentRegistry is null && _compositionStore is null)
            return CompositionRecoveryResult.Skipped("no composition recovery source registered");

        var failures = new List<string>();

        // 1) 版本恢复：同组合复用已持久化版本号，新组合从 max+1 继续。
        var versionsRecovered = false;
        if (_persistentRegistry is not null)
        {
            try
            {
                var versionResult = await _persistentRegistry.RecoverFromStoreAsync(sessionId, ct).ConfigureAwait(false);
                versionsRecovered = versionResult.RecordsFound;
                if (versionResult.FailureReason is not null)
                    failures.Add($"version recovery failed: {versionResult.FailureReason}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // C01-A：调用方取消必须向上传播，不得伪装成成功
            }
            catch (Exception ex)
            {
                failures.Add($"version recovery failed: {ex.GetType().Name}: {ex.Message}");
                _logger?.LogWarning(
                    ex,
                    "[CompositionRecovery] Failed to recover composition versions for session={Session} — continuing with tool hydration",
                    sessionId);
            }
        }

        // 2) 工具集合水合（append-only 不收缩）。单项失败不阻断另一项，但状态如实上报。
        var toolsHydrated = false;
        var hydratedToolCount = 0;
        if (_compositionStore is not null)
        {
            try
            {
                var record = await _compositionStore.GetLatestAsync(sessionId, ct).ConfigureAwait(false);
                if (record?.ToolIds is { Count: > 0 })
                {
                    _sessionManager.HydrateToolIds(sessionId, record.ToolIds);
                    toolsHydrated = true;
                    hydratedToolCount = record.ToolIds.Count;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // C01-A：调用方取消必须向上传播，不得伪装成成功
            }
            catch (Exception ex)
            {
                failures.Add($"tool hydration failed: {ex.GetType().Name}: {ex.Message}");
                _logger?.LogWarning(
                    ex,
                    "[CompositionRecovery] Failed to hydrate tool ids for session={Session}",
                    sessionId);
            }
        }

        if (failures.Count > 0)
            return CompositionRecoveryResult.Failure(
                versionsRecovered, toolsHydrated, hydratedToolCount, string.Join("; ", failures));

        if (!versionsRecovered && !toolsHydrated)
            return CompositionRecoveryResult.NoRecord(
                "no persisted composition version or tool ids for session");

        return CompositionRecoveryResult.Recovered(versionsRecovered, toolsHydrated, hydratedToolCount);
    }
}
