using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

/// <summary>
/// C01-A：版本恢复结果。替代原 void 返回值 + 「静默降级为纯内存」——读取/恢复失败可被调用方观察。
/// </summary>
public readonly record struct CompositionVersionRecoveryResult(
    bool StoreAvailable,
    bool RecordsFound,
    long MaxPersistedVersion,
    string? FailureReason)
{
    /// <summary>是否失败（存在失败原因）。</summary>
    public bool IsFailure => FailureReason is not null;

    /// <summary>未注册 store（纯内存降级，非失败）。</summary>
    public static CompositionVersionRecoveryResult NotAvailable => new(false, false, 0, null);

    /// <summary>store 可用但该 session 无记录（合法空态，非失败）。</summary>
    public static CompositionVersionRecoveryResult NoRecords => new(true, false, 0, null);

    /// <summary>恢复成功（已预热到 <paramref name="maxPersistedVersion"/>）。</summary>
    public static CompositionVersionRecoveryResult Recovered(long maxPersistedVersion) =>
        new(true, true, maxPersistedVersion, null);

    /// <summary>恢复失败（读取异常 / 下游取消等）。</summary>
    public static CompositionVersionRecoveryResult Failed(string reason) => new(true, false, 0, reason);
}

/// <summary>
/// 持久化写穿的 composition 版本登记表（P0-5 步骤 2 / C01-B）。
///
/// 职责：
/// - 热路径只走内存：组合 <see cref="CompositionVersionRegistry"/>（纯内存）做 revision 分配/内容身份派生，
///   <see cref="Observe"/> 同步返回；
/// - **提交是 CAS**：写穿携带 <c>expectedRevision</c>，由 <see cref="ICompositionStore.AppendAsync"/> 单事务条件插入；
///   结果区分 Committed / Conflict / Unavailable（R4）；
/// - **失败可观察（R5，AC7）**：写穿失败不再「静默降级为纯内存继续」——
///   失败计入 <see cref="WriteThroughFailureCount"/> 并记录在 <see cref="TryGetLastCommitResult"/> 中；
///   「必须执行」路径（轮边界）应改用 <see cref="CommitAsync"/>，直接拿到可重试的
///   <c>composition_commit_unavailable</c> 结构化结果，不得静默发出未提交形状。
/// - 取消照常传播（R6）：调用方 ct 取消 → <see cref="OperationCanceledException"/>，不吞成纯内存继续。
///
/// 只持久化指纹与元数据（含 ContentId 内容身份），绝不保存 prompt/tool schema 正文。
/// 构造函数允许 <paramref name="store"/> 为 null：此时整体退化为纯内存登记表。
/// </summary>
public sealed class PersistentCompositionVersionRegistry : ICompositionVersionRegistry
{
    // 用具体类型（而非接口）持有内存注册表：RecoverFromStoreAsync / 冲突重读需要调用 Seed 预热 revision 下界。
    private readonly CompositionVersionRegistry _inner;
    private readonly ICompositionStore? _store;
    private readonly ILogger<PersistentCompositionVersionRegistry>? _logger;
    private readonly ConcurrentDictionary<string, long> _persistedVersions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CompositionAppendResult> _lastCommitResults = new(StringComparer.Ordinal);
    private long _writeThroughFailureCount;

    /// <summary>
    /// 写穿/提交失败（Unavailable / 抛异常）累计次数。
    /// 「失败可观察」的计数面：不再有静默降级，失败至少在这里可见。
    /// </summary>
    public long WriteThroughFailureCount => Interlocked.Read(ref _writeThroughFailureCount);

    public PersistentCompositionVersionRegistry(
        ICompositionStore? store,
        ILogger<PersistentCompositionVersionRegistry>? logger = null)
    {
        _store = store;
        _logger = logger;
        _inner = new CompositionVersionRegistry();
    }

    /// <summary>最近一次提写结果（可观察面）；该 session 尚无提交记录时返回 null。</summary>
    public CompositionAppendResult? TryGetLastCommitResult(string sessionId)
        => _lastCommitResults.TryGetValue(sessionId, out var result) ? result : null;

    /// <inheritdoc />
    public CompositionObservation Observe(
        string sessionId,
        string systemPromptHash,
        string toolSpecHash,
        IReadOnlyList<string>? toolIds = null,
        int permissionEpoch = 0,
        string? skillManifestHash = null,
        string? permissionFingerprint = null,
        string? canonicalSystemPrefixHash = null)
    {
        var observation = _inner.Observe(sessionId, systemPromptHash, toolSpecHash, toolIds, permissionEpoch, skillManifestHash, permissionFingerprint);

        if (_store is null)
            return observation; // 无 store：未注册持久化（非失败）

        // 每次观测都有新的 revision → 尽力写穿（CAS）。失败不阻断热路径，但必须可观察（计数 + 最近结果）。
        _ = WriteThroughAsync(sessionId, systemPromptHash, toolSpecHash, observation, toolIds, skillManifestHash, canonicalSystemPrefixHash);

        return observation;
    }

