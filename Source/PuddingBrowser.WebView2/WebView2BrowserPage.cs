using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using PuddingBrowser.Abstractions;
using PuddingBrowser.Automation;

namespace PuddingBrowser.WebView2;

/// <summary>
/// Real WebView2 browser page with full navigation support.
/// All CoreWebView2 operations are dispatched through IWebView2UiDispatcher
/// to satisfy WebView2's UI thread constraint.
/// Surface ownership: WpfBrowserSurfaceHost owns the Control; Page only unsubscribes events.
/// </summary>
public sealed class WebView2BrowserPage : IBrowserPage
{
    private readonly IWebView2UiDispatcher _dispatcher;
    private readonly Func<WebView2BrowserPage, Task>? _onNavigationChanged;
    private readonly SemaphoreSlim _navigationGate = new(1, 1);
    private readonly SemaphoreSlim _domGate = new(1, 1);
    private CoreWebView2? _coreWebView;
    private long _pageVersion;
    private bool _canGoBack;
    private bool _canGoForward;
    private bool _isLoading;
    private bool _disposed;

    /// <summary>
    /// 页面级 ref 作用域 token（设计 §5）：由**驱动侧**生成，页面脚本写不出来。
    /// ref 形如 <c>p{token}-v{版本}-n{序号}</c>；因为序号是每页独立生成的，
    /// 没有它就无法区分「页面 A 的 v7-n3」与「页面 B 的 v7-n3」。
    /// </summary>
    private readonly string _refScopeToken = Guid.NewGuid().ToString("N");

    public PageId Id { get; }
    public BrowserContextId ContextId { get; }
    public IBrowserSurface? Surface { get; }

    public long PageVersion => Interlocked.Read(ref _pageVersion);

    /// <summary>页面级 ref 作用域 token（诊断/测试用；不进工具输出，避免被当作可复用标识）。</summary>
    internal string RefScopeToken => _refScopeToken;

    /// <summary>
    /// 最近一次被自动处理的页面脚本对话框摘要（有界、无敏感内容）；没有过则为 <c>null</c>。
    /// **不用于继续交互**，只用于说明"刚才有个弹窗被处理了，所以页面没卡住"。
    /// </summary>
    internal string? LastScriptDialog { get; private set; }

    /// <summary>被自动处理的页面脚本对话框累计次数（诊断用）。</summary>
    internal int ScriptDialogCount { get; private set; }
    public PageInfo Info { get; private set; }
    public bool CanGoBack => _canGoBack;
    public bool CanGoForward => _canGoForward;
    public bool IsLoading => _isLoading;

    internal WebView2BrowserPage(
        PageId id,
        BrowserContextId contextId,
        IBrowserSurface? surface,
        IWebView2UiDispatcher dispatcher,
        Func<WebView2BrowserPage, Task>? onNavigationChanged)
    {
        Id = id;
        ContextId = contextId;
        Surface = surface;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _onNavigationChanged = onNavigationChanged;

        if (surface is not null)
        {
            _coreWebView = surface.CoreWebView;
            SubscribeCoreWebViewEvents(surface.CoreWebView);
        }

        Info = new PageInfo
        {
            Id = id,
            ContextId = contextId,
            Title = string.Empty,
            Url = "about:blank",
            PageVersion = 0
        };
    }

    private void SubscribeCoreWebViewEvents(CoreWebView2 coreWebView)
    {
        coreWebView.NavigationStarting += OnNavigationStarting;
        coreWebView.NavigationCompleted += OnNavigationCompleted;
        coreWebView.DocumentTitleChanged += OnDocumentTitleChanged;
        coreWebView.SourceChanged += OnSourceChanged;
        coreWebView.ProcessFailed += OnProcessFailed;
        coreWebView.ScriptDialogOpening += OnScriptDialogOpening;
    }

