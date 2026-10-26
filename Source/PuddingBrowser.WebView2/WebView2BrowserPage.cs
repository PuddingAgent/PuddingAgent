using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using PuddingBrowser.Abstractions;

namespace PuddingBrowser.WebView2;

/// <summary>
/// Real WebView2 browser page with full navigation support.
/// All CoreWebView2 operations are dispatched through IWebView2UiDispatcher
/// to satisfy WebView2's UI thread constraint.
/// Surface ownership: WpfBrowserSurfaceHost owns the Control; Page only unsubscribes events.
/// </summary>
public sealed class WebView2BrowserPage : IBrowserPage, IPointerInputPage
{
    private readonly IWebView2UiDispatcher _dispatcher;
    private readonly Func<WebView2BrowserPage, Task>? _onNavigationChanged;
    /// <summary>
    /// 指针轨迹点之间的停留时长（毫秒）。
    ///
    /// 它是**行为特征**的一環：CDP 调用是同步快发，6 个 <c>mouseMoved</c> 不打停就是 0 毫秒走完全程，
    /// 页面上看到的是“分步的位置”却“零时间”，比瞬移更可疑。
    /// 12ms × 6 步 ≈ 70ms，与真人把鼠标移向目标的量级一致；真实生效后仍需真机验收（单测只能钉住计划器）。
    /// </summary>
    internal const int PointerMoveSettleMilliseconds = 12;

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

    /// <summary>页面事件日志（由注入策略创建；没有策略时为 null ⇒ 如实不上报）。</summary>
    private IBrowserPageEventLog? _eventLog;

    private IBrowserDriverPolicy _policy = NullBrowserDriverPolicy.Instance;

    /// <summary>
    /// 注入的驱动策略（§7/§8）：未注入时用安全默认（页面同样不会被卡住，只是原生输入/截图明确不支持）。
    /// 换策略时**重建事件日志**，避免把上一份策略的日志带过去。
    /// </summary>
    internal IBrowserDriverPolicy Policy
    {
        get => _policy;
        set
        {
            _policy = value ?? NullBrowserDriverPolicy.Instance;
            _eventLog = _policy.CreateEventLog();
        }
    }

    /// <summary>被自动处理的页面脚本对话框累计次数（诊断用）。</summary>
    internal int ScriptDialogCount { get; private set; }

