using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PuddingBrowser.Abstractions;
using PuddingBrowser.WebView2;
using PuddingDesktop.Browser;

namespace PuddingDesktop.Views;

/// <summary>
/// Windows 11 style dual-tab browser workspace with Agent Activity Pane.
/// Initializes real WebView2 runtime, connects Bridge dispatcher to Controller.
/// </summary>
public partial class BrowserWorkspaceView : UserControl, IAsyncDisposable
{
    private BrowserWorkspaceController? _controller;
    private BrowserBridgeCommandDispatcher? _dispatcher;
    private IDesktopBrowserBridgeClient? _bridgeClient;
    private WpfBrowserSurfaceHost? _surfaceHost;
    private WebView2BrowserRuntime? _runtime;
    private string? _dataRoot;
    private bool _initialized;
    private bool _disposed;

    public BrowserWorkspaceView()
    {
        InitializeComponent();
        DataContext = this;
    }

    /// <summary>
    /// Initializes the browser workspace with real WebView2 runtime.
    /// Called by MainWindow after DataRoot is available.
    /// Core not being started does NOT block initialization — user can still browse.
    /// </summary>
    public async Task InitializeAsync(
        string dataRoot,
        BrowserBridgeCommandDispatcher dispatcher,
        IDesktopBrowserBridgeClient bridgeClient,
        CancellationToken cancellationToken)
    {
        if (_initialized) return;
        _dataRoot = dataRoot;
        _dispatcher = dispatcher;
        _bridgeClient = bridgeClient;

        try
        {
            // Create UI dispatcher for WebView2 thread marshaling
            var uiDispatcher = new WpfUiDispatcher(Dispatcher);

            // Create surface host (owns the SurfaceContainer panel)
            _surfaceHost = new WpfBrowserSurfaceHost(uiDispatcher, SurfaceContainer);

            // Create real WebView2 runtime with agent-browser UDF
            var agentUdf = Path.GetFullPath(Path.Combine(dataRoot, "browser", "agent-browser", "user-data"));
            _runtime = new WebView2BrowserRuntime(uiDispatcher, _surfaceHost, dataRoot);

            // Create controller with constructor injection (no half-init state)
            _controller = new BrowserWorkspaceController(_runtime, _surfaceHost, uiDispatcher);
            _controller.PropertyChanged += OnControllerPropertyChanged;

            // Initialize controller (creates persistent context)
            await _controller.InitializeAsync(dataRoot, cancellationToken);

            // Bind dispatcher to controller — this is the critical product wiring
            dispatcher.SetHandler(_controller);

            // Subscribe to bridge state changes
            _bridgeClient.StateChanged += OnBridgeStateChanged;

            // Update UI state
            UpdateBridgeStatus();
            _initialized = true;

            StatusBarText.Text = "Browser workspace ready";
        }
        catch (Exception ex)
        {
            // Initialization failure shows recoverable error in Browser page only
            // Does NOT block Desktop, Settings, Runtime Center or Workbench
            StatusBarText.Text = $"Browser init failed: {ex.Message}";
            EmptyState.Visibility = Visibility.Visible;
        }
    }

    // ─── Tab Events ──────────────────────────────────────────────────────────

    private async void NewTab_Click(object sender, RoutedEventArgs e)
    {
        if (_controller is null) return;
        try
        {
            await _controller.CreatePageAsync(null, true);
            UpdateEmptyState();
        }
        catch (Exception ex)
        {
            StatusBarText.Text = $"New tab failed: {ex.Message}";
        }
    }

