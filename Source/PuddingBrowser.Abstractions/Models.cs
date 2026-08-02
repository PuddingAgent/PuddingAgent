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
