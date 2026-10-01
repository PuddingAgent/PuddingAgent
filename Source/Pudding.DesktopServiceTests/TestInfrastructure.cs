using System.Collections.Concurrent;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;
using Pudding.DesktopService;
using Proto = Pudding.Rpc.Protocol.V1;

namespace DesktopServiceTests;

internal static class TestWait
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(10);

    public static async Task UntilAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        var limit = DateTime.UtcNow + (timeout ?? Default);
        while (!condition())
        {
            if (DateTime.UtcNow > limit)
            {
                throw new TimeoutException($"Condition was not met within the test budget: {because}");
            }

            await Task.Delay(10);
        }
    }
}

/// <summary>测试时钟：真实时间 + 手动偏移（既能让 deadline 可预测，也不破坏真实定时器语义）。</summary>
internal sealed class TestTimeProvider : TimeProvider
{
    private long _offsetTicks;

    public override DateTimeOffset GetUtcNow() =>
        new(DateTimeOffset.UtcNow.UtcTicks + Interlocked.Read(ref _offsetTicks), TimeSpan.Zero);

    public void Advance(TimeSpan delta) => Interlocked.Add(ref _offsetTicks, delta.Ticks);
}

/// <summary>
/// 假 UI 调度器：可以模拟「UI 线程就在当前线程」（内联执行）、「UI 线程忙」（排队等待显式泵）、
/// 「队列拒绝入队」与「调度器已释放」四种真实形态。
/// </summary>
internal sealed class ManualUiDispatcher : IDesktopUiDispatcher
{
    private readonly object _sync = new();
    private readonly Queue<Func<Task>> _queue = new();

    public bool HasThreadAccess { get; set; }

    public bool RefuseQueue { get; set; }

    public bool IsDisposed { get; set; }

    public int QueuedCount
    {
        get
        {
            lock (_sync)
            {
                return _queue.Count;
            }
        }
    }

    public Task InvokeAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken) =>
        InvokeAsync(
            async token =>
            {
                await action(token);
                return true;
            },
            cancellationToken);

    public Task<T> InvokeAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(ManualUiDispatcher));
        }

        if (RefuseQueue)
        {
            throw new InvalidOperationException("dispatcher queue is closed");
        }

        if (HasThreadAccess)
        {
            return action(cancellationToken);
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _queue.Enqueue(async () =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        completion.TrySetCanceled(cancellationToken);
                        return;
                    }

                    completion.TrySetResult(await action(cancellationToken));
                }
                catch (OperationCanceledException)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
        }

        return completion.Task;
    }

    /// <summary>模拟 UI 线程开始处理排队的回调（不等待回调内部完成）。</summary>
    public int Pump()
    {
        Func<Task>[] pending;
        lock (_sync)
        {
            pending = _queue.ToArray();
            _queue.Clear();
        }

        foreach (var item in pending)
        {
            _ = item();
        }

        return pending.Length;
    }
}

/// <summary>记录型 UI 动作表面：可脚本化返回值、阻塞门与抛异常。</summary>
internal sealed class RecordingUiSurface : IDesktopUiSurface
{
    private readonly ConcurrentQueue<string> _calls = new();

    public TaskCompletionSource? Gate { get; set; }

    public Func<NavigateRequest, DesktopCallContext, CancellationToken, Task<CapabilityResult<NavigateResult>>>? NavigateHandler { get; set; }

    public Func<JavascriptRequest, DesktopCallContext, CancellationToken, Task<CapabilityResult<JavascriptResult>>>? JavascriptHandler { get; set; }

    public IReadOnlyList<string> Calls => _calls.ToArray();

    public int NavigateCount => _calls.Count(call => call.StartsWith("navigate", StringComparison.Ordinal));

    public int JavascriptCount => _calls.Count(call => call.StartsWith("javascript", StringComparison.Ordinal));

    public int NotificationCount => _calls.Count(call => call.StartsWith("notification", StringComparison.Ordinal));