    /// <summary>被自动处理的页面周边事件累计次数（诊断用）。</summary>
    internal int PageEventCount { get; private set; }
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
            ApplyFingerprintAlignment(surface.CoreWebView);
            // EnsureCoreWebView2Async has already created the initial about:blank
            // document. Its creation predates these navigation subscriptions.
            // Version 0 remains reserved for a page without a live surface.
            _pageVersion = 1;
            SubscribeCoreWebViewEvents(surface.CoreWebView);
        }

        Info = new PageInfo
        {
            Id = id,
            ContextId = contextId,
            Title = string.Empty,
            Url = "about:blank",
            PageVersion = PageVersion
        };
    }

    /// <summary>
    /// 指纹对齐（见 <see cref="WebView2FingerprintScript"/>）：对**后续文档**注册启动脚本，
    /// 同时对**当前已加载文档**补一次，否则本次会话里 <c>chrome.webview</c> 依旧可见。
    /// 失败不抛：指纹对齐是“尽力而为”，不能因为它让页面开不了。
    /// </summary>
    private void ApplyFingerprintAlignment(CoreWebView2 coreWebView)
    {
        try
        {
            _ = coreWebView.AddScriptToExecuteOnDocumentCreatedAsync(WebView2FingerprintScript.Source);
        }
        catch
        {
            // 忽略：注册失败仍然可以在下次导航重试
        }

        try
        {
            _ = coreWebView.ExecuteScriptAsync(WebView2FingerprintScript.Source);
        }
        catch
        {
            // 忽略：当前文档可能还没就绪，导航后会由上面的启动脚本接手
        }
    }

    /// <summary>
    ///
    /// 这张清单是**故意冻结**的（由 <c>DriverEventHandlingTests</c> 钉住）：新增/删除订阅都必须走一次有意识的改动，
    /// 因为它等于"我们接管了哪些会让流程停在那里的事件"。
    ///
    /// **已知缺口（2026-10-08 核实）**：**文件选择器无法接管**。WebView2 SDK 1.0.4078.44 的
    /// <c>Microsoft.Web.WebView2.Core</c> 里**没有** FilePicker/FileChooser 相关 API
    /// （在程序集里检索两个关键字均为 0 处），因此"页面自己弹出文件选择框"这件事我们**接管不了**，
    /// 也就**不假装**：对策只有两条 —— ① 上传走 <c>DOM.setFileInputFiles</c>（真实浏览器路径，见 FileUploadPlan），
    /// ② 明确不支持"页面自行弹选择器"的场景（若页面脚本触发，行为取决于宿主 UI，不在自动化可控范围）。
    /// 若将来 SDK 升级提供该事件，这里是唯一该加订阅的地方，同时要更新冻结清单与本节说明。
    /// </summary>
    private void SubscribeCoreWebViewEvents(CoreWebView2 coreWebView)
    {
        coreWebView.NavigationStarting += OnNavigationStarting;
        coreWebView.NavigationCompleted += OnNavigationCompleted;
        coreWebView.DocumentTitleChanged += OnDocumentTitleChanged;
        coreWebView.SourceChanged += OnSourceChanged;
        coreWebView.ProcessFailed += OnProcessFailed;
        coreWebView.ScriptDialogOpening += OnScriptDialogOpening;
        coreWebView.PermissionRequested += OnPermissionRequested;
        coreWebView.NewWindowRequested += OnNewWindowRequested;
        coreWebView.DownloadStarting += OnDownloadStarting;
    }

    private void UnsubscribeCoreWebViewEvents(CoreWebView2 coreWebView)
    {
        coreWebView.NavigationStarting -= OnNavigationStarting;
        coreWebView.NavigationCompleted -= OnNavigationCompleted;
        coreWebView.DocumentTitleChanged -= OnDocumentTitleChanged;
        coreWebView.SourceChanged -= OnSourceChanged;
        coreWebView.ProcessFailed -= OnProcessFailed;
        coreWebView.ScriptDialogOpening -= OnScriptDialogOpening;
        coreWebView.PermissionRequested -= OnPermissionRequested;
        coreWebView.NewWindowRequested -= OnNewWindowRequested;
        coreWebView.DownloadStarting -= OnDownloadStarting;
    }

    /// <summary>
    /// 权限请求（摄像头/麦克风/定位/通知/剪贴板…）：**一律拒绝**并如实记录。
    /// 不处理时 WebView2 会弹权限询问等人点，自动化流程就卡在那里；而"默认允许"更糟——
    /// 页面不该因为想要就拿到设备权限。
    /// </summary>
    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = CoreWebView2PermissionState.Deny;
        e.Handled = true;

        _eventLog?.Add(Policy.DescribePermissionRequest(e.PermissionKind.ToString(), e.Uri));
        PageEventCount++;
    }

    /// <summary>
    /// 新窗口（<c>window.open</c>/<c>target=_blank</c>）：**抑制**弹窗并记录目标地址。
    /// 不偷偷改成"在当前页打开"——那会丢掉当前页面状态，是调用方没要求的语义改变。
    /// </summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;

        _eventLog?.Add(Policy.DescribeWindowRequest(e.Uri));
        PageEventCount++;
    }

    /// <summary>
    /// 下载：**取消**并记录建议文件名。
    /// 让页面自己决定往哪儿写文件属于需要授权与明确去向的副作用，默认不做。
    /// </summary>
    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
        e.Handled = true;

        _eventLog?.Add(Policy.DescribeDownloadRequest(e.ResultFilePath, e.DownloadOperation?.Uri));
        PageEventCount++;
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
            CoreWebView2ScriptDialogKind.Alert => "Alert",
            CoreWebView2ScriptDialogKind.Confirm => "Confirm",
            CoreWebView2ScriptDialogKind.Prompt => "Prompt",
            _ => "Alert",
        };

        var decision = Policy.AnswerScriptDialog(kind, e.Message);

        // 接管：本版本的 API 没有 `Handled` 属性，语义是「**调用 Accept 即为接受，不调用即为取消**」，
        // 且事件被处理后 WebView2 不再弹默认模态框（页面不会被卡住）。
        if (decision.Accept)
        {
            e.Accept();
        }

        // prompt 的回填文本**永远不提供**（编一个文本替用户提交等于伪造输入）。
        _eventLog?.Add(decision.Summary);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        _isLoading = true;
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        var url = _coreWebView?.Source ?? "about:blank";
        var previous = Info.Url;
        Info = Info with { Url = url };

        // 同文档导航（SPA 的 pushState/replaceState）把整页内容换掉了，却不触发 NavigationCompleted。
        // pageVersion 是「旧 ref 是否还有效」的唯一判据 ⇒ 这里必须补上递增，
        // 否则旧引用会在新路由上被判为仍然有效（实测：抖音推荐流→我的，URL/标题都变了，版本仍是 2）。
        if (PageVersionPolicy.BumpsOnSourceChange(previous, url, _isLoading))
        {
            Interlocked.Increment(ref _pageVersion);
            Info = Info with { PageVersion = PageVersion };
        }
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
        var snapshot = await _dispatcher.InvokeAsync(
            () => WebView2DomClient.SnapshotAsync(webView, PageVersion, _refScopeToken, options, ct), ct);

        // 页面事件事实随快照回传：调用方据此知道"刚才发生了什么、页面为什么没卡住"。
        return snapshot with { PageEvents = DescribePageEvents() };
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
        var payload = SerializeCdpParameters(parameters);

        var result = await _dispatcher.InvokeAsync(
            async () =>
            {
                ct.ThrowIfCancellationRequested();
                if (method == "Page.captureScreenshot" && Surface?.CanCaptureScreenshot != true)
                    throw new BrowserOperationException("browser_not_available",
                        "Screenshot requires a visible browser surface; open the tool panel and select this browser tab.");
                // WebView2's COM task has no CancellationToken overload. Bound the wait so
                // a minimized/hidden renderer cannot hold the command or connection forever.
                // WinUI exposes IAsyncOperation<string>, WPF exposes Task<string>.
                async Task<string> CallAsync() => await webView.CallDevToolsProtocolMethodAsync(method, payload);
                return await CallAsync().WaitAsync(ct);
            }, ct).WaitAsync(ct)
            .ConfigureAwait(false);

        return System.Text.Json.JsonDocument.Parse(result);
    }

    internal static string SerializeCdpParameters(JsonElement? parameters) =>
        parameters is null or { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null }
            ? "{}"
            : parameters!.Value.GetRawText();
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
            ? ExecuteNativeAsync("fill", l, v, ct, clearFirst: o.ClearFirst)
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
    /// <summary>
    /// 把有界日志整理成随快照回传的事实（连续重复合并为 <c>(xN)</c>）；没有事件时给 <c>null</c>
    /// （不填「一切正常」之类的假事实）。
    /// </summary>
    private IReadOnlyList<string>? DescribePageEvents() =>
        _eventLog is not { IsEmpty: false } ? null : _eventLog.Entries.ToArray();

    /// <summary>
    /// §8.2 **坐标输入**（可选能力 <see cref="IPointerInputPage"/>）：坐标是 **CSS viewport 像素**，
    /// 由策略计划成 CDP 鼠标调用后逐条发出（按下/抬起**成对**）。
    /// 策略不支持坐标（返回 <c>null</c>）时**明确失败**，不静默降级成"往页面塞脚本"。
    /// 这里**不推进页面版本**：坐标点击是否改变页面只有回读页面状态才知道，凭猜推进会让旧 Ref 提前作废。
    /// </summary>
    public async Task PointerAsync(PointerInput input, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireWebView();

        var calls = input.Kind switch
        {
            PointerInputKind.Click => Policy.PlanPointerClick(input.X, input.Y, input.ClickCount),
            PointerInputKind.Move => Policy.PlanPointerMove(input.X, input.Y),
            PointerInputKind.Drag => Policy.PlanPointerDrag(input.X, input.Y, input.ToX, input.ToY, input.Steps),
            _ => throw new ArgumentOutOfRangeException(
                nameof(input), input.Kind, "Pointer input kind is not mapped."),
        };

        if (calls is null)
        {
            throw new BrowserOperationException(
                "browser_input_mode_not_supported",
                "coordinate input is not available on this surface; use a locator-based action");
        }

        foreach (var call in calls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parameters = JsonDocument.Parse(call.ParametersJson).RootElement.Clone();
            using var _ = await SendCdpAsync(call.Method, parameters, cancellationToken).ConfigureAwait(false);
        }
    }

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
    /// <summary>
    /// **滚到顶/底**（§7/G8）：按步送真实滚轮事件，每步回读位置与几何后问策略是否继续。
    ///
    /// 三个必须停的理由（策略给出）：到限 / 位置不再变化 / 步数封顶。这里额外加一道**本地硬上限**
    /// 兜底：即使策略被换成有缺陷的实现，也不可能把一次交互变成无限循环。
    /// 回读不到几何 ⇒ **明确失败**（拿不到"到没到"就不猜）。
    /// </summary>
    private async Task<string?> ScrollToArrivalAsync(
        CoreWebView2 webView, bool toBottom, double pointX, double pointY, CancellationToken ct)
    {
        const int HardStepCap = 64;

        double? previous = null;

        for (var steps = 0; ; steps++)
        {
            ct.ThrowIfCancellationRequested();

            var geometry = await ReadScrollGeometryAsync(webView, ct).ConfigureAwait(false)
                ?? throw new BrowserOperationException(
                    "browser_operation_failed",
                    "the page did not report scroll geometry, so an arrival scroll cannot be verified");

            var step = Policy.PlanArrivalStep(
                toBottom, geometry.Position, previous, geometry.ViewportHeight, geometry.ContentHeight, steps)
                ?? throw new BrowserOperationException(
                    "browser_input_mode_not_supported",
                    "native arrival scroll is not available on this surface; use input_mode=dom");

            if (!step.ShouldContinue)
            {
                return step.StopReason;
            }

            if (steps >= HardStepCap)
            {
                // 策略没有自己封顶时的兜底（不静默继续，也如实说明是谁停的）。
                return $"stopped after the driver's hard cap of {HardStepCap} steps";
            }

            var plan = Policy.PlanScroll(pointX, pointY, 0, step.Delta)
                ?? throw new BrowserOperationException(
                    "browser_input_mode_not_supported",
                    "native scroll is not available on this surface; use input_mode=dom");

            foreach (var call in plan)
            {
                ct.ThrowIfCancellationRequested();
                var parameters = JsonDocument.Parse(call.ParametersJson).RootElement.Clone();
                using var _ = await SendCdpAsync(call.Method, parameters, ct).ConfigureAwait(false);
            }

            previous = geometry.Position;
        }
    }

    /// <summary>回读滚动位置与几何（<c>scrollY</c>/<c>scrollHeight</c>/<c>innerHeight</c>）；读不到返回 null。</summary>
    private async Task<(double Position, double ViewportHeight, double ContentHeight)?> ReadScrollGeometryAsync(
        CoreWebView2 webView, CancellationToken ct)
    {
        const string Script =
            "(function(){var d=document.scrollingElement||document.documentElement;"
            + "return JSON.stringify({y:(window.scrollY||d.scrollTop||0),"
            + "sh:(d?d.scrollHeight:0),ih:(window.innerHeight||0)});})()";

        var raw = await _dispatcher.InvokeAsync(
            async () => await webView.ExecuteScriptAsync(Script), ct).ConfigureAwait(false);

        try
        {
            // ExecuteScriptAsync 返回的是 JS 值的 JSON 编码 ⇒ 需要解两层。
            using var outer = JsonDocument.Parse(raw);
            if (outer.RootElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            using var inner = JsonDocument.Parse(outer.RootElement.GetString() ?? string.Empty);
            if (!TryReadDouble(inner.RootElement, "y", out var position)
                || !TryReadDouble(inner.RootElement, "sh", out var contentHeight)
                || !TryReadDouble(inner.RootElement, "ih", out var viewportHeight))
            {
                return null;
            }

            return (position, viewportHeight, contentHeight);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value);
    }

    /// <summary>
    /// **把参考元素滚进视口**（§7 `to_ref`）：与滚到顶/底同构 —— 每步回读元素相对视口的上下边，
    /// 问策略"继续/停/为什么"，按步送真实滚轮事件；本地硬上限兜底。
    /// </summary>
    private async Task<string?> ScrollToRefAsync(
        CoreWebView2 webView, string elementRef, double pointX, double pointY, CancellationToken ct)
    {
        const int HardStepCap = 64;

        double? previousTop = null;

        for (var steps = 0; ; steps++)
        {
            ct.ThrowIfCancellationRequested();

            var rect = await ReadElementRectAsync(webView, elementRef, ct).ConfigureAwait(false)
                ?? throw new BrowserOperationException(
                    "browser_element_not_found",
                    "the to_ref target element is not in the page (it may have been replaced); re-observe the page");

            var step = Policy.PlanToRefStep(
                rect.Top, rect.Bottom, previousTop, rect.ViewportHeight, steps)
                ?? throw new BrowserOperationException(
                    "browser_input_mode_not_supported",
                    "native to_ref scroll is not available on this surface; use input_mode=dom");

            if (!step.ShouldContinue)
            {
                return step.StopReason;
            }

            if (steps >= HardStepCap)
            {
                return $"stopped after the driver's hard cap of {HardStepCap} steps";
            }

            var plan = Policy.PlanScroll(pointX, pointY, 0, step.Delta)
                ?? throw new BrowserOperationException(
                    "browser_input_mode_not_supported",
                    "native scroll is not available on this surface; use input_mode=dom");

            foreach (var call in plan)
            {
                ct.ThrowIfCancellationRequested();
                var parameters = JsonDocument.Parse(call.ParametersJson).RootElement.Clone();
                using var _ = await SendCdpAsync(call.Method, parameters, ct).ConfigureAwait(false);
            }

            previousTop = rect.Top;
        }
    }

    /// <summary>回读参考元素相对视口的上下边与视口高度；元素不在或读不到返回 null。</summary>
    private async Task<(double Top, double Bottom, double ViewportHeight)?> ReadElementRectAsync(
        CoreWebView2 webView, string elementRef, CancellationToken ct)
    {
        // 引用属性上的引号/反斜杠必须转义，否则选择器无效或选错元素（与上传路径同一规则）。
        var escaped = elementRef.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

        var script =
            "(function(){var el=document.querySelector('[data-pudding-ref=\"" + escaped + "\"]');"
            + "if(!el){return \"null\";}var r=el.getBoundingClientRect();"
            + "return JSON.stringify({t:r.top,b:r.bottom,ih:(window.innerHeight||0)});})()";

        var raw = await _dispatcher.InvokeAsync(
            async () => await webView.ExecuteScriptAsync(script), ct).ConfigureAwait(false);

        try
        {
            using var outer = JsonDocument.Parse(raw);
            if (outer.RootElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var payload = outer.RootElement.GetString();
            if (string.IsNullOrWhiteSpace(payload) || payload == "null")
            {
                return null;
            }

            using var inner = JsonDocument.Parse(payload);
            if (!TryReadDouble(inner.RootElement, "t", out var top)
                || !TryReadDouble(inner.RootElement, "b", out var bottom)
                || !TryReadDouble(inner.RootElement, "ih", out var viewportHeight))
            {
                return null;
            }

            return (top, bottom, viewportHeight);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<ScrollOutcome> ScrollNativelyAsync(
        CoreWebView2 webView, ScrollOptions options, CancellationToken ct)
    {        double pointX;
        double pointY;

        if (options.Target == ScrollTarget.Element && options.Locator is { } locator)
        {
            var handle = await QueryAsync(locator, ct).ConfigureAwait(false)
                ?? throw new BrowserOperationException(
                    "browser_element_not_found", "the scroll target element was not found");
            var box = await handle.GetBoundingBoxAsync(ct).ConfigureAwait(false)
                ?? throw new BrowserOperationException(
                    "browser_element_not_found", "the scroll target has no geometry");
            pointX = box.X + (box.Width / 2);
            pointY = box.Y + (box.Height / 2);
        }
        else
        {
            using var metrics = await SendCdpAsync("Page.getLayoutMetrics", null, ct).ConfigureAwait(false);
            var viewport = metrics.RootElement.GetProperty("cssLayoutViewport");
            pointX = viewport.GetProperty("clientWidth").GetDouble() / 2;
            pointY = viewport.GetProperty("clientHeight").GetDouble() / 2;
        }

        var deltaX = options.DeltaX ?? 0;
        var deltaY = options.DeltaY ?? 0;

        // 到达类目标（to_top/to_bottom）：原生滚轮没有原子"滚到底"，只能按步送并**必须能停**。
        // 每步都问策略"继续还是停、为什么停"，位置与几何**从页面回读**（不猜）。
        string? arrivalStopReason = null;

        if (options.Target is ScrollTarget.ToTop or ScrollTarget.ToBottom)
        {
            arrivalStopReason = await ScrollToArrivalAsync(
                webView,
                toBottom: options.Target == ScrollTarget.ToBottom,
                pointX,
                pointY,
                ct).ConfigureAwait(false);
        }
        else if (options.Target == ScrollTarget.ToRef)
        {
            // to_ref 需要参考元素：契约层已经强制必须有 locator（这里再确认一次，不猜）。
            var elementRef = options.Locator?.Name;
            if (string.IsNullOrWhiteSpace(elementRef))
            {
                throw new BrowserOperationException(
                    "browser_invalid_arguments", "scroll target 'to_ref' requires an element locator");
            }

            arrivalStopReason = await ScrollToRefAsync(webView, elementRef, pointX, pointY, ct).ConfigureAwait(false);
        }
        else
        {
            var plan = Policy.PlanScroll(pointX, pointY, deltaX, deltaY)
                ?? throw new BrowserOperationException(
                    "browser_input_mode_not_supported",
                    "native scroll is not available on this surface; use input_mode=dom");

            foreach (var call in plan)
            {
                ct.ThrowIfCancellationRequested();
                var parameters = JsonDocument.Parse(call.ParametersJson).RootElement.Clone();
                using var _ = await SendCdpAsync(call.Method, parameters, ct).ConfigureAwait(false);
            }
        }

        Interlocked.Increment(ref _pageVersion);

        // 只读回读：位移 0 的"滚动"不改任何东西，只把前后位置/边界/内容变化取回来。
        var outcome = await _dispatcher.InvokeAsync(
            () => WebView2DomClient.ScrollAsync(
                webView,
                PageVersion,
                _refScopeToken,
                new ScrollOptions { Target = options.Target, Locator = options.Locator, DeltaX = 0, DeltaY = 0 },
                ct),
            ct).ConfigureAwait(false);

        // 到达滚动为什么要停必须回给调用方：否则"停在边界"和"滚不动"没法区分。
        return arrivalStopReason is null ? outcome : outcome with { StopReason = arrivalStopReason };
    }

    /// <summary>
    /// **拖拽**（§7）：原生路径 = 从起点中心按下、分步移动到终点中心、抬起（见策略的 PlanDrag）。
    /// DOM 模式**明确 unsupported**：用脚本伪造 drag 事件在真实页面上经常不生效（尤其 HTML5 拖放），
    /// 假装成功比拒绝更糟。
    /// </summary>
    public async Task DragAsync(Locator s, Locator t, DragOptions o, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(o);

        if (o.InputMode != InputMode.Native)
        {
            throw new BrowserOperationException(
                "browser_input_mode_not_supported",
                "drag requires input_mode=native (the DOM path does not implement drag)");
        }

        var from = await QueryAsync(s, ct).ConfigureAwait(false)
            ?? throw new BrowserOperationException("browser_element_not_found", "no element matches the drag source");
        var to = await QueryAsync(t, ct).ConfigureAwait(false)
            ?? throw new BrowserOperationException("browser_element_not_found", "no element matches the drag target");

        var fromBox = await from.GetBoundingBoxAsync(ct).ConfigureAwait(false)
            ?? throw new BrowserOperationException("browser_element_not_found", "the drag source has no geometry");
        var toBox = await to.GetBoundingBoxAsync(ct).ConfigureAwait(false)
            ?? throw new BrowserOperationException("browser_element_not_found", "the drag target has no geometry");

        var plan = Policy.PlanDrag(
            fromBox.X + (fromBox.Width / 2), fromBox.Y + (fromBox.Height / 2),
            toBox.X + (toBox.Width / 2), toBox.Y + (toBox.Height / 2))
            ?? throw new BrowserOperationException(
                "browser_input_mode_not_supported", "native drag is not available on this surface");

        try
        {
            foreach (var call in plan)
            {
                ct.ThrowIfCancellationRequested();
                var parameters = JsonDocument.Parse(call.ParametersJson).RootElement.Clone();
                using var _ = await SendCdpAsync(call.Method, parameters, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Increment(ref _pageVersion);
        }
    }
    public Task SelectAsync(Locator l, IReadOnlyList<string> v, CancellationToken ct)
        => ExecuteInteractionAsync("select", l, v, deltaX: null, deltaY: null, ct);
    public Task CheckAsync(Locator l, bool c, CancellationToken ct)
        => ExecuteInteractionAsync("check", l, c, deltaX: null, deltaY: null, ct);
    /// <summary>
    /// **上传文件**（§7）：三步 CDP —— 拿根节点 → 按元素上的 data-pudding-ref 查节点 → 把文件交给节点。
    ///
    /// 为什么不是"往页面里塞 files"：页面脚本**无权**设置 <c>input.files</c>（浏览器安全模型），
    /// 因此这条路只能走浏览器自身的 CDP；做不到时明确 unsupported，绝不假装成功。
    /// </summary>
    public async Task SetInputFilesAsync(Locator l, IReadOnlyList<string> p, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(l);
        ArgumentNullException.ThrowIfNull(p);

        if (p.Count == 0)
        {
            throw new BrowserOperationException("browser_invalid_arguments", "upload requires at least one file path");
        }

        // 先确认元素存在（否则后面的 querySelector 只会返回 0，报错含义会模糊）。
        _ = await QueryAsync(l, ct).ConfigureAwait(false)
            ?? throw new BrowserOperationException("browser_element_not_found", "no element matches the upload target");

        var documentParameters = Policy.BuildFileUploadDocumentParameters()
            ?? throw new BrowserOperationException(
                "browser_input_mode_not_supported", "file upload is not available on this surface");

        using var documentJson = JsonDocument.Parse(documentParameters);
        using (var document = await SendCdpAsync(
            "DOM.getDocument", documentJson.RootElement.Clone(), ct).ConfigureAwait(false))
        {
            if (!document.RootElement.TryGetProperty("root", out var root)
                || !root.TryGetProperty("nodeId", out var rootIdElement)
                || !rootIdElement.TryGetInt32(out var rootId))
            {
                throw new BrowserOperationException(
                    "browser_operation_failed", "the browser did not report a document root node");
            }

            var queryParameters = Policy.BuildFileUploadQueryParameters(rootId, l.Value)
                ?? throw new BrowserOperationException(
                    "browser_input_mode_not_supported", "file upload is not available on this surface");

            using var queryJson = JsonDocument.Parse(queryParameters);
            int nodeId;
            using (var query = await SendCdpAsync(
                "DOM.querySelector", queryJson.RootElement.Clone(), ct).ConfigureAwait(false))
            {
                if (!query.RootElement.TryGetProperty("nodeId", out var nodeIdElement)
                    || !nodeIdElement.TryGetInt32(out nodeId)
                    || nodeId == 0)
                {
                    // 查不到节点 ⇒ 引用已经不对了（节点被换掉/页面变了）：按引用过期处理。
                    throw new BrowserOperationException(
                        "stale_element_reference",
                        "the upload target node could not be resolved from its ref; re-observe the page");
                }
            }

            var setParameters = Policy.BuildFileUploadSetFilesParameters(nodeId, p)
                ?? throw new BrowserOperationException(
                    "browser_input_mode_not_supported", "file upload is not available on this surface");

            using var setJson = JsonDocument.Parse(setParameters);
            using var _ = await SendCdpAsync(
                "DOM.setFileInputFiles", setJson.RootElement.Clone(), ct).ConfigureAwait(false);
        }

        Interlocked.Increment(ref _pageVersion);
    }
    public async Task<WaitResult> WaitForAsync(WaitCondition c, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(c);
        if (string.IsNullOrWhiteSpace(c.Selector)
            && string.IsNullOrWhiteSpace(c.SelectorToHide)
            && string.IsNullOrWhiteSpace(c.UrlPattern)
            && c.BaselineNodeCount is null)
        {
            throw new BrowserOperationException(
                "browser_invalid_arguments",
                "A selector, selectorToHide, urlPattern, or baselineNodeCount is required");
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
            if (matched && c.BaselineNodeCount is { } baseline)
            {
                // 内容增长（G8 无限滚动）：读到元素数才算数；读不到 ⇒ 条件**未知**，本轮不满足（不猜）。
                var count = await _dispatcher.InvokeAsync(
                    () => WebView2DomClient.EvaluateNodeCountAsync(webView, ct), ct);
                matched = count is { } actual && actual > baseline;
            }

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

        // 参数语义（格式/quality/整页 clip）由注入策略决定（Automation 的 ScreenshotPlan 单独测试）。
        double contentX = 0, contentY = 0, contentWidth = 0, contentHeight = 0;
        if (o.FullPage)
        {
            using var metrics = await SendCdpAsync("Page.getLayoutMetrics", null, ct).ConfigureAwait(false);
            if (!metrics.RootElement.TryGetProperty("cssContentSize", out var content)
                || !content.TryGetProperty("width", out var width)
                || !content.TryGetProperty("height", out var height))
            {
                throw new BrowserOperationException(
                    "browser_operation_failed",
                    "the browser did not report a content size, so a full-page screenshot cannot be taken honestly");
            }

            contentX = content.TryGetProperty("x", out var x) ? x.GetDouble() : 0;
            contentY = content.TryGetProperty("y", out var y) ? y.GetDouble() : 0;
            contentWidth = width.GetDouble();
            contentHeight = height.GetDouble();
        }

        async Task<byte[]> CaptureAtAsync(int? quality, CancellationToken token)
        {
            var parameters = Policy.BuildScreenshotParameters(
                quality, o.FullPage, contentX, contentY, contentWidth, contentHeight)
                ?? throw new BrowserOperationException(
                    "browser_operation_not_supported",
                    "screenshot is not available on this surface (no driver policy supports it)");

            using var document = JsonDocument.Parse(parameters);
            using var captured = await SendCdpAsync(
                "Page.captureScreenshot", document.RootElement.Clone(), token).ConfigureAwait(false);

            try
            {
                var base64 = captured.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String
                    ? data.GetString()
                    : null;
                if (string.IsNullOrEmpty(base64))
                {
                    throw new InvalidOperationException("the browser returned no screenshot payload");
                }

                return Convert.FromBase64String(base64);
            }
            catch (Exception exception) when (exception is InvalidOperationException or FormatException)
            {
                throw new BrowserOperationException("browser_operation_failed", exception.Message);
            }
        }

        // 画质阶梯（仅 JPEG）：detail=low 承诺 512px/**40 KiB**，而 512px 已是硬上限，
        // 固定 q80 在密集页面上必然撞预算（抖音搜索结果实测 43502/56061 B）——
        // “固定画质 + 硬预算”这对组合不可能总成立，所以按**结果**选画质：
        // 第一个装进预算的档位胜出，降到底仍超就如实报错（仍不截断、不冒充可读）。
        int? usedQuality = o.Quality;
        byte[] bytes;
        if (o.Quality is { } startQuality)
        {
            // 档位由**策略**给（Policy.ScreenshotQualitySteps）：驱动只负责按序拍，
            // 这样阶梯逻辑既不依赖 Automation（WinUI 面只允许引用 Abstractions），
            // 也不必在驱动里复制一份。策略不支持降档时只拍一次。
            var steps = Policy.ScreenshotQualitySteps(startQuality);
            if (steps.Count == 0)
            {
                steps = [startQuality];
            }

            byte[]? best = null;
            var bestQuality = steps[0];
            var fits = false;

            foreach (var attemptQuality in steps)
            {
                var candidate = await CaptureAtAsync(attemptQuality, ct).ConfigureAwait(false);
                if (best is null || candidate.Length < best.Length)
                {
                    best = candidate;
                    bestQuality = attemptQuality;
                }

                if (o.MaxBytes is not { } limit || candidate.Length <= limit)
                {
                    best = candidate;
                    bestQuality = attemptQuality;
                    fits = true;
                    break;
                }
            }

            bytes = best!;
            usedQuality = bestQuality;

            if (!fits && o.MaxBytes is { } jpegLimit)
            {
                throw new BrowserOperationException(
                    "browser_resource_exhausted",
                    $"screenshot is {bytes.Length} bytes even at q{bestQuality} (the lowest deliberate quality),"
                    + $" over the {jpegLimit}-byte budget; capture a smaller region or raise the budget"
                    + $" (a screenshot is never truncated to fit, and quality is not pushed below"
                    + $" q{steps[^1]} because a mush is not evidence)");
            }
        }
        else
        {
            // PNG 是无损的：没有 quality 可降，降“画质”对它无意义，只能如实报预算。
            bytes = await CaptureAtAsync(null, ct).ConfigureAwait(false);
            if (o.MaxBytes is { } losslessLimit && bytes.Length > losslessLimit)
            {
                throw new BrowserOperationException(
                    "browser_resource_exhausted",
                    $"screenshot is {bytes.Length} bytes, over the {losslessLimit}-byte budget;"
                    + " capture a smaller region, use jpeg, or raise the budget"
                    + " (a screenshot is never truncated to fit: half an image invites wrong conclusions)");
            }
        }

        if (o.FilePath is { Length: > 0 } path)
        {
            await System.IO.File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
        }

        return new ScreenshotResult { Bytes = bytes, FilePath = o.FilePath, Quality = usedQuality };
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
        CancellationToken ct,
        bool clearFirst = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(locator);

        var handle = await QueryAsync(locator, ct).ConfigureAwait(false)
            ?? throw new BrowserOperationException(
                "browser_element_not_found", $"no element matches {locator}");

        var box = await handle.GetBoundingBoxAsync(ct).ConfigureAwait(false)
            ?? throw new BrowserOperationException(
                "browser_element_not_found", "the matched element has no geometry; native input needs a box");

        var centerX = box.X + (box.Width / 2);
        var centerY = box.Y + (box.Height / 2);

        IReadOnlyList<DriverCall>? plan;
        switch (action)
        {
            case "click":
                plan = Policy.PlanClick(centerX, centerY, 1);
                break;
            case "hover":
                plan = Policy.PlanHover(centerX, centerY);
                break;
            case "fill":
            case "type":
            case "press":
            {
                // 键盘类动作**必须先让目标获得焦点**：CDP 的 Input.* 只作用于当前焦点元素，
                // 不会自己去找 locator 指向的节点。少了这一下，insertText 会落到上一个焦点持有者身上
                // （2026-10-26 实测：抖音搜索框表现为「文字追加到旧值后面」）。
                // 指针类动作本身就是聚焦动作，不需要额外点击。
                var focusPlan = Policy.PlanClick(centerX, centerY, 1);

                // 【2026-10-26 实测结论】非 ASCII（中文/emoji/全角）**不走剪贴板**：
                // 合成的 Ctrl+V 只把文字写进 DOM，Draft.js 的 EditorState 仍为空
                // （占位符不消失、发送按钮不出现，评论提交不了）；而 Input.insertText
                // 会同时更新 DOM 与框架状态。PlanFill / PlanType 内部已按 ASCII/非 ASCII
                // 分流，驱动层不再二次判断。

                IReadOnlyList<DriverCall>? body = action switch
                {
                    "fill" => Policy.PlanFill(value),
                    "type" => Policy.PlanType(value),
                    _ => Policy.PlanPress(value),
                };
                if (focusPlan is null || body is null)
                {
                    plan = null;
                    break;
                }

                // fill 的契约是「设置值」，而 insertText 是在光标处插入。
                // ClearFirst 因此必须真的先全选再删除，否则「替换」会被静默变成「拼接」。
                if (action == "fill" && clearFirst && !string.IsNullOrEmpty(value))
                {
                    var selectAll = Policy.PlanPress("Control+A");
                    var delete = Policy.PlanPress("Delete");
                    if (selectAll is null || delete is null)
                    {
                        plan = null;
                        break;
                    }
                    body = [.. selectAll, .. delete, .. body];
                }

                plan = [.. focusPlan, .. body];
                break;
            }
            default:
                plan = null;
                break;
        }

        // 策略返回 null ⇒ 这一面不支持该动作：**明确拒绝**，绝不静默回落成 DOM 输入。
        if (plan is null)
        {
            throw new BrowserOperationException(
                "browser_input_mode_not_supported",
                $"native input is not available for action '{action}' on this surface; use input_mode=dom");
        }

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

                // **时间维度**：轨迹点若在同一毫秒内发完，等于“瞬移但分 6 份记日志”。
                // 只对**无按键按下**的移动停留：拖拽的中间步已在按下态，节奏由拖动本身表达。
                if (call.Method == "Input.dispatchMouseEvent"
                    && parameters.TryGetProperty("type", out var callType)
                    && callType.GetString() == "mouseMoved"
                    && parameters.TryGetProperty("buttons", out var heldButtons)
                    && heldButtons.TryGetInt32(out var heldCount)
                    && heldCount == 0)
                {
                    await Task.Delay(PointerMoveSettleMilliseconds, ct).ConfigureAwait(false);
                }
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
        foreach (var call in Policy.ReleaseModifiers(modifiers) ?? [])
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