    /// <summary>
    /// 「必须执行」提交路径（C01-B R5 / AC7）：把一次观测以 CAS 语义提交到 store，
    /// 返回结构化结果（Committed / Conflict / Unavailable）。轮边界调用方应据此决定是否允许 Provider 调用。
    /// - 该 revision 已提交 → 幂等返回 <see cref="CompositionAppendOutcome.Committed"/>，不重复写；
    /// - 存储不可用 → <see cref="CompositionAppendOutcome.Unavailable"/>（可重试 <c>composition_commit_unavailable</c>）；
    /// - CAS 冲突 → <see cref="CompositionAppendOutcome.Conflict"/>（回报 expected/actual），并重读 head 重算基线。
    /// 调用方 ct 取消 → 抛 <see cref="OperationCanceledException"/>（R6 取消传播，不吞成纯内存继续）。
    /// </summary>
    public async Task<CompositionAppendResult> CommitAsync(
        string sessionId,
        CompositionObservation observation,
        string systemPromptHash,
        string toolSpecHash,
        IReadOnlyList<string>? toolIds = null,
        string? skillManifestHash = null,
        string? canonicalSystemPrefixHash = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_store is null)
            return CompositionAppendResult.Unavailable("composition store not configured");

        var gate = _writeGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CommitCoreAsync(
                sessionId, systemPromptHash, toolSpecHash, observation, toolIds, skillManifestHash, canonicalSystemPrefixHash, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 从 store 恢复 session 已持久化 revision（跨重启，P0-5 缺陷修复）：
    /// 用全量记录抬高内存注册表的 revision 下界（不复用旧 revision，只防倒退/分叉），
    /// 并同步 <see cref="_persistedVersions"/> 到已持久化最大 revision，保证后续 CAS 的
    /// <c>expectedRevision</c> 与实际 head 一致。
    /// store 为 null（<see cref="CompositionVersionRecoveryResult.NotAvailable"/>）/ session 无记录
    /// （<see cref="CompositionVersionRecoveryResult.NoRecords"/>）/ 读取异常（<see cref="CompositionVersionRecoveryResult.Failed"/>）：
    /// 不抛给调用方，但以显式状态返回（C01-A「失败有明确状态」，不再静默降级为成功）。
    /// 调用方 ct 已取消时抛 <see cref="OperationCanceledException"/>（C01-A 取消传播）。
    /// </summary>
    public async Task<CompositionVersionRecoveryResult> RecoverFromStoreAsync(string sessionId, CancellationToken ct = default)
    {
        if (_store is null)
            return CompositionVersionRecoveryResult.NotAvailable;
        if (string.IsNullOrWhiteSpace(sessionId))
            return CompositionVersionRecoveryResult.NoRecords;

        try
        {
            var records = await _store.LoadAsync(sessionId, ct).ConfigureAwait(false);
            if (records is null || records.Count == 0)
                return CompositionVersionRecoveryResult.NoRecords;

            _inner.Seed(sessionId, records);
            var maxPersistedVersion = records.Max(r => r.CompositionVersion);
            _persistedVersions[sessionId] = maxPersistedVersion;
            return CompositionVersionRecoveryResult.Recovered(maxPersistedVersion);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // C01-A 取消传播：调用方取消必须可观察，不得伪装成成功/静默降级。
            throw;
        }
        catch (OperationCanceledException)
        {
            // 下游取消但调用方未取消：保持「不抛」约定，但改为显式失败状态（不再静默成功）。
            return CompositionVersionRecoveryResult.Failed("load cancelled downstream (caller not cancelled)");
        }
        catch (Exception ex)
        {
            // 版本恢复失败不抛（不阻断调用方），但以显式失败状态返回，由调用方观察。
            _logger?.LogWarning(
                ex,
                "[CompositionRegistry] version recovery failed (session={SessionId}); degraded to in-memory",
                sessionId);
            return CompositionVersionRecoveryResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>尽力写穿（fire-and-forget，热路径不阻塞）；失败可观察（计数 + 最近结果），不静默降级。</summary>
    private async Task WriteThroughAsync(
        string sessionId,
        string systemPromptHash,
        string toolSpecHash,
        CompositionObservation observation,
        IReadOnlyList<string>? toolIds,
        string? skillManifestHash,
        string? canonicalSystemPrefixHash)
    {
        var gate = _writeGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CommitCoreAsync(
                sessionId, systemPromptHash, toolSpecHash, observation, toolIds, skillManifestHash, canonicalSystemPrefixHash, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 热路径不抛：只记录 + 计数（可观察），不再宣称「降级为纯内存即为正常」。
            Interlocked.Increment(ref _writeThroughFailureCount);
            _lastCommitResults[sessionId] = CompositionAppendResult.Unavailable($"{ex.GetType().Name}: {ex.Message}");
            _logger?.LogWarning(
                ex,
                "[CompositionRegistry] write-through failed (session={SessionId} revision={Revision}); commit is observable and retryable",
                sessionId,
                observation.Revision);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>CAS 提交核心（调用方保证已持有 per-session 写门）。</summary>
    private async Task<CompositionAppendResult> CommitCoreAsync(
        string sessionId,
        string systemPromptHash,
        string toolSpecHash,
        CompositionObservation observation,
        IReadOnlyList<string>? toolIds,
        string? skillManifestHash,
        string? canonicalSystemPrefixHash,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 幂等：该 revision 已提交过（例如 Observe 的尽力写穿已成功）→ 直接成功，不重复写。
        if (observation.Revision <= _persistedVersions.GetValueOrDefault(sessionId))
            return CompositionAppendResult.Committed(observation.Revision);

        var record = new SessionCompositionRecord
        {
            SessionId = sessionId,
            CompositionVersion = observation.Revision,
            ContentId = observation.ContentId,
            SystemPromptHash = systemPromptHash,
            ToolSpecHash = toolSpecHash,
            PrefixHash = CompositionSnapshot.ComputePrefixHash(systemPromptHash, toolSpecHash),
            SkillManifestHash = skillManifestHash,
            ToolIds = toolIds ?? Array.Empty<string>(),
            ChangeReason = observation.ChangeReason,
            // P0-5 step 4c：以注册表内部检测后的权限纪元为准（含指纹变化自增）；显式传入值作为下限。
            PermissionEpoch = observation.PermissionEpoch,
            // C01-B AC4：曝光纪元与权限纪元分离落库（工具发现只推进 ExposureRevision）。
            ExposureRevision = observation.ExposureRevision,
            CanonicalSystemPrefixHash = canonicalSystemPrefixHash,
        };

        // CAS：expectedRevision = 本进程已知 head。不得用「查 MAX 再 INSERT」代替预期版本检查。
        var expectedRevision = _persistedVersions.GetValueOrDefault(sessionId);

        CompositionAppendResult result;
        try
        {
            result = await _store!.AppendAsync(record, expectedRevision, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // R6：取消传播
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _writeThroughFailureCount);
            var failed = CompositionAppendResult.Unavailable($"{ex.GetType().Name}: {ex.Message}");
            _lastCommitResults[sessionId] = failed;
            _logger?.LogWarning(
                ex,
                "[CompositionRegistry] append failed (session={SessionId} revision={Revision} expected={Expected}); retryable",
                sessionId,
                observation.Revision,
                expectedRevision);
            return failed;
        }

        _lastCommitResults[sessionId] = result;

        if (result.IsCommitted)
        {
            _persistedVersions[sessionId] = Math.Max(_persistedVersions.GetValueOrDefault(sessionId), result.Revision);
            return result;
        }

        if (result.IsConflict)
        {
            // CAS 冲突：重读 head 并抬高基线，调用方须重算 proposed composition 后重试（01-...md:255）。
            Interlocked.Increment(ref _writeThroughFailureCount);
            _logger?.LogWarning(
                "[CompositionRegistry] CAS conflict (session={SessionId} revision={Revision} expected={Expected} actual={Actual}); re-read and recompute required",
                sessionId,
                observation.Revision,
                result.ExpectedRevision,
                result.ActualRevision);
            await RefreshFromStoreAsync(sessionId, ct).ConfigureAwait(false);
            return result;
        }

        // Unavailable：可重试，不伪装已提交。
        Interlocked.Increment(ref _writeThroughFailureCount);
        _logger?.LogWarning(
            "[CompositionRegistry] append unavailable (session={SessionId} revision={Revision}); reason={Reason}",
            sessionId,
            observation.Revision,
            result.FailureReason);
        return result;
    }

    /// <summary>CAS 冲突后重读 store：抬高 revision 下界与已知 head，供重算 proposed composition。</summary>
    private async Task RefreshFromStoreAsync(string sessionId, CancellationToken ct)
    {
        var records = await _store!.LoadAsync(sessionId, ct).ConfigureAwait(false);
        if (records is null || records.Count == 0)
            return;

        _inner.Seed(sessionId, records);
        _persistedVersions[sessionId] =
            Math.Max(_persistedVersions.GetValueOrDefault(sessionId), records.Max(r => r.CompositionVersion));
    }
}
