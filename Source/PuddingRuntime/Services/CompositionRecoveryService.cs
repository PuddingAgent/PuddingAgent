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
/// C01-B-3：新增 <see cref="SchemaExactRestore"/> / <see cref="UnresolvedToolIds"/>，
/// 区分「定义级精确恢复」与「仅 ID 级恢复」（<see cref="HydratedToolCount"/> 不足以证明定义等价）。
/// </summary>
public sealed record CompositionRecoveryResult(
    CompositionRecoveryStatus Status,
    bool VersionsRecovered,
    bool ToolsHydrated,
    int HydratedToolCount,
    string? FailureReason)
{
    /// <summary>
    /// 是否可判定为**定义级精确恢复**（C01-B-3 / R13）：
    /// true ⟺ 记录中的**每一条** <see cref="SessionCompositionRecord.ToolBindings"/> 项
    /// 都能在当前 catalog 找到**同 definitionHash** 的定义。
    /// false 表示「无法证明定义等价」（历史行 ToolBindings 为 NULL、工具已不存在、定义哈希不符、
    /// 或未接线 catalog）——此时**不得谎称精确恢复**，也不得因此阻塞会话（R15）。
    /// </summary>
    public bool SchemaExactRestore { get; init; }

    /// <summary>
    /// 已**证明**无法解析的工具定义 ID（工具已不存在或 canonical 定义哈希不符），按绑定顺序、去重。
    /// 仅当 <see cref="SchemaExactRestore"/> 为 false 时可能非空；
    /// 历史行（NULL 绑定）无任何「被证明缺失/不符」的项，因此为空（不可恢复的是「证据」本身，不是具体工具）。
    /// </summary>
    public IReadOnlyList<string> UnresolvedToolIds { get; init; } = Array.Empty<string>();

    /// <summary>是否失败（<see cref="CompositionRecoveryStatus.Failed"/>）。</summary>
    public bool IsFailure => Status == CompositionRecoveryStatus.Failed;

    /// <summary>无恢复来源。</summary>
    public static CompositionRecoveryResult Skipped(string reason) =>
        new(CompositionRecoveryStatus.Skipped, false, false, 0, reason);

    /// <summary>无记录（合法空态）。</summary>
    public static CompositionRecoveryResult NoRecord(string reason) =>
        new(CompositionRecoveryStatus.NoRecord, false, false, 0, reason);

    /// <summary>恢复出内容且无失败。<paramref name="schemaExactRestore"/> 默认 false（未验证即不得谎称精确）。</summary>
    public static CompositionRecoveryResult Recovered(
        bool versionsRecovered,
        bool toolsHydrated,
        int hydratedToolCount,
        bool schemaExactRestore = false,
        IReadOnlyList<string>? unresolvedToolIds = null) =>
        new(CompositionRecoveryStatus.Recovered, versionsRecovered, toolsHydrated, hydratedToolCount, null)
        {
            SchemaExactRestore = schemaExactRestore,
            UnresolvedToolIds = unresolvedToolIds ?? Array.Empty<string>(),
        };

    /// <summary>恢复失败（部分成功也如实记录在 VersionsRecovered / ToolsHydrated / HydratedToolCount）。</summary>
    public static CompositionRecoveryResult Failure(
        bool versionsRecovered,
        bool toolsHydrated,
        int hydratedToolCount,
        string reason,
        bool schemaExactRestore = false,
        IReadOnlyList<string>? unresolvedToolIds = null) =>
        new(CompositionRecoveryStatus.Failed, versionsRecovered, toolsHydrated, hydratedToolCount, reason)
        {
            SchemaExactRestore = schemaExactRestore,
            UnresolvedToolIds = unresolvedToolIds ?? Array.Empty<string>(),
        };
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
    private readonly IToolDefinitionCatalog? _toolDefinitionCatalog;

    // C01-A single-flight：同一 session 的并发恢复复用首个在飞任务。
    // 用 Lazy(ExecutionAndPublication) 保证 GetOrAdd 的工厂即使被并发执行多次，也只有一个主体真正跑恢复。
    private readonly ConcurrentDictionary<string, Lazy<Task<CompositionRecoveryResult>>> _inFlight =
        new(StringComparer.Ordinal);

    public CompositionRecoveryService(
        AgentSessionManager sessionManager,
        ICompositionStore? compositionStore = null,
        ILogger<CompositionRecoveryService>? logger = null,
        PersistentCompositionVersionRegistry? persistentRegistry = null,
        IToolDefinitionCatalog? toolDefinitionCatalog = null)
    {
        _sessionManager = sessionManager;
        _compositionStore = compositionStore;
        _logger = logger;
        _persistentRegistry = persistentRegistry;
        _toolDefinitionCatalog = toolDefinitionCatalog;
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
        var schemaExactRestore = false;
        var unresolvedToolIds = (IReadOnlyList<string>)Array.Empty<string>();
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

                // C01-B-3 AC4-A：定义级恢复判定（R13）。
                // 不谎称（无法证明 → SchemaExactRestore=false）、不阻塞（不抛、不收缩可见集）。
                (schemaExactRestore, unresolvedToolIds) = EvaluateToolBindings(record, _toolDefinitionCatalog);
                if (!schemaExactRestore && unresolvedToolIds.Count > 0)
                {
                    _logger?.LogWarning(
                        "[CompositionRecovery] tool definition identity not resolvable (session={Session} unresolved={Unresolved}); "
                        + "reason={Reason}; continuing with current definitions (non-blocking)",
                        sessionId,
                        string.Join(",", unresolvedToolIds),
                        CompositionChangeReasons.ToolDefinitionMissing + "/" + CompositionChangeReasons.ToolDefinitionChanged);
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
                versionsRecovered, toolsHydrated, hydratedToolCount, string.Join("; ", failures),
                schemaExactRestore, unresolvedToolIds);

        if (!versionsRecovered && !toolsHydrated)
            return CompositionRecoveryResult.NoRecord(
                "no persisted composition version or tool ids for session");

        return CompositionRecoveryResult.Recovered(
            versionsRecovered, toolsHydrated, hydratedToolCount, schemaExactRestore, unresolvedToolIds);
    }

    /// <summary>
    /// 评估记录中的工具**定义身份**与当前 catalog 的匹配度（C01-B-3 / R13）。
    /// <para>
    /// <c>SchemaExactRestore = true</c> ⟺ <c>record.ToolBindings</c> 非空，且**每一条**绑定都能在当前 catalog
    /// 找到**同 <c>definitionHash</c>** 的定义（哈希口径复用 <see cref="CompositionSnapshot.ComputeToolDefinitionHash"/>）。
    /// </para>
    /// <para>
    /// 不精确的情况（均不得谎称精确，也不得阻塞执行）：
    /// 工具已不存在 / 定义哈希不符 → 记入 <c>UnresolvedToolIds</c>；
    /// 绑定为空或 NULL（历史行 / 未接线）→ <c>false</c> 且 <c>UnresolvedToolIds</c> 为空
    /// （不可恢复的是「证据」本身，不是任何具体工具，不得虚构缺失项）；
    /// 未接线 catalog → <c>false</c>（无法证明），<c>UnresolvedToolIds</c> 为空。
    /// </para>
    /// </summary>
    internal static (bool SchemaExactRestore, IReadOnlyList<string> UnresolvedToolIds) EvaluateToolBindings(
        SessionCompositionRecord? record,
        IToolDefinitionCatalog? catalog)
    {
        var bindings = record?.ToolBindings;
        if (bindings is null || bindings.Count == 0 || catalog is null)
            return (false, Array.Empty<string>());

        var definitions = catalog.GetAvailableToolDefinitions();
        if (definitions is null || definitions.Count == 0)
            return (false, Array.Empty<string>());

        var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
            byId[definition.Name] = CompositionSnapshot.ComputeToolDefinitionHash(definition);

        var unresolved = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            if (binding is null || string.IsNullOrWhiteSpace(binding.ToolId))
                continue;

            // 工具已不存在，或定义已变更（canonical 哈希不符）→ 均不得当作可精确恢复。
            if (!byId.TryGetValue(binding.ToolId, out var currentHash)
                || !string.Equals(currentHash, binding.DefinitionHash, StringComparison.Ordinal))
            {
                if (seen.Add(binding.ToolId))
                    unresolved.Add(binding.ToolId);
            }
        }

        return (unresolved.Count == 0, unresolved);
    }
}