    private void UnsubscribeCoreWebViewEvents(CoreWebView2 coreWebView)
    {
        coreWebView.NavigationStarting -= OnNavigationStarting;
        coreWebView.NavigationCompleted -= OnNavigationCompleted;
        coreWebView.DocumentTitleChanged -= OnDocumentTitleChanged;
        coreWebView.SourceChanged -= OnSourceChanged;
        coreWebView.ProcessFailed -= OnProcessFailed;
        coreWebView.ScriptDialogOpening -= OnScriptDialogOpening;
    }

    /// <summary>
    /// 页面脚本对话框（<c>alert</c>/<c>confirm</c>/<c>prompt</c>/<c>beforeunload</c>）。
    ///
    /// **必须处理**：不处理时 WebView2 会弹一个模态框并**卡住页面脚本**，之后所有动作都会莫名失败
    /// （这类"悬停不动"的故障最难排查）。这里按 <see cref="ScriptDialogPolicy"/> 确定性作答，
    /// 并把有界的事实摘要留在页面上（<see cref="LastScriptDialog"/>），供回执/诊断说明"刚发生了什么"。
    /// </summary>
    private void OnScriptDialogOpening(object? sender, CoreWebView2ScriptDialogOpeningEventArgs e)
    {
        var kind = e.Kind switch
        {
            CoreWebView2ScriptDialogKind.Alert => ScriptDialogPolicy.Kind.Alert,
            CoreWebView2ScriptDialogKind.Confirm => ScriptDialogPolicy.Kind.Confirm,
            CoreWebView2ScriptDialogKind.Prompt => ScriptDialogPolicy.Kind.Prompt,
            _ => ScriptDialogPolicy.Kind.Alert,
        };

        var answer = ScriptDialogPolicy.Decide(kind);

        // 接管：本版本的 API 没有 `Handled` 属性，语义是「**调用 Accept 即为接受，不调用即为取消**」，
        // 且事件被处理后 WebView2 不再弹默认模态框（页面不会被卡住）。
        if (answer == ScriptDialogPolicy.Answer.Accept)
        {
            e.Accept();
        }

        // prompt 的回填文本**永远不提供**（编一个文本替用户提交等于伪造输入）。
        var summary = ScriptDialogPolicy.Describe(kind, e.Message, answer);
        LastScriptDialog = summary;
        ScriptDialogCount++;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        _isLoading = true;
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        Info = Info with { Url = _coreWebView?.Source ?? "about:blank" };
    }

    private void OnDocumentTitleChanged(object? sender, object e)
    {
        Info = Info with { Title = _coreWebView?.DocumentTitle ?? string.Empty };
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _isLoading = false;
        _canGoBack = _coreWebView?.CanGoBack == true;
        _canGoForward = _coreWebView?.CanGoForward == true;
        Interlocked.Increment(ref _pageVersion);
        Info = Info with
        {
            Url = _coreWebView?.Source ?? Info.Url,
            Title = _coreWebView?.DocumentTitle ?? Info.Title,
            PageVersion = PageVersion
        };
        _onNavigationChanged?.Invoke(this);
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        // Convert to page failed state — do not crash WPF process
        Info = Info with { Title = $"[Process Failed: {e.ProcessFailedKind}]" };
        Interlocked.Increment(ref _pageVersion);
    }

