using System.Collections.ObjectModel;

namespace PuddingBrowser.Automation;

/// <summary>
/// **页面事件日志**（设计方案 §8）：把"被自动处理的页面事件"有界地留下来，
/// 让调用方知道「刚才发生了什么」，而不是页面悄悄自己处理完、外面一头雾水。
///
/// 三条约束：
/// ① **有界**：只保留最近 <see cref="DefaultCapacity"/> 条（日志不能变成内存增长点）；
/// ② **有序且可去重**：同一段摘要连续重复时合并计数（一次页面里弹出十个一样的权限询问，
///    报十遍没有信息量，报"×10"才有）；
/// ③ **不编造**：没有事件就是空列表，不填"正常"之类的假事实。
/// </summary>
public sealed class PageEventLog
{
    public const int DefaultCapacity = 8;

    private readonly List<Entry> _entries = [];
    private readonly int _capacity;

    public PageEventLog(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>一条事件：摘要 + 连续重复次数（重复只累加，不再占位）。</summary>
    public sealed record Entry(string Summary, int Count = 1);

    /// <summary>累计发生次数（含被容量挤掉的，用于如实报告"总共有多少次"）。</summary>
    public int TotalCount { get; private set; }

    /// <summary>追加一条；与上一条相同则只把计数 +1（保留顺序与容量）。</summary>
    public void Add(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return;
        }

        TotalCount++;

        if (_entries.Count > 0 && string.Equals(_entries[^1].Summary, summary, StringComparison.Ordinal))
        {
            _entries[^1] = _entries[^1] with { Count = _entries[^1].Count + 1 };
            return;
        }

        _entries.Add(new Entry(summary));
        if (_entries.Count > _capacity)
        {
            _entries.RemoveAt(0);
        }
    }

    /// <summary>当前保留的事件（只读快照；顺序 = 发生顺序）。</summary>
    public IReadOnlyList<Entry> Entries => new ReadOnlyCollection<Entry>(_entries);

    /// <summary>是否没有任何事件（调用方据此决定要不要报告）。</summary>
    public bool IsEmpty => _entries.Count == 0;
}
