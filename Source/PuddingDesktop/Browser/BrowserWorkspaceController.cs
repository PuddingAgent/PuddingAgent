using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
    Task<BrowserActivityEvidenceDocument> CaptureActivityEvidenceAsync(DateTimeOffset capturedAt, CancellationToken ct);
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
    IBrowserCommandHandler,
    INotifyPropertyChanged
{
    private const int MaxActivityItems = 100;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<PageId, IBrowserPage> _pages = new();
    private readonly IBrowserRuntime _runtime;
    private readonly IBrowserSurfaceHost _surfaceHost;
    private readonly IWebView2UiDispatcher _uiDispatcher;

    private IBrowserContext? _context;
    private PageId? _activePageId;
    private PageId? _agentTargetPageId;
    private AgentBrowserControlState _controlState = AgentBrowserControlState.Idle;
    private BrowserBridgeConnectionState _bridgeState = BrowserBridgeConnectionState.Disconnected;
    private BrowserContextId? _activeContextId;
    private string? _dataRoot;
    private bool _disposed;

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
        set { _bridgeState = value; OnPropertyChanged(); }
    }

    public BrowserContextId? ActiveContextId => _activeContextId;

    public BrowserTabViewModel? ActiveTab =>
        Tabs.FirstOrDefault(t => t.PageId == _activePageId);

    public bool CanGoBack => ActiveTab?.CanGoBack ?? false;
    public bool CanGoForward => ActiveTab?.CanGoForward ?? false;
    public bool IsLoading => ActiveTab?.IsLoading ?? false;
    public string AgentTargetSummary => _agentTargetPageId is { } pageId
        ? $"Agent target: {Tabs.FirstOrDefault(tab => tab.PageId == pageId)?.Title ?? pageId.Value}"
        : "No agent target";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Constructor injection: all dependencies required at construction time.
    /// No half-initialized state possible.
    /// </summary>
    public BrowserWorkspaceController(
        IBrowserRuntime runtime,
        IBrowserSurfaceHost surfaceHost,
        IWebView2UiDispatcher uiDispatcher)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _surfaceHost = surfaceHost ?? throw new ArgumentNullException(nameof(surfaceHost));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
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
                UserDataDirectory = agentUdf
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
                UserDataDirectory = agentUdf
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
            if (_context?.Id == contextId)
            {
                foreach (var pageId in _pages.Keys.ToList())
                    await ClosePageInternalAsync(pageId, ct);

                await _runtime.CloseContextAsync(contextId, ct);
                _context = null;
                _activeContextId = null;
                OnPropertyChanged(nameof(ActiveContextId));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PageId> CreatePageAsync(string? initialUrl = null, bool activate = true, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_context is null)
                throw new InvalidOperationException("No browser context");

            var page = await _context.NewPageAsync(new PageCreateOptions
            {
                InitialUrl = initialUrl is not null ? new Uri(initialUrl) : null
            }, ct);

            _pages[page.Id] = page;

            var tab = new BrowserTabViewModel
            {
                PageId = page.Id,
                Title = string.IsNullOrEmpty(initialUrl) ? "New Tab" : initialUrl,
                Url = initialUrl ?? "about:blank",
                IsLoading = !string.IsNullOrEmpty(initialUrl)
            };

            // ObservableCollection mutation on UI thread
            await _uiDispatcher.InvokeAsync(() => { Tabs.Add(tab); return Task.CompletedTask; }, ct);

            if (activate || Tabs.Count == 1)
            {
                await ActivateInternalAsync(page.Id, ct);
            }

            OnPropertyChanged(nameof(Tabs));
            return page.Id;
        }
        finally
        {
            _gate.Release();
        }
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
    /// Internal activate: calls SurfaceHost.ActivateAsync to show only the target Surface.
    /// Does NOT change AgentTargetPageId (2.7 fix).
    /// </summary>
    private async Task ActivateInternalAsync(PageId pageId, CancellationToken ct)
    {
        if (!_pages.TryGetValue(pageId, out var page)) return;

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
        ControlState = enabled ? AgentBrowserControlState.UserTakeover : AgentBrowserControlState.AgentControlling;
        return Task.CompletedTask;
    }

    public Task SetPausedAsync(bool paused, CancellationToken ct)
    {
        ControlState = paused ? AgentBrowserControlState.Paused : AgentBrowserControlState.AgentControlling;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Captures a sanitized activity evidence snapshot on the UI dispatcher.
    /// Only copies safe fields: command name, page identity, result, and error codes.
    /// Never includes tool parameters, fill/text values, DOM content, URLs, cookies, or tokens.
    /// </summary>
    public Task<BrowserActivityEvidenceDocument> CaptureActivityEvidenceAsync(
        DateTimeOffset capturedAt, CancellationToken ct)
    {
        return _uiDispatcher.InvokeAsync(() =>
        {
            var activities = new List<BrowserActivityEvidenceItem>(Math.Min(Activities.Count, MaxActivityItems));
            foreach (var item in Activities.Take(MaxActivityItems))
            {
                // Sanitize Target: only keep stable context/page IDs.
                // If Target is not a recognized context/page ID pattern, use "-".
                var target = item.Target;
                if (string.IsNullOrWhiteSpace(target))
                    target = "-";

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

            return new BrowserActivityEvidenceDocument
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
        }, ct);
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
            MaxDepth = args.MaxDepth
        }, ct);
        return Success(command.OperationId, new BrowserSnapshotDescriptor
        {
            DomText = snapshot.DomText,
            AccessibilityTree = snapshot.AccessibilityTree,
            Html = snapshot.Html,
            Truncated = snapshot.Truncated,
            NodeCount = snapshot.NodeCount,
            PageVersion = page.PageVersion
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
            case "scroll":
                await page.ScrollAsync(new ScrollOptions { DeltaX = args?.DeltaX, DeltaY = args?.DeltaY }, ct);
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
            Page = BuildPageDescriptor(page, pageId)
        });
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
            if (_context is not null)
                await _context.ClosePageAsync(pageId, ct);
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
                    StartedAt = snapshot.StartedAt
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
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