    /// <summary>
    /// Navigates to the given URL. Serializes concurrent navigations per page
    /// to prevent NavigationCompleted handler cross-completion.
    /// All CoreWebView2 access goes through the UI dispatcher.
    /// </summary>
    public async Task<NavigationResult> GotoAsync(
        Uri url, NavigationOptions options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        if (_coreWebView is null)
            return new NavigationResult { Url = url, Ok = false, ErrorText = "No WebView" };

        await _navigationGate.WaitAsync(ct);
        try
        {
            return await _dispatcher.InvokeAsync(async () =>
            {
                var tcs = new TaskCompletionSource<NavigationResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

#if WINUI_BROWSER
                Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs>? handler = null;
#else
                EventHandler<CoreWebView2NavigationCompletedEventArgs>? handler = null;
#endif
                handler = (s, e) =>
                {
                    _coreWebView!.NavigationCompleted -= handler;
                    var sourceStr = _coreWebView.Source;
                    var resultUrl = sourceStr is not null ? new Uri(sourceStr, UriKind.RelativeOrAbsolute) : url;
                    tcs.TrySetResult(new NavigationResult
                    {
                        Url = resultUrl.IsAbsoluteUri ? resultUrl : url,
                        Ok = e.IsSuccess,
                        StatusCode = (int)e.HttpStatusCode,
                        ErrorText = e.IsSuccess ? null : e.WebErrorStatus.ToString()
                    });
                };

                _coreWebView.NavigationCompleted += handler;
                _isLoading = true;
                _coreWebView.Navigate(url.ToString());
                Info = Info with { Url = url.ToString() };

                var timeout = options.TimeoutMs > 0
                    ? TimeSpan.FromMilliseconds(options.TimeoutMs)
                    : TimeSpan.FromSeconds(30);

                using var timeoutCts = new CancellationTokenSource(timeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                try
                {
                    return await tcs.Task.WaitAsync(linked.Token);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    _coreWebView.NavigationCompleted -= handler;
                    _coreWebView.Stop();
                    _isLoading = false;
                    return new NavigationResult { Url = url, Ok = false, ErrorText = "Navigation timeout" };
                }
                catch (OperationCanceledException)
                {
                    _coreWebView.NavigationCompleted -= handler;
                    _isLoading = false;
                    return new NavigationResult { Url = url, Ok = false, ErrorText = "Navigation cancelled" };
                }
            }, ct);
        }
        finally
        {
            _navigationGate.Release();
        }
    }

    public Task GoBackAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _dispatcher.InvokeAsync(() =>
        {
            if (_coreWebView?.CanGoBack == true)
                _coreWebView.GoBack();
            return Task.CompletedTask;
        }, ct);
    }

