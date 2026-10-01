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

public sealed record NavigateRequest
{
    public NavigateRequest(DesktopPageTarget target, Uri url, DesktopPageVersion expectedPageVersion = default)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Url = url ?? throw new ArgumentNullException(nameof(url));
        if (!url.IsAbsoluteUri)
        {
            throw new ArgumentException("Navigation URL must be absolute.", nameof(url));
        }

        ExpectedPageVersion = expectedPageVersion;
    }

    public DesktopPageTarget Target { get; }

    public Uri Url { get; }

    /// <summary>期望的当前页面版本；<see cref="DesktopPageVersion.Unknown"/> 表示不校验（仅导航，不做页面内交互）。</summary>
    public DesktopPageVersion ExpectedPageVersion { get; }
}

public sealed record NavigateResult(
    NavigateDisposition Disposition,
    Uri? CurrentUrl,
    DesktopPageVersion PageVersion);

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
