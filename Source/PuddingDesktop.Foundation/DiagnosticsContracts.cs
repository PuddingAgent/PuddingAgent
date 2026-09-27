namespace PuddingDesktop.Foundation;

/// <summary>One runtime timeline entry. Text arrives already redacted by Core's shared query operation.</summary>
public sealed record RuntimeTimelineEntry(
    string Id, string Kind, string Component, string Operation, string Status,
    string SessionId, string AgentInstanceId, string RunId, string TraceId, string CorrelationId,
    DateTimeOffset StartedAtUtc, DateTimeOffset? CompletedAtUtc, long? DurationMs,
    string Summary, string Error, IReadOnlyDictionary<string, string> Metadata)
{
    public string StatusText => DiagnosticsText.DescribeStatus(Status);
    public string IdentityText =>
        $"组件 {Component} · 操作 {Operation} · {StatusText}" +
        (DurationMs is { } duration ? $" · {DiagnosticsText.DescribeDuration(duration)}" : "");
    public string CorrelationText
    {
        get
        {
            var parts = new List<string>();
            if (SessionId.Length > 0) parts.Add($"会话 {SessionId}");
            if (RunId.Length > 0) parts.Add($"Run {RunId}");
            if (TraceId.Length > 0) parts.Add($"Trace {TraceId}");
            if (AgentInstanceId.Length > 0) parts.Add($"Agent {AgentInstanceId}");
            return parts.Count == 0 ? "没有关联 ID（该事件未上报）" : string.Join(" · ", parts);
        }
    }
    public bool HasError => Error.Length > 0;
}

public sealed record RuntimeTimelinePage(
    IReadOnlyList<RuntimeTimelineEntry> Items, int Page, int PageSize, int Total)
{
    public static RuntimeTimelinePage Empty { get; } = new([], 1, 100, 0);
    public int PageCount => PageSize <= 0 ? 0 : (Total + PageSize - 1) / PageSize;
    public bool CanGoBack => Page > 1;
    public bool CanGoForward => Page < PageCount;
    public string PageText => Total == 0
        ? "没有匹配的事件"
        : $"第 {Page}/{PageCount} 页 · 共 {Total} 条 · 每页 {PageSize}";
}

public sealed record RuntimeTimelineFilter(
    string SessionId, string RunId, string TraceId, string AgentInstanceId, string Component, string Status,
    int Page, int PageSize, string SortOrder, string DisplayMode)
{
    public static RuntimeTimelineFilter Default { get; } = new("", "", "", "", "", "", 1, 100, "desc", "raw");
    public string DescribeText
    {
        get
        {
            var parts = new List<string>();
            if (SessionId.Length > 0) parts.Add($"会话={SessionId}");
            if (RunId.Length > 0) parts.Add($"Run={RunId}");
            if (TraceId.Length > 0) parts.Add($"Trace={TraceId}");
            if (AgentInstanceId.Length > 0) parts.Add($"Agent={AgentInstanceId}");
            if (Component.Length > 0) parts.Add($"组件={Component}");
            if (Status.Length > 0) parts.Add($"状态={Status}");
            return parts.Count == 0 ? "未设置筛选（最近的事件）" : string.Join(" · ", parts);
        }
    }
}

/// <summary>Component health as Core reports it; these are counts, not a health probe invented here.</summary>
public sealed record RuntimeComponentHealth(
    string Component, string Status, int StartedCount, int SucceededCount, int FailedCount,
    int RetriedCount, int CancelledCount, DateTimeOffset? LastSeenAtUtc)
{
    public string StatusText => DiagnosticsText.DescribeHealth(Status);
    public string CountsText =>
        $"开始 {StartedCount} · 成功 {SucceededCount} · 失败 {FailedCount} · 重试 {RetriedCount} · 取消 {CancelledCount}";
    public string LastSeenText => LastSeenAtUtc is { } seen
        ? $"最近 {RuntimeNodeText.DescribeHeartbeat(seen, DateTimeOffset.UtcNow)}"
        : "没有时间戳";
    public bool HasFailures => FailedCount > 0;
}

public sealed record DiagnosticsOverview(
    IReadOnlyList<RuntimeComponentHealth> Components, RuntimeTimelinePage RecentFailures)
{
    public static DiagnosticsOverview Empty { get; } = new([], RuntimeTimelinePage.Empty);
    public int Started => Components.Sum(c => c.StartedCount);
    public int Succeeded => Components.Sum(c => c.SucceededCount);
    public int Failed => Components.Sum(c => c.FailedCount);
    public int Retried => Components.Sum(c => c.RetriedCount);
    public int Cancelled => Components.Sum(c => c.CancelledCount);
    public int UnhealthyCount => Components.Count(c => !string.Equals(c.Status, "Healthy", StringComparison.OrdinalIgnoreCase));
    public string HeadlineText =>
        $"组件 {Components.Count} · 未健康 {UnhealthyCount} · 开始 {Started} · 成功 {Succeeded} · 失败 {Failed}";
    public string ExtraText => $"重试 {Retried} · 取消 {Cancelled} · 近期失败事件 {RecentFailures.Total}";
}

