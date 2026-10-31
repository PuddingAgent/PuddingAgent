namespace PuddingGrep;

/// <summary>
/// 一行匹配的三种结果。刻意**不是** <c>bool</c>：
/// <para>
/// 「没匹配」与「超时了没法判断」是两件事。把它们折成一个 <c>false</c>，
/// 上层就会把「正则坏到跑不完」读成「这里确实没有」—— 这正是错误否定（空洞否定）的来源。
/// </para>
/// <para>
/// 模式本身非法时不进入本类型：<see cref="GrepMatcher.Match"/> 会抛 <see cref="ArgumentException"/>，
/// 因为「模式坏了」是调用方错误，不该伪装成一次正常的三态判断。
/// </para>
/// </summary>
public enum GrepMatchOutcome
{
    /// <summary>查过了，没有命中。</summary>
    NoMatch,

    /// <summary>命中，且 <see cref="GrepLineMatch.Spans"/> 非空。</summary>
    Matched,

    /// <summary>超时：<b>无法判断</b>是否命中。不得被读成「没有」。</summary>
    Timeout,
}

/// <summary>
/// 一行匹配的结果：三态 + 命中位置。
/// </summary>
/// <param name="Outcome">结果三态。</param>
/// <param name="Spans">命中位置；仅当 <paramref name="Outcome"/> 为 <see cref="GrepMatchOutcome.Matched"/> 时非空。</param>
public readonly record struct GrepLineMatch(GrepMatchOutcome Outcome, IReadOnlyList<GrepSpan> Spans)
{
    /// <summary>是否命中（<see cref="GrepMatchOutcome.Timeout"/> 不算命中，也不算未命中）。</summary>
    public bool HasMatch => Outcome == GrepMatchOutcome.Matched;

    /// <summary>查过了，没有命中。</summary>
    public static GrepLineMatch NoMatch() => new(GrepMatchOutcome.NoMatch, []);

    /// <summary>超时，无法判断。</summary>
    public static GrepLineMatch Timeout() => new(GrepMatchOutcome.Timeout, []);

    /// <summary>命中。<paramref name="spans"/> 必须非空 —— 空命中列表是自相矛盾的。</summary>
    public static GrepLineMatch Matched(IReadOnlyList<GrepSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        if (spans.Count == 0)
            throw new ArgumentException("Matched 必须带非空 spans；空列表应表达为 NoMatch。", nameof(spans));

        return new(GrepMatchOutcome.Matched, spans);
    }
}