    public int PageStateCount => _calls.Count(call => call.StartsWith("page_state", StringComparison.Ordinal));

    public int ShellStatusCount => _calls.Count(call => string.Equals(call, "shell_status", StringComparison.Ordinal));

    /// <summary>surface 只报告它知道的窗口/托盘部分；其余由 DesktopService 补齐。</summary>
    public Func<CancellationToken, Task<CapabilityResult<DesktopShellStatus>>>? ShellStatusHandler { get; set; }

    public async Task<CapabilityResult<NavigateResult>> NavigateAsync(
        DesktopCallContext context, NavigateRequest request, CancellationToken cancellationToken)
    {
        _calls.Enqueue($"navigate:{request.Target}");
        await AwaitGateAsync(cancellationToken);
        return NavigateHandler is null
            ? CapabilityResult<NavigateResult>.Success(
                new NavigateResult(NavigateDisposition.Completed, request.Url, DesktopPageVersion.Require(9)))
            : await NavigateHandler(request, context, cancellationToken);
    }

    public async Task<CapabilityResult<JavascriptResult>> ExecuteJavascriptAsync(
        DesktopCallContext context, JavascriptRequest request, CancellationToken cancellationToken)
    {
        _calls.Enqueue($"javascript:{request.Target}");
        await AwaitGateAsync(cancellationToken);
        return JavascriptHandler is null
            ? CapabilityResult<JavascriptResult>.Success(new JavascriptResult(JavascriptValueKind.String, "\"ok\"", false))
            : await JavascriptHandler(request, context, cancellationToken);
    }

    public async Task<CapabilityResult<DesktopPageState>> GetPageStateAsync(
        DesktopCallContext context, DesktopPageTarget target, CancellationToken cancellationToken)
    {
        _calls.Enqueue($"page_state:{target}");
        await AwaitGateAsync(cancellationToken);
        return CapabilityResult<DesktopPageState>.Success(
            new DesktopPageState(target, new Uri("https://example.com/state"), DesktopPageVersion.Require(9), DesktopPageReadiness.Complete));
    }

    public async Task<CapabilityResult<DesktopNotificationResult>> ShowNotificationAsync(
        DesktopCallContext context, DesktopNotificationRequest request, CancellationToken cancellationToken)
    {
        _calls.Enqueue("notification");
        await AwaitGateAsync(cancellationToken);
        return CapabilityResult<DesktopNotificationResult>.Success(new DesktopNotificationResult(true, "n-1"));
    }

    public async Task<CapabilityResult<DesktopShellStatus>> GetShellStatusAsync(
        DesktopCallContext context, CancellationToken cancellationToken)
    {
        _calls.Enqueue("shell_status");
        await AwaitGateAsync(cancellationToken);

        return ShellStatusHandler is null
            ? CapabilityResult<DesktopShellStatus>.Success(new DesktopShellStatus(
                DesktopWindowState.HiddenToTray, trayVisible: true, DesktopAutomationState.Free, openPageCount: 0))
            : await ShellStatusHandler(cancellationToken);
    }

    public int DialogCount => _calls.Count(call => call.StartsWith("dialog", StringComparison.Ordinal));

    public Func<DesktopDialogRequest, CancellationToken, Task<CapabilityResult<DesktopDialogResult>>>? DialogHandler { get; set; }

    public async Task<CapabilityResult<DesktopDialogResult>> RequestDialogAsync(
        DesktopCallContext context, DesktopDialogRequest request, CancellationToken cancellationToken)
    {
        _calls.Enqueue("dialog");
        await AwaitGateAsync(cancellationToken);

        return DialogHandler is null
            ? CapabilityResult<DesktopDialogResult>.Success(new DesktopDialogResult(DesktopDialogChoice.Ok))
            : await DialogHandler(request, cancellationToken);
    }

    public int ClipboardCount => _calls.Count(call => call.StartsWith("clipboard", StringComparison.Ordinal));

