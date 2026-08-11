namespace PuddingRuntime.Services;

/// <summary>
/// 上下文窗口管理器的命名常量。
/// 集中管理 magic numbers、默认值、阈值，消除分散在 ContextWindowManager 中的硬编码字面量。
/// </summary>
public static class ContextWindowConstants
{
    // ── 会话超时 ────────────────────────────────────────────────
    /// <summary>默认会话超时：1 小时。</summary>
    public static readonly TimeSpan DefaultSessionTimeout = TimeSpan.FromHours(1);

    // ── Token 预算与估算 ────────────────────────────────────────
    /// <summary>默认 token 预算上限（用于 DB/JSONL 上下文回填等场景）。</summary>
    public const int DefaultMaxTokenBudget = 8000;

    /// <summary>Token 估算：约 1 token ≈ N 字符（中英混排折中）。</summary>
    public const int TokenEstimateCharDivisor = 3;

    /// <summary>Token 预算循环中，保留消息的最小条数（避免过早截断）。</summary>
    public const int MinMessagesBeforeTokenBreak = 2;

    // ── 数据库回填限制 ──────────────────────────────────────────
    /// <summary>数据库回填时从 DB 取回的最大消息数。</summary>
    public const int MaxDbFetchMessages = 100;

    // ── 历史裁剪（TrimHistory）参数 ─────────────────────────────
    /// <summary>Token→消息条数换算比率（~2500 tokens/msg）。</summary>
    public const int TokenToMessageRatio = 2500;

    /// <summary>裁剪时消息条数的最小保底值。</summary>
    public const int MinMaxMessagesFloor = 40;

    // ── 历史剪枝（History Pruning）回退默认值 ──────────────────
    /// <summary>剪枝最大消息条数的回退默认值（与 ContextCompactionOptions 默认值一致）。</summary>
    public const int DefaultHistoryPruningMaxMessages = 100;

    // ── 工作总结重试回退默认值 ──────────────────────────────────
    /// <summary>等待工作总结的最大重试次数回退默认值（与 ContextCompactionOptions 默认值一致）。</summary>
    public const int DefaultMaxWorkSummaryRetries = 3;

    /// <summary>等待工作总结的最大总时长（秒）回退默认值（与 ContextCompactionOptions 默认值一致）。</summary>
    public const int DefaultMaxWaitForWorkSummarySeconds = 180;

    // ── 工作总结提取参数 ────────────────────────────────────────
    /// <summary>从历史中搜索工作总结时，检查最近 N 条 Assistant 消息。</summary>
    public const int WorkSummarySearchWindow = 5;

    // ── 日志预览长度 ────────────────────────────────────────────
    /// <summary>工作总结日志预览最大长度。</summary>
    public const int WorkSummaryLogPreviewLength = 120;

    /// <summary>压缩摘要日志预览最大长度。</summary>
    public const int CompactionSummaryLogPreviewLength = 200;

    // ── 内容类型常量 ────────────────────────────────────────────
    /// <summary>压缩摘要的内容类型标识。</summary>
    public const string CompactSummaryContentType = "compact_summary";

    // ── 提示词标记字符串 ────────────────────────────────────────
    /// <summary>工作总结提示词注入时的去重检测标记。</summary>
    public const string WorkSummaryPromptMarker = "会话压缩即将触发";

    // ── 压缩原因常量 ────────────────────────────────────────────
    /// <summary>自动压缩触发原因描述。</summary>
    public const string AutoCompactionReason = "context_window_auto_compaction";
}
