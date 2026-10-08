namespace PuddingBrowser.Abstractions;

public sealed record BrowserViewport
{
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
}

public sealed record BrowserContextOptions
{
    public BrowserContextId? Id { get; init; }
    public string? UserDataDirectory { get; init; }
    public bool Persistent { get; init; } = true;
    public string? UserAgent { get; init; }
    public string? AcceptLanguage { get; init; }
    public BrowserViewport? Viewport { get; init; }
    public string? DownloadDirectory { get; init; }
    public IReadOnlyDictionary<string, string> AdditionalBrowserArguments { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// 驱动策略（§7/§8）：由宿主注入（Desktop 侧把 Automation 的纯策略适配进来）。
    /// 为 <c>null</c> 时驱动用 <see cref="NullBrowserDriverPolicy"/> 的安全默认——页面同样不会被卡住。
    /// </summary>
    public IBrowserDriverPolicy? DriverPolicy { get; init; }
}

public sealed record PageCreateOptions
{
    public Uri? InitialUrl { get; init; }
    public bool Activate { get; init; } = true;
    public string? Title { get; init; }
}

public enum BrowserPermission
{
    Camera,
    Microphone,
    Notifications,
    Geolocation
}
