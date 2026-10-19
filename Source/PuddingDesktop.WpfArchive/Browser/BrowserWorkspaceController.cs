using System.Collections.ObjectModel;
using Pudding.DesktopConnection;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;
using Pudding.DesktopService.Browser;
using PuddingBrowser.Abstractions;
using PuddingBrowser.Protocol;
using PuddingBrowser.WebView2;

namespace PuddingDesktop.Browser;

public interface IBrowserWorkspaceController : IAsyncDisposable
{
    ObservableCollection<BrowserTabViewModel> Tabs { get; }
    ObservableCollection<AgentBrowserActivityItem> Activities { get; }
    PageId? ActivePageId { get; }
    PageId? AgentTargetPageId { get; }
    AgentBrowserControlState ControlState { get; }
    BrowserBridgeConnectionState BridgeState { get; }
    BrowserContextId? ActiveContextId { get; }
    BrowserTabViewModel? ActiveTab { get; }
    bool CanGoBack { get; }
    bool CanGoForward { get; }
    bool IsLoading { get; }
    string AgentTargetSummary { get; }

    Task InitializeAsync(string dataRoot, CancellationToken ct);
    Task<BrowserContextId> CreateContextAsync(CancellationToken ct);
    Task CloseContextAsync(BrowserContextId contextId, CancellationToken ct);
    Task<PageId> CreatePageAsync(string? initialUrl = null, bool activate = true, CancellationToken ct = default);
    Task ActivateAsync(PageId pageId, CancellationToken ct);
    Task ClosePageAsync(PageId pageId, CancellationToken ct);
    Task NavigateAsync(PageId pageId, Uri uri, CancellationToken ct);
    Task GoBackAsync(PageId pageId, CancellationToken ct);
    Task GoForwardAsync(PageId pageId, CancellationToken ct);
    Task ReloadAsync(PageId pageId, CancellationToken ct);
    Task StopAsync(PageId pageId, CancellationToken ct);
    Task AssignAgentTargetAsync(PageId pageId, CancellationToken ct);
    Task SetUserTakeoverAsync(bool enabled, CancellationToken ct);
    Task SetPausedAsync(bool paused, CancellationToken ct);
    Task ApplyActivityAsync(AgentBrowserActivitySnapshot snapshot, CancellationToken ct);
    Task ApplyOperationStateAsync(AgentBrowserOperationStateSnapshot snapshot, CancellationToken ct);
    Task<BrowserActivityEvidenceDocument> CaptureActivityEvidenceAsync(DateTimeOffset capturedAt, CancellationToken ct);
    string CurrentAgentSummary { get; }
}

/// <summary>
/// Activity item for the Agent Activity Pane.
/// </summary>
public sealed class AgentBrowserActivityItem : INotifyPropertyChanged
{
    private DateTimeOffset? _completedAt;
    private string? _errorCode;
    private bool _isCompleted;
    private bool? _success;

