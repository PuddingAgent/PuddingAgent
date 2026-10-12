using System.Collections.Concurrent;
using PuddingBrowser.Protocol;

namespace PuddingDesktop.Browser;

/// <summary>
/// Caches terminal command results for idempotency.
/// Keeps at most 512 entries or 10 minutes, whichever comes first.
/// Duplicate operationId returns cached result without re-execution.
/// </summary>
/// <summary>
/// **重放载荷存储**（不是第二套去重权威）。
///
/// 方案 §4.2 要求"优先扩展已有操作账本/registry，**不为 Bridge 再造一个按 Guid 缓存**"。
/// 与字面措辞的关系（刻意保留本类，理由如下）：
/// <list type="bullet">
/// <item><b>去重权威是账本</b>：本类**只在账本裁决为 `ReplayCompleted` 的分支内**被查询，
/// 而该分支的前提是**同键**（进程实例 + **真实连接世代** + operationId）**且同指纹**（指纹含调用者身份与完整载荷）；
/// 指纹不同/结果未知 ⇒ 账本返回 `Conflict`，**根本到不了本类**。</item>
/// <item><b>本类只承载"重放时的载荷"</b>：账本只记终态字符串（completed/failed），真正的结果对象放这里；
/// 条目过期或被驱逐后若发生重放 ⇒ 返回**显式错误** `operation_result_unavailable`（不编造结果）。</item>
/// </list>
/// ⇒ 因此它**不构成"跨调用者/跨世代复用"的入口**（该结论有审计记录与用例支撑）；保留它是为了不把载荷塞进账本类型。
/// </summary>
public sealed class BrowserOperationResultCache
{
    private const int MaxEntries = 512;
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<Guid, CacheEntry> _cache = new();
    private readonly ConcurrentQueue<Guid> _insertionOrder = new();

    public bool TryGet(Guid operationId, out BrowserBridgeCommandResult? result)
    {
        if (_cache.TryGetValue(operationId, out var entry))
        {
            if (DateTimeOffset.UtcNow - entry.CachedAt <= MaxAge)
            {
                result = entry.Result;
                return true;
            }
            // Expired
            _cache.TryRemove(operationId, out _);
        }
        result = null;
        return false;
    }

    public void Add(Guid operationId, BrowserBridgeCommandResult result)
    {
        var entry = new CacheEntry(result, DateTimeOffset.UtcNow);
        _cache[operationId] = entry;
        _insertionOrder.Enqueue(operationId);
        Evict();
    }

    private void Evict()
    {
        var now = DateTimeOffset.UtcNow;

        // Evict by age
        foreach (var kvp in _cache)
        {
            if (now - kvp.Value.CachedAt > MaxAge)
            {
                _cache.TryRemove(kvp.Key, out _);
            }
        }

        // Evict by count
        while (_cache.Count > MaxEntries && _insertionOrder.TryDequeue(out var oldest))
        {
            _cache.TryRemove(oldest, out _);
        }
    }

    public void Clear()
    {
        _cache.Clear();
        while (_insertionOrder.TryDequeue(out _)) { }
    }

    private sealed record CacheEntry(BrowserBridgeCommandResult Result, DateTimeOffset CachedAt);
}
