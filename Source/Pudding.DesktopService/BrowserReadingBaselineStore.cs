using Pudding.Contracts.Desktop;

namespace Pudding.DesktopService;

/// <summary>
/// 读取基线存储（设计方案 §145）：保存"某个修订标识对应的那份文本"，
/// 从而让 <c>since_revision</c> 能给出**真正的行级差异**，而不是只回答"变了/没变"。
///
/// 三条硬性约束：
/// ① **按页隔离**：基线只在同一页内可比（跨页比较毫无意义）；
/// ② **有界**：每页只保留最近若干份，且单份超过行数上限就**不存**（宁可让调用方重新读取，
///    也不保存一份被截断的基线——那会算出错误差异）；
/// ③ **随生命周期清除**：页面/上下文关闭必须清掉（否则"页面没了、内容还留着"，
///    与授权/租约的清理原则一致）。因此由 <see cref="BrowserPageLifecycle"/> 持有并清理。
/// </summary>
public sealed class BrowserReadingBaselineStore
{
    /// <summary>单份基线的行数上限；超过就不保存（宁缺勿错）。</summary>
    public const int MaxLinesPerBaseline = 20_000;

    private readonly int _capacityPerPage;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, Dictionary<string, IReadOnlyList<string>>> _pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<string>> _order = new(StringComparer.Ordinal);

    public BrowserReadingBaselineStore(int capacityPerPage = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityPerPage, 1);
        _capacityPerPage = capacityPerPage;
    }

    /// <summary>记住一份基线。行数超上限 ⇒ 不保存并返回 <c>false</c>（调用方据此不承诺 diff）。</summary>
    public bool Remember(DesktopPageTarget target, string revision, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        ArgumentNullException.ThrowIfNull(lines);

        if (lines.Count > MaxLinesPerBaseline)
        {
            return false;
        }

        var key = target.Key;
        lock (_sync)
        {
            if (!_pages.TryGetValue(key, out var baselines))
            {
                baselines = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
                _pages[key] = baselines;
                _order[key] = new Queue<string>();
            }

            if (!baselines.ContainsKey(revision))
            {
                _order[key].Enqueue(revision);
            }

            baselines[revision] = lines;

            while (_order[key].Count > _capacityPerPage)
            {
                var evicted = _order[key].Dequeue();
                baselines.Remove(evicted);
            }
        }

        return true;
    }

    /// <summary>取出某份基线；没有（或已被淘汰）⇒ <c>false</c>（调用方必须走"重新读取"）。</summary>
    public bool TryGet(DesktopPageTarget target, string revision, out IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(target);

        lines = [];
        if (string.IsNullOrWhiteSpace(revision))
        {
            return false;
        }

        lock (_sync)
        {
            return _pages.TryGetValue(target.Key, out var baselines)
                && baselines.TryGetValue(revision, out lines!);
        }
    }

    /// <summary>清掉一页的全部基线；返回清掉的份数。</summary>
    public int ClearPage(DesktopPageTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_sync)
        {
            if (!_pages.Remove(target.Key, out var baselines))
            {
                return 0;
            }

            _order.Remove(target.Key);
            return baselines.Count;
        }
    }

    /// <summary>清掉一个上下文下所有页面的基线；返回清掉的份数。</summary>
    public int ClearContext(string contextId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextId);

        lock (_sync)
        {
            var prefix = contextId + "/";
            var keys = _pages.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            var cleared = 0;
            foreach (var key in keys)
            {
                cleared += _pages[key].Count;
                _pages.Remove(key);
                _order.Remove(key);
            }

            return cleared;
        }
    }

    /// <summary>当前保存的基线份数（诊断/断言用）。</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _pages.Values.Sum(baselines => baselines.Count);
            }
        }
    }
}
