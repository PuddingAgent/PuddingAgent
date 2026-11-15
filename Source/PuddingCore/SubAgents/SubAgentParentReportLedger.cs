namespace PuddingCode.SubAgents;

/// <summary>
/// 按 run 保存 <see cref="SubAgentParentReportBudget"/> 的容器，让「回信额度」在同一个 run 的
/// 多次工具调用之间累积，而不是每次调用都从零开始。
/// <para>
/// 为什么需要容器：<see cref="SubAgentParentReportBudget"/> 是状态对象，工具每次被调用时都可能
/// 拿到一个新的上下文，只有按 run 归并才能表达「这已经是他第 5 条回信了」。键用
/// <c>RuntimeExecutionIdentity.RunId</c>，因为它是子代理执行实例的稳定标识；用 SessionId 会把
/// 同一会话里的多次委派混成一份额度。
/// </para>
/// <para>
/// <b>淘汰只允许放宽，不允许收紧</b>：过期或被挤出容器的条目会丢掉账本，等价于下一次从 0 开始。
/// 这个方向的错误只会让某个 run 多发几条，不会让子代理永久报不回来——后者一旦发生，
/// 汇报通道就被一个 bug 静默掐断，而调用方看不出任何异常。
/// </para>
/// <para>
/// 线程边界：本类只保证<b>映射本身</b>线程安全。账本内部是普通计数，调用方必须按 run 串行使用
/// （Agent 循环是串行的：同一 run 的两次工具调用不会重叠）。
/// </para>
/// </summary>
public sealed class SubAgentParentReportLedger
{
    /// <summary>默认最多同时保留多少份账本。</summary>
    public const int DefaultMaxEntries = 512;

    /// <summary>默认多久没再活动就算过期。</summary>
    public static readonly TimeSpan DefaultEntryTtl = TimeSpan.FromHours(2);

    private sealed class Entry
    {
        public required SubAgentParentReportBudget Budget { get; init; }

        /// <summary>最后一次被取用或创建的时刻，用于过期与挤出。</summary>
        public DateTimeOffset LastSeen { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <param name="maxEntries">同时保留的账本上限；小于 1 时按 1 处理。</param>
    /// <param name="entryTtl">条目空闲多久后可被清理；为 null 用默认值，负数按 0 处理。</param>
    public SubAgentParentReportLedger(int maxEntries = DefaultMaxEntries, TimeSpan? entryTtl = null)
    {
        MaxEntries = maxEntries < 1 ? 1 : maxEntries;

        var ttl = entryTtl ?? DefaultEntryTtl;
        EntryTtl = ttl < TimeSpan.Zero ? TimeSpan.Zero : ttl;
    }

    /// <summary>同时保留的账本上限。</summary>
    public int MaxEntries { get; }

    /// <summary>条目空闲多久后可被清理。</summary>
    public TimeSpan EntryTtl { get; }

    /// <summary>当前保留的账本数量。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    /// <summary>
    /// 取得该 run 的账本；没有则按给定配置新建。
    /// </summary>
    /// <param name="runId">子代理执行实例 id；空白值共用同一个账本，调用方应先校验。</param>
    /// <param name="now">调用方提供的当前时刻（注入而非内部取时钟，便于逐分支断言）。</param>
    /// <param name="maxMessages">新建时的额度上限；已存在则忽略（首次创建的配置生效）。</param>
    /// <param name="minInterval">新建时的最小间隔；已存在则忽略。</param>
    /// <returns>该 run 的账本。</returns>
    public SubAgentParentReportBudget GetOrCreate(
        string runId,
        DateTimeOffset now,
        int? maxMessages = null,
        TimeSpan? minInterval = null)
    {
        var key = runId?.Trim() ?? string.Empty;

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.LastSeen = now;
                return existing.Budget;
            }

            EvictExpired(now);
            while (_entries.Count >= MaxEntries)
                EvictOldest();

            var budget = new SubAgentParentReportBudget(
                maxMessages ?? SubAgentParentReportBudget.DefaultMaxMessages,
                minInterval);

            _entries[key] = new Entry { Budget = budget, LastSeen = now };
            return budget;
        }
    }

    /// <summary>丢弃该 run 的账本（run 终止时调用，避免长期累积）。</summary>
    /// <returns>确实存在并已移除时返回 <c>true</c>。</returns>
    public bool Remove(string runId)
    {
        var key = runId?.Trim() ?? string.Empty;

        lock (_gate)
            return _entries.Remove(key);
    }

    /// <summary>清空全部账本。</summary>
    public void Clear()
    {
        lock (_gate)
            _entries.Clear();
    }

    private void EvictExpired(DateTimeOffset now)
    {
        List<string>? expired = null;

        foreach (var (key, entry) in _entries)
        {
            // 严格大于：EntryTtl 为 0 时，同一时刻创建的条目不算过期，
            // 否则插入第二条就会把第一条挤掉，上限与过期两条规则会互相干扰。
            if (now - entry.LastSeen > EntryTtl)
                (expired ??= []).Add(key);
        }

        if (expired is null)
            return;

        foreach (var key in expired)
            _entries.Remove(key);
    }

    private void EvictOldest()
    {
        string? oldestKey = null;
        var oldestSeen = DateTimeOffset.MaxValue;

        foreach (var (key, entry) in _entries)
        {
            // 同刻并列时按 key 序取第一个，保证挤出结果可复现（不依赖字典枚举顺序）。
            if (entry.LastSeen < oldestSeen
                || (entry.LastSeen == oldestSeen
                    && string.CompareOrdinal(key, oldestKey) < 0))
            {
                oldestSeen = entry.LastSeen;
                oldestKey = key;
            }
        }

        if (oldestKey is not null)
            _entries.Remove(oldestKey);
    }
}
