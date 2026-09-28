namespace PuddingDesktop.Foundation;

/// <summary>One subagent run as Core's list endpoint reports it.</summary>
public sealed record SubAgentRun(
    string RunId, string ParentSessionId, string SubSessionId, string WorkspaceId, string AgentInstanceId,
    string TemplateId, string Status, string StartedAt, string CompletedAt, long TotalDurationMs,
    int TotalRounds, int TotalToolCalls, string ErrorMessage)
{
    public string StatusText => SubAgentRunText.DescribeStatus(Status);
    public string IdentityText => $"{RunId} · {StatusText} · 模板 {TemplateId}";
    public string CountsText => $"时长 {DiagnosticsText.DescribeDuration(TotalDurationMs)} · 轮次 {TotalRounds} · 工具 {TotalToolCalls}";
    public string TimeText => Finished
        ? $"{StartedAt} → {CompletedAt}"
        : $"{StartedAt} 起（未结束）";
    public bool Finished => CompletedAt.Length > 0;
    public bool HasError => ErrorMessage.Length > 0;
}

public sealed record SubAgentRunPage(IReadOnlyList<SubAgentRun> Items, int Total, int Offset, int Limit)
{
    public static SubAgentRunPage Empty { get; } = new([], 0, 0, 20);
    public bool CanGoBack => Offset > 0;
    public bool CanGoForward => Offset + Limit < Total;
    public string PageText => Total == 0
        ? "没有匹配的子代理运行"
        : $"第 {Offset / Math.Max(1, Limit) + 1} 页 · 共 {Total} 次运行 · 每页 {Limit}";
}

public sealed record SubAgentRunFilter(
    string ParentSessionId, string WorkspaceId, string AgentInstanceId, string Status, int Limit, int Offset)
{
    public static SubAgentRunFilter Default { get; } = new("", "", "", "", 20, 0);
    public string DescribeText
    {
        get
        {
            var parts = new List<string>();
            if (ParentSessionId.Length > 0) parts.Add($"父会话={ParentSessionId}");
            if (WorkspaceId.Length > 0) parts.Add($"工作区={WorkspaceId}");
            if (AgentInstanceId.Length > 0) parts.Add($"Agent 实例={AgentInstanceId}");
            if (Status.Length > 0) parts.Add($"状态={Status}");
            return parts.Count == 0 ? "未设置筛选（Core 返回的全部运行）" : string.Join(" · ", parts);
        }
    }
}

public sealed record SubAgentRunEvent(
    string EventId, string EventType, string Timestamp, int PayloadSize, string PayloadPreview);

public sealed record SubAgentRunDetail(
    SubAgentRun Summary, string Task, string Output, IReadOnlyDictionary<string, string> LlmProfiles,
    IReadOnlyDictionary<string, string> Trace, int EventCount, int ToolCallCount, string DegradedText)
{
    public bool IsDegraded => DegradedText.Length > 0;
    public string TaskText => Task.Length == 0 ? "（没有任务描述）" : Task;
    public string OutputText => Output.Length == 0 ? "（没有输出）" : Output;
    public string ProfileText => LlmProfiles.Count == 0
        ? "没有记录 LLM profile"
        : string.Join(" · ", LlmProfiles.Select(pair => $"{pair.Key}={pair.Value}"));
    public string TraceText => Trace.Count == 0
        ? "没有 trace 信息"
        : string.Join(" · ", Trace.Select(pair => $"{pair.Key}={pair.Value}"));
}

public interface ISubAgentRunSettings
{
    Task<SubAgentRunPage> ListAsync(SubAgentRunFilter filter, CancellationToken cancellationToken = default);
    /// <summary>Null means Core has no archive for that run id.</summary>
    Task<SubAgentRunDetail?> GetAsync(string runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SubAgentRunEvent>> ListEventsAsync(string runId, int limit, int offset, CancellationToken cancellationToken = default);
}

public static class SubAgentRunText
{
    /// <summary>Core's list endpoint accepts any status string; these are the ones it actually writes.</summary>
    public static IReadOnlyList<string> Statuses { get; } = ["running", "succeeded", "failed", "cancelled", "completed", "budget_exhausted", "timed_out", "interrupted"];

    public const int MinLimit = 1;
    public const int MaxLimit = 500;

    public const string DegradedNotice =
        "归档降级标记非空表示该运行曾丢弃事件：时间线不完整，工具/事件计数可能少于实际发生的。界面按原样标出，不假装完整。";

    public const string SubSessionNotice =
        "子会话 ID（subSessionId）是**子代理身份的复用单位**，runId 才是本次运行：同一子会话可以对应多次运行，诊断时不要用子会话数代替运行数。";

    public const string PayloadNotice =
        "事件列表给出 payload 大小与 200 字符预览；完整 payload 需要打开单条事件（Core 为回放保留了完整内容）。";

    public static string DescribeStatus(string? status) => status switch
    {
        null or "" => "状态未知",
        var value when string.Equals(value, "running", StringComparison.OrdinalIgnoreCase) => "运行中",
        var value when string.Equals(value, "succeeded", StringComparison.OrdinalIgnoreCase) => "成功",
        var value when string.Equals(value, "completed", StringComparison.OrdinalIgnoreCase) => "已完成",
        var value when string.Equals(value, "failed", StringComparison.OrdinalIgnoreCase) => "失败",
        var value when string.Equals(value, "cancelled", StringComparison.OrdinalIgnoreCase) => "已取消",
        var value when string.Equals(value, "budget_exhausted", StringComparison.OrdinalIgnoreCase) => "预算耗尽",
        var value when string.Equals(value, "timed_out", StringComparison.OrdinalIgnoreCase) => "超时",
        var value when string.Equals(value, "interrupted", StringComparison.OrdinalIgnoreCase) => "被中断",
        var value => value
    };

    public static int ClampLimit(int limit) => Math.Clamp(limit, MinLimit, MaxLimit);

    public static SubAgentRunFilter Normalize(SubAgentRunFilter filter) => filter with
    {
        ParentSessionId = filter.ParentSessionId.Trim(),
        WorkspaceId = filter.WorkspaceId.Trim(),
        AgentInstanceId = filter.AgentInstanceId.Trim(),
        Status = filter.Status.Trim(),
        Limit = ClampLimit(filter.Limit),
        Offset = Math.Max(0, filter.Offset),
    };

    public static IReadOnlyList<string> Validate(SubAgentRunFilter filter)
    {
        var errors = new List<string>();
        // Core 只按字符串筛状态，所以这里不限制取值，只限制分页（与 Core 的 1–500 一致）。
        if (filter.Limit is < MinLimit or > MaxLimit) errors.Add($"每页条数必须在 {MinLimit}–{MaxLimit} 之间（与 Core 一致）。");
        if (filter.Offset < 0) errors.Add("偏移量必须 >= 0。");
        return errors;
    }

    public static string DescribeDegraded(string? firstFailureAt, string? lastError, int droppedCount)
    {
        var parts = new List<string> { $"曾丢弃 {droppedCount} 条事件" };
        if (!string.IsNullOrWhiteSpace(firstFailureAt)) parts.Add($"首次 {firstFailureAt}");
        if (!string.IsNullOrWhiteSpace(lastError)) parts.Add($"最后错误 {lastError}");
        return string.Join(" · ", parts);
    }
}