    public Task GoForwardAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _dispatcher.InvokeAsync(() =>
        {
            if (_coreWebView?.CanGoForward == true)
                _coreWebView.GoForward();
            return Task.CompletedTask;
        }, ct);
    }

    public Task ReloadAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _dispatcher.InvokeAsync(() =>
        {
            _coreWebView?.Reload();
            return Task.CompletedTask;
        }, ct);
    }

    public Task StopAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _dispatcher.InvokeAsync(() =>
        {
            _coreWebView?.Stop();
            return Task.CompletedTask;
        }, ct);
    }

    public Task BringToFrontAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task<PageSnapshot> SnapshotAsync(SnapshotOptions options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);
        var webView = RequireWebView();
        return await _dispatcher.InvokeAsync(
            () => WebView2DomClient.SnapshotAsync(webView, PageVersion, _refScopeToken, options, ct), ct);
    }

    public async Task<IElementHandle?> QueryAsync(Locator locator, CancellationToken ct)
        => (await QueryAllAsync(locator, ct)).FirstOrDefault();

    public async Task<IReadOnlyList<IElementHandle>> QueryAllAsync(Locator locator, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(locator);
        var webView = RequireWebView();
        var version = PageVersion;
        var elements = await _dispatcher.InvokeAsync(
            () => WebView2DomClient.LocateAsync(webView, version, _refScopeToken, locator, ct), ct);
        var fingerprint = $"{locator.Kind}:{locator.Value}:{locator.Name}:{locator.Nth}:{locator.HasText}";
        return elements.Select(info => (IElementHandle)new WebView2ElementHandle(
            Id, version, fingerprint, info)).ToArray();
    }
    public Task<BrowserScriptValue> EvaluateAsync(BrowserScript script, CancellationToken ct)
        => throw new NotSupportedException("CDP not in Phase 2A-1");
    public Task<IJsHandle> EvaluateHandleAsync(BrowserScript script, CancellationToken ct)
        => throw new NotSupportedException("CDP not in Phase 2A-1");
    /// <summary>
    /// 发一条 **CDP** 命令（设计方案 §7 的原生输入路径就靠它）。
    ///
    /// WebView2 自带 <c>CallDevToolsProtocolMethodAsync</c>，因此这里不需要另开调试端口；
    /// 参数原样透传（调用方负责序列化），返回的 JSON 由调用方解释。
    /// <c>SubscribeCdpAsync</c>（事件订阅）仍明确 unsupported —— 它需要另外的事件泵，
    /// 本轮不假装支持。
    /// </summary>
    public async Task<System.Text.Json.JsonDocument> SendCdpAsync(
        string method, System.Text.Json.JsonElement? parameters, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        var webView = RequireWebView();
        var payload = parameters is { ValueKind: System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null }
            ? "{}"
            : parameters!.Value.GetRawText();

        var result = await _dispatcher.InvokeAsync(
            () => webView.CallDevToolsProtocolMethodAsync(method, payload), ct).ConfigureAwait(false);

        return System.Text.Json.JsonDocument.Parse(result);
    }
    public Task<BrowserSubscriptionId> SubscribeCdpAsync(string eventName, CancellationToken ct)
        => throw new NotSupportedException("CDP not in Phase 2A-1");
    public Task UnsubscribeAsync(BrowserSubscriptionId sid, CancellationToken ct)
        => throw new NotSupportedException("CDP not in Phase 2A-1");
    public Task ClickAsync(Locator l, ClickOptions o, CancellationToken ct)
        => o.InputMode == InputMode.Native
            ? ExecuteNativeAsync("click", l, value: null, ct)
            : ExecuteInteractionAsync("click", l, value: null, deltaX: null, deltaY: null, ct);
    public Task FillAsync(Locator l, string v, FillOptions o, CancellationToken ct)
        => o.InputMode == InputMode.Native
            ? ExecuteNativeAsync("fill", l, v, ct)
            : ExecuteInteractionAsync("fill", l, v, deltaX: null, deltaY: null, ct);
    public Task TypeAsync(Locator l, string t, TypeOptions o, CancellationToken ct)
        => o.InputMode == InputMode.Native
            ? ExecuteNativeAsync("type", l, t, ct)
            : ExecuteInteractionAsync("type", l, t, deltaX: null, deltaY: null, ct);
    public Task PressAsync(Locator l, string k, KeyOptions o, CancellationToken ct)
        => o.InputMode == InputMode.Native
            ? ExecuteNativeAsync("press", l, k, ct)
            : ExecuteInteractionAsync("press", l, k, deltaX: null, deltaY: null, ct);
    public Task HoverAsync(Locator l, PointerOptions o, CancellationToken ct)
        => o.InputMode == InputMode.Native
            ? ExecuteNativeAsync("hover", l, value: null, ct)
            : ExecuteInteractionAsync("hover", l, value: null, deltaX: null, deltaY: null, ct);
    public async Task<ScrollOutcome> ScrollAsync(ScrollOptions o, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(o);
        var webView = RequireWebView();

        if (o.InputMode == InputMode.Native)
        {
            return await ScrollNativelyAsync(webView, o, ct).ConfigureAwait(false);
        }

        // 与其它动作一致：把脚本执行排到 UI 线程上（跨线程访问 WebView2 会直接失败）。
        var outcome = await _dispatcher.InvokeAsync(
            () => WebView2DomClient.ScrollAsync(webView, PageVersion, _refScopeToken, o, ct), ct)
            .ConfigureAwait(false);

        Interlocked.Increment(ref _pageVersion);
        return outcome;
    }
    /// <summary>
    /// **原生滚动**（§7 <c>inputMode=native</c>）：在正确的落点发真实滚轮事件（CDP
    /// <c>Input.dispatchMouseEvent(mouseWheel)</c>），而不是改 <c>scrollTop</c>。
    ///
    /// 事实从哪来：滚轮事件本身不带结果，因此滚动**之后**用只读观察（DOM 侧读位置/边界/内容变化）
    /// 把事实如实取回 —— 输入是原生的，观察是 DOM 的，这个分工在返回的
    /// <see cref="ScrollOutcome"/> 里如实成立（<c>Container</c> 等字段即观察结果）。
    ///
    /// 落点规则：有元素目标 ⇒ 元素中心（滚轮作用在指针下的可滚动祖先）；页面目标 ⇒ 视口中心
    /// （视口尺寸来自 CDP <c>Page.getLayoutMetrics</c>，不猜窗口大小）。
    /// </summary>
    private async Task<ScrollOutcome> ScrollNativelyAsync(
        CoreWebView2 webView, ScrollOptions options, CancellationToken ct)
    {
        NativeInputSequence.InputTarget target;

        if (options.Target == ScrollTarget.Element && options.Locator is { } locator)
        {
            var handle = await QueryAsync(locator, ct).ConfigureAwait(false)
                ?? throw new BrowserOperationException(
                    "browser_element_not_found", "the scroll target element was not found");
            var box = await handle.GetBoundingBoxAsync(ct).ConfigureAwait(false)
                ?? throw new BrowserOperationException(
                    "browser_element_not_found", "the scroll target has no geometry");
            target = new NativeInputSequence.InputTarget(box.X, box.Y, box.Width, box.Height);
        }
        else
        {
            using var metrics = await SendCdpAsync("Page.getLayoutMetrics", null, ct).ConfigureAwait(false);
            var viewport = metrics.RootElement.GetProperty("cssLayoutViewport");
            target = new NativeInputSequence.InputTarget(
                viewport.GetProperty("clientWidth").GetDouble() / 2,
                viewport.GetProperty("clientHeight").GetDouble() / 2,
                Width: 0,
                Height: 0);
        }

        var deltaX = options.DeltaX ?? 0;
        var deltaY = options.DeltaY ?? 0;

        foreach (var call in NativeInputSequence.PlanScroll(target, deltaX, deltaY))
        {
            ct.ThrowIfCancellationRequested();
            var parameters = JsonDocument.Parse(call.ParametersJson).RootElement.Clone();
            using var _ = await SendCdpAsync(call.Method, parameters, ct).ConfigureAwait(false);
        }

        Interlocked.Increment(ref _pageVersion);

        // 只读回读：位移 0 的"滚动"不改任何东西，只把前后位置/边界/内容变化取回来。
        return await _dispatcher.InvokeAsync(
            () => WebView2DomClient.ScrollAsync(
                webView,
                PageVersion,
                _refScopeToken,
                new ScrollOptions { Target = options.Target, Locator = options.Locator, DeltaX = 0, DeltaY = 0 },
                ct),
            ct).ConfigureAwait(false);
    }

    public Task DragAsync(Locator s, Locator t, DragOptions o, CancellationToken ct)
        => throw new NotSupportedException("DOM not in Phase 2A-1");
    public Task SelectAsync(Locator l, IReadOnlyList<string> v, CancellationToken ct)
        => ExecuteInteractionAsync("select", l, v, deltaX: null, deltaY: null, ct);
    public Task CheckAsync(Locator l, bool c, CancellationToken ct)
        => ExecuteInteractionAsync("check", l, c, deltaX: null, deltaY: null, ct);
    public Task SetInputFilesAsync(Locator l, IReadOnlyList<string> p, CancellationToken ct)
        => throw new NotSupportedException("DOM not in Phase 2A-1");
    public async Task<WaitResult> WaitForAsync(WaitCondition c, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(c);
        if (string.IsNullOrWhiteSpace(c.Selector)
            && string.IsNullOrWhiteSpace(c.SelectorToHide)
            && string.IsNullOrWhiteSpace(c.UrlPattern))
        {
            throw new BrowserOperationException(
                "browser_invalid_arguments", "A selector, selectorToHide, or urlPattern is required");
        }

        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(c.TimeoutMs, 1, 120_000));
        var started = System.Diagnostics.Stopwatch.StartNew();
        var webView = RequireWebView();
        while (started.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            var matched = true;
            if (!string.IsNullOrWhiteSpace(c.Selector))
            {
                matched &= await _dispatcher.InvokeAsync(
                    () => WebView2DomClient.EvaluateSelectorConditionAsync(
                        webView, c.Selector, hidden: false, ct), ct);
            }
            if (!string.IsNullOrWhiteSpace(c.SelectorToHide))
            {
                matched &= await _dispatcher.InvokeAsync(
                    () => WebView2DomClient.EvaluateSelectorConditionAsync(
                        webView, c.SelectorToHide, hidden: true, ct), ct);
            }
            if (!string.IsNullOrWhiteSpace(c.UrlPattern))
                matched &= WebView2DomClient.WildcardMatch(Info.Url, c.UrlPattern);
            if (matched)
                return new WaitResult { TimedOut = false };
            await Task.Delay(100, ct);
        }
        return new WaitResult { TimedOut = true, Error = "Timeout" };
    }
    /// <summary>
    /// **截图**（设计方案 §6 的视觉观察）：走 CDP <c>Page.captureScreenshot</c>（WebView2 自带 CDP 通道），
    /// 不自己拼 canvas —— 那是"看起来像截图"的另一回事（漏跨域资源、漏系统渲染）。
    ///
    /// 语义选择：
    /// ① <c>FullPage</c> ⇒ 用 <c>Page.getLayoutMetrics</c> 的内容尺寸做 clip 并 <c>captureBeyondViewport</c>，
    ///    因此**整页**而不是"当前视口"；
    /// ② 带 <c>Quality</c> ⇒ JPEG（画质参数只对有损格式有意义，PNG 上给了是无效输入，因此**明确拒绝**）；
    /// ③ <c>FilePath</c> 由调用方给出的路径写盘；写盘失败不吞掉（截图拿到了就是拿到了，写盘错误原样抛出）。
    /// </summary>
    public async Task<ScreenshotResult> ScreenshotAsync(ScreenshotOptions o, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(o);

        if (o.Quality is { } quality and (< 0 or > 100))
        {
            throw new BrowserOperationException(
                "browser_invalid_arguments", "screenshot quality must be between 0 and 100");
        }

        // 参数语义（格式/quality/整页 clip）由 ScreenshotPlan 一处决定并单独测试。
        ScreenshotPlan.ContentSize? contentSize = null;
        if (o.FullPage)
        {
            using var metrics = await SendCdpAsync("Page.getLayoutMetrics", null, ct).ConfigureAwait(false);
            if (!ScreenshotPlan.TryReadContentSize(metrics.RootElement, out var size))
            {
                throw new BrowserOperationException(
                    "browser_operation_failed",
                    "the browser did not report a content size, so a full-page screenshot cannot be taken honestly");
            }

            contentSize = size;
        }

        var json = ScreenshotPlan.Build(o.Quality, o.FullPage, contentSize);
        using var document = JsonDocument.Parse(json);
        using var captured = await SendCdpAsync(
            "Page.captureScreenshot", document.RootElement.Clone(), ct).ConfigureAwait(false);

        byte[] bytes;
        try
        {
            bytes = ScreenshotPlan.Decode(captured.RootElement);
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            throw new BrowserOperationException("browser_operation_failed", exception.Message);
        }

        if (o.FilePath is { Length: > 0 } path)
        {
            await System.IO.File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
        }

        // 字节预算：**绝不截断图片**（半张图会让读者得出错误结论），超了如实报资源耗尽。
        if (ScreenshotPlan.CheckBudget(bytes.Length, o.MaxBytes) == ScreenshotPlan.BudgetVerdict.Exceeded)
        {
            throw new BrowserOperationException(
                "browser_resource_exhausted",
                ScreenshotPlan.DescribeBudget(bytes.Length, o.MaxBytes!.Value));
        }

        return new ScreenshotResult { Bytes = bytes, FilePath = o.FilePath };
    }
    public Task<PdfResult> PrintToPdfAsync(PdfOptions o, CancellationToken ct)
        => throw new NotSupportedException("Not in Phase 2A-1");

    public Task OpenDevToolsAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _dispatcher.InvokeAsync(() =>
        {
            _coreWebView?.OpenDevToolsWindow();
            return Task.CompletedTask;
        }, ct);
    }

    private CoreWebView2 RequireWebView()
        => _coreWebView ?? throw new BrowserOperationException(
            "browser_not_available", "The browser page has no WebView2 surface");

    private async Task ExecuteInteractionAsync(
        string action,
        Locator? locator,
        object? value,
        double? deltaX,
        double? deltaY,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var webView = RequireWebView();
        await _domGate.WaitAsync(ct);
        try
        {
            _ = await _dispatcher.InvokeAsync(
                () => WebView2DomClient.InteractAsync(
                    webView, PageVersion, _refScopeToken, action, locator, value, deltaX, deltaY, ct), ct);
        }
        finally
        {
            _domGate.Release();
        }
    }

    /// <summary>
    /// **原生输入路径**（设计方案 §7）：按 <c>NativeInputSequence</c> 规划好的 CDP 调用逐条发出。
    ///
    /// 三条硬性约定：
    /// ① 需要元素几何 ⇒ 先按定位符取句柄与其包围盒；取不到就是"目标问题"，明确报错；
    /// ② **不静默回落**：本方法只做 native；不可用/不支持的动作由调用方（surface）明确拒绝；
    /// ③ 中途取消时**清理按键状态**（<c>ReleaseModifiers</c>），避免页面以为修饰键一直按着。
    /// </summary>
    private async Task ExecuteNativeAsync(
        string action,
        Locator locator,
        string? value,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(locator);

        var handle = await QueryAsync(locator, ct).ConfigureAwait(false)
            ?? throw new BrowserOperationException(
                "browser_element_not_found", $"no element matches {locator}");

        var box = await handle.GetBoundingBoxAsync(ct).ConfigureAwait(false)
            ?? throw new BrowserOperationException(
                "browser_element_not_found", "the matched element has no geometry; native input needs a box");

        var target = new NativeInputSequence.InputTarget(box.X, box.Y, box.Width, box.Height);
        var plan = action switch
        {
            "click" => NativeInputSequence.PlanClick(target),
            "hover" => NativeInputSequence.PlanHover(target),
            "fill" => NativeInputSequence.PlanFill(value),
            "type" => NativeInputSequence.PlanType(value),
            "press" => NativeInputSequence.PlanPress(value),
            _ => throw new BrowserOperationException(
                "browser_input_mode_not_supported",
                $"native input is not implemented for action '{action}'"),
        };

        var modifiers = 0;
        try
        {
            foreach (var call in plan)
            {
                ct.ThrowIfCancellationRequested();
                if (call.Method == "Input.dispatchKeyEvent"
                    && JsonDocument.Parse(call.ParametersJson).RootElement.TryGetProperty("modifiers", out var m))
                {
                    modifiers |= m.GetInt32();
                }

                var parameters = JsonDocument.Parse(call.ParametersJson).RootElement.Clone();
                using var _ = await SendCdpAsync(call.Method, parameters, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 取消也要把按键状态清干净（尽力而为；失败不掩盖取消本身）。
            await ReleaseModifiersAsync(modifiers, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            Interlocked.Increment(ref _pageVersion);
        }
    }

    private async Task ReleaseModifiersAsync(int modifiers, CancellationToken ct)
    {
        foreach (var call in NativeInputSequence.ReleaseModifiers(modifiers))
        {
            try
            {
                var parameters = JsonDocument.Parse(call.ParametersJson).RootElement.Clone();
                using var _ = await SendCdpAsync(call.Method, parameters, ct).ConfigureAwait(false);
            }
            catch
            {
                // 清理是尽力而为：它失败也不能掩盖真正的取消/异常。
            }
        }
    }

    /// <summary>
    /// Disposes the page. Only unsubscribes CoreWebView2 events.
    /// Surface disposal is owned by WpfBrowserSurfaceHost.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_coreWebView is not null)
        {
            try
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    UnsubscribeCoreWebViewEvents(_coreWebView);
                    _coreWebView.Stop();
                    return Task.CompletedTask;
                }, CancellationToken.None);
            }
            catch { /* best effort during dispose */ }
        }

        // Do NOT dispose Surface here — WpfBrowserSurfaceHost owns it
        _navigationGate.Dispose();
        _domGate.Dispose();
    }
}
