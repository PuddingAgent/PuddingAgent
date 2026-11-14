using System.Collections.Concurrent;

namespace PuddingCode.SubAgents;

/// <summary>
/// 子代理运行取消注册表——以 subSessionId 为键，持有真正驱动执行的 CancellationTokenSource，
/// 使外部（父 Agent、池管理器、运维入口）能够中断一个**正在执行中**的子代理。
///
/// 存在理由（缺陷 D1，见 Docs/03_multi_agent/2026-11-14-子代理可控性与多模型适配诊断.md）：
/// 修复前，三个「停止」入口（SubAgentManager.CancelAllAsync / SubAgentPool.SleepAsync /
/// SubAgentPool.DestroyAsync）都只改动状态字段并写归档/日志，从不向执行链发出取消信号；
/// 真正驱动执行的 CancellationTokenSource 是 SubAgentManager 内部的一个 using var 局部变量，
/// 外界无法取得，因此「停止」实际是空转，运行会继续计费直到超时或轮次耗尽。
///
/// 本类型只承担一件事：让那个令牌可被登记、可被取消、可被移除。它不持有任何执行逻辑，
/// 也不解析子代理状态——那是调用方的职责。
///
/// 语义约定：
///   · Register 返回的令牌由调用方喂给执行链；注册是幂等的，同键重复注册会先取消旧令牌。
///   · Cancel 只发信号、不移除条目；条目移除交给 TryRemove，以便取消之后仍可查询"曾登记过"。
///   · TryRemove 会释放底层的 CancellationTokenSource。
/// </summary>
public sealed class SubAgentRunCancellationRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _entries = new(StringComparer.Ordinal);

    /// <summary>当前登记中的运行数量。</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// 为指定子代理运行登记一个可取消令牌，并与外部取消源联结。
    /// 若同一 subSessionId 已登记，先取消并释放旧令牌再重建（防止重入后旧令牌失效导致停不下来）。
    /// </summary>
    /// <param name="subSessionId">子代理会话标识，必须非空。</param>
    /// <param name="external">外部取消源（父 turn 令牌等），其取消会传递到返回的令牌。</param>
    /// <returns>交给执行链使用的令牌。</returns>
    public CancellationToken Register(string subSessionId, CancellationToken external)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subSessionId);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(external);

        // 必须在可能被并发释放之前就取到 token：同键并发登记时，本 cts 可能被另一线程的
        // updateFactory 当成 stale 释放，之后访问 cts.Token 会抛 ObjectDisposedException。
        var token = cts.Token;

        // 注意：AddOrUpdate 返回的是字典中的**新**值，拿不到旧值；
        // 因此旧令牌的取消必须放在 updateFactory 内部完成，否则重复登记时旧 loop 会继续烧钱。
        _entries.AddOrUpdate(
            subSessionId,
            _ => cts,
            (_, stale) =>
            {
                DisposeCancelled(stale);
                return cts;
            });

        return token;
    }

    /// <summary>
    /// 向指定运行发出取消信号。不移除登记条目——调用方收尾时再 TryRemove。
    /// </summary>
    /// <returns>是否确有登记条目被取消。</returns>
    public bool Cancel(string subSessionId)
    {
        if (string.IsNullOrWhiteSpace(subSessionId))
            return false;

        if (!_entries.TryGetValue(subSessionId, out var cts))
            return false;

        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // 已被移出并释放：视为未命中，不向上抛。
            return false;
        }
    }

    /// <summary>该运行当前是否仍在登记中（不代表未取消）。</summary>
    public bool IsRegistered(string subSessionId)
        => !string.IsNullOrWhiteSpace(subSessionId) && _entries.ContainsKey(subSessionId);

    /// <summary>
    /// 该运行是否已被取消。未登记时返回 false（未知不等于已取消）。
    /// </summary>
    public bool IsCancellationRequested(string subSessionId)
        => !string.IsNullOrWhiteSpace(subSessionId)
           && _entries.TryGetValue(subSessionId, out var cts)
           && cts.IsCancellationRequested;

    /// <summary>
    /// 移除登记并释放底层 CancellationTokenSource。终态写入之后调用。
    /// </summary>
    /// <returns>是否确有条目被移除。</returns>
    public bool TryRemove(string subSessionId)
    {
        if (string.IsNullOrWhiteSpace(subSessionId))
            return false;

        if (!_entries.TryRemove(subSessionId, out var cts))
            return false;

        try { cts.Dispose(); } catch { /* 释放失败不影响语义 */ }
        return true;
    }

    private static void DisposeCancelled(CancellationTokenSource stale)
    {
        try
        {
            stale.Cancel();
            stale.Dispose();
        }
        catch { /* 旧令牌可能已释放，忽略 */ }
    }
}
