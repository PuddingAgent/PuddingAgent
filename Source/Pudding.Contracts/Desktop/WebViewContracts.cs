namespace Pudding.Contracts.Desktop;

/// <summary>
/// 导航结果状态。<see cref="Accepted"/> 表示请求已被接受（可能仍在加载），
/// <see cref="Completed"/> 表示导航本身完成 —— 两者都<b>不</b>代表 DOM 可用于交互；
/// 交互就绪请通过 <see cref="DesktopPageState"/> / 事件等待，不能把 Navigate 返回当作 DOM 已就绪。
/// </summary>
public enum NavigateDisposition
{
    Accepted,
    Completed,
}

/// <summary>
/// 导航动作（加宽 2026-10-02，缺口 #3）。线名冻结在 <see cref="DesktopNavigationActionWire"/>：
/// 与交互动作同一模式——线缆上走字符串，新增动作不需要新的 oneof 分支。
/// </summary>
public enum DesktopNavigationAction
{
    Goto,
    Back,
    Forward,
    Reload,
    Stop,
}

/// <summary>导航动作线名（真源在本文件，快照由契约测试断言）。</summary>
public static class DesktopNavigationActionWire
{
    public static string NameOf(DesktopNavigationAction action) => action switch
    {
        DesktopNavigationAction.Goto => "goto",
        DesktopNavigationAction.Back => "back",
        DesktopNavigationAction.Forward => "forward",
        DesktopNavigationAction.Reload => "reload",
        DesktopNavigationAction.Stop => "stop",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Navigation action is not registered."),
    };

    /// <summary>严格解析：不知道的动作不猜测、不回退（fail closed）。空串按 goto（兼容旧帧）。</summary>
    public static bool TryParse(string? name, out DesktopNavigationAction action)
    {
        if (string.IsNullOrEmpty(name))
        {
            action = DesktopNavigationAction.Goto;
            return true;
        }

        foreach (var candidate in Enum.GetValues<DesktopNavigationAction>())
        {
            if (string.Equals(NameOf(candidate), name, StringComparison.Ordinal))
            {
                action = candidate;
                return true;
            }
        }

        action = default;
        return false;
    }
}

public sealed record NavigateRequest
{
    public const int DefaultTimeoutMs = 30_000;

    public const int MaxTimeoutMs = 600_000;

    public NavigateRequest(
        DesktopPageTarget target,
        Uri? url = null,
        DesktopPageVersion expectedPageVersion = default,
        DesktopNavigationAction action = DesktopNavigationAction.Goto,
        int timeoutMs = DefaultTimeoutMs)
    {
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "Navigation action is not registered.");
        }

        if (action == DesktopNavigationAction.Goto)
        {
            // 只有 goto 需要地址；其余动作作用于当前页（地址由页面自己决定）。
            Url = url ?? throw new ArgumentNullException(nameof(url), "'goto' requires a URL.");
            if (!Url.IsAbsoluteUri)
            {
                throw new ArgumentException("Navigation URL must be absolute.", nameof(url));
            }
        }
        else
        {
            Url = url;
        }

        if (timeoutMs is < 1 or > MaxTimeoutMs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeoutMs), timeoutMs, $"Timeout must be in [1, {MaxTimeoutMs}] ms.");
        }

        Target = target ?? throw new ArgumentNullException(nameof(target));
        ExpectedPageVersion = expectedPageVersion;
        Action = action;
        TimeoutMs = timeoutMs;
    }

    public DesktopPageTarget Target { get; }

    /// <summary>goto 的目标地址；其余动作（back/forward/reload/stop）为 <c>null</c>。</summary>
    public Uri? Url { get; }

    /// <summary>期望的当前页面版本；<see cref="DesktopPageVersion.Unknown"/> 表示不校验（仅导航，不做页面内交互）。</summary>
    public DesktopPageVersion ExpectedPageVersion { get; }

    public DesktopNavigationAction Action { get; }

    /// <summary>导航超时（毫秒）。只对 goto 生效——其余动作用运行时的默认行为。</summary>
    public int TimeoutMs { get; }
}

public sealed record NavigateResult(
    NavigateDisposition Disposition,
    Uri? CurrentUrl,
    DesktopPageVersion PageVersion,
    // 加宽（2026-10-02，缺口 #5）：导航本身的结果事实。**未知时为 null**（不猜）：
    // back/forward/reload/stop 在运行时没有等价返回值，因此它们不回带 ok/status/error。
    bool? Ok = null,
    int? StatusCode = null,
    string? ErrorText = null,
    // 加宽（2026-10-02，缺口 #14，见 #12 同源问题）：导航后的页面标题；未知时为 null。
    string? Title = null);

/// <summary>
/// 脚本返回值形态。为避免字符串二次 JSON 编码，<c>JsonValue</c> 始终是<b>裸 JSON 片段</b>：
/// <see cref="String"/> 携带带引号的 JSON 字符串，<see cref="Undefined"/>/<see cref="Null"/> 携带 <c>null</c>。
/// </summary>
public enum JavascriptValueKind
{
    Undefined,
    Null,
    Boolean,
    Number,
    String,
    Json,
}

public sealed record JavascriptRequest
{
    public const int DefaultMaxResultBytes = 256 * 1024;

    public const int MaxResultBytesLimit = 4 * 1024 * 1024;

    public JavascriptRequest(
        DesktopPageTarget target,
        string script,
        DesktopPageVersion expectedPageVersion = default,
        int maxResultBytes = DefaultMaxResultBytes)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Script = string.IsNullOrWhiteSpace(script)
            ? throw new ArgumentException("Script must be non-empty.", nameof(script))
            : script;
        if (maxResultBytes < 1 || maxResultBytes > MaxResultBytesLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxResultBytes), maxResultBytes, $"Max result bytes must be in [1, {MaxResultBytesLimit}].");
        }

        ExpectedPageVersion = expectedPageVersion;
        MaxResultBytes = maxResultBytes;
    }

    public DesktopPageTarget Target { get; }

    /// <summary>脚本正文。审计与日志<b>禁止</b>记录该字段。</summary>
    public string Script { get; }

    public DesktopPageVersion ExpectedPageVersion { get; }

    /// <summary>结果上限（字节，按 UTF-8 计），超出即截断并置位 <see cref="JavascriptResult.Truncated"/>。</summary>
    public int MaxResultBytes { get; }
}

public sealed record JavascriptResult(JavascriptValueKind Kind, string? JsonValue, bool Truncated);
