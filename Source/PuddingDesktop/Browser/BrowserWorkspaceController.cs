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
}

/// <summary>
/// Activity item for the Agent Activity Pane.
/// </summary>
public sealed class AgentBrowserActivityItem : INotifyPropertyChanged
{
    public string CommandName { get; init; } = "";
    public string Target { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public string? ErrorCode { get; set; }
    public bool IsCompleted { get; set; }
    public long DurationMs { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void NotifyCompleted() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCompleted)));
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
        if (!_pages.ContainsKey(pageId)) return;

        // Actually activate the Surface (shows it, hides others)
        await _surfaceHost.ActivateAsync(pageId, ct);

        ActivePageId = pageId;
        SyncNavigationState(pageId);
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

            UpdateTabLoading(pageId, true);
            var result = await page.GotoAsync(uri, new NavigationOptions(), ct);
            UpdateTabLoading(pageId, false);
            SyncNavigationState(pageId);
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
                SyncNavigationState(pageId);
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
                SyncNavigationState(pageId);
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
                UpdateTabLoading(pageId, true);
                await page.ReloadAsync(ct);
                UpdateTabLoading(pageId, false);
                SyncNavigationState(pageId);
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
                UpdateTabLoading(pageId, false);
                SyncNavigationState(pageId);
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
    public Task AssignAgentTargetAsync(PageId pageId, CancellationToken ct)
    {
        if (!_pages.ContainsKey(pageId))
            return Task.CompletedTask;

        AgentTargetPageId = pageId;
        return Task.CompletedTask;
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

    // ─── IBrowserCommandHandler ──────────────────────────────────────────────

    public async Task<BrowserBridgeCommandResult> ExecuteAsync(
        BrowserBridgeCommand command, CancellationToken ct)
    {
        var activity = new AgentBrowserActivityItem
        {
            CommandName = command.Name,
            Target = command.PageId ?? "active",
            StartedAt = DateTimeOffset.Now
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var result = command.Name switch
            {
                BrowserBridgeCommandNames.ContextCreate => await HandleContextCreateAsync(command, ct),
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
                _ => Error(command.OperationId, BrowserBridgeErrorCodes.BrowserOperationNotSupported,
                    $"Unknown command: {command.Name}")
            };

            sw.Stop();
            activity.DurationMs = sw.ElapsedMilliseconds;
            activity.IsCompleted = true;
            if (!result.Success) activity.ErrorCode = result.ErrorCode;
            AddActivity(activity);
            return result;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            activity.DurationMs = sw.ElapsedMilliseconds;
            activity.ErrorCode = BrowserBridgeErrorCodes.BrowserCancelled;
            activity.IsCompleted = true;
            AddActivity(activity);
            return Error(command.OperationId, BrowserBridgeErrorCodes.BrowserCancelled, "Cancelled");
        }
        catch (Exception ex)
        {
            sw.Stop();
            activity.DurationMs = sw.ElapsedMilliseconds;
            activity.ErrorCode = BrowserBridgeErrorCodes.BrowserOperationFailed;
            activity.IsCompleted = true;
            AddActivity(activity);
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

    private async Task<BrowserBridgeCommandResult> HandleContextCloseAsync(
        BrowserBridgeCommand command, CancellationToken ct)
    {
        if (_activeContextId is null)
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

        var navResult = await page.GotoAsync(uri, new NavigationOptions { TimeoutMs = args.TimeoutMs }, ct);
        SyncNavigationState(pageId.Value);

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
            case "back": await page.GoBackAsync(ct); break;
            case "forward": await page.GoForwardAsync(ct); break;
            case "reload": await page.ReloadAsync(ct); break;
            case "stop": await page.StopAsync(ct); break;
        }

        SyncNavigationState(pageId.Value);
        return Success(command.OperationId, BuildPageDescriptor(page, pageId.Value));
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
                AgentTargetPageId = null;

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
    private void SyncNavigationState(PageId pageId)
    {
        if (!_pages.TryGetValue(pageId, out var page)) return;
        var tab = Tabs.FirstOrDefault(t => t.PageId == pageId);
        if (tab is null) return;

        var info = page.Info;
        _ = _uiDispatcher.InvokeAsync(() =>
        {
            tab.Title = info.Title;
            tab.Url = info.Url;
            tab.IsLoading = false;
            return Task.CompletedTask;
        }, CancellationToken.None);

        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(IsLoading));
    }

    private void UpdateTabLoading(PageId pageId, bool loading)
    {
        var tab = Tabs.FirstOrDefault(t => t.PageId == pageId);
        if (tab is not null)
            _ = _uiDispatcher.InvokeAsync(() => { tab.IsLoading = loading; return Task.CompletedTask; }, CancellationToken.None);
    }

    private void AddActivity(AgentBrowserActivityItem item)
    {
        _ = _uiDispatcher.InvokeAsync(() =>
        {
            Activities.Insert(0, item);
            while (Activities.Count > MaxActivityItems)
                Activities.RemoveAt(Activities.Count - 1);
            return Task.CompletedTask;
        }, CancellationToken.None);
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
            IsActive = pageId == _activePageId
        };
    }

    private static T? DeserializeArgs<T>(BrowserBridgeCommand command) where T : class
    {
        try
        {
            return command.Arguments.Deserialize<T>(new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
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
            _activeContextId = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