    public Func<ClipboardReadRequest, CancellationToken, Task<CapabilityResult<DesktopClipboardContent>>>? ClipboardHandler { get; set; }

    public async Task<CapabilityResult<DesktopClipboardContent>> ReadClipboardAsync(
        DesktopCallContext context, ClipboardReadRequest request, CancellationToken cancellationToken)
    {
        _calls.Enqueue("clipboard");
        await AwaitGateAsync(cancellationToken);

        // 夹具：默认回带一段文本；调用方可用 ClipboardHandler 覆盖（含超预算、空剪贴板等情形）。
        return ClipboardHandler is null
            ? CapabilityResult<DesktopClipboardContent>.Success(
                new DesktopClipboardContent("clipboard-text", truncated: false))
            : await ClipboardHandler(request, cancellationToken);
    }

    public int TabsCount => _calls.Count(call => call.StartsWith("tabs", StringComparison.Ordinal));

    public Func<BrowserTabsRequest, CancellationToken, Task<CapabilityResult<DesktopTabsResult>>>? TabsHandler { get; set; }

    public async Task<CapabilityResult<DesktopTabsResult>> TabsAsync(
        DesktopCallContext context, BrowserTabsRequest request, CancellationToken cancellationToken)
    {
        _calls.Enqueue($"tabs:{request.Action}");
        await AwaitGateAsync(cancellationToken);

        if (TabsHandler is not null)
        {
            return await TabsHandler(request, cancellationToken);
        }

        var next = DesktopPageVersion.Require(request.ExpectedPageVersion.Value + 1);
        var remaining = request.Action == DesktopTabAction.Close
            ? new DesktopContexts([])
            : new DesktopContexts(
            [
                new DesktopContextInfo("ctx-1", DesktopContextTrust.AgentAuthorized,
                [
                    new DesktopPageInfo(request.Target, next, title: "active", isActive: true, isAgentTarget: true),
                ]),
            ]);

        return CapabilityResult<DesktopTabsResult>.Success(new DesktopTabsResult(
            request.Target,
            request.Action,
            new DesktopPageState(request.Target, new Uri("https://example.com/tab"), next, DesktopPageReadiness.Complete),
            tabClosed: request.Action == DesktopTabAction.Close,
            remaining));
    }

    public int ContextsCount => _calls.Count(call => string.Equals(call, "contexts", StringComparison.Ordinal));

    public async Task<CapabilityResult<DesktopContexts>> GetContextsAsync(
        DesktopCallContext context, CancellationToken cancellationToken)
    {
        _calls.Enqueue("contexts");
        await AwaitGateAsync(cancellationToken);

        return CapabilityResult<DesktopContexts>.Success(new DesktopContexts(
        [
            new DesktopContextInfo("ctx-1", DesktopContextTrust.AgentAuthorized,
            [
                new DesktopPageInfo(
                    new DesktopPageTarget("ctx-1", "page-1"),
                    DesktopPageVersion.Require(9),
                    title: "示例页",
                    url: new Uri("https://example.com/1"),
                    isActive: true,
                    isAgentTarget: true),
            ]),
        ]));
    }

    public int WaitForCount => _calls.Count(call => call.StartsWith("wait_for", StringComparison.Ordinal));

    public async Task<CapabilityResult<DesktopWaitResult>> WaitForAsync(
        DesktopCallContext context, BrowserWaitForRequest request, CancellationToken cancellationToken)
    {
        _calls.Enqueue($"wait_for:{request.Condition}");
        await AwaitGateAsync(cancellationToken);

        // 探针/夹具语义：条件满足（TimedOut=false），并把结束时的页面状态带回来。
        return CapabilityResult<DesktopWaitResult>.Success(new DesktopWaitResult(
            request.Target,
            request.Condition,
            timedOut: false,
            new DesktopPageState(
                request.Target,
                new Uri("https://example.com/waited"),
                DesktopPageVersion.Require(9),
                DesktopPageReadiness.Complete)));
    }