    private async void TabItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (_controller is null) return;
        if (sender is FrameworkElement { DataContext: BrowserTabViewModel tab })
        {
            await _controller.ActivateAsync(tab.PageId, CancellationToken.None);
            UpdateAddressBar();
        }
    }

    private async void TabClose_Click(object sender, MouseButtonEventArgs e)
    {
        if (_controller is null) return;
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: BrowserTabViewModel tab })
        {
            await _controller.ClosePageAsync(tab.PageId, CancellationToken.None);
            UpdateEmptyState();
            UpdateAddressBar();
        }
    }

    // ─── Navigation Events ───────────────────────────────────────────────────

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_controller?.ActivePageId is { } pageId)
            await _controller.GoBackAsync(pageId, CancellationToken.None);
    }

    private async void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_controller?.ActivePageId is { } pageId)
            await _controller.GoForwardAsync(pageId, CancellationToken.None);
    }

    private async void ReloadStop_Click(object sender, RoutedEventArgs e)
    {
        if (_controller?.ActivePageId is not { } pageId) return;

        var activeTab = _controller.ActiveTab;
        if (activeTab?.IsLoading == true)
            await _controller.StopAsync(pageId, CancellationToken.None);
        else
            await _controller.ReloadAsync(pageId, CancellationToken.None);
    }

    private async void AddressBar_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (_controller?.ActivePageId is not { } pageId) return;

        var input = AddressBar.Text.Trim();
        if (string.IsNullOrWhiteSpace(input)) return;

        // No scheme → prepend https://
        if (!input.Contains("://"))
            input = "https://" + input;

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            StatusBarText.Text = "Invalid address";
            return;
        }

        AddressBar.Text = uri.ToString();
        try
        {
            await _controller.NavigateAsync(pageId, uri, CancellationToken.None);
            StatusBarText.Text = "Ready";
        }
        catch (Exception ex)
        {
            StatusBarText.Text = $"Navigation failed: {ex.Message}";
        }
    }

    // ─── Agent Control Events ────────────────────────────────────────────────

    private async void AgentHandoff_Click(object sender, RoutedEventArgs e)
    {
        if (_controller is null) return;
        await _controller.SetUserTakeoverAsync(false, CancellationToken.None);
        StatusBarText.Text = "Page handed to Agent";
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_controller is null) return;
        await _controller.SetPausedAsync(true, CancellationToken.None);
        _dispatcher?.SetPaused(true);
    }

    private async void Takeover_Click(object sender, RoutedEventArgs e)
    {
        if (_controller is null) return;
        await _controller.SetUserTakeoverAsync(true, CancellationToken.None);
        _dispatcher?.SetUserTakeover(true);
    }

    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (_controller is null) return;
        await _controller.SetPausedAsync(false, CancellationToken.None);
        await _controller.SetUserTakeoverAsync(false, CancellationToken.None);
        _dispatcher?.SetPaused(false);
        _dispatcher?.SetUserTakeover(false);
    }

    // ─── Activity Pane ───────────────────────────────────────────────────────

    private void CollapsePane_Click(object sender, RoutedEventArgs e)
    {
        ActivityPane.Visibility = Visibility.Collapsed;
        ExpandPaneButton.Visibility = Visibility.Visible;
    }

    private void ExpandPane_Click(object sender, RoutedEventArgs e)
    {
        ActivityPane.Visibility = Visibility.Visible;
        ExpandPaneButton.Visibility = Visibility.Collapsed;
    }

    // ─── State Updates ───────────────────────────────────────────────────────

    private void OnControllerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => OnControllerPropertyChanged(sender, e));
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(BrowserWorkspaceController.ActiveTab):
                UpdateAddressBar();
                UpdateReloadStopIcon();
                break;
            case nameof(BrowserWorkspaceController.ControlState):
                ControlStateText.Text = _controller?.ControlState.ToString() ?? "Idle";
                break;
            case nameof(BrowserWorkspaceController.Tabs):
                UpdateEmptyState();
                break;
        }
    }

    private void OnBridgeStateChanged(object? sender, BrowserBridgeStateChangedEventArgs e)
    {
        Dispatcher.InvokeAsync(UpdateBridgeStatus);
    }

    private void UpdateBridgeStatus()
    {
        var state = _bridgeClient?.State ?? BrowserBridgeConnectionState.Disconnected;
        BridgeStatusText.Text = state switch
        {
            BrowserBridgeConnectionState.Connected => "Connected",
            BrowserBridgeConnectionState.Connecting => "Connecting...",
            BrowserBridgeConnectionState.Reconnecting => "Reconnecting...",
            BrowserBridgeConnectionState.Failed => "Failed",
            _ => "Disconnected"
        };

        if (_controller is not null)
            _controller.BridgeState = state;
    }

    private void UpdateAddressBar()
    {
        var tab = _controller?.ActiveTab;
        AddressBar.Text = tab?.Url ?? string.Empty;
    }

    private void UpdateReloadStopIcon()
    {
        var isLoading = _controller?.ActiveTab?.IsLoading == true;
        // E72C = Refresh, E71A = Stop (X)
        ReloadStopIcon.Text = isLoading ? "\uE71A" : "\uE72C";
    }

    private void UpdateEmptyState()
    {
        var hasTabs = _controller?.Tabs.Count > 0;
        EmptyState.Visibility = hasTabs ? Visibility.Collapsed : Visibility.Visible;
    }

    // ─── Disposal ────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            // Unset handler first to reject new commands (type-safe clear)
            if (_controller is not null)
                _dispatcher?.ClearHandler(_controller);

            if (_bridgeClient is not null)
                _bridgeClient.StateChanged -= OnBridgeStateChanged;

            if (_controller is not null)
            {
                _controller.PropertyChanged -= OnControllerPropertyChanged;
                await _controller.DisposeAsync();
            }

            // Runtime disposal is handled by Controller
            _runtime = null;
            _surfaceHost = null;
        }
        catch
        {
            // Browser disposal errors must not block Desktop exit
        }
    }
}
