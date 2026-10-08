namespace PuddingCode.Runtime;

/// <summary>
/// 压缩摘要的**请求侧内容标记**唯一合同。
/// <para>
/// 请求消息不携带 DB 侧的 <c>ContentType</c>（<c>compact_summary</c>），所以「哪条消息是压缩摘要」
/// 只能按正文标记识别。仓内实际存在两条摘要写入路径，标记不同：
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="Canonical"/>（<c>&lt;compact_summary&gt;</c>）：持久摘要路径
/// （<c>ContextCompactionService</c> / <c>AgentContextCompactionSummaryGenerator</c> /
/// <c>FlashContextCompactionSummaryGenerator</c>），落库时 <c>ContentType = compact_summary</c>。
/// </description></item>
/// <item><description>
/// <see cref="WarmPrefixCheckpoint"/>（<c>&lt;compacted-summary&gt;</c>）：warm-prefix checkpoint 路径
/// （长 Agent 循环的前缀重放压缩，正文由 <c>WarmPrefixCompaction.TryCreateCheckpoint</c> 写入）。
/// </description></item>
/// </list>
/// <para>
/// 计量分层（<c>ContextUsageSnapshot.CompactionSummaryTokens</c>）与前端「压缩后记忆」色段必须经
/// <see cref="ContainsMarker"/> 同源识别。只认其中一个标记会把另一条路径的摘要误计入「对话消息」，
/// 表现为摘要桶恒为 0、有效输入压力被高估（诊断 2026-10-07 §4.3）。
/// </para>
/// <para>
/// 该合同位于 PuddingCore：计量在 PuddingCore、压缩写入在 PuddingRuntime，方向不得反转。
/// 新增摘要标记路径时必须在此登记，禁止各自比较字面量。
/// </para>
/// </summary>
public static class ContextSummaryMarkers
{
    /// <summary>持久压缩摘要标记（既有主路径）。</summary>
    public const string Canonical = "<compact_summary>";

    /// <summary>warm-prefix checkpoint 标记（仅内存前缀重放路径）。</summary>
    public const string WarmPrefixCheckpoint = "<compacted-summary>";

    /// <summary>
    /// 全部被承认的摘要标记。warm-prefix 保留自身标记是为了继续兼容已写入历史/checkpoint 的既有文本，
    /// 不表示允许新增第三套标记。
    /// </summary>
    public static IReadOnlyList<string> All { get; } = [Canonical, WarmPrefixCheckpoint];

    /// <summary>
    /// 正文是否携带任一压缩摘要标记。大小写不敏感，与既有逐字面量比较行为一致。
    /// </summary>
    public static bool ContainsMarker(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return false;

        return content.Contains(Canonical, StringComparison.OrdinalIgnoreCase)
            || content.Contains(WarmPrefixCheckpoint, StringComparison.OrdinalIgnoreCase);
    }
}
