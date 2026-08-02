using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PuddingDesktop.Browser;
using PuddingDesktop.Hosting;
using PuddingDesktop.Diagnostics;
using PuddingDesktop.Runtime;
using PuddingDesktop.Theming;
using PuddingDesktop.ViewModels;
using PuddingDesktop.Views;

namespace PuddingDesktop;

public sealed partial class MainWindow : Window
{
    private readonly DesktopApplicationCoordinator _coordinator;
    private readonly RuntimeCenterViewModel _statusVm;
    private readonly WindowsThemeService _themeService;
    private readonly DesktopTrayIconService _trayIconService;
    private readonly CancellationTokenSource _browserLifetimeCts = new();
    private bool _isClosing;
    private bool _shutdownCompleted;
    private bool _webViewInitialized;
    private bool _browserInitialized;
    private bool _initialized;

    public MainWindow() : this(new DesktopApplicationCoordinator()) { }

    public MainWindow(DesktopApplicationCoordinator coordinator)
    {
        _coordinator = coordinator;
        _statusVm = new RuntimeCenterViewModel(coordinator);
        _themeService = new WindowsThemeService();
        _trayIconService = new DesktopTrayIconService();
        _themeService.ApplyTo(System.Windows.Application.Current.Resources);
        DataContext = _statusVm;

        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            WindowsBackdropService.Apply(this, _themeService.IsDarkMode);
            try { _trayIconService.Initialize(this); }
            catch (Exception ex) { DesktopDiagnosticLog.Write("TrayIconInitialization", ex); }
        };
        StateChanged += (_, _) => UpdateMaximizeGlyph();

        _trayIconService.OpenRequested += (_, _) => _coordinator.ActivateMainWindow();
        _trayIconService.StartRequested += async (_, _) => await RunTrayCommandAsync(_coordinator.StartCoreAsync);
        _trayIconService.StopRequested += async (_, _) => await RunTrayCommandAsync(_coordinator.StopCoreAsync);
        _trayIconService.RestartRequested += async (_, _) => await RunTrayCommandAsync(_coordinator.RestartCoreAsync);
        _trayIconService.ExitRequested += async (_, _) => await RequestCloseAsync(explicitExit: true);

        // Mark initialized before wiring events — prevents NavButton_Checked
        // from accessing null x:Name fields during XAML load.
        _initialized = true;

        _statusVm.PropertyChanged += (_, _) =>
            Dispatcher.Invoke(() => UpdateStatusDisplay());

        _coordinator.StateChanged += OnCoordinatorStateChanged;

        WorkbenchPage.WorkbenchReady += OnWorkbenchReady;
        WorkbenchPage.WorkbenchFailed += OnWorkbenchFailed;
        BrowserPage.RetryInitializationRequested += OnBrowserRetryInitializationRequested;

