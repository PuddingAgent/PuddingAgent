using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using PuddingBrowser.WebView2;
using PuddingDesktop.Browser;
using PuddingDesktop.Configuration;
using PuddingDesktop.Hosting;
using PuddingDesktop.Runtime;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices.WindowsRuntime;

namespace PuddingDesktop;

public sealed partial class MainWindow : Window
{
    private readonly DesktopApplicationCoordinator _coordinator;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _webGate = new(1, 1);
    private readonly SemaphoreSlim _browserGate = new(1, 1);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CoreProcessMetricsSampler _metrics = new();
    private Microsoft.UI.Xaml.Controls.WebView2? _web;
    private CoreWebView2Environment? _webEnvironment;
    private BrowserWorkspaceController? _browser;
    private DesktopTrayIcon? _tray;
    private Uri? _webOrigin;
    private long _webGeneration;
    private bool _ready, _closing, _closed;
    private bool _restartRequired, _loadingAppearance;
    private string? _stateError;
    private readonly PuddingDesktop.Foundation.SkeletonSettingsStore _appearance = new(Path.Combine(App.StateRoot, "appearance"));

    public MainWindow(DesktopApplicationCoordinator coordinator)
    {
        _coordinator = coordinator;
        InitializeComponent();
        Title = "Pudding";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 920));
        _ready = true;
        _coordinator.StateChanged += OnStateChanged;
        AppWindow.Closing += (sender, e) => { if (!_closed) { e.Cancel = true; _ = RequestCloseAsync(false); } };
        Root.Loaded += async (_, _) =>
        {
            try { _tray = new DesktopTrayIcon(this, () => _ = RequestCloseAsync(true)); }
            catch (Exception ex) { App.WriteDiagnostic(ex); }
            await LoadSettingsAsync();
            _loadingAppearance = true;
            var appearance = await _appearance.LoadAsync();
            ThemeBox.SelectedIndex = appearance.Settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
            _loadingAppearance = false;
            _timer.Start();
#if DEBUG
            var report = Environment.GetEnvironmentVariable("PUDDING_LAUNCHER_SMOKE_REPORT");
            if (!string.IsNullOrEmpty(report)) _ = RunSmokeAsync(report);
#endif
        };
        _timer.Tick += (_, _) => { if (RuntimePane.Visibility == Visibility.Visible && AppWindow.IsVisible) RefreshRuntimePanel(); };
        LogText.SizeChanged += (_, _) => FollowLatestLog();
        RuntimeLogScroll.SizeChanged += (_, _) => FollowLatestLog();
        FollowLogsBox.Checked += (_, _) => FollowLatestLog();
        RefreshStatus();
    }

    private void OnNavigate(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_ready) return;
        var page = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        WorkbenchPane.Visibility = page == "web" ? Visibility.Visible : Visibility.Collapsed;
        BrowserPane.Visibility = page == "browser" ? Visibility.Visible : Visibility.Collapsed;
        RuntimePane.Visibility = page == "runtime" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPane.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "settings") _ = LoadSettingsAsync();
        if (page == "runtime") RefreshRuntimePanel();
        else _metrics.Sample(null);
        if (page == "web" && _coordinator.WorkbenchAddress is { } address) _ = LoadWorkbenchAsync(address);
    }

    private void OnStateChanged(object? sender, DesktopStateChangedEventArgs e) => UiThread.Post(() =>
    {
        _stateError = e.Error;
        RefreshStatus(e.Error);
        if (e.Current == DesktopStartupState.CoreReady && e.WorkbenchAddress is { } address)
            _ = LoadWorkbenchAsync(address);
        if (e.Current is DesktopStartupState.CoreStopped or DesktopStartupState.CoreFailed
            or DesktopStartupState.CoreStopping or DesktopStartupState.CoreRestartScheduled or DesktopStartupState.CoreCircuitOpen)
        {
            _webGeneration++;
            _webOrigin = null;
            LoadingPanel.Visibility = Visibility.Visible;
            _web?.CoreWebView2?.Navigate("about:blank");
        }
    });

    private void RefreshStatus(string? error = null)
    {
        if (error is not null) _stateError = error;
        error ??= _stateError;
        var runtime = _coordinator.RuntimeSnapshot;
        StatusText.Text = $"Core · {runtime.State}    {(_coordinator.CoreAddress?.Authority ?? "尚未连接")}";
        var statusState = _coordinator.State is DesktopStartupState.CoreFailed or DesktopStartupState.InvalidConfiguration or DesktopStartupState.DebugFailed
            ? DesktopRuntimeState.Failed : runtime.State;
        var (label, symbol, color) = statusState switch
        {
            DesktopRuntimeState.Ready => ("运行中", Symbol.Accept, Windows.UI.Color.FromArgb(255, 29, 139, 85)),
            DesktopRuntimeState.Starting => ("启动中", Symbol.Sync, Windows.UI.Color.FromArgb(255, 181, 112, 15)),
            DesktopRuntimeState.RestartScheduled => ("等待重启", Symbol.Sync, Windows.UI.Color.FromArgb(255, 181, 112, 15)),
            DesktopRuntimeState.Stopping => ("停止中", Symbol.Pause, Windows.UI.Color.FromArgb(255, 181, 112, 15)),
            DesktopRuntimeState.Failed => ("启动失败", Symbol.Cancel, Windows.UI.Color.FromArgb(255, 202, 65, 65)),
            DesktopRuntimeState.CircuitOpen => ("恢复已暂停", Symbol.Cancel, Windows.UI.Color.FromArgb(255, 202, 65, 65)),
            DesktopRuntimeState.Stopped => ("已停止", Symbol.Stop, Windows.UI.Color.FromArgb(255, 116, 124, 139)),
            _ => ("未启动", Symbol.Pause, Windows.UI.Color.FromArgb(255, 116, 124, 139))
        };
        var brush = new SolidColorBrush(color);
        RuntimeStateText.Text = label;
        RuntimeStateText.Foreground = RuntimeStateIcon.Foreground = RuntimeNavigationItem.Foreground = brush;
        RuntimeStateIcon.Symbol = symbol;
        RuntimePidText.Text = runtime.State == DesktopRuntimeState.Ready ? $"PID {runtime.Session?.ProcessId}" : $"Core · {runtime.State}";
        var startup = runtime.State == DesktopRuntimeState.Ready && runtime.Session is { ReadyAt: { } ready } session ? $" · 启动耗时 {(ready - session.StartedAt).TotalSeconds:F1} 秒" : "";
        RuntimeText.Text = $"接口：{_coordinator.CoreAddress?.Authority ?? "未连接"}{startup}\n数据目录：{_coordinator.DataRoot ?? "尚未设置"}";
        if (!string.IsNullOrWhiteSpace(error ?? runtime.LastError)) RuntimeText.Text += $"\n{error ?? runtime.LastError}";
        if (runtime.State != DesktopRuntimeState.Ready) UpdateMetrics(null);
        LoadingText.Text = error ?? runtime.LastError ?? "等待独立 Core 就绪；可在运行中心查看日志，或在启动设置中修改路径。";
    }

    private void RefreshRuntimePanel()
    {
        RefreshStatus();
        var runtime = _coordinator.RuntimeSnapshot;
        UpdateMetrics(_metrics.Sample(runtime.State == DesktopRuntimeState.Ready ? runtime.Session?.ProcessId : null));
        var text = _coordinator.CoreLogBuffer.GetTail(300);
        if (LogText.Text == text) return;
        LogText.Text = text;
        FollowLatestLog();
    }

    private void FollowLatestLog()
    {
        if (FollowLogsBox.IsChecked == true)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => { if (!_closing && FollowLogsBox.IsChecked == true) RuntimeLogScroll.ChangeView(null, RuntimeLogScroll.ScrollableHeight, null, true); });
    }

    private void UpdateMetrics(CoreProcessMetrics? metrics)
    {
        RuntimeCpuText.Text = metrics?.CpuPercent is { } cpu ? $"{cpu:F1}%" : "—";
        RuntimeCpuBar.Value = metrics?.CpuPercent ?? 0;
        RuntimeCpuBar.Opacity = metrics?.CpuPercent is null ? 0.25 : 1;
        RuntimeMemoryText.Text = metrics is null ? "—" : $"{metrics.WorkingSetBytes / 1048576d:F0} MiB";
        RuntimeUptimeText.Text = metrics is null ? "—" : $"{(int)metrics.Uptime.TotalHours:00}:{metrics.Uptime.Minutes:00}:{metrics.Uptime.Seconds:00}";
        RuntimeStartedText.Text = metrics is null ? "进程未运行或指标不可读" : $"启动于 {metrics.StartedAt.LocalDateTime:MM-dd HH:mm:ss}";
    }

    private async Task LoadWorkbenchAsync(Uri address)
    {
        var generation = _webGeneration;
        await _webGate.WaitAsync(_lifetime.Token);
        try
        {
            if (generation != _webGeneration || _closing || _webOrigin == address) return;
            _coordinator.BeginWebViewInitialization();
            if (_web is null)
            {
                _webEnvironment = await CoreWebView2Environment.CreateWithOptionsAsync(null,
                    Path.Combine(_coordinator.DataRoot!, "browser", "workbench", "user-data"), null);
                _web = new Microsoft.UI.Xaml.Controls.WebView2();
                WorkbenchHost.Children.Add(_web);
                await _web.EnsureCoreWebView2Async(_webEnvironment);
                _web.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
                _web.CoreWebView2.NavigationStarting += (_, e) =>
                {
                    if (_webOrigin is not null && Uri.TryCreate(e.Uri, UriKind.Absolute, out var target)
                        && target.Scheme is "http" or "https" && target.GetLeftPart(UriPartial.Authority) != _webOrigin.GetLeftPart(UriPartial.Authority))
                    { e.Cancel = true; OpenExternal(e.Uri); }
                };
                var currentWeb = _web;
                _web.CoreWebView2.NavigationCompleted += (_, e) =>
                {
                    if (_closing || !ReferenceEquals(_web, currentWeb) || _webOrigin is null || !currentWeb.CoreWebView2.Source.StartsWith(_webOrigin.GetLeftPart(UriPartial.Authority) + "/", StringComparison.OrdinalIgnoreCase)) return;
                    if (e.IsSuccess) { LoadingPanel.Visibility = Visibility.Collapsed; _coordinator.NotifyWorkbenchReady(); }
                    else { _webOrigin = null; _coordinator.NotifyWorkbenchFailed(e.WebErrorStatus.ToString()); }
                };
                _web.CoreWebView2.ProcessFailed += (_, e) =>
                {
                    if (_closing || !ReferenceEquals(_web, currentWeb)) return;
                    _webOrigin = null;
                    LoadingPanel.Visibility = Visibility.Visible;
                    _coordinator.NotifyWorkbenchFailed(e.ProcessFailedKind.ToString());
                };
            }
            if (generation != _webGeneration || _closing) return;
            _webOrigin = address;
            _web.CoreWebView2.Navigate(new Uri(address, "/admin/").ToString());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _webOrigin = null; App.WriteDiagnostic(ex); LoadingText.Text = ex.Message; _coordinator.NotifyWorkbenchFailed(ex.Message); }
        finally { _webGate.Release(); }
    }

    private static void OpenExternal(string address)
    {
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    internal async Task<bool> InitializeBrowserWorkspaceAsync(string dataRoot, CancellationToken ct)
    {
        await _browserGate.WaitAsync(ct);
        try
        {
            if (_browser is not null) return true;
            var dispatcher = new WinUiDispatcher(DispatcherQueue);
            var surfaces = new WinUiBrowserSurfaceHost(dispatcher, BrowserSurface);
            var runtime = new WebView2BrowserRuntime(dispatcher, surfaces, dataRoot);
            var browser = new BrowserWorkspaceController(runtime, surfaces, dispatcher);
            try { await browser.InitializeAsync(dataRoot, ct); }
            catch { await browser.DisposeAsync(); throw; }
            _browser = browser;
            BrowserTabs.ItemsSource = browser.Tabs;
            _coordinator.BridgeDispatcher.SetHandler(browser);
            _coordinator.BridgeDispatcher.ActivityChanged += async (_, e) =>
            { try { await browser.ApplyActivityAsync(e.Snapshot, _lifetime.Token); } catch (OperationCanceledException) { } catch (Exception ex) { App.WriteDiagnostic(ex); } };
            _coordinator.BridgeDispatcher.OperationStateChanged += async (_, e) =>
            { try { await browser.ApplyOperationStateAsync(e.Snapshot, _lifetime.Token); } catch (OperationCanceledException) { } catch (Exception ex) { App.WriteDiagnostic(ex); } };
            browser.PropertyChanged += (_, _) => UiThread.Post(() =>
            {
                if (_closing) return;
                BrowserStatus.Text = $"{browser.ControlState} · {browser.AgentTargetSummary} · {browser.CurrentAgentSummary}";
                if (browser.ActiveTab is { } tab) { BrowserTabs.SelectedItem = tab; AddressBox.Text = tab.Url ?? ""; }
            });
            return true;
        }
        finally { _browserGate.Release(); }
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        try { await operation(_lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.WriteDiagnostic(ex); RefreshStatus(ex.Message); }
    }
    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_restartRequired) { RefreshStatus("数据目录已改变，请退出并重开 Desktop。"); return; }
        if (_coordinator.RuntimeSnapshot.State == DesktopRuntimeState.Ready && _coordinator.WorkbenchAddress is { } address)
        {
            _web?.Close(); _web = null; _webOrigin = null; WorkbenchHost.Children.Clear();
            await LoadWorkbenchAsync(address);
        }
        else await RunAsync(_coordinator.StartCoreAsync);
    }
    private async void OnStop(object sender, RoutedEventArgs e) => await RunAsync(_coordinator.StopCoreAsync);
    private async void OnRestart(object sender, RoutedEventArgs e)
    {
        if (_restartRequired) { RefreshStatus("数据目录已改变，请退出并重开 Desktop。"); return; }
        await RunAsync(_coordinator.RestartCoreAsync);
    }
    private async void OnExit(object sender, RoutedEventArgs e) => await RequestCloseAsync(true);
    private async void OnNewTab(object sender, RoutedEventArgs e) { if (_browser is not null) await RunAsync(async ct => { await _browser.CreatePageAsync("about:blank", true, ct); }); }
    private async void OnBrowserTab(object sender, SelectionChangedEventArgs e) { if (_browser is not null && BrowserTabs.SelectedItem is BrowserTabViewModel tab && _browser.ActivePageId != tab.PageId) await RunAsync(ct => _browser.ActivateAsync(tab.PageId, ct)); }
    private async void OnCloseTab(object sender, RoutedEventArgs e) { if (_browser is not null && sender is FrameworkElement { DataContext: BrowserTabViewModel tab }) await RunAsync(ct => _browser.ClosePageAsync(tab.PageId, ct)); }
    private async void OnBack(object sender, RoutedEventArgs e) { if (_browser?.ActivePageId is { } page) await RunAsync(ct => _browser.GoBackAsync(page, ct)); }
    private async void OnReload(object sender, RoutedEventArgs e) { if (_browser?.ActivePageId is { } page) await RunAsync(ct => _browser.ReloadAsync(page, ct)); }
    private async void OnTarget(object sender, RoutedEventArgs e) { if (_browser?.ActivePageId is { } page) await RunAsync(ct => _browser.AssignAgentTargetAsync(page, ct)); }
    private async void OnTakeover(object sender, RoutedEventArgs e) { if (_browser is not null) await RunAsync(ct => _browser.SetUserTakeoverAsync(Takeover.IsChecked == true, ct)); }
    private async void OnGo(object sender, RoutedEventArgs e) => await NavigateBrowserAsync();
    private async void OnAddressKey(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e) { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; await NavigateBrowserAsync(); } }
    private async Task NavigateBrowserAsync()
    {
        if (_browser is null) return;
        var text = AddressBox.Text.Trim();
        if (!text.Contains("://")) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) { BrowserStatus.Text = "请输入 HTTP 或 HTTPS 地址。"; return; }
        await RunAsync(async ct => { var page = _browser.ActivePageId ?? await _browser.CreatePageAsync(null, true, ct); await _browser.NavigateAsync(page, uri, ct); });
    }
    private async Task LoadSettingsAsync()
    {
        try
        {
            var settings = await new FileDesktopBootstrapSettingsStore().LoadAsync(_lifetime.Token);
            DataRootBox.Text = settings.DataRoot ?? @"D:\data";
            CorePathBox.Text = settings.CoreExecutablePath ?? "";
            TrayBox.IsChecked = settings.CloseBehavior == DesktopCloseBehavior.MinimizeToTray;
            var result = await new SystemConfigurationService().LoadAsync(DataRootBox.Text, _lifetime.Token);
            if (result.Config is { } config) { PortBox.Value = config.Desktop.Core.Port; AutoStartBox.IsChecked = config.Desktop.Core.AutoStart; }
        }
        catch (Exception ex) { SettingsStatus.Text = ex.Message; }
    }
    private async void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_coordinator.RuntimeSnapshot.State is not (DesktopRuntimeState.Idle or DesktopRuntimeState.Stopped or DesktopRuntimeState.Failed or DesktopRuntimeState.CircuitOpen))
                throw new InvalidOperationException("请先停止 Core，再修改启动设置。");
            var root = Path.GetFullPath(DataRootBox.Text.Trim());
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("请选择已存在的数据目录。");
            if (!double.IsFinite(PortBox.Value) || PortBox.Value is < 1 or > 65535 || PortBox.Value != Math.Truncate(PortBox.Value)) throw new InvalidOperationException("端口必须是 1 至 65535 的整数。");
            var store = new FileDesktopBootstrapSettingsStore();
            var previous = await store.LoadAsync(_lifetime.Token);
            // Validate before writing either configuration file; invalid paths
            // must not report a successful save or partially update Core settings.
            var corePath = string.IsNullOrWhiteSpace(CorePathBox.Text) ? null : Core.CoreExecutableResolver.Resolve(CorePathBox.Text.Trim());
            var settings = previous with { DataRoot = root, CoreExecutablePath = corePath, CloseBehavior = TrayBox.IsChecked == true ? DesktopCloseBehavior.MinimizeToTray : DesktopCloseBehavior.ExitAndStopCore };
            await new SystemConfigurationService().UpdateDesktopCoreSettingsAsync(root, core => core with { Port = (int)PortBox.Value, AutoStart = AutoStartBox.IsChecked == true }, _lifetime.Token);
            await store.SaveAsync(settings, _lifetime.Token);
            _restartRequired |= !string.Equals(_coordinator.DataRoot, root, StringComparison.OrdinalIgnoreCase);
            _coordinator.ApplyCloseBehavior(settings.CloseBehavior);
            SettingsStatus.Text = "已保存。启动 Core 后生效；切换数据目录请重开 Desktop。";
        }
        catch (Exception ex) { SettingsStatus.Text = ex.Message; }
    }
    private async void OnTheme(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        Root.RequestedTheme = ThemeBox.SelectedIndex switch { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default };
        if (!_loadingAppearance)
        {
            try { await _appearance.SaveAsync(new(new(), Root.RequestedTheme.ToString(), "Mica")); }
            catch (Exception ex) { App.WriteDiagnostic(ex); }
        }
    }
    internal async Task RequestCloseAsync(bool explicitExit)
    {
        if (_closing) return;
        if (!explicitExit && _tray?.Available == true && _coordinator.BackgroundMode.ShouldMinimizeToTray()) { AppWindow.Hide(); return; }
        _closing = true;
        _coordinator.RequestExplicitExit();
        _timer.Stop();
        try
        {
            await _coordinator.StopAsync(CancellationToken.None);
            await _lifetime.CancelAsync();
            await _webGate.WaitAsync();
            try { _web?.Close(); _web = null; } finally { _webGate.Release(); }
            if (_browser is not null) await _browser.DisposeAsync();
            await _coordinator.DisposeAsync();
            _tray?.Dispose();
            _closed = true;
            Close();
            await ((App)Application.Current).FinishAsync();
        }
        catch (Exception ex) { App.WriteDiagnostic(ex); _closing = false; RefreshStatus(ex.Message); }
    }

