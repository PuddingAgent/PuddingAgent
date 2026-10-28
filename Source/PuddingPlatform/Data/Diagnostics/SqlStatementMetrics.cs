using System.Collections.Concurrent;

namespace PuddingPlatform.Data.Diagnostics;

/// <summary>一条语句的累计统计（快照，不可变）。</summary>
/// <param name="Key">归一化后的语句 key。</param>
/// <param name="Calls">执行次数。</param>
/// <param name="TotalTicks">累计耗时（<see cref="System.Diagnostics.Stopwatch"/> tick）。</param>
/// <param name="MaxTicks">单次最大耗时。</param>
/// <param name="Rows">累计返回行数（对非查询为 <c>null</c>）。</param>
internal sealed record SqlStatementSnapshot(
    string Key,
    long Calls,
    long TotalTicks,
    long MaxTicks,
    long? Rows)
{
    /// <summary>平均耗时（tick）；无调用时为 0。</summary>
    internal long AverageTicks => Calls > 0 ? TotalTicks / Calls : 0;
}

/// <summary>
/// 按归一化语句聚合的进程内统计。线程安全、有界、无 IO。
/// </summary>
/// <remarks>
/// <para>
/// 用途：直接回答"哪条 SQL 被跑了几百万次"——这是在 <c>pudding_platform.db</c> 上追查
/// 4 KiB 页读风暴时缺的那一层。文件级观测（句柄 / IO 计数器 / ETW）只能告诉你在读哪个文件，
/// 语句级统计才能告诉你是哪条查询在读它。
/// </para>
/// <para>
/// <b>有界是硬要求</b>：SQL 形状理论上可以无限多（动态 IN 列表、动态表名）。没有上限时
/// 统计自己会变成内存泄漏，而且是那种"跑了三天才炸"的类型。超过上限的部分归入
/// <see cref="OverflowKey"/>，既不丢总数，也不让字典继续长。
/// </para>
/// </remarks>
internal sealed class SqlStatementMetrics
{
    /// <summary>超出 <see cref="MaxTrackedStatements"/> 后所有新形状归入的 key。</summary>
    internal const string OverflowKey = "<overflow>";

    /// <summary>默认最多跟踪多少种语句形状。</summary>
    internal const int DefaultMaxTrackedStatements = 512;

    private readonly ConcurrentDictionary<string, Entry> _entries =
        new(StringComparer.Ordinal);

    private readonly int _maxTracked;

    internal SqlStatementMetrics(int maxTrackedStatements = DefaultMaxTrackedStatements)
    {
        if (maxTrackedStatements <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maxTrackedStatements),
                maxTrackedStatements,
                "上限必须为正；否则统计无法记录任何语句。");

        _maxTracked = maxTrackedStatements;
    }

    /// <summary>当前跟踪的语句形状数（含 overflow 项，若已产生）。</summary>
    internal int TrackedStatementCount => _entries.Count;

    /// <summary>是否已经发生过溢出。</summary>
    internal bool Overflowed => _entries.ContainsKey(OverflowKey);

    /// <summary>
    /// 记录一次执行。
    /// </summary>
    /// <param name="sql">原始 SQL（内部会归一化）。</param>
    /// <param name="elapsedTicks">本次耗时。</param>
    /// <param name="rowCount">返回行数；非查询传 <c>null</c>。</param>
    internal void Record(string? sql, long elapsedTicks, int? rowCount)
    {
        var key = SqlStatementKey.Normalize(sql);
        if (key.Length == 0)
            return;

        if (elapsedTicks < 0)
            elapsedTicks = 0;

        // 先按真实 key 尝试；已满且是新形状时落到 overflow，保证字典不继续增长。
        if (!_entries.TryGetValue(key, out var entry))
        {
            if (_entries.Count >= _maxTracked)
            {
                key = OverflowKey;
                entry = _entries.GetOrAdd(OverflowKey, static _ => new Entry());
            }
            else
            {
                entry = _entries.GetOrAdd(key, static _ => new Entry());
            }
        }

        Interlocked.Increment(ref entry.Calls);
        Interlocked.Add(ref entry.TotalTicks, elapsedTicks);

        // 最大值的无锁更新：只在确实更大时才写入。
        var currentMax = Interlocked.Read(ref entry.MaxTicks);
        while (elapsedTicks > currentMax)
        {
            var replaced = Interlocked.CompareExchange(ref entry.MaxTicks, elapsedTicks, currentMax);
            if (replaced == currentMax)
                break;
            currentMax = replaced;
        }

        if (rowCount is { } rows && rows > 0)
            Interlocked.Add(ref entry.Rows, rows);
    }

    /// <summary>
    /// 取快照，按调用次数降序（同次数按 key 序，保证输出稳定可比）。
    /// </summary>
    internal IReadOnlyList<SqlStatementSnapshot> Snapshot()
    {
        var list = new List<SqlStatementSnapshot>(_entries.Count);
        foreach (var kvp in _entries)
        {
            list.Add(new SqlStatementSnapshot(
                kvp.Key,
                Interlocked.Read(ref kvp.Value.Calls),
                Interlocked.Read(ref kvp.Value.TotalTicks),
                Interlocked.Read(ref kvp.Value.MaxTicks),
                kvp.Value.Rows));
        }

        list.Sort(static (a, b) =>
        {
            var byCalls = b.Calls.CompareTo(a.Calls);
            return byCalls != 0 ? byCalls : string.CompareOrdinal(a.Key, b.Key);
        });

        return list;
    }

    private sealed class Entry
    {
        internal long Calls;
        internal long TotalTicks;
        internal long MaxTicks;
        internal long Rows;
    }
}
