namespace PuddingPlatform.Services;

/// <summary>
/// 单次运行归档的**只读一致性对账**结果（ADR-093 A93-0 第二刀）。
/// <para>
/// 此前三条链的水位只存在于内部实现里：权威事件数要数文件、投影游标在
/// <c>conversation-projection.cursor</c>、索引行在 DB；而 <c>ProjectionFileStamp</c> 只是**内存优化**
/// （不对外可查）。因此「文件已写、索引/投影未跟上」这类部分成功只能靠人翻日志发现。
/// 本类型把差异摊开成可检查的事实（缺口 G2 的最小修复面）。
/// </para>
/// <para>
/// **严格只读**：不写任何文件、不改任何状态、**不修补差异**（修补属 A93-2 重建作业）。
/// </para>
/// </summary>
/// <param name="AuthoritativeEvents">权威链 <c>events.jsonl</c> 的非空行数。</param>
/// <param name="ProjectionCursor">投影游标值；文件缺失或不可解析时为 <c>-1</c>（不可得，而非 0）。</param>
/// <param name="UnprojectedEvents">权威事件数减去游标（不小于 0）：即"已落权威但尚未投影"的条数。</param>
/// <param name="DroppedEvents">降级台账条数（逐条可枚举）。</param>
/// <param name="DegradedDroppedEventCount">聚合标记里的丢弃计数；无标记时为 <c>null</c>。</param>
/// <param name="IndexReadable">索引是否**可读**。与 <see cref="IndexRowPresent"/> 分开，避免把"读不出来"误报成"没有行"。</param>
/// <param name="ArchiveStatus">来自权威 <c>run.json</c> 的状态（权威态）。</param>
/// <param name="IndexStatus">来自 DB 索引行的状态（投影态）。</param>
/// <param name="Findings">人类可读的差异清单；无差异时为空。</param>
public sealed record SubAgentArchiveConsistencyReport(
    string RunId,
    bool ArchiveFound,
    int AuthoritativeEvents,
    long ProjectionCursor,
    long UnprojectedEvents,
    int DroppedEvents,
    int? DegradedDroppedEventCount,
    bool IndexReadable,
    bool IndexRowPresent,
    string? IndexStatus,
    string? ArchiveStatus,
    bool IsIndexStatusConsistent,
    bool IsConsistent,
    IReadOnlyList<string> Findings)
{
    /// <summary>一行摘要，便于日志/诊断端点直接输出。</summary>
    public string Summary =>
        $"runId={RunId} archive={ArchiveFound} events={AuthoritativeEvents} "
        + $"cursor={ProjectionCursor} unprojected={UnprojectedEvents} dropped={DroppedEvents} "
        + $"index={(IndexRowPresent ? IndexStatus ?? "<null>" : "<absent>")} "
        + $"archiveStatus={ArchiveStatus ?? "<null>"} consistent={IsConsistent}";
}
