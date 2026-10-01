using System.Collections.Concurrent;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

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
