using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

/// <summary>
/// 持久化写穿的 composition 版本登记表（P0-5 步骤 2）。
///
/// 职责：
/// - 热路径只走内存：组合 <see cref="CompositionVersionRegistry"/>（纯内存）做版本分配/复用，
///   <see cref="Observe"/> 同步返回，写穿异步 fire-and-forget，不阻塞调用方；
/// - 仅当该 session 出现「新版本号」（版本单调递增、首次出现）时，异步写穿一条
///   <see cref="SessionCompositionRecord"/> 到 <see cref="ICompositionStore"/>（append-only）；
/// - 相同 hash 组合复用版本 → 不触发写穿（内存命中零 IO）；
/// - 写穿失败（AppendAsync 返回 false / 抛异常）不阻断 Observe 返回，仅记录日志，降级为纯内存。
///
/// 只持久化 SHA-256 指纹与元数据，绝不保存 prompt/tool schema 正文（对齐原文不脱敏原则）。
/// 构造函数允许 <paramref name="store"/> 为 null：此时整体退化为纯内存登记表。
/// </summary>
public sealed class PersistentCompositionVersionRegistry : ICompositionVersionRegistry
{
    private readonly ICompositionVersionRegistry _inner;
    private readonly ICompositionStore? _store;
    private readonly ILogger<PersistentCompositionVersionRegistry>? _logger;
    private readonly ConcurrentDictionary<string, long> _persistedVersions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeGates = new(StringComparer.Ordinal);

    public PersistentCompositionVersionRegistry(
        ICompositionStore? store,
        ILogger<PersistentCompositionVersionRegistry>? logger = null)
    {
        _store = store;
        _logger = logger;
        _inner = new CompositionVersionRegistry();
    }

    /// <inheritdoc />
    public CompositionObservation Observe(
        string sessionId,
        string systemPromptHash,
        string toolSpecHash,
        IReadOnlyList<string>? toolIds = null,
        int permissionEpoch = 0,
        string? skillManifestHash = null)
    {
        var observation = _inner.Observe(sessionId, systemPromptHash, toolSpecHash, toolIds, permissionEpoch, skillManifestHash);

        if (_store is null)
            return observation; // 无 store：纯内存降级

        // 仅在新版本号出现时异步写穿；相同组合复用版本 → 热路径只查内存，零 IO。
        if (observation.Version > _persistedVersions.GetValueOrDefault(sessionId))
            _ = WriteThroughAsync(sessionId, systemPromptHash, toolSpecHash, observation, toolIds, permissionEpoch, skillManifestHash);

        return observation;
    }

    /// <summary>异步写穿一条记录；失败仅记日志，不影响 Observe 返回值。</summary>
    private async Task WriteThroughAsync(
        string sessionId,
        string systemPromptHash,
        string toolSpecHash,
        CompositionObservation observation,
        IReadOnlyList<string>? toolIds,
        int permissionEpoch,
        string? skillManifestHash)
    {
        var gate = _writeGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // 双重检查：并发下可能已有更新的版本完成写穿（或当前版本已被更高版本覆盖）。
            if (observation.Version <= _persistedVersions.GetValueOrDefault(sessionId))
                return;

            var record = new SessionCompositionRecord
            {
                SessionId = sessionId,
                CompositionVersion = observation.Version,
                SystemPromptHash = systemPromptHash,
                ToolSpecHash = toolSpecHash,
                PrefixHash = CompositionSnapshot.ComputePrefixHash(systemPromptHash, toolSpecHash),
                SkillManifestHash = skillManifestHash,
                ToolIds = toolIds ?? Array.Empty<string>(),
                ChangeReason = observation.ChangeReason,
                PermissionEpoch = permissionEpoch,
                CanonicalSystemPrefixHash = null,
            };

            var ok = await _store!.AppendAsync(record).ConfigureAwait(false);
            if (ok)
            {
                _persistedVersions[sessionId] = observation.Version;
            }
            else
            {
                _logger?.LogWarning(
                    "[CompositionRegistry] append rejected (session={SessionId} version={Version}); degraded to in-memory",
                    sessionId,
                    observation.Version);
            }
        }
        catch (Exception ex)
        {
            // 写穿失败不阻断 Observe：仅记录，降级为纯内存。
            _logger?.LogDebug(
                ex,
                "[CompositionRegistry] write-through failed (session={SessionId} version={Version}); degraded to in-memory",
                sessionId,
                observation.Version);
        }
        finally
        {
            gate.Release();
        }
    }
}
