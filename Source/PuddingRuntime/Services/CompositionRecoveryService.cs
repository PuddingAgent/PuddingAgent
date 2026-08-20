using Microsoft.Extensions.Logging;
using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

/// <summary>
/// P0-5 步骤 5：Composition 恢复服务。
///
/// 在 Agent 执行开跑时从 <see cref="ICompositionStore"/> 读取 session 最新 Composition
/// 记录的 <see cref="SessionCompositionRecord.ToolIds"/>，追加水合到
/// <see cref="AgentSessionManager"/> 的进程内 append-only 工具集合，实现跨 1h 超时清理
/// / Core 重启后的工具集合恢复（append-only，不收缩）。
///
/// 恢复失败（store 未注册 / 读取异常 / 记录为空）一律静默降级为空集合，不阻断执行。
/// </summary>
public sealed class CompositionRecoveryService
{
    private readonly ICompositionStore? _compositionStore;
    private readonly AgentSessionManager _sessionManager;
    private readonly ILogger<CompositionRecoveryService>? _logger;
    private readonly PersistentCompositionVersionRegistry? _persistentRegistry;

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
    /// 再水合工具集合（append-only）。任何失败都静默降级，不抛给调用方、不阻断执行；
    /// 版本恢复失败不阻断工具集合恢复（各自 try/catch）。
    /// </summary>
    public async Task RecoverAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        // 1) 先恢复 composition 版本：同组合复用已持久化版本号，新组合从 max+1 继续。
        if (_persistentRegistry is not null)
        {
            try
            {
                await _persistentRegistry.RecoverFromStoreAsync(sessionId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 调用方主动取消：静默返回，不阻断执行。
                _logger?.LogDebug(
                    "[CompositionRecovery] Version recovery cancelled for session={Session}",
                    sessionId);
                return;
            }
            catch (Exception ex)
            {
                // 版本恢复失败：不阻断工具集合恢复（降级为仅水合工具）。
                _logger?.LogWarning(
                    ex,
                    "[CompositionRecovery] Failed to recover composition versions for session={Session} — continuing with tool hydration",
                    sessionId);
            }
        }

        // 2) 工具集合水合（原有逻辑，append-only 不收缩）。
        if (_compositionStore is null)
            return;

        try
        {
            var record = await _compositionStore.GetLatestAsync(sessionId, ct);
            if (record?.ToolIds is not { Count: > 0 })
                return;

            _sessionManager.HydrateToolIds(sessionId, record.ToolIds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 调用方主动取消：静默返回，不阻断执行。
            _logger?.LogDebug(
                "[CompositionRecovery] Recovery cancelled for session={Session}",
                sessionId);
        }
        catch (Exception ex)
        {
            // 恢复失败不阻断执行，静默降级为空集合（append-only 不收缩）。
            _logger?.LogWarning(
                ex,
                "[CompositionRecovery] Failed to hydrate tool ids for session={Session} — degrading to empty set",
                sessionId);
        }
    }
}