        UpdateStatusDisplay();
        UpdateNavigation();
        UpdateMaximizeGlyph();
    }

    private void OnCoordinatorStateChanged(object? sender, DesktopStateChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateNavigation();

            if (e.Current == DesktopStartupState.CoreReady
                && e.CoreAddress is not null
                && navWorkbench.IsChecked == true)
            {
                _ = InitializeWebView2Async(e.CoreAddress);
            }

            if (e.Current is DesktopStartupState.CoreStopped
                or DesktopStartupState.CoreFailed
                or DesktopStartupState.CoreRestartScheduled
                or DesktopStartupState.CoreCircuitOpen)
            {
                ResetWebView2();
            }
        });
    }

    private async Task InitializeWebView2Async(Uri coreAddress)
    {
        if (_webViewInitialized) return;
        _webViewInitialized = true;

        _coordinator.BeginWebViewInitialization();

        var dataRoot = _coordinator.DataRoot;
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            _webViewInitialized = false;
            _coordinator.NotifyWorkbenchFailed("尚未配置数据目录。");
            return;
        }

        await WorkbenchPage.InitializeAsync(coreAddress, dataRoot, CancellationToken.None);
    }

    private void ResetWebView2()
    {
        _webViewInitialized = false;
        WorkbenchPage.ResetForCoreStop();
    }

    /// <summary>
    /// Initializes the Browser Workspace (real WebView2 runtime) once DataRoot is available.
    /// Core not being started does NOT block this — user can still browse.
    /// Uses the Coordinator's existing BridgeDispatcher and BridgeClient.
    /// </summary>
    internal async Task<bool> InitializeBrowserWorkspaceAsync(
        string dataRoot,
        CancellationToken cancellationToken)
    {
        if (_browserInitialized) return true;
        if (string.IsNullOrWhiteSpace(dataRoot)) return false;

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _browserLifetimeCts.Token);
            if (BrowserPage is BrowserWorkspaceView browserView)
            {
                await browserView.InitializeAsync(
                    dataRoot,
                    _coordinator.BridgeDispatcher,
                    _coordinator.BridgeClient,
                    linkedCts.Token);
            }
            _browserInitialized = true;
            return true;
        }
        catch (Exception ex)
        {
            _browserInitialized = false;
            DesktopDiagnosticLog.Write("BrowserWorkspaceInit", ex);
            return false;
        }
    }

    private async void OnBrowserRetryInitializationRequested(object? sender, EventArgs e)
    {
        var dataRoot = _coordinator.DataRoot;
        if (string.IsNullOrWhiteSpace(dataRoot)) return;
        await InitializeBrowserWorkspaceAsync(dataRoot, _browserLifetimeCts.Token);
    }

    private void OnWorkbenchReady(object? sender, EventArgs e)
        => _coordinator.NotifyWorkbenchReady();

    private void OnWorkbenchFailed(object? sender, string error)
    {
        _webViewInitialized = false;
        _coordinator.NotifyWorkbenchFailed(error);
    }

    private void UpdateNavigation()
    {
        if (!_initialized) return;
        var state = _coordinator.State;
        if (navWorkbench.IsChecked == true)
        {
            if (state is DesktopStartupState.CoreReady or DesktopStartupState.WorkbenchReady
                or DesktopStartupState.WebViewInitializing)
            {
                WorkbenchPage.Visibility = Visibility.Visible;
                RuntimeCenterPage.Visibility = Visibility.Collapsed;
            }
            else
            {
                WorkbenchPage.Visibility = Visibility.Collapsed;
                RuntimeCenterPage.Visibility = Visibility.Visible;
            }
        }
    }

    private void UpdateStatusDisplay()
    {
        if (!_initialized) return;
        StatusText.Text = _statusVm.StatusText;
        AddressText.Text = _statusVm.BoundAddress;
        StartButton.IsEnabled = _statusVm.CanStart;
        StopButton.IsEnabled = _statusVm.CanStop;
        RestartButton.IsEnabled = _statusVm.CanRestart;
        _trayIconService.UpdateToolTip($"Pudding · {_statusVm.StatusText}");
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { MaximizeButton_Click(sender, e); return; }
        DragMove();
    }
    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void UpdateMaximizeGlyph()
        => MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    private async void CloseButton_Click(object sender, RoutedEventArgs e)
        => await RequestCloseAsync(explicitExit: false);
    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownCompleted) return;
        e.Cancel = true;
        await RequestCloseAsync(explicitExit: false);
    }
    private async Task RequestCloseAsync(bool explicitExit)
    {
        if (_isClosing) return;
        _isClosing = true;

        if (!explicitExit && _coordinator.BackgroundMode.ShouldMinimizeToTray())
        {
            Hide();
            _isClosing = false;
            return;
        }

        _coordinator.RequestExplicitExit();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await _coordinator.StopAsync(cts.Token);
        }
        catch { }

        WorkbenchPage.DisposeWebView();
        StoragePage.DisposeOperations();
        RuntimeCenterPage.DisposeOperations();
        _browserLifetimeCts.Cancel();

        // Dispose Browser Workspace (await to ensure WebView2 cleanup)
        try
        {
            if (BrowserPage is BrowserWorkspaceView browserView)
            {
                await browserView.DisposeAsync()
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex)
        {
            DesktopDiagnosticLog.Write("BrowserDispose", ex);
        }

        _statusVm.Dispose();
        _trayIconService.Dispose();
        BrowserPage.RetryInitializationRequested -= OnBrowserRetryInitializationRequested;
        _browserLifetimeCts.Dispose();
        _shutdownCompleted = true;
        Dispatcher.Invoke(Close);
    }

    private async Task RunTrayCommandAsync(Func<CancellationToken, Task> command)
    {
        try { await command(CancellationToken.None); }
        catch (Exception ex) { DesktopDiagnosticLog.Write("TrayCommand", ex); }
    }

    private void NavButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        if (sender is RadioButton btn)
        {
            WorkbenchPage.Visibility = Visibility.Collapsed;
            BrowserPage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Collapsed;
            RuntimeCenterPage.Visibility = Visibility.Collapsed;
            StoragePage.Visibility = Visibility.Collapsed;

            if (btn == navWorkbench)
            {
                var s = _coordinator.State;
                WorkbenchPage.Visibility = s is DesktopStartupState.CoreReady or DesktopStartupState.WorkbenchReady
                    or DesktopStartupState.WebViewInitializing ? Visibility.Visible : Visibility.Collapsed;
                RuntimeCenterPage.Visibility = WorkbenchPage.Visibility == Visibility.Visible
                    ? Visibility.Collapsed : Visibility.Visible;

                // A collapsed WebView2CompositionControl cannot reliably complete
                // initialization. Defer Workbench creation until this page is visible;
                // Core and the Agent Browser Bridge remain independently usable.
                if (WorkbenchPage.Visibility == Visibility.Visible
                    && s == DesktopStartupState.CoreReady
                    && _coordinator.CoreAddress is { } coreAddress)
                {
                    _ = InitializeWebView2Async(coreAddress);
                }
            }
            else if (btn == navBrowser)
                BrowserPage.Visibility = Visibility.Visible;
            else if (btn == navCore)
                RuntimeCenterPage.Visibility = Visibility.Visible;
            else if (btn == navStorage)
            {
                StoragePage.Visibility = Visibility.Visible;
                StoragePage.SetDataRoot(_coordinator.DataRoot);
                _ = StoragePage.RefreshAsync();
            }
            else if (btn == navSettings)
                SettingsPage.Visibility = Visibility.Visible;
        }
    }

    private async void StartCore_Click(object sender, RoutedEventArgs e)
    {
        try { await _coordinator.StartCoreAsync(CancellationToken.None); } catch { }
        UpdateStatusDisplay();
    }
    private async void StopCore_Click(object sender, RoutedEventArgs e)
    {
        try { await _coordinator.StopCoreAsync(CancellationToken.None); } catch { }
        UpdateStatusDisplay();
    }
    private async void RestartCore_Click(object sender, RoutedEventArgs e)
    {
        try { await _coordinator.RestartCoreAsync(CancellationToken.None); } catch { }
        UpdateStatusDisplay();
    }
    internal async Task RestartCoreViaCoordinatorAsync()
        => await _coordinator.RestartCoreAsync(CancellationToken.None);

    internal async Task ApplyDesktopRuntimeSettingsAsync(
        bool autoRestart,
        DesktopCloseBehavior closeBehavior)
    {
        _coordinator.ApplyCloseBehavior(closeBehavior);
        await _coordinator.SetAutoRestartAsync(autoRestart, CancellationToken.None);
    }
}
