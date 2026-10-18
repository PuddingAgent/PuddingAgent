namespace PuddingConversationReadModel;

/// <summary>
/// 历史页候选消息。调用方按「新 → 旧」降序传入（与键集查询 / SQL 窗口一致），组件输出保持同一顺序。
/// <para><see cref="IsDisplayable"/> 为 false 的候选只消耗扫描预算，不进入返回列表。</para>
/// <para><see cref="Content"/> 只需提供用于预览的正文；组件按 8 KiB UTF-8 截断，不保存完整正文。</para>
/// </summary>
public sealed record HistoryPageCandidate
{
    public required string MessageId { get; init; }
    public required string Role { get; init; }
    public string Content { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public string? TurnId { get; init; }
    public bool IsDisplayable { get; init; } = true;
}

/// <summary>有界历史页条目：保留稳定身份与预览截断标记，不返回完整大正文。</summary>
public sealed record BoundedHistoryPageItem(
    string MessageId,
    string Role,
    string Preview,
    bool PreviewTruncated,
    int OriginalContentBytes,
    string? TurnId);

/// <summary>
/// 历史页边界结果。计数彼此独立：
/// <list type="bullet">
/// <item><see cref="SkippedNonDisplayableCount"/>：不可显示候选数；</item>
/// <item><see cref="DuplicateCount"/>：重复 messageId 被忽略数（保留第一次出现，保持稳定身份）；</item>
/// <item><see cref="OmittedForItemLimitCount"/>：因页条数上限未返回的可显示候选数；</item>
/// <item><see cref="OmittedForByteBudgetCount"/>：因 128 KiB 页预算未返回的可显示候选数；</item>
/// <item><see cref="HasMore"/>：传入窗口中仍有未返回的候选（调用方应以此决定继续翻页；false 仅表示窗口已耗尽）。</item>
/// </list>
/// </summary>
public sealed record BoundedHistoryPage(
    IReadOnlyList<BoundedHistoryPageItem> Items,
    int CandidateCount,
    int SkippedNonDisplayableCount,
    int DuplicateCount,
    int OmittedForItemLimitCount,
    int OmittedForByteBudgetCount,
    int TotalPreviewBytes,
    bool HasMore);

/// <summary>
/// 历史页预算器（Plan §5.2、§5.4）：默认 20、最大 50 条可显示消息；单个 preview ≤ 8 KiB；
/// 整页预算 ≤ 128 KiB（含固定 JSON 开销估算）；不因去重/不可显示候选无限扩展候选窗口
/// （候选窗口由调用方按 <see cref="ConversationReadModelLimits.HistoryPageCandidateScanLimit"/> 提供）。
/// </summary>
public static class HistoryPageBudgeter
{
    public static BoundedHistoryPage Bound(
        IReadOnlyList<HistoryPageCandidate> candidatesNewestFirst,
        int? requestedLimit = null,
        ConversationReadModelLimits? limits = null)
        => HistoryPageBounder.Bound(candidatesNewestFirst, requestedLimit, limits ?? ConversationReadModelLimits.Default);
}

/// <summary>
/// 过程明细候选条目；调用方按 sequence 升序传入（旧 → 新），组件从尾部（最新）向前扫描，
/// 输出仍按 sequence 升序（渲染顺序）。
/// </summary>
public sealed record ProcessItemCandidate
{
    public required long Sequence { get; init; }
    public required string EventId { get; init; }
    public required string Kind { get; init; }
    public string Preview { get; init; } = string.Empty;
    public int PayloadBytes { get; init; }
    public bool IsDisplayable { get; init; } = true;
}

/// <summary>有界过程条目：预览 ≤ 16 KiB，保留 original payload 字节数供 UI 判断“展开全文”。</summary>
public sealed record BoundedProcessItem(
    long Sequence,
    string EventId,
    string Kind,
    string Preview,
    bool PreviewTruncated,
    int OriginalPayloadBytes);

/// <summary>过程页停止原因。</summary>
public enum DetailPageTruncation
{
    None = 0,
    ItemLimit = 1,
    ByteBudget = 2,
    ScanBudget = 3,
}

/// <summary>
/// 过程页边界结果（Plan §5.3）：
/// <list type="bullet">
/// <item>默认 100 项、最大 200 项、单页 256 KiB；单个巨型条目预览 ≤ 16 KiB；</item>
/// <item><see cref="ScannedBackThroughSequence"/>：实际扫描到的最低 sequence（含不可显示事件）；</item>
/// <item><see cref="NextBeforeSequence"/>：继续向更早翻页的排他边界；耗尽调用方提供窗口时为 null；</item>
/// <item>首个候选即被字节预算拒绝（尚未消费任何候选）时，<see cref="ScannedBackThroughSequence"/> 报告该候选
/// sequence，<see cref="NextBeforeSequence"/> 取其 +1，保证下一页可重新取回该候选；</item>
/// <item><see cref="HasMoreBefore"/>：仅当仍在调用方提供的候选窗口内、且还有更早候选时为 true；</item>
/// <item>空展示页也必须报告扫描边界，调用方据此推进，不能原地重试。</item>
/// </list>
/// </summary>
public sealed record BoundedProcessPage(
    IReadOnlyList<BoundedProcessItem> Items,
    long ScannedBackThroughSequence,
    long? NextBeforeSequence,
    bool HasMoreBefore,
    int OmittedNonDisplayableCount,
    int TotalPreviewBytes,
    DetailPageTruncation Truncation);

/// <summary>过程明细页预算器（Plan §5.3、§5.4）。</summary>
public static class ProcessPageBudgeter
{
    public static BoundedProcessPage Bound(
        IReadOnlyList<ProcessItemCandidate> candidatesAscending,
        int? requestedLimit = null,
        ConversationReadModelLimits? limits = null)
        => ProcessPageBounder.Bound(candidatesAscending, requestedLimit, limits ?? ConversationReadModelLimits.Default);
}

/// <summary>SSE 单次恢复停止原因（Plan §4.3：2000 条、2 MiB、1s 任一触顶即停）。</summary>
public enum SseCatchUpStopReason
{
    None = 0,
    EventLimit = 1,
    ByteLimit = 2,
    TimeBudget = 3,
}

/// <summary>
/// SSE 大差额恢复预算计量器。语义（冻结）：
/// <list type="bullet">
/// <item>按事件逐条 <see cref="TryConsume"/>；触发限额的事件**不**计入消费量，返回 false 并冻结停止原因；</item>
/// <item>停止后再次调用直接返回 false，不重复计数；<paramref name="elapsed"/> 必须非负且单调不减；</item>
/// <item>超过事件/字节/耗时任一预算即停止继续扫描，由调用方转 <c>snapshot_required</c> 语义（§4.3）。</item>
/// </list>
/// </summary>
public sealed class SseCatchUpMeter
{
    private readonly SseCatchUpMeterState _state;

    public SseCatchUpMeter(ConversationReadModelLimits? limits = null)
        => _state = new SseCatchUpMeterState(limits ?? ConversationReadModelLimits.Default);

    public int EventsConsumed => _state.EventsConsumed;

    public long BytesConsumed => _state.BytesConsumed;

    public SseCatchUpStopReason StopReason => _state.StopReason;

    public bool TryConsume(int eventBytes, TimeSpan elapsed)
        => _state.TryConsume(eventBytes, elapsed);
}
