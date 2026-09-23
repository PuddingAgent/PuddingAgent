namespace PuddingPlatform.Services;

/// <summary>
/// 一条被丢弃的观测事件（ADR-093 A93-0 降级台账 `archive-degraded-events.jsonl` 的一行）。
/// <para>
/// 与聚合标记 <c>archive-degraded.json</c>（只有 <c>DroppedEventCount</c> / <c>LastEventType</c> / <c>LastError</c>）
/// 的区别很关键：聚合能回答「丢了几条」，本类型逐条存在才能回答「<b>丢了哪几条</b>」，
/// 使降级可对账、可补齐、可定位——这是 ADR-093 缺口 G1 的最小修复面。
/// </para>
/// </summary>
/// <param name="Seq">
/// 台账内序号（1 起）。它<b>不是</b>权威事件序号：事件写失败时其权威序号不存在，
/// 故此处只用于稳定排序与行数对账；<c>-1</c> 表示序号不可得（读取台账失败），不得据此丢弃本行。
/// </param>
/// <param name="EventId">本应写入 <c>events.jsonl</c> 的事件身份；tool-audit 类降级为空字符串（其无事件身份）。</param>
/// <param name="EventType">被丢弃的事件类型（tool-audit 降级为 <c>tool_audit:{Tool}</c>）。</param>
/// <param name="Error">底层异常消息（截断由调用方决定，此处保留原样）。</param>
/// <param name="At">丢弃发生时刻（ISO-8601，UTC）。</param>
public sealed record SubAgentArchiveDroppedEvent(
    long Seq,
    string EventId,
    string EventType,
    string Error,
    string At);
