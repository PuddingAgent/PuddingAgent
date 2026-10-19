using System.Text.Json;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Abstractions;
using PuddingBrowser.Protocol;

namespace PuddingHost.BrowserBridge;

public sealed class RemoteBrowserPage : IBrowserPage, IPointerInputPage
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
            new PageActivateArguments
            {
                PageId = Id.Value,
                // H3（tabs）：activate 有副作用，钉住本客户端已知版本；未知留空由 Desktop 拒绝。
                ExpectedPageVersion = PageVersion > 0 ? PageVersion : null,
            });

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
                MaxDepth = options.MaxDepth,
                ScopeRef = options.ScopeRef,
                SinceRevision = options.SinceRevision,
                IncludeContentBlocks = options.IncludeContentBlocks
            },
            ContextId, Id, TimeSpan.FromSeconds(30), ct);
        return new PageSnapshot
        {
            DomText = result.DomText,
            AccessibilityTree = result.AccessibilityTree,
            Html = result.Html,
            Truncated = result.Truncated,
            NodeCount = result.NodeCount,
            // 修订与差异由宿主（真正读到内容的那一侧）判定，这里只做形状搬运。
            Revision = result.Revision,
            Changed = result.Changed,
            ContentBlocks = result.ContentBlocks,
            PageEvents = result.PageEvents,
            Diff = result.Diff is { } diff
                ? new TextDiff
                {
                    Added = diff.Added,
                    Removed = diff.Removed,
                    AddedCount = diff.AddedCount,
                    RemovedCount = diff.RemovedCount,
                    Truncated = diff.Truncated,
                }
                : null
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
    /// <summary>
    /// 桥接路径的滚动：**目标模型与结果事实都过线**（§7）。
    /// 远端未报告事实时返回 <c>unreported</c>（不编造"滚了 0 像素"）。
    /// </summary>
    public async Task<ScrollOutcome> ScrollAsync(ScrollOptions options, CancellationToken ct)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(options);

        var result = await _runtime.ExecuteAsync<BrowserInteractionResultDescriptor>(
            BrowserBridgeCommandNames.PageInteract,
            new PageInteractArguments
            {
                Action = "scroll",
                // H3：写操作必须钉住版本；用客户端当前页面版本（0/未知留空，由 Desktop 拒绝而非猜）。
                ExpectedPageVersion = PageVersion > 0 ? PageVersion : null,
                Locator = options.Locator is null ? null : ToDescriptor(options.Locator),
                DeltaX = options.DeltaX,
                DeltaY = options.DeltaY,
                ScrollTarget = ScrollTargetWire.NameOf(options.Target),
            },
            ContextId,
            Id,
            TimeSpan.FromSeconds(30),
            ct).ConfigureAwait(false);

        Apply(result.Page);

        return result.Scroll is { } scroll
            ? new ScrollOutcome
            {
                Container = scroll.Container,
                BeforeX = scroll.BeforeX,
                BeforeY = scroll.BeforeY,
                AfterX = scroll.AfterX,
                AfterY = scroll.AfterY,
                ConsumedDeltaX = scroll.ConsumedDeltaX,
                ConsumedDeltaY = scroll.ConsumedDeltaY,
                AtBoundary = scroll.AtBoundary,
                ContentChanged = scroll.ContentChanged,
            }
            : new ScrollOutcome { Container = "unreported" };
    }

    // 滚动目标线名由 Abstractions 的 ScrollTargetWire 统一给出（此处曾自己写一份 switch）。
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

    /// <summary>
    /// §8.2 坐标输入：转发到桥命令 <c>page.interact</c>（动作名 <c>pointer_click</c>/<c>pointer_move</c> + 坐标）。
    /// 拖拽需要终点坐标，而线缆尚未携带 ⇒ **明确拒绝**（不臆造终点）。
    /// </summary>
    public Task PointerAsync(PointerInput input, CancellationToken cancellationToken) => input.Kind switch
    {
        PointerInputKind.Click => InteractAsync(
            "pointer_click", locator: null, text: null, values: null, isChecked: null,
            deltaX: null, deltaY: null, ct: cancellationToken, x: input.X, y: input.Y),
        PointerInputKind.Move => InteractAsync(
            "pointer_move", locator: null, text: null, values: null, isChecked: null,
            deltaX: null, deltaY: null, ct: cancellationToken, x: input.X, y: input.Y),
        PointerInputKind.Drag => InteractAsync(
            "pointer_drag", locator: null, text: null, values: null, isChecked: null,
            deltaX: null, deltaY: null, ct: cancellationToken,
            x: input.X, y: input.Y, toX: input.ToX, toY: input.ToY),
        _ => throw new ArgumentOutOfRangeException(nameof(input), input.Kind, "Pointer input kind is not mapped."),
    };

    private async Task InteractAsync(
        string action,
        Locator? locator,
        string? text,
        IReadOnlyList<string>? values,
        bool? isChecked,
        double? deltaX,
        double? deltaY,
        CancellationToken ct,
        double? x = null,         double? y = null,         string? observationId = null,
        double? toX = null,         double? toY = null)
    {
        EnsureNotDisposed();
        var result = await _runtime.ExecuteAsync<BrowserInteractionResultDescriptor>(
            BrowserBridgeCommandNames.PageInteract,
            new PageInteractArguments
            {
                Action = action,
                // H3：写操作钉住版本（0/未知留空，Desktop 拒绝）。
                ExpectedPageVersion = PageVersion > 0 ? PageVersion : null,
                Locator = locator is null ? null : ToDescriptor(locator),
                Text = text,
                Values = values,
                Checked = isChecked,
                DeltaX = deltaX,
                DeltaY = deltaY,
            X = x,
            Y = y,
            ObservationId = observationId,
            ToX = toX,             ToY = toY
            }, ContextId, Id, TimeSpan.FromSeconds(30), ct);
        Apply(result.Page);
    }

    private static BrowserLocatorDescriptor ToDescriptor(Locator locator) => new()
    {
        // 线缆上的 kind 是**线名**（kebab-case，真源 DesktopLocatorKindWire），不是枚举名：
        // 接收端 BrowserBridgeCommandAdapter.TryMapLocator 按线名严格解析、不猜测不回退，
        // 送 "AltText"/"Role" 这类枚举名会被判为"未注册"⇒ 十种定位全部 fail closed。
        Kind = LocatorWireName(locator.Kind),
        Value = locator.Value,
        Name = locator.Name,
        Exact = locator.Exact,
        Nth = locator.Nth,
        HasText = locator.HasText
    };

    /// <summary>
    /// 运行时定位枚举 → 线名。两套枚举成员名一一对应（见 <see cref="DesktopLocatorKind"/> 注释：
    /// 「与 PuddingBrowser.Abstractions.LocatorKind 等价，取值为线名真源」），因此按成员名过渡一次，
    /// 再由 <see cref="DesktopLocatorKindWire"/> 给出唯一线名。成员名若不相等则**响亮失败**，不静默退化。
    /// </summary>
    internal static string LocatorWireName(LocatorKind kind)
    {
        if (!Enum.TryParse<DesktopLocatorKind>(kind.ToString(), ignoreCase: false, out var desktopKind))
        {
            throw new BrowserOperationException(
                BrowserBridgeErrorCodes.BrowserOperationNotSupported,
                $"locator kind '{kind}' has no wire equivalent");
        }

        return DesktopLocatorKindWire.NameOf(desktopKind);
    }

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
