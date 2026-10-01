namespace Pudding.Contracts.Desktop;

/// <summary>等待条件类型（与既有 Bridge 的 <c>page.waitFor</c> 参数等价；线名真源）。</summary>
public enum DesktopWaitConditionKind
{
    /// <summary>等待选择器出现。</summary>
    Selector,

    /// <summary>等待选择器消失（例如加载遮罩）。</summary>
    SelectorHidden,

    /// <summary>等待地址匹配（子串或模式）。</summary>
    UrlPattern,
}

/// <summary>等待条件线名（真源在本文件，快照由契约测试断言）。</summary>
public static class DesktopWaitConditionKindWire
{
    public static string NameOf(DesktopWaitConditionKind kind) => kind switch
    {
        DesktopWaitConditionKind.Selector => "selector",
        DesktopWaitConditionKind.SelectorHidden => "selector-hidden",
        DesktopWaitConditionKind.UrlPattern => "url-pattern",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Wait condition kind is not registered."),
    };

    public static bool TryParse(string? name, out DesktopWaitConditionKind kind)
    {
        foreach (var candidate in Enum.GetValues<DesktopWaitConditionKind>())
        {
            if (string.Equals(NameOf(candidate), name, StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }
}

/// <summary>等待条件：类型 + 值。</summary>
public sealed record DesktopWaitCondition
{
    public const int MaxValueLength = 2048;

    public DesktopWaitCondition(DesktopWaitConditionKind kind, string value)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Wait condition kind is not registered.");
        }

        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxValueLength)
        {
            throw new ArgumentException(
                $"Wait condition value must be 1..{MaxValueLength} characters.", nameof(value));
        }

        Kind = kind;
        Value = value;
    }

    public DesktopWaitConditionKind Kind { get; }

    public string Value { get; }

    public override string ToString() => $"{DesktopWaitConditionKindWire.NameOf(Kind)}({Value})";
}

/// <summary>
/// 等待请求：显式页面目标 + 条件 + 超时。
///
/// 语义要点：<b>超时不是错误</b>——结果里用 <c>TimedOut</c> 如实标注，并照样回带页面状态，
/// 让调用方自己决定重试/换条件/放弃；把它当异常会让上层做出错误的重试决策。
/// </summary>
public sealed record BrowserWaitForRequest
{
    public const int DefaultTimeoutMs = 30_000;

    public const int MaxTimeoutMs = 600_000;

    public BrowserWaitForRequest(
        DesktopPageTarget target,
        DesktopWaitCondition condition,
        int timeoutMs = DefaultTimeoutMs,
        DesktopPageVersion expectedPageVersion = default)
    {
        if (timeoutMs is < 1 or > MaxTimeoutMs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeoutMs), timeoutMs, $"Timeout must be in [1, {MaxTimeoutMs}] ms.");
        }

        Target = target ?? throw new ArgumentNullException(nameof(target));
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
        TimeoutMs = timeoutMs;
        ExpectedPageVersion = expectedPageVersion;
    }

    public DesktopPageTarget Target { get; }

    public DesktopWaitCondition Condition { get; }

    /// <summary>等待上限；Desktop 必须在此上限内返回（超时用 <c>TimedOut</c> 标注）。</summary>
    public int TimeoutMs { get; }

    /// <summary>期望页面版本；<see cref="DesktopPageVersion.Unknown"/> 表示不约束。</summary>
    public DesktopPageVersion ExpectedPageVersion { get; }

    public TimeSpan Timeout => TimeSpan.FromMilliseconds(TimeoutMs);
}

/// <summary>等待结果：是否超时 + 等待结束时的页面状态（超时也要带回来）。</summary>
public sealed record DesktopWaitResult
{
    public DesktopWaitResult(
        DesktopPageTarget target,
        DesktopWaitCondition condition,
        bool timedOut,
        DesktopPageState page,
        string? error = null)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
        TimedOut = timedOut;
        Page = page ?? throw new ArgumentNullException(nameof(page));
        Error = string.IsNullOrEmpty(error) ? null : error;
    }

    public DesktopPageTarget Target { get; }

    public DesktopWaitCondition Condition { get; }

    /// <summary>条件在上限内没有满足：这是**正常结果**，不是失败。</summary>
    public bool TimedOut { get; }

    /// <summary>等待结束时的页面状态（版本可能是等待期间推进后的版本）。</summary>
    public DesktopPageState Page { get; }

    /// <summary>可选的诊断说明（例如条件语法问题）；不用于表达「超时」。</summary>
    public string? Error { get; }

    public override string ToString() =>
        $"wait {Condition} @{Target} → {(TimedOut ? "timed out" : "satisfied")} v{Page.Version.Value}";
}
