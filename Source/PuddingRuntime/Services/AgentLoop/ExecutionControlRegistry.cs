using System.Collections.Concurrent;

namespace PuddingRuntime.Services.AgentLoop;

/// <summary>
/// 执行控制注册表——为每个 Session 维护独立的 CancellationTokenSource 和冻结标志。
///
/// Controller 可通过此注册表向正在运行的 Agent Loop 下发以下控制信号：
///   · Cancel  — 取消执行（执行链在下一个检查点停止）
///   · Freeze  — 冻结（同时取消，进入 Frozen 状态，不自动恢复）
///   · Resume  — 解除冻结标志（恢复执行需重新创建令牌）
/// </summary>
public sealed class ExecutionControlRegistry
{
    private sealed record ControlEntry(CancellationTokenSource Cts, bool Frozen = false);

    private readonly ConcurrentDictionary<string, ControlEntry> _entries = new();

    /// <summary>
    /// 为指定 Session 创建与外部 ct 联结的 CancellationToken 并注册到表中。
    /// 若 Session 已注册，先安全取消旧令牌再重新创建（防止重入）。
    /// </summary>
    public CancellationToken CreateLinkedToken(string sessionId, CancellationToken external)
    {
        if (_entries.TryRemove(sessionId, out var old))
        {
            try { old.Cts.Cancel(); old.Cts.Dispose(); } catch { /* ignore */ }
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        _entries[sessionId] = new ControlEntry(cts);
        return cts.Token;
    }

    /// <summary>向目标 Session 发出取消信号。</summary>
    public void Cancel(string sessionId)
    {
        if (_entries.TryGetValue(sessionId, out var entry))
            try { entry.Cts.Cancel(); } catch { /* ignore */ }
    }

    /// <summary>冻结目标 Session——同时发出取消信号使 Loop 在下一个检查点停下。</summary>
    public void Freeze(string sessionId)
    {
        if (_entries.TryGetValue(sessionId, out var entry))
        {
            _entries[sessionId] = entry with { Frozen = true };
            try { entry.Cts.Cancel(); } catch { /* ignore */ }
        }
    }

    /// <summary>解除目标 Session 的冻结标志（不自动恢复令牌；恢复执行需重新调用 CreateLinkedToken）。</summary>
    public void Resume(string sessionId)
    {
        if (_entries.TryGetValue(sessionId, out var entry))
            _entries[sessionId] = entry with { Frozen = false };
    }

    /// <summary>查询目标 Session 是否处于冻结状态。</summary>
    public bool IsFrozen(string sessionId) =>
        _entries.TryGetValue(sessionId, out var e) && e.Frozen;

    /// <summary>Session 执行结束后清理控制条目，释放 CancellationTokenSource。</summary>
    public void Remove(string sessionId)
    {
        if (_entries.TryRemove(sessionId, out var entry))
            try { entry.Cts.Dispose(); } catch { /* ignore */ }
    }
}