public interface IDiagnosticsSettings
{
    Task<RuntimeTimelinePage> QueryTimelineAsync(RuntimeTimelineFilter filter, CancellationToken cancellationToken = default);
    /// <summary>The overview card: component health plus the most recent failed events.</summary>
    Task<DiagnosticsOverview> LoadOverviewAsync(CancellationToken cancellationToken = default);
}

public static class DiagnosticsText
{
    public const string RedactionNotice =
        "查询结果在 Core 侧已脱敏（与 Web 同一份策略）：元数据里 key/token/secret/password/authorization 这类字段的值会变成 " +
        "***REDACTED***，超过 500 字符的文本会截断。";

    public const string FreeTextGapNotice =
        "登记差异：Core 的文本脱敏只做「截断」，**不会**清洗自由文本里长得像密钥的字符串（例如 Summary/Error 里的 sk-…）。" +
        "所以事件文本仍可能带出敏感串——界面原样显示已脱敏结果，不假装它被抹掉了。";

    public const string HealthNotice =
        "组件健康是 Core 按时间线事件统计出来的计数（开始/成功/失败/重试/取消），不是本机探针，也不是「服务是否在跑」的结论。";

    public const string EmptyTimelineNotice =
        "没有事件时列表为空是正常状态：Core 的四个来源（RuntimeActivity / EventQueue / ConversationEvent / SubAgentRun）都查过了。";

    public static IReadOnlyList<string> Statuses { get; } =
        ["started", "succeeded", "failed", "retried", "cancelled"];

    public static IReadOnlyList<string> SortOrders { get; } = ["desc", "asc"];
    public static IReadOnlyList<string> DisplayModes { get; } = ["raw", "user"];

    public static string DescribeStatus(string? status) => status switch
    {
        null or "" => "状态未知",
        var value when string.Equals(value, "started", StringComparison.OrdinalIgnoreCase) => "已开始",
        var value when string.Equals(value, "succeeded", StringComparison.OrdinalIgnoreCase) => "成功",
        var value when string.Equals(value, "failed", StringComparison.OrdinalIgnoreCase) => "失败",
        var value when string.Equals(value, "retried", StringComparison.OrdinalIgnoreCase) => "重试",
        var value when string.Equals(value, "cancelled", StringComparison.OrdinalIgnoreCase) => "已取消",
        var value => value
    };

    public static string DescribeHealth(string? status) => status switch
    {
        null or "" => "未知",
        var value when string.Equals(value, "Healthy", StringComparison.OrdinalIgnoreCase) => "健康",
        var value when string.Equals(value, "Degraded", StringComparison.OrdinalIgnoreCase) => "降级",
        var value when string.Equals(value, "Unhealthy", StringComparison.OrdinalIgnoreCase) => "不健康",
        var value => value
    };

    public static string DescribeDuration(long milliseconds) => milliseconds switch
    {
        < 1000 => $"{milliseconds} ms",
        < 60_000 => $"{milliseconds / 1000.0:0.##} s",
        _ => $"{milliseconds / 60_000.0:0.##} min"
    };

    /// <summary>Page size stays inside Core's own clamp (1..500).</summary>
    public static int ClampPageSize(int pageSize) => Math.Clamp(pageSize, 1, 500);

    public static RuntimeTimelineFilter Normalize(RuntimeTimelineFilter filter) => filter with
    {
        SessionId = filter.SessionId.Trim(),
        RunId = filter.RunId.Trim(),
        TraceId = filter.TraceId.Trim(),
        AgentInstanceId = filter.AgentInstanceId.Trim(),
        Component = filter.Component.Trim(),
        Status = filter.Status.Trim(),
        Page = Math.Max(1, filter.Page),
        PageSize = ClampPageSize(filter.PageSize),
        SortOrder = SortOrders.Contains(filter.SortOrder, StringComparer.OrdinalIgnoreCase) ? filter.SortOrder : "desc",
        DisplayMode = DisplayModes.Contains(filter.DisplayMode, StringComparer.OrdinalIgnoreCase) ? filter.DisplayMode : "raw",
    };

    public static IReadOnlyList<string> Validate(RuntimeTimelineFilter filter)
    {
        var errors = new List<string>();
        if (filter.Page < 1) errors.Add("页码至少为 1。");
        if (filter.PageSize is < 1 or > 500) errors.Add("每页条数必须在 1–500 之间（与 Core 一致）。");
        if (filter.Status.Length > 0 && !Statuses.Contains(filter.Status, StringComparer.OrdinalIgnoreCase))
            errors.Add($"状态取值不在 Core 的事件状态里：{filter.Status}。");
        return errors;
    }
}