    public int InteractCount => _calls.Count(call => call.StartsWith("interact", StringComparison.Ordinal));

    public Func<BrowserInteractRequest, CancellationToken, Task<CapabilityResult<DesktopInteractionResult>>>? InteractHandler { get; set; }

    public async Task<CapabilityResult<DesktopInteractionResult>> InteractAsync(
        DesktopCallContext context, BrowserInteractRequest request, CancellationToken cancellationToken)
    {
        _calls.Enqueue($"interact:{request.Action}");
        await AwaitGateAsync(cancellationToken);

        // 交互会推进页面版本：返回的状态版本高于请求版本，旧 Ref 自此作废。
        return InteractHandler is null
            ? CapabilityResult<DesktopInteractionResult>.Success(new DesktopInteractionResult(
                request.Target,
                new DesktopPageState(
                    request.Target,
                    new Uri("https://example.com/after"),
                    DesktopPageVersion.Require(request.ExpectedPageVersion.Value + 1),
                    DesktopPageReadiness.Complete),
                request.Locator is null
                    ? null
                    : new DesktopElementRef("e1", "button", DesktopPageVersion.Require(request.ExpectedPageVersion.Value + 1))))
            : await InteractHandler(request, cancellationToken);
    }

    public int LocateCount => _calls.Count(call => call.StartsWith("locate", StringComparison.Ordinal));

    public Func<BrowserLocateRequest, CancellationToken, Task<CapabilityResult<DesktopLocateResult>>>? LocateHandler { get; set; }

    public async Task<CapabilityResult<DesktopLocateResult>> LocateAsync(
        DesktopCallContext context, BrowserLocateRequest request, CancellationToken cancellationToken)
    {
        _calls.Enqueue($"locate:{request.Locator}");
        await AwaitGateAsync(cancellationToken);

        return LocateHandler is null
            ? CapabilityResult<DesktopLocateResult>.Success(new DesktopLocateResult(
                request.Target,
                request.Locator,
                [
                    new DesktopElementRef(
                        "e1", "button", DesktopPageVersion.Require(9), role: "button", name: "提交", visible: true),
                ],
                truncated: false,
                DesktopPageVersion.Require(9)))
            : await LocateHandler(request, cancellationToken);
    }

    public int SnapshotCount => _calls.Count(call => call.StartsWith("snapshot", StringComparison.Ordinal));

    public Func<BrowserSnapshotRequest, CancellationToken, Task<CapabilityResult<DesktopSnapshot>>>? SnapshotHandler { get; set; }

    public async Task<CapabilityResult<DesktopSnapshot>> SnapshotAsync(
        DesktopCallContext context, BrowserSnapshotRequest request, CancellationToken cancellationToken)
    {
        _calls.Enqueue($"snapshot:{request.Target}");
        await AwaitGateAsync(cancellationToken);

        return SnapshotHandler is null
            ? CapabilityResult<DesktopSnapshot>.Success(new DesktopSnapshot(
                request.Target,
                "body > main",
                "document",
                null,
                truncated: false,
                nodeCount: 42,
                DesktopPageVersion.Require(9)))
            : await SnapshotHandler(request, cancellationToken);
    }

    private Task AwaitGateAsync(CancellationToken cancellationToken) =>
        Gate is null ? Task.CompletedTask : Gate.Task.WaitAsync(cancellationToken);
}

/// <summary>被测服务 + 三个可替换边界（调度器/表面/时钟）。</summary>
internal sealed class ServiceHarness
{
    private ServiceHarness(
        ManualUiDispatcher dispatcher,
        RecordingUiSurface surface,
        TestTimeProvider clock,
        DesktopService service)
    {
        Dispatcher = dispatcher;
        Surface = surface;
        Clock = clock;
        Service = service;
    }

    public ManualUiDispatcher Dispatcher { get; }