    public required Guid OperationId { get; init; }
    public required string CommandName { get; init; }
    public required string Target { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt => _completedAt;
    public string? ErrorCode => _errorCode;
    public bool IsCompleted => _isCompleted;
    public bool? Success => _success;
    public string? AgentInstanceId { get; init; }
    public string? SessionId { get; init; }
    public string? RunId { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public long DurationMs => _completedAt is { } completed
        ? Math.Max(0, (long)(completed - StartedAt).TotalMilliseconds)
        : 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Apply(AgentBrowserActivitySnapshot snapshot)
    {
        _completedAt = snapshot.CompletedAt;
        _errorCode = snapshot.ErrorCode;
        _isCompleted = snapshot.IsCompleted;
        _success = snapshot.Success;
        OnPropertyChanged(nameof(CompletedAt));
        OnPropertyChanged(nameof(ErrorCode));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(Success));
        OnPropertyChanged(nameof(DurationMs));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Real browser workspace controller that owns a WebView2BrowserRuntime, Context, and Pages.
/// 
/// Phase 2A-1 final acceptance fixes:
/// - Constructor injection (no SetRuntime half-init)
/// - SurfaceHost.ActivateAsync called on tab switch (2.6)
/// - AgentTargetPageId assignable and stable across user tab switches (2.7)
/// - All ObservableCollection mutations via UI Dispatcher (2.8)
/// - Navigation state (Title/Url/Loading/CanGoBack/CanGoForward) synced after every op
/// - All Page operations through unified gate
/// - Activity tracking (max 100 items)
/// </summary>
public sealed class BrowserWorkspaceController :
    IBrowserWorkspaceController,
    IBrowserWorkspaceLifecycle,
    IBrowserPagePresenter,
    IBrowserCommandHandler,
    INotifyPropertyChanged
{
    private const int MaxActivityItems = 100;

    public event Action<BrowserContextId>? ContextCreated;
    public event Action<BrowserContextId>? ContextClosed;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<PageId, IBrowserPage> _pages = new();
    private readonly IBrowserRuntime _runtime;
    private readonly IBrowserSurfaceHost _surfaceHost;
    private readonly IWebView2UiDispatcher _uiDispatcher;
    private readonly BrowserAutomationGateSync? _automationGates;
    private readonly BrowserReadingBaselineStore _readings;
    private readonly Func<IDesktopCapabilityExecutor?>? _capabilityExecutor;
    /// <summary>
    /// Shell 侧「把承载区显示出来」的动作（展开工具工作区、选中该页标签、强制布局）。
    /// 省略时 <see cref="PresentPageAsync"/> 只能激活表面而无法展开折叠的工作区。
    /// </summary>
    private readonly Func<PageId, CancellationToken, Task>? _presentRequested;

    private IBrowserContext? _context;
    private PageId? _activePageId;
    private PageId? _agentTargetPageId;
    private BrowserSessionStatus? _sessionStatus;
    private AgentBrowserControlState _controlState = AgentBrowserControlState.Idle;
    private BrowserBridgeConnectionState _bridgeState = BrowserBridgeConnectionState.Disconnected;
    private BrowserContextId? _activeContextId;
    private string? _dataRoot;
    private bool _disposed;

    // State machine flags (doc 6.2): UserTakeover > Paused > AgentControlling > Idle
    private bool _userTakeover;
    private bool _paused;
    private AgentBrowserOperationStateSnapshot? _lastOperationSnapshot;
    private string? _currentAgentSummary;

    public ObservableCollection<BrowserTabViewModel> Tabs { get; } = new();
    public ObservableCollection<AgentBrowserActivityItem> Activities { get; } = new();

    public PageId? ActivePageId
    {
        get => _activePageId;
        private set { _activePageId = value; OnPropertyChanged(); OnPropertyChanged(nameof(ActiveTab)); }
    }

    public PageId? AgentTargetPageId
    {
        get => _agentTargetPageId;
        private set { _agentTargetPageId = value; OnPropertyChanged(); }
    }

    public AgentBrowserControlState ControlState
    {
        get => _controlState;
        private set { _controlState = value; OnPropertyChanged(); }
    }

    public BrowserBridgeConnectionState BridgeState
    {
        get => _bridgeState;
        set
        {
            _bridgeState = value;
            OnPropertyChanged();
            // Clear current agent summary when bridge disconnects (doc 6.2)
            if (value == BrowserBridgeConnectionState.Disconnected)
                CurrentAgentSummary = null;
        }
    }

    public BrowserContextId? ActiveContextId => _activeContextId;

    public BrowserTabViewModel? ActiveTab =>
        Tabs.FirstOrDefault(t => t.PageId == _activePageId);

    public bool CanGoBack => ActiveTab?.CanGoBack ?? false;
    public bool CanGoForward => ActiveTab?.CanGoForward ?? false;
    public bool IsLoading => ActiveTab?.IsLoading ?? false;
    public string AgentTargetSummary
    {
        get
        {
            if (_agentTargetPageId is not { } pageId)
            {
                return "No agent target";
            }

            var title = Tabs.FirstOrDefault(tab => tab.PageId == pageId)?.Title ?? pageId.Value;

            // ADR-097 D3：交页要由可信 Core 会话确认归属后才能显示“已交出”；
            // 之前只能显示“等待会话确认”，不能因为点了按钮就谎称 Agent 已取得控制。
            var state = _sessionStatus switch
            {
                { HasTarget: true } => "session confirmed",
                { HasPendingHandover: true } => "waiting for session confirmation",
                _ => "not yet confirmed by a core session",
            };

            return $"Agent target: {title} ({state})";
        }
    }

    /// <summary>会话控制面快照（来自 Bridge 派发器的同一权威；UI 只读）。</summary>
    public BrowserSessionStatus? ControlSession => _sessionStatus;

    /// <summary>应用会话控制面快照：目标摘要据此区分“待确认/已交出/已失效”。</summary>
    public async Task ApplySessionStatusAsync(BrowserSessionStatus status, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(status);

        await _uiDispatcher.InvokeAsync(() =>
        {
            _sessionStatus = status;
            OnPropertyChanged(nameof(ControlSession));
            OnPropertyChanged(nameof(AgentTargetSummary));
            return Task.CompletedTask;
        }, ct);
    }

    public string CurrentAgentSummary
    {
        get => _currentAgentSummary ?? "-";
        private set { _currentAgentSummary = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Constructor injection: all dependencies required at construction time.
    /// No half-initialized state possible.
    /// </summary>
    public BrowserWorkspaceController(
        IBrowserRuntime runtime,
        IBrowserSurfaceHost surfaceHost,
        IWebView2UiDispatcher uiDispatcher,
        BrowserAutomationGateSync? automationGates = null,
        BrowserReadingBaselineStore? readings = null,
        Func<IDesktopCapabilityExecutor?>? capabilityExecutor = null,
        Func<PageId, CancellationToken, Task>? presentRequested = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _surfaceHost = surfaceHost ?? throw new ArgumentNullException(nameof(surfaceHost));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
        _automationGates = automationGates;
        // 生产侧传入生命周期端口持有的那一份（页面关闭时会清）；省略则自建（测试/独立使用）。
        _readings = readings ?? new BrowserReadingBaselineStore();
        // 桥侧截图复用能力通道的**同一个执行器**（准入/闸门/预算一致）；省略 ⇒ 该能力在 Bridge 路径上明确不支持。
        _capabilityExecutor = capabilityExecutor;
        _presentRequested = presentRequested;
    }

    /// <summary>
    /// 注册一个**执行闸门**并立即与当前控制状态对齐。
    ///
    /// 为什么注册点在这里：`SetUserTakeoverAsync`/`SetPausedAsync` 是本组件唯一的控制权写入点，
    /// 把所有闸门挂在它下面，任何调用方（UI 开关、能力通道启停、未来的接口）都不可能只写一半
    /// （审计 2026-10-08 结论 #1：Shell 只写了 UI 状态机，两条执行闸门读不到它）。
    /// </summary>
    public void RegisterAutomationGate(IBrowserAutomationGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);

        if (_automationGates is null)
        {
            return;
        }

        _automationGates.Register(gate);
        _automationGates.ApplyTo(gate, _userTakeover, _paused);
    }

    /// <summary>撤销执行闸门（能力通道停止时调用）。</summary>
    public bool UnregisterAutomationGate(IBrowserAutomationGate gate) =>
        _automationGates?.Unregister(gate) ?? false;

    /// <summary>
    /// 当前页面的**真实版本**（供目标注册表喂版本）。未知/尚未创建时返回 <c>false</c>——
    /// 不返回伪造的 0，否则准入会把"未知"当成一个可比较的版本。
    /// </summary>
    internal bool TryGetPageVersion(PageId pageId, out long version)
    {
        if (_pages.TryGetValue(pageId, out var page) && page.PageVersion > 0)
        {
            version = page.PageVersion;
            return true;
        }

        version = 0;
        return false;
    }

    /// <summary>
    /// Initializes the persistent Agent Browser context.
    /// Uses DataRoot/browser/agent-browser/user-data as UDF (distinct from Workbench).
    /// </summary>
    public async Task InitializeAsync(string dataRoot, CancellationToken ct)
    {
        _dataRoot = dataRoot;

        await _gate.WaitAsync(ct);
        try
        {
            if (_context is not null) return; // already initialized

            var agentUdf = Path.GetFullPath(Path.Combine(dataRoot, "browser", "agent-browser", "user-data"));
            Directory.CreateDirectory(agentUdf);

            _context = await _runtime.CreateContextAsync(new BrowserContextOptions
            {
                Persistent = true,
                UserDataDirectory = agentUdf,
                // 驱动策略注入（§7/§8）：驱动源码只依赖 Abstractions，这里把 Automation 的纯策略接上。
                DriverPolicy = AutomationDriverPolicy.Instance
            }, ct);

            _activeContextId = _context.Id;
            OnPropertyChanged(nameof(ActiveContextId));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BrowserContextId> CreateContextAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_activeContextId.HasValue)
                return _activeContextId.Value;

            if (_dataRoot is null)
                throw new InvalidOperationException("Not initialized");

            var agentUdf = Path.GetFullPath(Path.Combine(_dataRoot, "browser", "agent-browser", "user-data"));
            Directory.CreateDirectory(agentUdf);

            _context = await _runtime.CreateContextAsync(new BrowserContextOptions
            {
                Persistent = true,
                UserDataDirectory = agentUdf,
                // 驱动策略注入（§7/§8）：驱动源码只依赖 Abstractions，这里把 Automation 的纯策略接上。
                DriverPolicy = AutomationDriverPolicy.Instance
            }, ct);

            _activeContextId = _context.Id;
            OnPropertyChanged(nameof(ActiveContextId));
            return _activeContextId.Value;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseContextAsync(BrowserContextId contextId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (await _runtime.GetContextAsync(contextId, ct) is not null)
            {
                foreach (var pageId in _pages.Values.Where(p => p.ContextId == contextId).Select(p => p.Id).ToList())
                    await ClosePageInternalAsync(pageId, ct);

                await _runtime.CloseContextAsync(contextId, ct);
                ContextClosed?.Invoke(contextId);
                if (_context?.Id == contextId)
                {
                    _context = null;
                    _activeContextId = null;
                    OnPropertyChanged(nameof(ActiveContextId));
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PageId> CreatePageAsync(string? initialUrl = null, bool activate = true, CancellationToken ct = default)
        => (await CreatePageAsync(_activeContextId ?? throw new InvalidOperationException("No browser context"),
            new PageCreateOptions { InitialUrl = initialUrl is null ? null : new Uri(initialUrl), Activate = activate }, ct)).Id;

    public async Task<IBrowserContext> CreateContextAsync(BrowserContextOptions options, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var created = await _runtime.CreateContextAsync(options with { DriverPolicy = AutomationDriverPolicy.Instance }, ct);
            try { ContextCreated?.Invoke(created.Id); }
            catch
            {
                await _runtime.CloseContextAsync(created.Id, CancellationToken.None);
                throw;
            }
            if (_context is null)
            {
                _context = created;
                _activeContextId = created.Id;
                OnPropertyChanged(nameof(ActiveContextId));
            }
            return created;
        }
        finally { _gate.Release(); }
    }

    public async Task<IBrowserPage> CreatePageAsync(BrowserContextId contextId, PageCreateOptions options, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var context = await _runtime.GetContextAsync(contextId, ct)
                ?? throw new InvalidOperationException("No browser context");
            var page = await context.NewPageAsync(options, ct);

            _pages[page.Id] = page;
            try
            {
                var tab = new BrowserTabViewModel
                {
                    PageId = page.Id,
                    ContextId = page.ContextId,
                    Title = string.IsNullOrEmpty(page.Info.Title) ? "New Tab" : page.Info.Title,
                    Url = page.Info.Url,
                    IsLoading = page.IsLoading
                };

                // ObservableCollection mutation and registration complete before success.
                await _uiDispatcher.InvokeAsync(() => { Tabs.Add(tab); return Task.CompletedTask; }, ct);

                if (options.Activate || Tabs.Count == 1)
                    await ActivateInternalAsync(page.Id, ct);

                OnPropertyChanged(nameof(Tabs));
                return page;
            }
            catch
            {
                // Never return a live page whose UI/lifecycle projection failed.
                await ClosePageInternalAsync(page.Id, CancellationToken.None);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ActivatePageAsync(BrowserContextId contextId, PageId pageId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { RequireOwnedPage(contextId, pageId); await ActivateInternalAsync(pageId, ct); }
        finally { _gate.Release(); }
    }

    public async Task ClosePageAsync(BrowserContextId contextId, PageId pageId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { RequireOwnedPage(contextId, pageId); await ClosePageInternalAsync(pageId, ct); }
        finally { _gate.Release(); }
    }

    private void RequireOwnedPage(BrowserContextId contextId, PageId pageId)
    {
        if (!_pages.TryGetValue(pageId, out var page) || page.ContextId != contextId)
            throw new InvalidOperationException("Page is not owned by the requested workspace context");
    }

    public async Task ActivateAsync(PageId pageId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await ActivateInternalAsync(pageId, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 让这一页**真正可见**（截图与原生输入的前提）。
    ///
    /// 顺序不能反：先请 Shell 展开承载区，再激活表面。承载区折叠时
    /// <c>Control.IsVisible</c> 为 false，任何截图都拿不到合成结果；
    /// 反过来先激活再展开，会在「面板仍折叠」的瞬间激活，表面依旧不可见。
    /// 用户手动折叠只应抑制「后台活动自动弹面板」，不能让需要渲染的调用必然失败。
    /// </summary>
    public async Task PresentPageAsync(BrowserContextId contextId, PageId pageId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            RequireOwnedPage(contextId, pageId);
            if (_presentRequested is { } present)
            {
                await present(pageId, ct);
            }
            await ActivateInternalAsync(pageId, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Internal activate: calls SurfaceHost.ActivateAsync to show only the target Surface.
    /// Does NOT change AgentTargetPageId (2.7 fix).
    /// </summary>
    private async Task ActivateInternalAsync(PageId pageId, CancellationToken ct)
    {
        if (!_pages.TryGetValue(pageId, out var page)) return;

        _context = await _runtime.GetContextAsync(page.ContextId, ct)
            ?? throw new InvalidOperationException("Page context is no longer available");
        _activeContextId = page.ContextId;
        OnPropertyChanged(nameof(ActiveContextId));

        // Actually activate the Surface (shows it, hides others)
        await _surfaceHost.ActivateAsync(pageId, ct);
        await page.BringToFrontAsync(ct);

        await _uiDispatcher.InvokeAsync(() =>
        {
            foreach (var tab in Tabs)
                tab.IsActive = tab.PageId == pageId;
            return Task.CompletedTask;
        }, ct);

        ActivePageId = pageId;
        await SyncNavigationStateAsync(pageId, ct);
    }

    public async Task ClosePageAsync(PageId pageId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await ClosePageInternalAsync(pageId, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task NavigateAsync(PageId pageId, Uri uri, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_pages.TryGetValue(pageId, out var page)) return;

            await UpdateTabLoadingAsync(pageId, true, ct);
            await page.GotoAsync(uri, new NavigationOptions(), ct);
            await UpdateTabLoadingAsync(pageId, false, ct);
            await SyncNavigationStateAsync(pageId, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task GoBackAsync(PageId pageId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_pages.TryGetValue(pageId, out var page))
            {
                await page.GoBackAsync(ct);
                await SyncNavigationStateAsync(pageId, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task GoForwardAsync(PageId pageId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_pages.TryGetValue(pageId, out var page))
            {
                await page.GoForwardAsync(ct);
                await SyncNavigationStateAsync(pageId, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReloadAsync(PageId pageId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_pages.TryGetValue(pageId, out var page))
            {
                await UpdateTabLoadingAsync(pageId, true, ct);
                await page.ReloadAsync(ct);
                await UpdateTabLoadingAsync(pageId, false, ct);
                await SyncNavigationStateAsync(pageId, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(PageId pageId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_pages.TryGetValue(pageId, out var page))
            {
                await page.StopAsync(ct);
                await UpdateTabLoadingAsync(pageId, false, ct);
                await SyncNavigationStateAsync(pageId, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Assigns the Agent target page. This page is used for Bridge commands without explicit PageId.
    /// Does NOT change when user switches visible tab (2.7 fix).
    /// </summary>
    public async Task AssignAgentTargetAsync(PageId pageId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_pages.ContainsKey(pageId))
                return;

            AgentTargetPageId = pageId;
            // 唯一授权动作必须下发到准入侧：否则闸门读不到「用户已选页」，按钮就只是 UI 装饰。
            _automationGates?.ApplyAgentTarget(_pages[pageId].ContextId.Value, pageId.Value);
            await _uiDispatcher.InvokeAsync(() =>
            {
                foreach (var tab in Tabs)
                    tab.IsAgentTarget = tab.PageId == pageId;
                return Task.CompletedTask;
            }, ct);
            OnPropertyChanged(nameof(AgentTargetSummary));
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SetUserTakeoverAsync(bool enabled, CancellationToken ct)
    {
        _userTakeover = enabled;
        RecalculateControlState();
        // 唯一写入点：把接管推给所有执行闸门。否则按钮只改 UI 状态机（审计结论 #1）。
        _automationGates?.ApplyUserTakeover(enabled);
        return Task.CompletedTask;
    }

    public Task SetPausedAsync(bool paused, CancellationToken ct)
    {
        _paused = paused;
        RecalculateControlState();
        _automationGates?.ApplyPaused(paused);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Applies operation state snapshot from Dispatcher and recalculates control state.
    /// Priority: UserTakeover > Paused > AgentControlling > Idle (doc 6.2).
    /// </summary>
    public async Task ApplyOperationStateAsync(AgentBrowserOperationStateSnapshot snapshot, CancellationToken ct)
    {
        await _uiDispatcher.InvokeAsync(() =>
        {
            _lastOperationSnapshot = snapshot;

            // Update current agent summary from most recent origin
            if (snapshot.MostRecentOrigin is { } origin)
            {
                CurrentAgentSummary = $"{origin.AgentInstanceId} · {origin.ToolName}";
            }

            RecalculateControlState();
            return Task.CompletedTask;
        }, ct);
    }

    /// <summary>
    /// Recalculates ControlState per fixed priority: UserTakeover > Paused > AgentControlling > Idle.
    /// </summary>
    private void RecalculateControlState()
    {
        if (_userTakeover)
            ControlState = AgentBrowserControlState.UserTakeover;
        else if (_paused)
            ControlState = AgentBrowserControlState.Paused;
        else if (_lastOperationSnapshot?.ActiveOperationCount > 0)
            ControlState = AgentBrowserControlState.AgentControlling;
        else
            ControlState = AgentBrowserControlState.Idle;
    }

    /// <summary>
    /// Captures a sanitized activity evidence snapshot on the UI dispatcher.
    /// Only copies safe fields: command name, page identity, result, and error codes.
    /// Never includes tool parameters, fill/text values, DOM content, URLs, cookies, or tokens.
    /// </summary>
    public async Task<BrowserActivityEvidenceDocument> CaptureActivityEvidenceAsync(
        DateTimeOffset capturedAt, CancellationToken ct)
    {
        BrowserActivityEvidenceDocument? result = null;
        await _uiDispatcher.InvokeAsync(() =>
        {
            var activities = new List<BrowserActivityEvidenceItem>(Math.Min(Activities.Count, MaxActivityItems));
            foreach (var item in Activities.Take(MaxActivityItems))
            {
                // Sanitize Target: only keep stable context/page IDs.
                // If Target is not a recognized context/page ID pattern, use "-".
                var target = SanitizeEvidenceTarget(item.Target);

                activities.Add(new BrowserActivityEvidenceItem
                {
                    OperationId = item.OperationId,
                    CommandName = item.CommandName,
                    Target = target,
                    StartedAt = item.StartedAt,
                    CompletedAt = item.CompletedAt,
                    Success = item.Success,
                    ErrorCode = item.ErrorCode
                });
            }

            // Sort by StartedAt ascending for faithful call order
            activities.Sort((a, b) => a.StartedAt.CompareTo(b.StartedAt));

            result = new BrowserActivityEvidenceDocument
            {
                SchemaVersion = 1,
                CapturedAt = capturedAt,
                BridgeState = _bridgeState.ToString(),
                ControlState = _controlState.ToString(),
                ActiveContextId = _activeContextId?.Value,
                ActivePageId = _activePageId?.Value,
                AgentTargetPageId = _agentTargetPageId?.Value,
                Activities = activities
            };
            return Task.CompletedTask;
        }, ct);
        return result!;
    }

    /// <summary>
    /// Allowlist validation for evidence Target values. Real page/context IDs
    /// contain only letters, digits, dots and hyphens (GUID-like tokens), max
    /// 128 chars. Anything else (URLs, selectors, free text, or values with
    /// underscores/colons/slashes) is replaced with "-" so the sanitized
    /// export can never leak user data smuggled through Target (doc 79 7.3).
    /// </summary>
    private static string SanitizeEvidenceTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return "-";
        if (target.Length > 128)
            return "-";
        foreach (var c in target)
        {
            var allowed = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-';
            if (!allowed)
                return "-";
        }
        return target;
    }

    // ─── IBrowserCommandHandler ──────────────────────────────────────────────

    public async Task<BrowserBridgeCommandResult> ExecuteAsync(
        BrowserBridgeCommand command, CancellationToken ct)
    {
        try
        {
            return command.Name switch
            {
                BrowserBridgeCommandNames.ContextCreate => await HandleContextCreateAsync(command, ct),
                BrowserBridgeCommandNames.ContextList => await HandleContextListAsync(command, ct),
                BrowserBridgeCommandNames.ContextGetInfo => await HandleContextGetInfoAsync(command, ct),
                BrowserBridgeCommandNames.ContextClose => await HandleContextCloseAsync(command, ct),
                BrowserBridgeCommandNames.PageCreate => await HandlePageCreateAsync(command, ct),
                BrowserBridgeCommandNames.PageList => await HandlePageListAsync(command, ct),
                BrowserBridgeCommandNames.PageGetInfo => await HandlePageGetInfoAsync(command, ct),
                BrowserBridgeCommandNames.PageActivate => await HandlePageActivateAsync(command, ct),
                BrowserBridgeCommandNames.PageClose => await HandlePageCloseAsync(command, ct),
                BrowserBridgeCommandNames.PageGoto => await HandlePageGotoAsync(command, ct),
                BrowserBridgeCommandNames.PageGoBack => await HandlePageNavAsync(command, "back", ct),
                BrowserBridgeCommandNames.PageGoForward => await HandlePageNavAsync(command, "forward", ct),
                BrowserBridgeCommandNames.PageReload => await HandlePageNavAsync(command, "reload", ct),
                BrowserBridgeCommandNames.PageStop => await HandlePageNavAsync(command, "stop", ct),
                BrowserBridgeCommandNames.PageSnapshot => await HandlePageSnapshotAsync(command, ct),
                BrowserBridgeCommandNames.PageLocate => await HandlePageLocateAsync(command, ct),
                BrowserBridgeCommandNames.PageInteract => await HandlePageInteractAsync(command, ct),
                BrowserBridgeCommandNames.PageWaitFor => await HandlePageWaitForAsync(command, ct),
                BrowserBridgeCommandNames.PageScreenshot => await HandlePageScreenshotAsync(command, ct),
                _ => Error(command.OperationId, BrowserBridgeErrorCodes.BrowserOperationNotSupported,
                    $"Unknown command: {command.Name}")
            };

        }
        catch (OperationCanceledException)
        {
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserCancelled, "Cancelled");
        }
        catch (BrowserOperationException ex)
        {
            return Error(command.OperationId, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserOperationFailed, ex.Message);
        }
    }

    // ─── Command Handlers ────────────────────────────────────────────────────

    private async Task<BrowserBridgeCommandResult> HandleContextCreateAsync(
        BrowserBridgeCommand command, CancellationToken ct)
    {
        var contextId = await CreateContextAsync(ct);
        var descriptor = new BrowserContextDescriptor
        {
            ContextId = contextId.Value,
            UserDataDirectory = _context?.Info.UserDataDirectory ?? "",
            PageCount = _pages.Count
        };
        return Success(command.OperationId, descriptor);
    }

    private Task<BrowserBridgeCommandResult> HandleContextListAsync(
        BrowserBridgeCommand command, CancellationToken ct)
    {
        IReadOnlyList<BrowserContextDescriptor> contexts = _context is null
            ? []
            :
            [
                new BrowserContextDescriptor
                {
                    ContextId = _context.Id.Value,
                    UserDataDirectory = _context.Info.UserDataDirectory,
                    PageCount = _pages.Count
                }
            ];

        return Task.FromResult(Success(command.OperationId,
            new BrowserContextListDescriptor { Contexts = contexts }));
    }

    private Task<BrowserBridgeCommandResult> HandleContextGetInfoAsync(
        BrowserBridgeCommand command, CancellationToken ct)
    {
        var args = DeserializeArgs<ContextGetInfoArguments>(command);
        var requestedId = args?.ContextId ?? command.ContextId;
        if (_context is null
            || (!string.IsNullOrWhiteSpace(requestedId)
                && !string.Equals(requestedId, _context.Id.Value, StringComparison.Ordinal)))
        {
            return Task.FromResult(Error(command.OperationId,
                BrowserBridgeErrorCodes.BrowserContextNotFound, "Context not found"));
        }

        return Task.FromResult(Success(command.OperationId, new BrowserContextDescriptor
        {
            ContextId = _context.Id.Value,
            UserDataDirectory = _context.Info.UserDataDirectory,
            PageCount = _pages.Count
        }));
    }

    private async Task<BrowserBridgeCommandResult> HandleContextCloseAsync(
        BrowserBridgeCommand command, CancellationToken ct)
    {
        var args = DeserializeArgs<ContextCloseArguments>(command);
        var requestedId = args?.ContextId ?? command.ContextId;
        if (_activeContextId is null
            || (!string.IsNullOrWhiteSpace(requestedId)
                && !string.Equals(requestedId, _activeContextId.Value.Value, StringComparison.Ordinal)))
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserContextNotFound, "No context");
        await CloseContextAsync(_activeContextId.Value, ct);
        return SuccessEmpty(command.OperationId);
    }

    private async Task<BrowserBridgeCommandResult> HandlePageCreateAsync(
        BrowserBridgeCommand command, CancellationToken ct)
    {
        var args = DeserializeArgs<PageCreateArguments>(command);
        var pageId = await CreatePageAsync(args?.InitialUrl, args?.Activate ?? true, ct);
        if (!_pages.TryGetValue(pageId, out var page))
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserPageNotFound, "Page creation failed");
        if (args?.Activate ?? true)
            await AssignAgentTargetAsync(pageId, ct);
        return Success(command.OperationId, BuildPageDescriptor(page, pageId));
    }

    private Task<BrowserBridgeCommandResult> HandlePageListAsync(
        BrowserBridgeCommand command, CancellationToken ct)
    {
        var descriptors = _pages.Values.Select(p => BuildPageDescriptor(p, p.Id)).ToList();
        return Task.FromResult(Success(command.OperationId, new BrowserPageListDescriptor { Pages = descriptors }));
    }

    private Task<BrowserBridgeCommandResult> HandlePageGetInfoAsync(
        BrowserBridgeCommand command, CancellationToken ct)
    {
        var pageId = ResolveTargetPageId(command);
        if (pageId is null || !_pages.TryGetValue(pageId.Value, out var page))
            return Task.FromResult(Error(command.OperationId, BrowserBridgeErrorCodes.BrowserPageNotFound, "Page not found"));
        return Task.FromResult(Success(command.OperationId, BuildPageDescriptor(page, pageId.Value)));
    }

    private async Task<BrowserBridgeCommandResult> HandlePageActivateAsync(
        BrowserBridgeCommand command, CancellationToken ct)
    {
        var args = DeserializeArgs<PageActivateArguments>(command);
        var pageIdStr = args?.PageId ?? command.PageId;
        if (string.IsNullOrWhiteSpace(pageIdStr))
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserInvalidCommand, "Missing pageId");
        var pageId = new PageId(pageIdStr);
        if (!_pages.ContainsKey(pageId))
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserPageNotFound, "Page not found");
        await ActivateAsync(pageId, ct);
        await AssignAgentTargetAsync(pageId, ct);
        return Success(command.OperationId, BuildPageDescriptor(_pages[pageId], pageId));
    }

    private async Task<BrowserBridgeCommandResult> HandlePageCloseAsync(
        BrowserBridgeCommand command, CancellationToken ct)
    {
        var pageId = ResolveTargetPageId(command);
        if (pageId is null || !_pages.ContainsKey(pageId.Value))
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserPageNotFound, "Page not found");
        await ClosePageAsync(pageId.Value, ct);
        return SuccessEmpty(command.OperationId);
    }

    private async Task<BrowserBridgeCommandResult> HandlePageGotoAsync(
        BrowserBridgeCommand command, CancellationToken ct)
    {
        var args = DeserializeArgs<PageGotoArguments>(command);
        if (args is null || string.IsNullOrWhiteSpace(args.Url))
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserInvalidCommand, "Missing url");
        if (!Uri.TryCreate(args.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserInvalidCommand, "URL must be absolute http/https");

        var pageId = ResolveTargetPageId(command);
        if (pageId is null || !_pages.TryGetValue(pageId.Value, out var page))
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserPageNotFound, "Page not found");

        await _gate.WaitAsync(ct);
        NavigationResult navResult;
        try
        {
            await UpdateTabLoadingAsync(pageId.Value, true, ct);
            navResult = await page.GotoAsync(uri, new NavigationOptions { TimeoutMs = args.TimeoutMs }, ct);
            await UpdateTabLoadingAsync(pageId.Value, false, ct);
            await SyncNavigationStateAsync(pageId.Value, ct);
        }
        finally
        {
            _gate.Release();
        }

        var descriptor = new BrowserNavigationResultDescriptor
        {
            Url = navResult.Url.ToString(),
            Ok = navResult.Ok,
            StatusCode = navResult.StatusCode,
            ErrorText = navResult.ErrorText,
            Page = BuildPageDescriptor(page, pageId.Value)
        };
        return Success(command.OperationId, descriptor);
    }

    private async Task<BrowserBridgeCommandResult> HandlePageNavAsync(
        BrowserBridgeCommand command, string action, CancellationToken ct)
    {
        var pageId = ResolveTargetPageId(command);
        if (pageId is null || !_pages.TryGetValue(pageId.Value, out var page))
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserPageNotFound, "Page not found");

        switch (action)
        {
            case "back": await GoBackAsync(pageId.Value, ct); break;
            case "forward": await GoForwardAsync(pageId.Value, ct); break;
            case "reload": await ReloadAsync(pageId.Value, ct); break;
            case "stop": await StopAsync(pageId.Value, ct); break;
        }
        return Success(command.OperationId, BuildPageDescriptor(page, pageId.Value));
    }

    private async Task<BrowserBridgeCommandResult> HandlePageScreenshotAsync(
        BrowserBridgeCommand command, CancellationToken ct)
    {
        if (_capabilityExecutor?.Invoke() is not { } executor)
        {
            return Error(
                command.OperationId,
                BrowserBridgeErrorCodes.BrowserOperationNotSupported,
                "screenshot is not available over the bridge transport in this host configuration");
        }

        var page = ResolvePage(command, out var pageId);
        if (page is null)
        {
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserPageNotFound, "Page not found");
        }

        var args = DeserializeArgs<PageScreenshotArguments>(command);
        if (args is null || args.ExpectedPageVersion <= 0)
        {
            return Error(
                command.OperationId,
                BrowserBridgeErrorCodes.BrowserInvalidCommand,
                "screenshot must pin expected_page_version (> 0): an observation is only valid within the version it captured");
        }

        var scope = DesktopScreenshotScope.Viewport;
        if (!string.IsNullOrWhiteSpace(args.Scope) && !DesktopScreenshotScopeWire.TryParse(args.Scope, out scope))
        {
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserInvalidCommand,
                "screenshot scope is not registered (viewport, region, full_page)");
        }

        var detail = DesktopScreenshotDetail.Low;
        if (!string.IsNullOrWhiteSpace(args.Detail) && !DesktopScreenshotDetailWire.TryParse(args.Detail, out detail))
        {
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserInvalidCommand,
                "screenshot detail is not registered (low, high, original)");
        }

        var identity = BridgeCallerContext.Create(
            new DesktopInstanceId("default"),
            new OperationId(command.OperationId.ToString("N")),
            command.DeadlineUtc,
            correlationId: null,
            command.Caller?.Kind,
            command.Caller?.CallerId,
            command.Caller?.TaskId,
            command.Caller?.ParentTaskId);
        if (identity.IsFailure)
        {
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserUnauthorized, identity.Error!.Message);
        }

        if (!DesktopCapabilities.TryGetByName("browser.screenshot", out var descriptor))
        {
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserOperationNotSupported,
                "the screenshot capability is not registered in this build");
        }

        var target = new DesktopPageTarget(command.ContextId ?? string.Empty, pageId.Value);
        var request = DesktopCapabilityRequest.ForScreenshot(new BrowserScreenshotRequest(
            target,
            DesktopPageVersion.Require(args.ExpectedPageVersion),
            scope,
            detail,
            locator: null,
            regionX: args.RegionX,
            regionY: args.RegionY,
            regionWidth: args.RegionWidth,
            regionHeight: args.RegionHeight,
            format: string.IsNullOrWhiteSpace(args.Format) ? "png" : args.Format!.Trim()));

        var response = await executor.ExecuteAsync(descriptor, request, identity.Value, ct);
        if (response.IsFailure)
        {
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserOperationFailed, response.Error!.Message);
        }

        if (response.Screenshot is not { } shot)
        {
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserInvalidCommand,
                "screenshot response payload is missing");
        }

        return Success(command.OperationId, new BrowserScreenshotDescriptor
        {
            ObservationId = shot.Metadata.ObservationId,
            MimeType = shot.Metadata.MimeType,
            ByteCount = shot.Metadata.ByteCount,
            SourceWidth = shot.Metadata.SourceWidth,
            SourceHeight = shot.Metadata.SourceHeight,
            OutputWidth = shot.Metadata.OutputWidth,
            OutputHeight = shot.Metadata.OutputHeight,
            PageVersion = shot.Metadata.PageVersion,
            DeviceScaleFactor = shot.Metadata.DeviceScaleFactor,
            ViewportWidth = shot.Metadata.ViewportWidth,
            ViewportHeight = shot.Metadata.ViewportHeight,
            ScrollX = shot.Metadata.ScrollX,
            ScrollY = shot.Metadata.ScrollY,
            CropX = shot.Metadata.CropX,
            CropY = shot.Metadata.CropY,
            CropWidth = shot.Metadata.CropWidth,
            CropHeight = shot.Metadata.CropHeight,
            DomRevision = shot.Metadata.DomRevision,
            ViewportRevision = shot.Metadata.ViewportRevision,
            Detail = DesktopScreenshotDetailWire.NameOf(shot.Metadata.Detail),
            Chunks = shot.Chunks.Select(chunk => new BrowserScreenshotChunkDescriptor
            {
                Index = chunk.Index,
                IsLast = chunk.IsLast,
                Base64 = Convert.ToBase64String(chunk.Data),
            }).ToArray(),
        });
    }

    private async Task<BrowserBridgeCommandResult> HandlePageSnapshotAsync(
        BrowserBridgeCommand command, CancellationToken ct)
    {
        var page = ResolvePage(command, out var pageId);
        if (page is null)
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserPageNotFound, "Page not found");
        var args = DeserializeArgs<PageSnapshotArguments>(command) ?? new PageSnapshotArguments();
        var snapshot = await page.SnapshotAsync(new SnapshotOptions
        {
            IncludeDom = args.IncludeDom,
            IncludeAccessibilityTree = args.IncludeAccessibilityTree,
            IncludeHidden = args.IncludeHidden,
            IncludeIframes = args.IncludeIframes,
            IncludeShadowDom = args.IncludeShadowDom,
            IncludeHtml = args.IncludeHtml,
            MaxNodes = args.MaxNodes,
            MaxTextLength = args.MaxTextLength,
            MaxDepth = args.MaxDepth,
            // 作用域（§151）：过线的 ref 交给驱动决定从哪个节点开始遍历；
            // 过期引用由驱动明确拒绝（stale_element_reference），不退化成正页快照。
            ScopeRef = args.ScopeRef,
            IncludeContentBlocks = args.IncludeContentBlocks
        }, ct);

        // 修订与行级差异（§145）：判定只有一份（DesktopService 的解析器），基线在本进程内，
        // 用的内容与要回给调用方的一致（否则 diff 会描述一份调用方看不到的文本）。
        var options = new DesktopSnapshotOptions(
            includeDom: args.IncludeDom,
            includeAccessibilityTree: args.IncludeAccessibilityTree,
            includeHtml: args.IncludeHtml,
            maxNodes: args.MaxNodes,
            maxTextLength: args.MaxTextLength,
            includeHidden: args.IncludeHidden,
            includeIframes: args.IncludeIframes,
            includeShadowDom: args.IncludeShadowDom,
            maxDepth: args.MaxDepth,
            scopeRef: args.ScopeRef,
            sinceRevision: args.SinceRevision);

        var reading = BrowserReadingDiffResolver.Resolve(
            _readings,
            new DesktopPageTarget(page.ContextId.Value, pageId.Value),
            options,
            // 没有活版本时传 Unknown（解析器会如实处理：不比较就不给修订，要比较就 refresh_required），
            // 绝不用 Require(0) 抛异常——那会把"页面还没版本"变成一次命令失败。
            page.PageVersion > 0 ? DesktopPageVersion.Require(page.PageVersion) : DesktopPageVersion.Unknown,
            snapshot.AccessibilityTree ?? snapshot.DomText ?? snapshot.Html);

        if (reading.IsFailure)
        {
            return Error(
                command.OperationId,
                BrowserBridgeErrorCodes.BrowserInvalidCommand,
                reading.Failure!.Message);
        }

        return Success(command.OperationId, new BrowserSnapshotDescriptor
        {
            DomText = snapshot.DomText,
            AccessibilityTree = snapshot.AccessibilityTree,
            Html = snapshot.Html,
            Truncated = snapshot.Truncated,
            NodeCount = snapshot.NodeCount,
            PageVersion = page.PageVersion,
            Revision = reading.Revision,
            Changed = reading.Changed,
            ContentBlocks = snapshot.ContentBlocks,
            PageEvents = snapshot.PageEvents,
            Diff = reading.Diff is { } diff
                ? new BrowserTextDiffDescriptor
                {
                    Added = diff.Added,
                    Removed = diff.Removed,
                    AddedCount = diff.AddedCount,
                    RemovedCount = diff.RemovedCount,
                    Truncated = diff.Truncated,
                }
                : null
        });
    }

    private async Task<BrowserBridgeCommandResult> HandlePageLocateAsync(
        BrowserBridgeCommand command, CancellationToken ct)
    {
        var page = ResolvePage(command, out _);
        if (page is null)
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserPageNotFound, "Page not found");
        var args = DeserializeArgs<PageLocateArguments>(command);
        if (args?.Locator is null)
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserInvalidCommand, "Missing locator");
        var locator = ToLocator(args.Locator);
        var handles = await page.QueryAllAsync(locator, ct);
        var descriptors = handles.Take(100).Select(handle => ToElementDescriptor(handle.Info, handle.PageVersion)).ToArray();
        foreach (var handle in handles)
            await handle.DisposeAsync();
        return Success(command.OperationId, new BrowserLocateResultDescriptor
        {
            Elements = descriptors,
            Truncated = handles.Count > descriptors.Length
        });
    }

    private async Task<BrowserBridgeCommandResult> HandlePageInteractAsync(
        BrowserBridgeCommand command, CancellationToken ct)
    {
        var page = ResolvePage(command, out var pageId);
        if (page is null)
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserPageNotFound, "Page not found");
        var args = DeserializeArgs<PageInteractArguments>(command);
        var action = args?.Action?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(action))
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserInvalidCommand, "Missing action");
        var locator = args?.Locator is null ? null : ToLocator(args.Locator);
        ScrollOutcome? scroll = null;
        switch (action)
        {
            case "click" when locator is not null:
                await page.ClickAsync(locator, new ClickOptions(), ct);
                break;
            case "fill" when locator is not null:
                await page.FillAsync(locator, args?.Text ?? string.Empty, new FillOptions(), ct);
                break;
            case "type" when locator is not null:
                await page.TypeAsync(locator, args?.Text ?? string.Empty, new TypeOptions(), ct);
                break;
            case "press" when locator is not null && !string.IsNullOrWhiteSpace(args?.Text):
                await page.PressAsync(locator, args.Text, new KeyOptions(), ct);
                break;
            case "hover" when locator is not null:
                await page.HoverAsync(locator, new PointerOptions(), ct);
                break;
            case "pointer_drag" when args?.X is { } dx && args.Y is { } dy
                && args.ToX is { } tx && args.ToY is { } ty:
            {
                if (page is not PuddingBrowser.Abstractions.IPointerInputPage dragPointer)
                {
                    return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserInvalidCommand,
                        "coordinate input is not available on this browser surface");
                }

                await dragPointer.PointerAsync(
                    new PuddingBrowser.Abstractions.PointerInput(
                        PuddingBrowser.Abstractions.PointerInputKind.Drag, dx, dy, tx, ty),
                    ct).ConfigureAwait(false);
                break;
            }

            case "pointer_click" or "pointer_move" when args?.X is { } px && args.Y is { } py:
            {
                // §8.2 坐标输入：页面必须以**可选能力**实现；否则**明确失败**（绝不静默忽略坐标）。
                if (page is not PuddingBrowser.Abstractions.IPointerInputPage pointer)
                {
                    return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserInvalidCommand,
                        "coordinate input is not available on this browser surface");
                }

                if (!string.IsNullOrEmpty(args.ObservationId))
                {
                    // 尚无观察登记处可校验 ⇒ 带 id 的请求一律拒绝（与能力通道侧同规则）。
                    return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserInvalidCommand,
                        "screenshot-derived coordinates require a live observation; none is registered yet");
                }

                await pointer.PointerAsync(
                    new PuddingBrowser.Abstractions.PointerInput(
                        action == "pointer_click"
                            ? PuddingBrowser.Abstractions.PointerInputKind.Click
                            : PuddingBrowser.Abstractions.PointerInputKind.Move,
                        px,
                        py),
                    ct).ConfigureAwait(false);
                break;
            }

            case "scroll":
                scroll = await page.ScrollAsync(
                    new ScrollOptions
                    {
                        DeltaX = args?.DeltaX,
                        DeltaY = args?.DeltaY,
                        // 目标模型来自调用方；未给时按"有无 locator/位移"推导（与能力通道同一套规则）。
                        Target = ResolveScrollTarget(args?.ScrollTarget, locator, args?.DeltaX, args?.DeltaY),
                        Locator = locator,
                    }, ct);
                break;
            case "select" when locator is not null && args?.Values is { Count: > 0 }:
                await page.SelectAsync(locator, args.Values, ct);
                break;
            case "check" when locator is not null && args?.Checked is not null:
                await page.CheckAsync(locator, args.Checked.Value, ct);
                break;
            case "drag" or "upload":
                throw new BrowserOperationException(
                    BrowserBridgeErrorCodes.BrowserOperationNotSupported,
                    $"Interaction '{action}' is not available in Phase 2A-3");
            default:
                return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserInvalidCommand,
                    "Invalid interaction arguments");
        }