#if DEBUG
    private async Task RunSmokeAsync(string path)
    {
        var checks = new List<string>();
        var pids = new List<int>();
        try
        {
            async Task WaitReady()
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                while (_coordinator.State != DesktopStartupState.WorkbenchReady) await Task.Delay(100, timeout.Token);
            }
            await WaitReady();
            var pid = _coordinator.RuntimeSnapshot.Session!.ProcessId;
            if (pid == Environment.ProcessId) throw new Exception("Core must be a separate process");
            pids.Add(pid); checks.Add("independent Core ready and Web workbench loaded");
            if (_tray?.Available != true) throw new Exception("Tray icon unavailable in lifecycle test");
            _coordinator.ApplyCloseBehavior(DesktopCloseBehavior.MinimizeToTray);
            await RequestCloseAsync(false);
            if (AppWindow.IsVisible || Process.GetProcessById(pid).HasExited) throw new Exception("Tray close failed to retain Core");
            _coordinator.ActivateMainWindow();
            _coordinator.ApplyCloseBehavior(DesktopCloseBehavior.ExitAndStopCore);
            checks.Add("close hides to tray while Core lives; window reactivated");
            Navigation.SelectedItem = RuntimeNavigationItem;
            await Task.Delay(1100);
            RefreshRuntimePanel();
            if (RuntimeMemoryText.Text == "—" || RuntimeCpuText.Text == "—") throw new Exception("Runtime process metrics unavailable");
            var logWidth = RuntimeLogScroll.ActualWidth;
            var logHeight = RuntimeLogScroll.ActualHeight;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 740));
            await Task.Delay(250);
            if (RuntimeLogScroll.ActualWidth >= logWidth || RuntimeLogScroll.ActualHeight >= logHeight || RuntimeLogScroll.ActualHeight <= 0)
                throw new Exception("Runtime log viewport did not resize");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 920));
            await Task.Delay(250);
            if (RuntimeLogScroll.ScrollableHeight - RuntimeLogScroll.VerticalOffset > 2) throw new Exception("Log follow did not settle after resize");
            checks.Add("runtime CPU/memory sampled; log viewport adapts in both dimensions");
            var capture = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await capture.RenderAsync(Root);
            var pixels = await capture.GetPixelsAsync();
            var imagePath = Path.ChangeExtension(path, ".png");
            await File.WriteAllBytesAsync(imagePath, []);
            var imageFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(imagePath);
            using (var stream = await imageFile.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite))
            {
                var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                    (uint)capture.PixelWidth, (uint)capture.PixelHeight, 96, 96, pixels.ToArray());
                await encoder.FlushAsync();
            }
            Navigation.SelectedItem = Navigation.MenuItems[0];
            if (_browser is null) throw new Exception("Agent browser controller was not mounted");
            BrowserPane.Visibility = Visibility.Visible;
            var page = await _browser.CreatePageAsync(new Uri(_coordinator.CoreAddress!, "/health").ToString(), true, _lifetime.Token);
            await _browser.AssignAgentTargetAsync(page, _lifetime.Token);
            var other = await _browser.CreatePageAsync("about:blank", true, _lifetime.Token);
            if (_browser.AgentTargetPageId != page) throw new Exception("Visible tab changed Agent target");
            await _browser.SetUserTakeoverAsync(true, _lifetime.Token);
            await _browser.SetUserTakeoverAsync(false, _lifetime.Token);
            await _browser.ClosePageAsync(other, _lifetime.Token);
            await _browser.ClosePageAsync(page, _lifetime.Token);
            BrowserPane.Visibility = Visibility.Collapsed;
            checks.Add("WinUI browser surfaces, navigation, stable Agent target and human takeover");
            if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name is "PuddingHost" or "PuddingRuntime" or "PuddingPlatform")) throw new Exception("Business host loaded in Shell");
            checks.Add("no business Host loaded in Shell");
            var old = Process.GetProcessById(pid);
            await _coordinator.RestartCoreAsync(_lifetime.Token);
            await WaitReady();
            pid = _coordinator.RuntimeSnapshot.Session!.ProcessId;
            if (pids[0] == pid || !old.HasExited) throw new Exception("Restart did not replace child");
            pids.Add(pid); checks.Add("restart replaced Core process and reloaded Web");
            using (var crashed = Process.GetProcessById(pid))
            {
                crashed.Kill();
                await crashed.WaitForExitAsync();
                using var crashTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (_coordinator.RuntimeSnapshot.State == DesktopRuntimeState.Ready) await Task.Delay(100, crashTimeout.Token);
                await _coordinator.StartCoreAsync(_lifetime.Token);
                await WaitReady();
                pid = _coordinator.RuntimeSnapshot.Session!.ProcessId;
                pids.Add(pid);
                checks.Add("Core crash leaves Shell alive; manual start recovers Web");
            }
            using var second = Process.GetProcessById(pid);
            await _coordinator.StopCoreAsync(_lifetime.Token);
            if (!second.HasExited) throw new Exception("Core survived stop");
            checks.Add("Core stopped while Shell remained available");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { success = true, shellPid = Environment.ProcessId, corePids = pids, coreExecutablePath = _coordinator.CoreExecutablePath, checks }));
        }
        catch (Exception ex) { App.WriteDiagnostic(ex); await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { success = false, error = ex.ToString(), state = _coordinator.State.ToString(), checks, corePids = pids })); }
        finally { await RequestCloseAsync(true); }
    }
#endif
}