    public RecordingUiSurface Surface { get; }

    public TestTimeProvider Clock { get; }

    public DesktopService Service { get; }

    public DesktopTargetRegistry Targets => Service.Targets;

    public static readonly DesktopPageTarget AgentPage = new("ctx-agent", "page-1");

    public static readonly DesktopPageTarget WebPage = new("ctx-web", "web-1");

    public static readonly DesktopPageTarget WorkbenchPage = new("ctx-work", "wb-1");

    public static DesktopCapabilityDescriptor Descriptor(DesktopCapability capability) =>
        DesktopCapabilities.All.Single(descriptor => descriptor.Capability == capability);

    public static ServiceHarness Create(
        DesktopCapability? allowed = null,
        DesktopContextTrust shellCallerTrust = DesktopContextTrust.Untrusted,
        bool hasThreadAccess = false,
        Action<ManualUiDispatcher>? configureDispatcher = null,
        TimeProvider? clock = null)
    {
        var dispatcher = new ManualUiDispatcher { HasThreadAccess = hasThreadAccess };
        configureDispatcher?.Invoke(dispatcher);

        var registry = new DesktopTargetRegistry();
        registry.RegisterContext("ctx-agent", DesktopContextTrust.AgentAuthorized);
        registry.RegisterPage(AgentPage, DesktopPageVersion.Require(1), DesktopPageReadiness.Interactive);
        registry.RegisterContext("ctx-web", DesktopContextTrust.Untrusted);
        registry.RegisterPage(WebPage, DesktopPageVersion.Require(5), DesktopPageReadiness.Complete);
        registry.RegisterContext("ctx-work", DesktopContextTrust.Workbench);
        registry.RegisterPage(WorkbenchPage, DesktopPageVersion.Require(2), DesktopPageReadiness.Complete);

        var options = new DesktopServiceOptions
        {
            AllowedCapabilities = allowed
                ?? (DesktopCapability.WebViewNavigate
                    | DesktopCapability.WebViewExecuteJavascript
                    | DesktopCapability.WebViewPageState
                    | DesktopCapability.ShellNotification),
            ShellCallerTrust = shellCallerTrust,
        };

        var testClock = clock as TestTimeProvider ?? new TestTimeProvider();
        var surface = new RecordingUiSurface();
        var service = new DesktopService(dispatcher, surface, registry, options, testClock);
        return new ServiceHarness(dispatcher, surface, testClock, service);
    }

    public DesktopCallContext Context(string operationId = "op-1", TimeSpan? deadline = null) =>
        new(
            new DesktopInstanceId("desk-1"),
            new OperationId(operationId),
            Clock.GetUtcNow() + (deadline ?? TimeSpan.FromSeconds(30)));

    public Task<DesktopCapabilityResponse> ExecuteAsync(
        DesktopCapability capability,
        DesktopCapabilityRequest request,
        DesktopCallContext? context = null,
        CancellationToken cancellationToken = default) =>
        Service.ExecuteAsync(Descriptor(capability), request, context ?? Context(), cancellationToken);

    public DesktopCapabilityRequest NavigateRequest(DesktopPageTarget target, DesktopPageVersion expected = default, string url = "https://example.com/a") =>
        DesktopCapabilityRequest.ForNavigate(new NavigateRequest(target, new Uri(url), expected));

    public DesktopCapabilityRequest JavascriptRequest(DesktopPageTarget target, DesktopPageVersion expected = default) =>
        DesktopCapabilityRequest.ForJavascript(new JavascriptRequest(target, "return 1;", expected));

    public DesktopCapabilityRequest NotificationRequest() =>
        DesktopCapabilityRequest.ForNotification(new DesktopNotificationRequest("标题", "内容"));
}

/// <summary>测试工程的仓库根定位。</summary>
internal static class RepoLayout
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PuddingAgentNetwork.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Repository root (PuddingAgentNetwork.slnx) not found above {AppContext.BaseDirectory}.");
    }
}