        await SyncNavigationStateAsync(pageId, ct);
        return Success(command.OperationId, new BrowserInteractionResultDescriptor
        {
            // Do not query the locator again after a committed interaction. Click,
            // press and form actions may already have navigated or replaced the node;
            // a post-action lookup would misreport success as a stale-reference error.
            Element = null,
            Page = BuildPageDescriptor(page, pageId),

            // 滚动事实如实回传（§7）：没滚过就没有事实（null），而不是"滚了 0 像素"。
            Scroll = scroll is null
                ? null
                : new BrowserScrollReportDescriptor
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
        });
    }

    /// <summary>
    /// 解析滚动目标（设计方案 §7）：显式线名优先，否则按"有 locator 有位移 ⇒ element、
    /// 有 locator 无位移 ⇒ to_ref、无 locator ⇒ page"推导。未登记的线名**不猜测**，一律拒绝。
    /// </summary>
    private static ScrollTarget ResolveScrollTarget(
        string? wire, Locator? locator, double? deltaX, double? deltaY)
    {
        if (!string.IsNullOrWhiteSpace(wire))
        {
            // ⚠️ 这里是**本地** switch（旧 WPF 归档项目不引用 Contracts，故不引契约层的线名真源）；
            // 与契约层 DesktopScrollTargetWire / Abstractions ScrollTargetWire 的逐项一致
            // 由跨层门禁 ScrollTargetVocabularyTests 钉住（改一侧就会红）。
            return wire.Trim().ToLowerInvariant() switch
            {
                "page" => ScrollTarget.Page,
                "element" => ScrollTarget.Element,
                "to_ref" => ScrollTarget.ToRef,
                "to_top" => ScrollTarget.ToTop,
                "to_bottom" => ScrollTarget.ToBottom,
                _ => throw new BrowserOperationException(
                    BrowserBridgeErrorCodes.BrowserInvalidCommand,
                    $"Unsupported scroll target '{wire}'"),
            };
        }

        if (locator is null)
        {
            return ScrollTarget.Page;
        }

        return deltaX is null && deltaY is null ? ScrollTarget.ToRef : ScrollTarget.Element;
    }

    private async Task<BrowserBridgeCommandResult> HandlePageWaitForAsync(
        BrowserBridgeCommand command, CancellationToken ct)
    {
        var page = ResolvePage(command, out var pageId);
        if (page is null)
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserPageNotFound, "Page not found");
        var args = DeserializeArgs<PageWaitForArguments>(command) ?? new PageWaitForArguments();
        var result = await page.WaitForAsync(new WaitCondition
        {
            Selector = args.Selector,
            SelectorToHide = args.SelectorToHide,
            UrlPattern = args.UrlPattern,
            TimeoutMs = args.TimeoutMs
        }, ct);
        return Success(command.OperationId, new BrowserWaitResultDescriptor
        {
            TimedOut = result.TimedOut,
            Error = result.Error,
            Page = BuildPageDescriptor(page, pageId)
        });
    }

    // ─── Target Resolution (2.7 fix) ────────────────────────────────────────

    /// <summary>
    /// Resolves target page for Bridge commands. Fixed priority:
    /// 1. command explicit PageId
    /// 2. AgentTargetPageId
    /// 3. Returns null (browser_page_not_found) — does NOT fall back to active tab
    /// </summary>
    private PageId? ResolveTargetPageId(BrowserBridgeCommand command)
    {
        if (!string.IsNullOrWhiteSpace(command.PageId))
            return new PageId(command.PageId);
        return _agentTargetPageId;
    }

    private IBrowserPage? ResolvePage(BrowserBridgeCommand command, out PageId pageId)
    {
        var resolved = ResolveTargetPageId(command);
        if (resolved is not null && _pages.TryGetValue(resolved.Value, out var page))
        {
            pageId = resolved.Value;
            return page;
        }
        pageId = default;
        return null;
    }

    private static Locator ToLocator(BrowserLocatorDescriptor descriptor)
    {
        var normalized = descriptor.Kind.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        if (!Enum.TryParse<LocatorKind>(normalized, ignoreCase: true, out var kind))
            throw new BrowserOperationException("browser_invalid_arguments", $"Unknown locator kind: {descriptor.Kind}");
        return new Locator
        {
            Kind = kind,
            Value = descriptor.Value,
            Name = descriptor.Name,
            Exact = descriptor.Exact,
            Nth = descriptor.Nth,
            HasText = descriptor.HasText
        };
    }

    private static BrowserElementDescriptor ToElementDescriptor(BrowserElementInfo info, long pageVersion)
        => new()
        {
            Ref = info.Ref,
            Tag = info.Tag,
            Role = info.Role,
            Name = info.Name,
            Text = info.Text,
            Visible = info.Visible,
            Enabled = info.Enabled,
            Checked = info.Checked,
            PageVersion = pageVersion,
            BoundingBox = info.BoundingBox is null ? null : new BrowserBoundingBoxDescriptor
            {
                X = info.BoundingBox.X,
                Y = info.BoundingBox.Y,
                Width = info.BoundingBox.Width,
                Height = info.BoundingBox.Height
            }
        };

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task ClosePageInternalAsync(PageId pageId, CancellationToken ct)
    {
        if (_pages.Remove(pageId, out var page))
        {
            if (await _runtime.GetContextAsync(page.ContextId, ct) is { } ownerContext)
                await ownerContext.ClosePageAsync(pageId, ct);
            else
                await page.DisposeAsync();

            // Remove Surface
            await _surfaceHost.CloseAsync(pageId, ct);

            // Remove tab on UI thread
            await _uiDispatcher.InvokeAsync(() =>
            {
                var tab = Tabs.FirstOrDefault(t => t.PageId == pageId);
                if (tab is not null) Tabs.Remove(tab);
                return Task.CompletedTask;
            }, ct);

            // Clear agent target if this was the target (2.7)
            if (_agentTargetPageId == pageId)
            {
                AgentTargetPageId = null;
                OnPropertyChanged(nameof(AgentTargetSummary));

                // Clear current agent summary when target page closes (doc 6.2)
                CurrentAgentSummary = null;
            }

            // Activate adjacent page if this was active
            if (_activePageId == pageId)
            {
                var nextTab = Tabs.LastOrDefault();
                if (nextTab is not null)
                    await ActivateInternalAsync(nextTab.PageId, ct);
                else
                    ActivePageId = null;
            }

            OnPropertyChanged(nameof(Tabs));
        }
    }

    /// <summary>
    /// Syncs navigation state (Title, Url, IsLoading) from the real page to the Tab ViewModel.
    /// PageInfo does not expose CanGoBack/CanGoForward — those are tracked via page events.
    /// </summary>
    private async Task SyncNavigationStateAsync(PageId pageId, CancellationToken ct)
    {
        if (!_pages.TryGetValue(pageId, out var page)) return;
        var tab = Tabs.FirstOrDefault(t => t.PageId == pageId);
        if (tab is null) return;

        var info = page.Info;
        await _uiDispatcher.InvokeAsync(() =>
        {
            tab.Title = info.Title;
            tab.Url = info.Url;
            tab.IsLoading = page.IsLoading;
            tab.CanGoBack = page.CanGoBack;
            tab.CanGoForward = page.CanGoForward;
            return Task.CompletedTask;
        }, ct);

        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(IsLoading));
    }

    private async Task UpdateTabLoadingAsync(PageId pageId, bool loading, CancellationToken ct)
    {
        var tab = Tabs.FirstOrDefault(t => t.PageId == pageId);
        if (tab is not null)
            await _uiDispatcher.InvokeAsync(() => { tab.IsLoading = loading; return Task.CompletedTask; }, ct);
    }

    public async Task ApplyActivityAsync(AgentBrowserActivitySnapshot snapshot, CancellationToken ct)
    {
        await _uiDispatcher.InvokeAsync(() =>
        {
            var item = Activities.FirstOrDefault(existing => existing.OperationId == snapshot.OperationId);
            if (item is null)
            {
                item = new AgentBrowserActivityItem
                {
                    OperationId = snapshot.OperationId,
                    CommandName = snapshot.CommandName,
                    Target = snapshot.Target,
                    StartedAt = snapshot.StartedAt,
                    AgentInstanceId = snapshot.AgentInstanceId,
                    SessionId = snapshot.SessionId,
                    RunId = snapshot.RunId,
                    ToolCallId = snapshot.ToolCallId,
                    ToolName = snapshot.ToolName
                };
                Activities.Insert(0, item);
            }
            item.Apply(snapshot);
            while (Activities.Count > MaxActivityItems)
                Activities.RemoveAt(Activities.Count - 1);
            return Task.CompletedTask;
        }, ct);
    }

    private BrowserPageDescriptor BuildPageDescriptor(IBrowserPage page, PageId pageId)
    {
        return new BrowserPageDescriptor
        {
            ContextId = page.ContextId.Value,
            PageId = pageId.Value,
            Title = page.Info.Title,
            Url = page.Info.Url,
            PageVersion = page.PageVersion,
            IsActive = pageId == _activePageId,
            IsAgentTarget = pageId == _agentTargetPageId,
            CanGoBack = page.CanGoBack,
            CanGoForward = page.CanGoForward,
            IsLoading = page.IsLoading
        };
    }

    private static T? DeserializeArgs<T>(BrowserBridgeCommand command) where T : class
    {
        try
        {
            return command.Arguments.Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch { return null; }
    }

    private static BrowserBridgeCommandResult Success(Guid operationId, object value)
        => new()
        {
            OperationId = operationId,
            Success = true,
            Value = JsonSerializer.SerializeToElement(value, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            })
        };

    private static BrowserBridgeCommandResult SuccessEmpty(Guid operationId)
        => new()
        {
            OperationId = operationId,
            Success = true,
            Value = JsonSerializer.SerializeToElement(new { })
        };

    private static BrowserBridgeCommandResult Error(Guid operationId, string code, string msg)
        => new()
        {
            OperationId = operationId,
            Success = false,
            ErrorCode = code,
            ErrorMessage = msg
        };

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _gate.WaitAsync();
        try
        {
            foreach (var pageId in _pages.Keys.ToList())
            {
                if (_pages.Remove(pageId, out var page))
                    await page.DisposeAsync();
            }

            await _uiDispatcher.InvokeAsync(() =>
            {
                Tabs.Clear();
                Activities.Clear();
                return Task.CompletedTask;
            }, CancellationToken.None);

            if (_context is not null)
                await _context.DisposeAsync();
            _context = null;

            await _runtime.DisposeAsync();
            ActivePageId = null;
            AgentTargetPageId = null;
            OnPropertyChanged(nameof(AgentTargetSummary));
            _activeContextId = null;

            // Clear agent operation state on dispose (doc 6.2)
            _lastOperationSnapshot = null;
            CurrentAgentSummary = null;
            _userTakeover = false;
            _paused = false;
            ControlState = AgentBrowserControlState.Idle;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
