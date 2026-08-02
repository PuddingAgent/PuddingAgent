using System.Text.Json;
using PuddingBrowser.Abstractions;
using PuddingBrowser.Protocol;

namespace PuddingHost.BrowserBridge;

public sealed class RemoteBrowserPage : IBrowserPage
{
    private readonly RemoteBrowserRuntime _runtime;
    private bool _disposed;

    internal RemoteBrowserPage(RemoteBrowserRuntime runtime, BrowserPageDescriptor descriptor)
    {
        _runtime = runtime;
        Apply(descriptor);
    }

    public PageId Id { get; private set; }
    public BrowserContextId ContextId { get; private set; }
    public long PageVersion => Info.PageVersion;
    public PageInfo Info { get; private set; } = null!;
    public bool CanGoBack { get; private set; }
    public bool CanGoForward { get; private set; }
    public bool IsLoading { get; private set; }

    public async Task<NavigationResult> GotoAsync(
        Uri url,
        NavigationOptions options,
        CancellationToken ct)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(options);
        var result = await _runtime.ExecuteAsync<BrowserNavigationResultDescriptor>(
            BrowserBridgeCommandNames.PageGoto,
            new PageGotoArguments { Url = url.ToString(), TimeoutMs = options.TimeoutMs },
            ContextId,
            Id,
            TimeSpan.FromMilliseconds(Math.Max(1_000, options.TimeoutMs + 2_000)),
            ct);
        Apply(result.Page);
        return new NavigationResult
        {
            Url = new Uri(result.Url, UriKind.Absolute),
            Ok = result.Ok,
            StatusCode = result.StatusCode,
            ErrorText = result.ErrorText
        };
    }

    public Task GoBackAsync(CancellationToken ct)
        => ExecuteNavigationAsync(BrowserBridgeCommandNames.PageGoBack, ct);
    public Task GoForwardAsync(CancellationToken ct)
        => ExecuteNavigationAsync(BrowserBridgeCommandNames.PageGoForward, ct);
    public Task ReloadAsync(CancellationToken ct)
        => ExecuteNavigationAsync(BrowserBridgeCommandNames.PageReload, ct);
    public Task StopAsync(CancellationToken ct)
        => ExecuteNavigationAsync(BrowserBridgeCommandNames.PageStop, ct);
    public Task BringToFrontAsync(CancellationToken ct)
        => ExecuteNavigationAsync(BrowserBridgeCommandNames.PageActivate, ct,
            new PageActivateArguments { PageId = Id.Value });

    private async Task ExecuteNavigationAsync(
        string commandName,
        CancellationToken ct,
        object? arguments = null)
    {
        EnsureNotDisposed();
        var descriptor = await _runtime.ExecuteAsync<BrowserPageDescriptor>(
            commandName,
            arguments ?? new { },
            ContextId,
            Id,
            TimeSpan.FromSeconds(30),
            ct);
        Apply(descriptor);
    }

    internal static PageInfo ToPageInfo(BrowserPageDescriptor descriptor) => new()
    {
        Id = new PageId(descriptor.PageId),
        ContextId = new BrowserContextId(descriptor.ContextId),
        Title = descriptor.Title,
        Url = descriptor.Url,
        PageVersion = descriptor.PageVersion
    };

    private void Apply(BrowserPageDescriptor descriptor)
    {
        Id = new PageId(descriptor.PageId);
        ContextId = new BrowserContextId(descriptor.ContextId);
        Info = ToPageInfo(descriptor);
        CanGoBack = descriptor.CanGoBack;
        CanGoForward = descriptor.CanGoForward;
        IsLoading = descriptor.IsLoading;
    }

    public async Task<PageSnapshot> SnapshotAsync(SnapshotOptions options, CancellationToken ct)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(options);
        var result = await _runtime.ExecuteAsync<BrowserSnapshotDescriptor>(
            BrowserBridgeCommandNames.PageSnapshot,
            new PageSnapshotArguments
            {
                IncludeDom = options.IncludeDom,
                IncludeAccessibilityTree = options.IncludeAccessibilityTree,
                IncludeHidden = options.IncludeHidden,
                IncludeIframes = options.IncludeIframes,
                IncludeShadowDom = options.IncludeShadowDom,
                IncludeHtml = options.IncludeHtml,
                MaxNodes = options.MaxNodes,
                MaxTextLength = options.MaxTextLength,
                MaxDepth = options.MaxDepth
            },
            ContextId, Id, TimeSpan.FromSeconds(30), ct);
        return new PageSnapshot
        {
            DomText = result.DomText,
            AccessibilityTree = result.AccessibilityTree,
            Html = result.Html,
            Truncated = result.Truncated,
            NodeCount = result.NodeCount
        };
    }

    public async Task<IElementHandle?> QueryAsync(Locator locator, CancellationToken ct)
        => (await QueryAllAsync(locator, ct)).FirstOrDefault();

    public async Task<IReadOnlyList<IElementHandle>> QueryAllAsync(Locator locator, CancellationToken ct)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(locator);
        var result = await _runtime.ExecuteAsync<BrowserLocateResultDescriptor>(
            BrowserBridgeCommandNames.PageLocate,
            new PageLocateArguments { Locator = ToDescriptor(locator) },
            ContextId, Id, TimeSpan.FromSeconds(15), ct);
        return result.Elements.Select(element => (IElementHandle)new RemoteBrowserElementHandle(
            Id, element.PageVersion, ToInfo(element), $"{locator.Kind}:{locator.Value}" )).ToArray();
    }
    public Task<BrowserScriptValue> EvaluateAsync(BrowserScript script, CancellationToken ct) => Unsupported<BrowserScriptValue>();
    public Task<IJsHandle> EvaluateHandleAsync(BrowserScript script, CancellationToken ct) => Unsupported<IJsHandle>();
    public Task<JsonDocument> SendCdpAsync(string method, JsonElement? parameters, CancellationToken ct) => Unsupported<JsonDocument>();
    public Task<BrowserSubscriptionId> SubscribeCdpAsync(string eventName, CancellationToken ct) => Unsupported<BrowserSubscriptionId>();
    public Task UnsubscribeAsync(BrowserSubscriptionId subscriptionId, CancellationToken ct) => Unsupported();
    public Task ClickAsync(Locator locator, ClickOptions options, CancellationToken ct)
        => InteractAsync("click", locator, null, null, null, null, null, ct);
    public Task FillAsync(Locator locator, string value, FillOptions options, CancellationToken ct)
        => InteractAsync("fill", locator, value, null, null, null, null, ct);
    public Task TypeAsync(Locator locator, string text, TypeOptions options, CancellationToken ct)
        => InteractAsync("type", locator, text, null, null, null, null, ct);
    public Task PressAsync(Locator locator, string key, KeyOptions options, CancellationToken ct)
        => InteractAsync("press", locator, key, null, null, null, null, ct);
    public Task HoverAsync(Locator locator, PointerOptions options, CancellationToken ct)
        => InteractAsync("hover", locator, null, null, null, null, null, ct);
    public Task ScrollAsync(ScrollOptions options, CancellationToken ct)
        => InteractAsync("scroll", null, null, null, null, options.DeltaX, options.DeltaY, ct);
    public Task DragAsync(Locator source, Locator target, DragOptions options, CancellationToken ct) => Unsupported();
    public Task SelectAsync(Locator locator, IReadOnlyList<string> values, CancellationToken ct)
        => InteractAsync("select", locator, null, values, null, null, null, ct);
    public Task CheckAsync(Locator locator, bool isChecked, CancellationToken ct)
        => InteractAsync("check", locator, null, null, isChecked, null, null, ct);
    public Task SetInputFilesAsync(Locator locator, IReadOnlyList<string> paths, CancellationToken ct) => Unsupported();
    public async Task<WaitResult> WaitForAsync(WaitCondition condition, CancellationToken ct)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(condition);
        var result = await _runtime.ExecuteAsync<BrowserWaitResultDescriptor>(
            BrowserBridgeCommandNames.PageWaitFor,
            new PageWaitForArguments
            {
                Selector = condition.Selector,
                SelectorToHide = condition.SelectorToHide,
                UrlPattern = condition.UrlPattern,
                TimeoutMs = condition.TimeoutMs
            },
            ContextId, Id,
            TimeSpan.FromMilliseconds(Math.Clamp(condition.TimeoutMs, 1, 120_000) + 2_000), ct);
        Apply(result.Page);
        return new WaitResult { TimedOut = result.TimedOut, Error = result.Error };
    }
    public Task<ScreenshotResult> ScreenshotAsync(ScreenshotOptions options, CancellationToken ct) => Unsupported<ScreenshotResult>();
    public Task<PdfResult> PrintToPdfAsync(PdfOptions options, CancellationToken ct) => Unsupported<PdfResult>();
    public Task OpenDevToolsAsync(CancellationToken ct) => Unsupported();

    private async Task InteractAsync(
        string action,
        Locator? locator,
        string? text,
        IReadOnlyList<string>? values,
        bool? isChecked,
        double? deltaX,
        double? deltaY,
        CancellationToken ct)
    {
        EnsureNotDisposed();
        var result = await _runtime.ExecuteAsync<BrowserInteractionResultDescriptor>(
            BrowserBridgeCommandNames.PageInteract,
            new PageInteractArguments
            {
                Action = action,
                Locator = locator is null ? null : ToDescriptor(locator),
                Text = text,
                Values = values,
                Checked = isChecked,
                DeltaX = deltaX,
                DeltaY = deltaY
            }, ContextId, Id, TimeSpan.FromSeconds(30), ct);
        Apply(result.Page);
    }

    private static BrowserLocatorDescriptor ToDescriptor(Locator locator) => new()
    {
        Kind = locator.Kind.ToString(),
        Value = locator.Value,
        Name = locator.Name,
        Exact = locator.Exact,
        Nth = locator.Nth,
        HasText = locator.HasText
    };

    private static BrowserElementInfo ToInfo(BrowserElementDescriptor descriptor) => new()
    {
        Ref = descriptor.Ref,
        Tag = descriptor.Tag,
        Role = descriptor.Role,
        Name = descriptor.Name,
        Text = descriptor.Text,
        Visible = descriptor.Visible,
        Enabled = descriptor.Enabled,
        Checked = descriptor.Checked,
        BoundingBox = descriptor.BoundingBox is null ? null : new BoundingBox
        {
            X = descriptor.BoundingBox.X,
            Y = descriptor.BoundingBox.Y,
            Width = descriptor.BoundingBox.Width,
            Height = descriptor.BoundingBox.Height
        }
    };

    private static Task Unsupported()
        => Task.FromException(new BrowserOperationException(
            BrowserBridgeErrorCodes.BrowserOperationNotSupported,
            "This browser page operation is not available in Phase 2A-2"));

    private static Task<T> Unsupported<T>()
        => Task.FromException<T>(new BrowserOperationException(
            BrowserBridgeErrorCodes.BrowserOperationNotSupported,
            "This browser page operation is not available in Phase 2A-2"));

    private void EnsureNotDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class RemoteBrowserElementHandle : IElementHandle
{
    public RemoteBrowserElementHandle(
        PageId pageId,
        long pageVersion,
        BrowserElementInfo info,
        string locatorFingerprint)
    {
        PageId = pageId;
        PageVersion = pageVersion;
        Info = info;
        LocatorFingerprint = locatorFingerprint;
        Id = new ElementHandleId(info.Ref);
    }

    public ElementHandleId Id { get; }
    public PageId PageId { get; }
    public long PageVersion { get; }
    public int? BackendNodeId => null;
    public string LocatorFingerprint { get; }
    public BrowserElementInfo Info { get; }

    public Task<BoundingBox?> GetBoundingBoxAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Info.BoundingBox);
    }

    public Task<BrowserScriptValue> EvaluateAsync(BrowserScript script, CancellationToken ct)
        => Task.FromException<BrowserScriptValue>(new BrowserOperationException(
            BrowserBridgeErrorCodes.BrowserOperationNotSupported,
            "Element evaluate is not available in Phase 2A-3"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
