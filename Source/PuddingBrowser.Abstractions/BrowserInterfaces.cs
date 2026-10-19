using System.Text.Json;

namespace PuddingBrowser.Abstractions;

/// <summary>
/// Product workspace ownership of contexts/pages. Successful creation includes
/// UI projection and lifecycle registration; tools must not bypass this owner.
/// </summary>
public interface IBrowserWorkspaceLifecycle
{
    Task<IBrowserContext> CreateContextAsync(BrowserContextOptions options, CancellationToken ct);
    Task CloseContextAsync(BrowserContextId contextId, CancellationToken ct);
    Task<IBrowserPage> CreatePageAsync(BrowserContextId contextId, PageCreateOptions options, CancellationToken ct);
    Task ActivatePageAsync(BrowserContextId contextId, PageId pageId, CancellationToken ct);
    Task ClosePageAsync(BrowserContextId contextId, PageId pageId, CancellationToken ct);
}

/// <summary>
/// 「把这一页真正呈现出来」的端口：展开被折叠的工作区、选中该页的标签，并让它的表面可见。
///
/// 为什么与 <see cref="IBrowserWorkspaceLifecycle"/> 分开：生命周期讲的是「创建/关闭」，
/// 呈现讲的是「让人看得见」。需要**真实渲染**的操作（截图、原生输入）在表面不可见时必然失败，
/// 这时工具调用应当先把页面呈现出来，而不是把失败丢给调用方；
/// 但呈现只对具备 UI 的宿主有意义 —— 独立实现的测试替身不必被迫实现它（接口隔离）。
/// </summary>
public interface IBrowserPagePresenter
{
    /// <summary>
    /// 确保页面成为可见的前台标签。返回后调用方可以假定「再试一次就会成功」；
    /// 无法呈现时抛 <see cref="InvalidOperationException"/>（不静默返回，避免调用方误以为已经可见）。
    /// </summary>
    Task PresentPageAsync(BrowserContextId contextId, PageId pageId, CancellationToken ct);
}

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
    bool CanGoBack { get; }
    bool CanGoForward { get; }
    bool IsLoading { get; }

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
    Task<ScrollOutcome> ScrollAsync(ScrollOptions options, CancellationToken ct);
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
    BrowserElementInfo Info { get; }

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