/// <summary>假监督器：生命周期与状态发布可脚本化，用来验证宿主的启停/单实例/超时语义。</summary>
internal sealed class FakeSupervisor : IDesktopConnectionSupervisor
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Disposed { get; private set; }

    public bool IgnoreCancellation { get; set; }

    public int Runs { get; private set; }

    public DesktopConnectionState State { get; private set; } = DesktopConnectionState.Idle;

    public ConnectionGeneration Generation { get; set; } = ConnectionGeneration.None;

    public int AttemptCount { get; set; }

    public DesktopCapabilityError? LastError { get; set; }

    public event Action<DesktopConnectionState>? StateChanged;

    public void Publish(DesktopConnectionState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public void Release() => _release.TrySetResult();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Runs++;

        if (IgnoreCancellation)
        {
            await _release.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>假双向流：测试扮演 Core（推送 CoreFrame、观察 DesktopFrame）。</summary>
internal sealed class FakeChannelStream : Pudding.DesktopConnection.DesktopChannelStream
{
    private readonly System.Threading.Channels.Channel<Proto.CoreFrame> _inbound =
        System.Threading.Channels.Channel.CreateUnbounded<Proto.CoreFrame>();
    private readonly List<Proto.DesktopFrame> _written = [];
    private readonly object _sync = new();

    public void Send(Proto.CoreFrame frame) => _inbound.Writer.TryWrite(frame);

    public IReadOnlyList<Proto.DesktopFrame> Written
    {
        get
        {
            lock (_sync)
            {
                return _written.ToArray();
            }
        }
    }

    public override async ValueTask<Proto.CoreFrame?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)
                ? await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (System.Threading.Channels.ChannelClosedException)
        {
            return null;
        }
    }

    public override ValueTask WriteAsync(Proto.DesktopFrame frame, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _written.Add(frame);
        }

        return ValueTask.CompletedTask;
    }

    public override ValueTask CompleteRequestStreamAsync() => ValueTask.CompletedTask;

    public override ValueTask DisposeAsync()
    {
        _inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeChannelStreamFactory : Pudding.DesktopConnection.IDesktopChannelStreamFactory
{
    private readonly object _sync = new();
    private FakeChannelStream? _stream;

    public FakeChannelStream? Stream
    {
        get
        {
            lock (_sync)
            {
                return _stream;
            }
        }
    }

    public ValueTask<Pudding.DesktopConnection.DesktopChannelStream> OpenAsync(CancellationToken cancellationToken)
    {
        var stream = new FakeChannelStream();
        lock (_sync)
        {
            _stream = stream;
        }

        return ValueTask.FromResult<Pudding.DesktopConnection.DesktopChannelStream>(stream);
    }
}

/// <summary>最小执行器：只为装配测试提供类型，不参与断言。</summary>
internal sealed class StubExecutor : Pudding.DesktopConnection.IDesktopCapabilityExecutor
{
    public Task<DesktopCapabilityResponse> ExecuteAsync(
        DesktopCapabilityDescriptor capability,
        DesktopCapabilityRequest request,
        DesktopCallContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(DesktopCapabilityResponse.Failure(
            DesktopCapabilityError.UnsupportedCapability(capability.Name)));
}

internal static class HostFrames
{
    public static Proto.CoreFrame HelloAck(ulong generation = 3) =>
        new()
        {
            HelloAck = new Proto.CoreHelloAck
            {
                ConnectionId = "host-connection",
                Generation = generation,
                NegotiatedVersion = new Proto.ProtocolRange { Minimum = 1, Maximum = 1 },
                Capabilities =
                {
                    new Proto.CapabilityDeclaration { Capability = "webview.navigate", Version = 1 },
                    new Proto.CapabilityDeclaration { Capability = "webview.execute_javascript", Version = 1 },
                    new Proto.CapabilityDeclaration { Capability = "shell.notification", Version = 1 },
                },
            },
        };
}

