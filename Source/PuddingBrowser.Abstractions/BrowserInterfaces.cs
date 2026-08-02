using System.Text.Json;

namespace PuddingBrowser.Abstractions;

// ── Browser Runtime ─────────────────────────────────────

public interface IBrowserRuntime : IAsyncDisposable
{
    BrowserRuntimeState State { get; }

    Task<IBrowserContext> CreateContextAsync(
        BrowserContextOptions options, CancellationToken ct);

    Task<IBrowserContext?> GetContextAsync(BrowserContextId id, CancellationToken ct);

    Task<IReadOnlyList<BrowserContextInfo>> ListContextsAsync(CancellationToken ct);

    Task CloseContextAsync(BrowserContextId id, CancellationToken ct);

    IAsyncEnumerable<BrowserEvent> WatchEventsAsync(
        BrowserEventFilter filter, CancellationToken ct);
}

// ── Browser Context ─────────────────────────────────────

public interface IBrowserContext : IAsyncDisposable
{
    BrowserContextId Id { get; }
    BrowserContextInfo Info { get; }

    Task<IBrowserPage> NewPageAsync(PageCreateOptions options, CancellationToken ct);

    Task<IBrowserPage?> GetPageAsync(PageId id, CancellationToken ct);

    Task<IReadOnlyList<PageInfo>> ListPagesAsync(CancellationToken ct);

    Task ClosePageAsync(PageId id, CancellationToken ct);

    Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(
        IReadOnlyList<Uri>? urls, CancellationToken ct);

    Task SetCookiesAsync(IReadOnlyList<BrowserCookie> cookies, CancellationToken ct);

    Task ClearCookiesAsync(CancellationToken ct);

    Task GrantPermissionsAsync(
        Uri origin, IReadOnlyList<BrowserPermission> permissions, CancellationToken ct);

    Task ResetPermissionsAsync(CancellationToken ct);
}

// ── Browser Page ────────────────────────────────────────

public interface IBrowserPage : IAsyncDisposable
{
    PageId Id { get; }
    BrowserContextId ContextId { get; }
    long PageVersion { get; }
    PageInfo Info { get; }

    Task<NavigationResult> GotoAsync(Uri url, NavigationOptions options, CancellationToken ct);
    Task GoBackAsync(CancellationToken ct);
    Task GoForwardAsync(CancellationToken ct);
    Task ReloadAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task BringToFrontAsync(CancellationToken ct);

    Task<PageSnapshot> SnapshotAsync(SnapshotOptions options, CancellationToken ct);

    Task<IElementHandle?> QueryAsync(Locator locator, CancellationToken ct);
    Task<IReadOnlyList<IElementHandle>> QueryAllAsync(Locator locator, CancellationToken ct);

    Task<BrowserScriptValue> EvaluateAsync(BrowserScript script, CancellationToken ct);
    Task<IJsHandle> EvaluateHandleAsync(BrowserScript script, CancellationToken ct);
    Task<JsonDocument> SendCdpAsync(string method, JsonElement? parameters, CancellationToken ct);

    Task<BrowserSubscriptionId> SubscribeCdpAsync(string eventName, CancellationToken ct);
    Task UnsubscribeAsync(BrowserSubscriptionId subscriptionId, CancellationToken ct);

    Task ClickAsync(Locator locator, ClickOptions options, CancellationToken ct);
    Task FillAsync(Locator locator, string value, FillOptions options, CancellationToken ct);
    Task TypeAsync(Locator locator, string text, TypeOptions options, CancellationToken ct);
    Task PressAsync(Locator locator, string key, KeyOptions options, CancellationToken ct);
    Task HoverAsync(Locator locator, PointerOptions options, CancellationToken ct);
    Task ScrollAsync(ScrollOptions options, CancellationToken ct);
    Task DragAsync(Locator source, Locator target, DragOptions options, CancellationToken ct);
    Task SelectAsync(Locator locator, IReadOnlyList<string> values, CancellationToken ct);
    Task CheckAsync(Locator locator, bool isChecked, CancellationToken ct);
    Task SetInputFilesAsync(Locator locator, IReadOnlyList<string> paths, CancellationToken ct);

    Task<WaitResult> WaitForAsync(WaitCondition condition, CancellationToken ct);
    Task<ScreenshotResult> ScreenshotAsync(ScreenshotOptions options, CancellationToken ct);
    Task<PdfResult> PrintToPdfAsync(PdfOptions options, CancellationToken ct);
    Task OpenDevToolsAsync(CancellationToken ct);
}

// ── Element Handle ──────────────────────────────────────

public interface IElementHandle : IAsyncDisposable
{
    ElementHandleId Id { get; }
    PageId PageId { get; }
    long PageVersion { get; }
    int? BackendNodeId { get; }
    string LocatorFingerprint { get; }

    Task<BoundingBox?> GetBoundingBoxAsync(CancellationToken ct);
    Task<BrowserScriptValue> EvaluateAsync(BrowserScript script, CancellationToken ct);
}

// ── JS Handle ───────────────────────────────────────────

public interface IJsHandle : IAsyncDisposable
{
    JsHandleId Id { get; }
    PageId PageId { get; }
    long PageVersion { get; }

    Task<BrowserScriptValue> EvaluateAsync(BrowserScript script, CancellationToken ct);
    Task<JsonDocument> GetPropertiesAsync(CancellationToken ct);
}
