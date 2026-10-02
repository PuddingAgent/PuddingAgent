using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using PuddingBrowser.WebView2;
using PuddingDesktop.Browser;
using PuddingDesktop.Configuration;
using PuddingDesktop.Foundation;
using PuddingDesktop.Hosting;
using PuddingDesktop.Runtime;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices.WindowsRuntime;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace PuddingDesktop;

public sealed partial class MainWindow : Window
{
    private const long MaxOutputPreviewBytes = 256 * 1024;

    private static readonly HashSet<string> PreviewableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".jsonl", ".csv", ".tsv", ".log", ".xml", ".yml", ".yaml",
        ".html", ".htm", ".css", ".js", ".ts", ".sql", ".cs", ".ps1", ".py", ".toml", ".ini",
    };

    private readonly DesktopApplicationCoordinator _coordinator;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _webGate = new(1, 1);
    private readonly SemaphoreSlim _browserGate = new(1, 1);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CoreProcessMetricsSampler _metrics = new();
    private Microsoft.UI.Xaml.Controls.WebView2? _web;
    private CoreWebView2Environment? _webEnvironment;
    private BrowserWorkspaceController? _browser;
    /// <summary>Browser workspace runtime (WebView2)：能力通道的浏览器表面映射与页面版本读取都来自它。</summary>
    private WebView2BrowserRuntime? _browserRuntime;
    /// <summary>工具区里的浏览器表面宿主：与主工作台是两个 WebView2，外观必须一起同步。</summary>
    private WinUiBrowserSurfaceHost? _browserSurfaces;
    private DesktopTrayIcon? _tray;
    private Uri? _webOrigin;
    private long _webGeneration;
    private bool _ready, _closing, _closed;
    private bool _restartRequired, _loadingAppearance;
    private string? _stateError;
    private readonly PuddingDesktop.Foundation.SkeletonSettingsStore _appearance = new(Path.Combine(App.StateRoot, "appearance"));

    // ── 能力通道接线所需的「宿主事实」（切片 C-3）─────────────────────────────
    // 这里只暴露**只有 Shell 知道**的事实；判定、装配与生命周期都在
    // DesktopApplicationCoordinator（组合根）与 Pudding.DesktopService（组件）里，
    // 本窗口不写任何能力通道的业务判断。
    internal WebView2BrowserRuntime? BrowserRuntime => _browserRuntime;

    internal BrowserWorkspaceController? BrowserWorkspace => _browser;

    internal nint CapabilityWindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    internal Microsoft.UI.Xaml.XamlRoot? CapabilityXamlRoot => Content?.XamlRoot;

    internal bool CapabilityTrayVisible => _tray is not null;

    internal Pudding.Contracts.Desktop.DesktopWindowState CapabilityWindowState =>
        _closing || _closed
            ? Pudding.Contracts.Desktop.DesktopWindowState.Closing
            : AppWindow.IsVisible
                ? Pudding.Contracts.Desktop.DesktopWindowState.Visible
                : Pudding.Contracts.Desktop.DesktopWindowState.HiddenToTray;

    /// <summary>系统通知：走托盘气泡；**没弹出来返回 false 而不是抛**（调用方据此回 Shown=false）。</summary>
    internal bool ShowCapabilityNotification(string title, string message) =>
        _tray?.ShowBalloon(title, message) ?? false;

    // Right tool workspace: instance tabs plus a layout preference that survives restart.
    private readonly ToolWorkspaceTabs _toolTabs = new();
    private readonly ToolWorkspaceActivityPolicy _toolActivity = new();
    private readonly ObservableCollection<ToolTabItem> _toolTabItems = [];
    private readonly Dictionary<string, UIElement> _toolTabContent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _outputPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _outputPreviews = new(StringComparer.Ordinal);
    private ToolWorkspaceLayout _toolLayout = new();
    private bool _toolExpanded, _toolZoomed, _toolDragging, _toolSyncing, _toolLayoutLoaded;
    private double _toolDragOriginX, _toolDragOriginWidth;
    private Brush? _splitterIdleBrush;

    public MainWindow(DesktopApplicationCoordinator coordinator)
    {
        _coordinator = coordinator;
        InitializeComponent();
        Title = "Pudding";
        AppWindow.SetIcon(DesktopIcon.FilePath);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 920));
        _ready = true;
        // IMG11：工具首页卡片的可用性标签取自 Foundation 真源，不在 XAML 里写死
        ApplyToolAvailabilityLabels();
        ToolTabList.ItemsSource = _toolTabItems;
        _coordinator.StateChanged += OnStateChanged;
        AppWindow.Closing += (sender, e) => { if (!_closed) { e.Cancel = true; _ = RequestCloseAsync(false); } };
        WorkbenchPane.SizeChanged += (_, _) => ApplyToolLayout();
        // SCROLL-001：外观选择为“跟随系统”时，系统主题变化也要重新套用宿主外观
        Root.ActualThemeChanged += (_, _) => ApplyWorkbenchAppearance();
        Root.Loaded += async (_, _) =>
        {
            try { _tray = new DesktopTrayIcon(this, () => _ = RequestCloseAsync(true)); }
            catch (Exception ex) { App.WriteDiagnostic(ex); }
            await LoadSettingsAsync();
            _loadingAppearance = true;
            var appearance = await _appearance.LoadAsync();
            ThemeBox.SelectedIndex = WorkbenchAppearance.ComboIndex(
                WorkbenchAppearance.ParsePreference(appearance.Settings.Theme));
            _loadingAppearance = false;
            // SCROLL-001：恢复保存的外观后立即套用宿主表面，避免启动阶段闪白
            ApplyWorkbenchAppearance();
            await LoadToolWorkspaceLayoutAsync();
            ApplyToolLayout();
            _timer.Start();
#if DEBUG
            var report = Environment.GetEnvironmentVariable("PUDDING_LAUNCHER_SMOKE_REPORT");
            if (!string.IsNullOrEmpty(report)) _ = RunSmokeAsync(report);
#endif
        };
        _timer.Tick += (_, _) =>
        {
            _toolActivity.Observe(ToolActivityInFlight());
            if (RuntimePane.Visibility == Visibility.Visible && AppWindow.IsVisible) RefreshRuntimePanel();
        };
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
        RuntimePane.Visibility = page == "runtime" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPane.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "settings") _ = LoadSettingsAsync();
        if (page == "runtime") RefreshRuntimePanel();
        else _metrics.Sample(null);
        if (page == "web")
        {
            ApplyToolLayout();
            if (_coordinator.WorkbenchAddress is { } address) _ = LoadWorkbenchAsync(address);
        }
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

    // ── Right-hand multi-tab tool workspace ─────────────────────────────────────

    private void OnExpandToolPanel(object sender, RoutedEventArgs e) => SetToolExpanded(true);

    private void OnCollapseToolPanel(object sender, RoutedEventArgs e) => SetToolExpanded(false);

    private void OnToolScrimTapped(object sender, TappedRoutedEventArgs e)
    {
        // Narrow-window overlay: clicking outside the panel dismisses it, as designed.
        SetToolExpanded(false);
    }

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_toolExpanded || _toolZoomed) return;
        if (!_toolLayout.Resolve(WorkbenchPane.ActualWidth, true).SpansContent) return;
        SetToolExpanded(false);
        args.Handled = true;
    }

    /// <summary>
    /// Expand/collapse is a display change only: tabs, browser pages, Agent targets and
    /// running tasks survive it, and collapsing never navigates a page to blank.
    /// </summary>
    private void SetToolExpanded(bool expanded)
    {
        if (_toolExpanded == expanded)
        {
            ApplyToolLayout();
            return;
        }
        _toolExpanded = expanded;
        if (expanded)
        {
            _toolActivity.NotifyUserExpanded();
            _toolTabs.EnsureHome();
            SyncToolTabItems();
            UpdateToolContentVisibility();
        }
        else
        {
            // Remember that the user closed the workspace during this activity round.
            _toolActivity.NotifyUserCollapsed();
        }
        ApplyToolLayout();
    }

    private void OnToggleToolZoom(object sender, RoutedEventArgs e)
    {
        if (!_toolExpanded) SetToolExpanded(true);
        _toolZoomed = !_toolZoomed;
        ApplyToolLayout();
    }

    private void ApplyToolLayout()
    {
        var allocation = _toolLayout.Resolve(WorkbenchPane.ActualWidth, _toolExpanded, _toolZoomed);
        var spans = allocation.SpansContent;
        var split = allocation.IsVisible && !spans;

        ToolPanel.Visibility = allocation.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        ToolEntryButton.Visibility = allocation.IsVisible ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(ToolPanel, spans ? 0 : 2);
        Grid.SetColumnSpan(ToolPanel, spans ? 3 : 1);
        // Covering the chat must not stretch the panel: keep its resolved width, right
        // aligned, so the visible chat strip outside it stays clickable for dismissal.
        ToolPanel.Width = spans ? allocation.ToolRegionWidth : double.NaN;
        ToolPanel.HorizontalAlignment = spans ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        ToolSplitterColumn.Width = split ? new GridLength(ToolWorkspaceLayout.SplitterWidth) : new GridLength(0);
        ToolSplitter.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
        ToolColumn.Width = split ? new GridLength(allocation.PanelWidth) : new GridLength(0);
        // The scrim belongs to the narrow-window overlay; zoom has no "outside" to click.
        ToolOverlayScrim.Visibility = spans && !_toolZoomed ? Visibility.Visible : Visibility.Collapsed;
        ToolZoomButton.Content = _toolZoomed ? "\uE73F" : "\uE740";
        ToolZoomButton.IsEnabled = allocation.IsVisible;
        ToolZoomButton.Visibility = allocation.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        ToolCollapseButton.Visibility = allocation.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        ToolAddButton.Visibility = allocation.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateToolEntryButton();
        UpdateToolContentVisibility();
    }

    private void UpdateToolEntryButton()
    {
        var summary = _toolTabs.DescribeActivity();
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new FontIcon { Glyph = "\uE71D", FontSize = 12 });
        content.Children.Add(new TextBlock
        {
            Text = summary.Length == 0 ? "工具区" : $"工具区 · {summary}",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (summary.Length > 0)
            content.Children.Add(new Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 213, 159, 97)),
                VerticalAlignment = VerticalAlignment.Center,
            });
        ToolEntryButton.Content = content;
    }

    private void SyncToolTabItems()
    {
        _toolSyncing = true;
        try
        {
            var desired = _toolTabs.Tabs.ToList();
            for (var index = 0; index < desired.Count; index++)
            {
                var item = _toolTabItems.FirstOrDefault(candidate => candidate.Id == desired[index].Id);
                if (item is null)
                {
                    _toolTabItems.Insert(Math.Min(index, _toolTabItems.Count), new ToolTabItem(desired[index]));
                    continue;
                }
                item.Refresh();
                var current = _toolTabItems.IndexOf(item);
                if (current != index) _toolTabItems.Move(current, index);
            }
            foreach (var stale in _toolTabItems.Where(item => _toolTabs.Find(item.Id) is null).ToList())
                _toolTabItems.Remove(stale);
            ToolTabList.SelectedItem = _toolTabItems.FirstOrDefault(item => item.Id == _toolTabs.ActiveTabId);
            // A crowded strip scrolls horizontally and also offers the full list.
            ToolOverflowButton.Visibility = _toolTabs.Tabs.Count > 2 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _toolSyncing = false; }
    }

    /// <summary>Rebuilds the "all tabs" list so it always reflects the open instances.</summary>
    private void OnToolOverflowOpening(object? sender, object e)
    {
        ToolOverflowMenu.Items.Clear();
        foreach (var tab in _toolTabs.Tabs)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = tab.Tooltip + (tab.IsRunning ? "（运行中）" : tab.HasUnread ? "（未读）" : string.Empty),
                IsChecked = tab.Id == _toolTabs.ActiveTabId,
            };
            var id = tab.Id;
            item.Click += (_, _) =>
            {
                _toolTabs.Activate(id);
                SyncToolTabItems();
                UpdateToolContentVisibility();
                _ = ActivateActiveToolAsync();
            };
            ToolOverflowMenu.Items.Add(item);
        }
    }

    private void UpdateToolContentVisibility()
    {
        var active = _toolTabs.ActiveTab;
        // Every non-browser instance owns one lazily created content host; browser tabs
        // share the single persistent surface host so a live WebView2 is never reparented.
        if (active is not null && active.Kind != ToolTabKind.Browser && !_toolTabContent.ContainsKey(active.Id))
        {
            var created = CreateToolContent(active);
            _toolTabContent[active.Id] = created;
            // The tool home is declared in XAML and already a child of this host.
            if (created.Parent is null) ToolTabContentHost.Children.Add(created);
        }

        var browserActive = active?.Kind == ToolTabKind.Browser;
        BrowserToolBar.Visibility = browserActive ? Visibility.Visible : Visibility.Collapsed;
        BrowserSurfaceHostPanel.Visibility = browserActive ? Visibility.Visible : Visibility.Collapsed;
        ToolEmptyState.Visibility = _toolTabs.Tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (id, content) in _toolTabContent)
            content.Visibility = active is not null && id == active.Id ? Visibility.Visible : Visibility.Collapsed;
        UpdateToolStatus();
    }

    /// <summary>
    /// IMG11：把「可用 / 待接入」写到工具首页卡片上。文案来自
    /// <see cref="ToolAvailabilityCatalog"/>，与标签页的 Availability 同一判断，
    /// 所以不会出现「卡片说可用、点开说未接入」（或反过来）的自我矛盾。
    /// </summary>
    private void ApplyToolAvailabilityLabels()
    {
        OutputCardAvailabilityText.Text = ToolAvailabilityCatalog.CardLabel(ToolTabKind.Output);
        TerminalCardAvailabilityText.Text = ToolAvailabilityCatalog.CardLabel(ToolTabKind.Terminal);
        BrowserCardAvailabilityText.Text = ToolAvailabilityCatalog.CardLabel(ToolTabKind.Browser);
        ArtifactCardAvailabilityText.Text = ToolAvailabilityCatalog.CardLabel(ToolTabKind.Artifact);
        PanelCardAvailabilityText.Text = ToolAvailabilityCatalog.CardLabel(ToolTabKind.Panel);
    }

    private void UpdateToolStatus()
    {
        var active = _toolTabs.ActiveTab;
        if (active is null)
        {
            ToolStatusText.Text = string.Empty;
            return;
        }
        // Nav affordances mirror the active page instead of always looking live.
        var browserActive = active.Kind == ToolTabKind.Browser && _browser is not null;
        BrowserBackButton.IsEnabled = browserActive && _browser!.CanGoBack;
        BrowserForwardButton.IsEnabled = browserActive && _browser!.CanGoForward;
        ToolStatusText.Text = active.Kind switch
        {
            ToolTabKind.Browser when _browser is not null =>
                $"{_browser.ControlState} · {_browser.AgentTargetSummary} · {_browser.CurrentAgentSummary}",
            ToolTabKind.Browser => "Agent 浏览器运行时尚未就绪。",
            _ => DescribeToolStatus(active),
        };
    }

    private static string DescribeToolStatus(ToolTab tab) => tab.Availability switch
    {
        // IMG12：工具首页没有「当前工具」，正常时就不留一句泛泛的「就绪」——
        // 状态条只该在真有信息时说话，避免和 Chat 下缘的执行状态看起来像同一件事。
        ToolTabAvailability.Ready when tab.Kind == ToolTabKind.Home
            && string.IsNullOrWhiteSpace(tab.StatusText) => string.Empty,
        ToolTabAvailability.Ready => string.IsNullOrWhiteSpace(tab.StatusText) ? "就绪" : tab.StatusText,
        ToolTabAvailability.Deferred => DeferredNotice(tab.Kind),
        _ => string.IsNullOrWhiteSpace(tab.StatusText) ? "当前不可用。" : tab.StatusText,
    };

    /// <summary>
    /// Deferred capabilities state what is missing. No tab shows a fake prompt, fake
    /// output or a running marker for work that no component executes yet.
    /// </summary>
    private static string DeferredNotice(ToolTabKind kind) => kind switch
    {
        ToolTabKind.Terminal => "终端会话组件尚未接入：该标签页只保留实例与状态位，不执行命令，也不伪装运行状态。",
        ToolTabKind.Artifact => "制成品预览尚未接入 Core 成果接口：暂不能列出或渲染 Agent 生成的文档、图表与网页。",
        ToolTabKind.Panel => "交互面板尚未接入：Agent 生成表单与筛选器的提交通道未建立，因此不提供输入控件。",
        _ => "该能力尚未接入。",
    };

    private ToolTab OpenTool(ToolTabDescriptor descriptor, bool focus = true)
    {
        var tab = _toolTabs.Open(descriptor, focus);
        SetToolExpanded(true);
        SyncToolTabItems();
        UpdateToolContentVisibility();
        return tab;
    }

    private FrameworkElement CreateToolContent(ToolTab tab) => tab.Kind switch
    {
        // Home is markup-declared: the visual design stays in XAML.
        ToolTabKind.Home => ToolHomePanel,
        ToolTabKind.Output => CreateOutputContent(tab),
        _ => CreateDeferredContent(tab),
    };

    private FrameworkElement CreateDeferredContent(ToolTab tab)
    {
        var panel = new StackPanel
        {
            Spacing = 8,
            Padding = new Thickness(24),
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        panel.Children.Add(new TextBlock { Text = tab.Title, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(tab.Subtitle))
            panel.Children.Add(new TextBlock { Text = tab.Subtitle, Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = DeferredNotice(tab.Kind), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        return panel;
    }

    private FrameworkElement CreateOutputContent(ToolTab tab)
    {
        var panel = new StackPanel
        {
            Spacing = 8,
            Padding = new Thickness(20),
            MaxWidth = 700,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        panel.Children.Add(new TextBlock { Text = tab.Title, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = DescribeToolStatus(tab),
            Opacity = 0.7,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });

        var path = _outputPaths.GetValueOrDefault(tab.Id);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(OutputAction("用默认程序打开", (_, _) => OpenWithShell(path)));
        actions.Children.Add(OutputAction("复制路径", (_, _) => CopyToClipboard(path)));
        actions.Children.Add(OutputAction("打开其他输出文件…", OnOpenOutputTab));
        panel.Children.Add(actions);

        if (_outputPreviews.TryGetValue(tab.Id, out var preview))
        {
            panel.Children.Add(new TextBlock { Text = "内容预览", Opacity = 0.6, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });
            panel.Children.Add(new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                MaxHeight = 320,
                Child = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new TextBlock
                    {
                        Text = preview,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                    },
                },
            });
        }
        return panel;
    }

    private static Button OutputAction(string text, RoutedEventHandler handler)
    {
        var button = new Button { Content = text };
        button.Click += handler;
        return button;
    }

    private static void OpenWithShell(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static void CopyToClipboard(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch (Exception ex) { App.WriteDiagnostic(ex); }
    }

    private void OnToolTabSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_toolSyncing) return;
        if (ToolTabList.SelectedItem is not ToolTabItem item) return;
        if (_toolTabs.ActiveTabId == item.Id) return;
        _toolTabs.Activate(item.Id);
        SyncToolTabItems();
        UpdateToolContentVisibility();
        _ = ActivateActiveToolAsync();
    }

    private async Task ActivateActiveToolAsync()
    {
        var active = _toolTabs.ActiveTab;
        if (active?.Kind == ToolTabKind.Browser && _browser is not null)
        {
            var page = _browser.Tabs.FirstOrDefault(candidate => ToolTabIdentity.Browser(candidate.PageId.Value) == active.Id)?.PageId;
            if (page is { } pageId && _browser.ActivePageId != pageId)
                await RunAsync(ct => _browser.ActivateAsync(pageId, ct));
        }
        UpdateToolStatus();
    }

    private async void OnCloseToolTab(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ToolTabItem item }) return;
        await CloseToolTabAsync(item.Id);
    }

    private async Task CloseToolTabAsync(string tabId)
    {
        var tab = _toolTabs.Find(tabId);
        if (tab is null) return;
        if (tab.Kind == ToolTabKind.Browser && _browser is not null)
        {
            // Closing a browser tab closes its page, following the existing target rules;
            // the toolbar "收起" is a different operation and never does this.
            var page = _browser.Tabs.FirstOrDefault(candidate => ToolTabIdentity.Browser(candidate.PageId.Value) == tabId)?.PageId;
            if (page is { } pageId) await RunAsync(ct => _browser.ClosePageAsync(pageId, ct));
            return;
        }
        _toolTabs.Close(tabId);
        if (_toolTabContent.Remove(tabId, out var content)) ToolTabContentHost.Children.Remove(content);
        _outputPaths.Remove(tabId);
        _outputPreviews.Remove(tabId);
        SyncToolTabItems();
        UpdateToolContentVisibility();
    }

    private void OnOpenHomeTab(object sender, RoutedEventArgs e)
    {
        SetToolExpanded(true);
        _toolTabs.EnsureHome();
        SyncToolTabItems();
        UpdateToolContentVisibility();
    }

    private void OnOpenTerminalTab(object sender, RoutedEventArgs e) => OpenTerminalTool();

    private void OpenTerminalTool() => OpenTool(new ToolTabDescriptor
    {
        Id = ToolTabIdentity.Terminal(Guid.NewGuid().ToString("N")),
        Kind = ToolTabKind.Terminal,
        Title = "终端 · 新会话",
        Subtitle = "会话组件待接入",
        // IMG11：与工具首页卡片标签同源（ToolAvailabilityCatalog）
        Availability = ToolAvailabilityCatalog.For(ToolTabKind.Terminal),
    });

    private void OnOpenArtifactTab(object sender, RoutedEventArgs e) => OpenTool(new ToolTabDescriptor
    {
        Id = ToolTabIdentity.Artifact(Guid.NewGuid().ToString("N")),
        Kind = ToolTabKind.Artifact,
        Title = "预览 · 制成品",
        Subtitle = "等待 Core 成果接口",
        Availability = ToolAvailabilityCatalog.For(ToolTabKind.Artifact),
    });

    private void OnOpenPanelTab(object sender, RoutedEventArgs e) => OpenTool(new ToolTabDescriptor
    {
        Id = ToolTabIdentity.Panel(Guid.NewGuid().ToString("N")),
        Kind = ToolTabKind.Panel,
        Title = "面板 · 交互",
        Subtitle = "等待 Agent 交互通道",
        Availability = ToolAvailabilityCatalog.For(ToolTabKind.Panel),
    });

    private async void OnOpenOutputTab(object sender, RoutedEventArgs e) => await PickAndOpenOutputAsync();

    private async Task PickAndOpenOutputAsync()
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id)
            {
                CommitButtonText = "打开",
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add("*");
            var result = await picker.PickSingleFileAsync();
            if (result is null) return;
            await OpenOutputAsync(result.Path);
        }
        catch (Exception ex)
        {
            ToolStatusText.Text = ex.Message;
            App.WriteDiagnostic(ex);
        }
    }

    private async Task OpenOutputAsync(string path)
    {
        var full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        if (!info.Exists) throw new FileNotFoundException("文件不存在。", full);

        var id = ToolTabIdentity.Output(full);
        _outputPaths[id] = full;
        if (await TryReadPreviewAsync(full, info) is { } preview) _outputPreviews[id] = preview;

        var parent = Path.GetDirectoryName(full);
        var size = info.Length < 1024
            ? $"{info.Length} B"
            : info.Length < 1024 * 1024 ? $"{info.Length / 1024d:F1} KB" : $"{info.Length / 1048576d:F1} MB";
        var type = string.IsNullOrEmpty(info.Extension) ? "未知格式" : info.Extension.TrimStart('.').ToUpperInvariant();
        var note = _outputPreviews.ContainsKey(id) ? "已生成文本预览。" : "该格式不支持文本预览，可用「用默认程序打开」。";

        OpenTool(new ToolTabDescriptor
        {
            Id = id,
            Kind = ToolTabKind.Output,
            Title = $"文件 · {info.Name}",
            Subtitle = parent,
            ResourceKey = full,
            StatusText = $"{full}\n{type} · {size}。{note}",
        });
    }

    private static async Task<string?> TryReadPreviewAsync(string path, FileInfo info)
    {
        if (info.Length > MaxOutputPreviewBytes) return null;
        if (!PreviewableExtensions.Contains(info.Extension)) return null;
        try
        {
            var text = await File.ReadAllTextAsync(path);
            return text.Length == 0 ? "(空文件)" : text;
        }
        catch (Exception ex)
        {
            App.WriteDiagnostic(ex);
            return null;
        }
    }

    // ── Divider: drag, double-click reset, keyboard adjust ──────────────────────

    private void OnSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_toolExpanded) return;
        var point = e.GetCurrentPoint(WorkbenchPane);
        if (!point.Properties.IsLeftButtonPressed) return;
        _toolDragging = true;
        _toolDragOriginX = point.Position.X;
        _toolDragOriginWidth = _toolLayout.Resolve(WorkbenchPane.ActualWidth, true).ToolRegionWidth;
        ToolSplitter.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_toolDragging) return;
        var width = WorkbenchPane.ActualWidth;
        var tool = _toolDragOriginWidth - (e.GetCurrentPoint(WorkbenchPane).Position.X - _toolDragOriginX);
        _toolLayout = _toolLayout.WithToolWidth(width, tool);
        ApplyToolLayout();
        e.Handled = true;
    }

    private async void OnSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_toolDragging) return;
        _toolDragging = false;
        ToolSplitter.ReleasePointerCaptures();
        e.Handled = true;
        await PersistToolLayoutAsync();
    }

    private void OnSplitterPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_toolDragging) return;
        _toolDragging = false;
        _ = PersistToolLayoutAsync();
    }

    /// <summary>Hover affordance so the divider reads as draggable.</summary>
    private void OnSplitterPointerEntered(object sender, PointerRoutedEventArgs e) => SetSplitterHighlight(true);

    private void OnSplitterPointerExited(object sender, PointerRoutedEventArgs e) => SetSplitterHighlight(false);

    private void SetSplitterHighlight(bool highlighted)
    {
        if (highlighted)
        {
            _splitterIdleBrush ??= ToolSplitterLine.Fill;
            ToolSplitterLine.Width = 3;
            ToolSplitterLine.Fill = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        }
        else
        {
            ToolSplitterLine.Width = 1;
            if (_splitterIdleBrush is not null) ToolSplitterLine.Fill = _splitterIdleBrush;
        }
    }

    private async void OnSplitterDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // IMG05（§13.3）：「恢复默认分栏」按当前上下文取目标比例 —— 只有工具首页时
        // 回到启动器比例，否则回到通用默认。这是用户显式动作，所以照旧持久化
        // （「不悄悄复写配置」指的是不能在切换标签页等隐式路径上改写比例）。
        var launcherOnly = _toolTabs.ActiveTab is null or { Kind: ToolTabKind.Home };
        _toolLayout = _toolLayout.ResetWidth(ToolWorkspaceLayout.DefaultRatioFor(launcherOnly));
        ApplyToolLayout();
        e.Handled = true;
        await PersistToolLayoutAsync();
    }

    private async void OnSplitterKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var step = e.Key switch
        {
            Windows.System.VirtualKey.Left => -24d,
            Windows.System.VirtualKey.Right => 24d,
            _ => 0d,
        };
        if (step == 0) return;
        var width = WorkbenchPane.ActualWidth;
        var tool = _toolLayout.Resolve(width, true).ToolRegionWidth + step;
        _toolLayout = _toolLayout.WithToolWidth(width, tool);
        ApplyToolLayout();
        e.Handled = true;
        await PersistToolLayoutAsync();
    }

    // ── Layout preference persistence (desktop.json) ────────────────────────────

    private async Task LoadToolWorkspaceLayoutAsync()
    {
        try
        {
            var settings = await new FileDesktopBootstrapSettingsStore().LoadAsync(_lifetime.Token);
            // IMG05（§13.3）：只有「用户从未配置过」才用初始化默认。
            // 首次使用且只显示工具首页 → 启动器比例 0.32；已保存比例（哪怕正好是 0.45）
            // 一律原样恢复。标签页切换不会再动这个比例。
            var launcherOnly = _toolTabs.ActiveTab is null or { Kind: ToolTabKind.Home };
            var preference = settings.ToolWorkspace?.Normalize();
            _toolLayout = new ToolWorkspaceLayout
            {
                // 已保存比例优先（哪怕正好是默认值）；只有未配置才用初始化默认。
                WidthRatio = ToolWorkspaceLayout.ResolveLoadedRatio(
                    preference?.WidthRatio,
                    launcherOnly),
                AutoExpandOnActivity = preference?.AutoExpandOnActivity ?? false,
            };
            _toolActivity.AutoExpandOnActivity = _toolLayout.AutoExpandOnActivity;
            AutoExpandMenuItem.IsChecked = _toolLayout.AutoExpandOnActivity;
        }
        catch (Exception ex) { App.WriteDiagnostic(ex); }
        _toolLayoutLoaded = true;
    }

    private async void OnToggleAutoExpand(object sender, RoutedEventArgs e)
    {
        _toolLayout = _toolLayout with { AutoExpandOnActivity = AutoExpandMenuItem.IsChecked };
        _toolActivity.AutoExpandOnActivity = _toolLayout.AutoExpandOnActivity;
        await PersistToolLayoutAsync();
    }

    /// <summary>
    /// A round is the span of continuous execution: a lingering unread marker is not activity.
    /// </summary>
    private bool ToolActivityInFlight() =>
        _toolTabs.RunningCount > 0 || (_browser?.Tabs.Any(tab => tab.IsLoading) ?? false);

    private async Task PersistToolLayoutAsync()
    {
        if (!_toolLayoutLoaded) return;
        try
        {
            // Never create or rewrite a launcher settings file that would drop the DataRoot.
            if (!File.Exists(DesktopBootstrapPathProvider.GetFilePath())) return;
            var store = new FileDesktopBootstrapSettingsStore();
            var settings = await store.LoadAsync(_lifetime.Token);
            if (string.IsNullOrWhiteSpace(settings.DataRoot)) return;
            var preference = new DesktopToolWorkspaceSettings
            {
                WidthRatio = _toolLayout.WidthRatio,
                AutoExpandOnActivity = _toolLayout.AutoExpandOnActivity,
            }.Normalize();
            if (settings.ToolWorkspace is { } current
                && Math.Abs(current.WidthRatio - preference.WidthRatio) < 0.0005
                && current.AutoExpandOnActivity == preference.AutoExpandOnActivity) return;
            await store.SaveAsync(settings with { ToolWorkspace = preference }, _lifetime.Token);
        }
        catch (Exception ex) { App.WriteDiagnostic(ex); }
    }

    // ── Agent browser inside the tool workspace ─────────────────────────────────

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
                // SCROLL-001：首帧之前就给 WebView2 铺主题底色，消除加载闪白
                ApplyWorkbenchAppearance();
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
            var surfaces = new WinUiBrowserSurfaceHost(dispatcher, BrowserSurfaceHostPanel);
            _browserSurfaces = surfaces;
            var runtime = new WebView2BrowserRuntime(dispatcher, surfaces, dataRoot);
            _browserRuntime = runtime;
            var browser = new BrowserWorkspaceController(runtime, surfaces, dispatcher);
            try { await browser.InitializeAsync(dataRoot, ct); }
            catch { await browser.DisposeAsync(); throw; }
            _browser = browser;
            // 外观必须在浏览器表面就绪后立刻同步一次：此后新建的页面会自动继承。
            ApplyBrowserSurfaceAppearance();
            _coordinator.BridgeDispatcher.SetHandler(browser);
            _coordinator.BridgeDispatcher.ActivityChanged += async (_, e) =>
            { try { await browser.ApplyActivityAsync(e.Snapshot, _lifetime.Token); } catch (OperationCanceledException) { } catch (Exception ex) { App.WriteDiagnostic(ex); } };
            _coordinator.BridgeDispatcher.OperationStateChanged += async (_, e) =>
            { try { await browser.ApplyOperationStateAsync(e.Snapshot, _lifetime.Token); } catch (OperationCanceledException) { } catch (Exception ex) { App.WriteDiagnostic(ex); } };
            // Every live page becomes one instance tab; the old inner tab strip is gone so
            // the outer tab and the page identity stay 1:1.
            browser.Tabs.CollectionChanged += (_, _) => UiThread.Post(SyncBrowserTabs);
            browser.PropertyChanged += (_, _) => UiThread.Post(() =>
            {
                if (_closing) return;
                SyncBrowserTabs();
                UpdateToolStatus();
                if (browser.ActiveTab is { } tab) AddressBox.Text = tab.Url ?? "";
            });
            SyncBrowserTabs();
            return true;
        }
        finally { _browserGate.Release(); }
    }

    private void SyncBrowserTabs()
    {
        if (_closing) return;
        var browser = _browser;
        if (browser is null)
        {
            SyncToolTabItems();
            return;
        }

        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in browser.Tabs)
        {
            var id = ToolTabIdentity.Browser(page.PageId.Value);
            live.Add(id);
            var activePage = browser.ActivePageId == page.PageId;
            _toolTabs.Open(new ToolTabDescriptor
            {
                Id = id,
                Kind = ToolTabKind.Browser,
                Title = string.IsNullOrWhiteSpace(page.Title) ? "新标签页" : page.Title,
                Subtitle = "Agent 浏览器",
                ResourceKey = page.PageId.Value,
                StatusText = page.Url,
                IsRunning = page.IsLoading,
            }, focus: activePage && _toolExpanded);
        }
        foreach (var stale in _toolTabItems.Where(item => item.Model.Kind == ToolTabKind.Browser && !live.Contains(item.Id)).ToList())
            _toolTabs.Close(stale.Id);

        // Background Agent activity never steals the tab or keyboard focus: while the
        // workspace is closed it only raises a marker, and the panel opens by itself
        // solely when the user enabled that preference and did not just collapse it.
        if (!_toolExpanded && browser.ActivePageId is { } background)
        {
            _toolTabs.MarkUnread(ToolTabIdentity.Browser(background.Value));
            if (_toolActivity.ShouldAutoExpand()) SetToolExpanded(true);
        }

        _toolActivity.Observe(ToolActivityInFlight());
        SyncToolTabItems();
        UpdateToolContentVisibility();
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
    private async void OnBack(object sender, RoutedEventArgs e) { if (_browser?.ActivePageId is { } page) await RunAsync(ct => _browser.GoBackAsync(page, ct)); }
    private async void OnForward(object sender, RoutedEventArgs e) { if (_browser?.ActivePageId is { } page) await RunAsync(ct => _browser.GoForwardAsync(page, ct)); }
    private void OnCopyAddress(object sender, RoutedEventArgs e) => CopyToClipboard(AddressBox.Text.Trim());
    private void OnOpenAddressExternally(object sender, RoutedEventArgs e) => OpenExternal(AddressBox.Text.Trim());
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
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) { ToolStatusText.Text = "请输入 HTTP 或 HTTPS 地址。"; return; }
        await RunAsync(async ct => { var page = _browser.ActivePageId ?? await _browser.CreatePageAsync(null, true, ct); await _browser.NavigateAsync(page, uri, ct); });
    }

    private async void OnOpenBrowserTab(object sender, RoutedEventArgs e) => await OpenBrowserToolAsync();

    /// <summary>Opens the Agent browser tool, creating the first page when none exists.</summary>
    private async Task OpenBrowserToolAsync()
    {
        SetToolExpanded(true);
        if (_browser is null) { ToolStatusText.Text = "Agent 浏览器运行时尚未就绪。"; return; }
        if (_browser.Tabs.Count == 0)
        {
            // No live page yet: create one; the collection hook opens its instance tab.
            await RunAsync(async ct => { await _browser.CreatePageAsync("about:blank", true, ct); });
            return;
        }
        var page = _browser.ActivePageId ?? _browser.Tabs[0].PageId;
        _toolTabs.Activate(ToolTabIdentity.Browser(page.Value));
        SyncToolTabItems();
        UpdateToolContentVisibility();
        await ActivateActiveToolAsync();
    }

    // Shortcuts advertised on the tool home cards; kept in sync with the card hints.
    private async void OnAcceleratorBrowser(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await OpenBrowserToolAsync();
    }

    private async void OnAcceleratorOutput(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await PickAndOpenOutputAsync();
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
    private async void OnBrowseDataRoot(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(AppWindow.Id)
            {
                CommitButtonText = "选择文件夹",
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
            };
            var result = await picker.PickSingleFolderAsync();
            if (result is null) return;
            if (!Directory.Exists(result.Path))
                throw new DirectoryNotFoundException("请选择已存在的数据目录。");
            DataRootBox.Text = result.Path;
            SettingsStatus.Text = "已选择数据目录；保存启动设置后生效，切换目录需重开 Desktop。";
        }
        catch (Exception ex) { SettingsStatus.Text = ex.Message; }
    }

    private async void OnBrowseCorePath(object sender, RoutedEventArgs e)
    {
        try
        {
            // The picker needs the window to parent the dialog; unpackaged WinUI
            // cannot fall back to a packaged application view.
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id)
            {
                CommitButtonText = "选择",
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
            };
            picker.FileTypeFilter.Add(".exe");
            var result = await picker.PickSingleFileAsync();
            if (result is null) return;
            if (!string.Equals(Path.GetFileName(result.Path), "PuddingAgent.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请选择名为 PuddingAgent.exe 的文件。");
            CorePathBox.Text = result.Path;
            SettingsStatus.Text = "已选择 Core 可执行文件；保存启动设置后生效。";
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
        ApplyWorkbenchAppearance();
        if (!_loadingAppearance)
        {
            try { await _appearance.SaveAsync(new(new(), Root.RequestedTheme.ToString(), "Mica")); }
            catch (Exception ex) { App.WriteDiagnostic(ex); }
        }
    }

    /// <summary>
    /// SCROLL-001（设计规格 §14.4）：宿主外观的唯一落点。
    /// 启动恢复、下拉切换、系统主题变化、WebView2 首次创建都调用这里，
    /// 避免「启动 / 切换 / 系统主题」三条分支各写一份颜色。
    /// <para>
    /// 只作用于宿主表面与 UA 默认控件：WebView2 首帧背景 + 原生滚动条等 UA 控件配色。
    /// Web 应用主题仍由 Web 自身（ThemeMode / color-scheme）决定，不由此处替代。
    /// 「跟随系统」必须映射为 <see cref="CoreWebView2PreferredColorScheme.Auto"/>，
    /// 否则会把宿主选择冒充成系统偏好，破坏 Web 侧“跟随系统”语义。
    /// </para>
    /// </summary>
    private void ApplyWorkbenchAppearance()
    {
        var preference = WorkbenchAppearance.PreferenceFromComboIndex(ThemeBox.SelectedIndex);
        // 生效配色取 ActualTheme：RequestedTheme 为 Default 时即系统实际主题
        var scheme = WorkbenchAppearance.Resolve(preference, Root.ActualTheme == ElementTheme.Dark);

        // 三处宿主表面共用同一份判断：标题栏按钮、工具区里的浏览器表面、主工作台。
        // 前两处不依赖 _web，所以放在它的 null 早退之前。
        ApplyCaptionButtonColors(scheme);
        ApplyBrowserSurfaceAppearance();

        var web = _web;
        if (web is null) return;
        web.DefaultBackgroundColor = ToColor(WorkbenchAppearance.BackgroundArgb(scheme));

        if (web.CoreWebView2 is null) return;
        web.CoreWebView2.Profile.PreferredColorScheme = ToWebView2Scheme(
            WorkbenchAppearance.PreferredColorSchemeFor(preference));
    }

    /// <summary>
    /// 标题栏按钮配色。窗口用了 <c>ExtendsContentIntoTitleBar</c>，最小化/最大化/关闭
    /// **不再自动跟随应用主题**：系统深色 + 应用浅色时，白色字形画在浅色标题栏上，
    /// 用户实测"与白色高度相似、对比度无法分辨"。背景给透明让 Mica 透出。
    /// </summary>
    private void ApplyCaptionButtonColors(WorkbenchColorScheme scheme)
    {
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonForegroundColor = ToColor(WorkbenchAppearance.CaptionGlyphArgb(scheme));
        titleBar.ButtonInactiveForegroundColor = ToColor(
            WorkbenchAppearance.CaptionInactiveGlyphArgb(scheme));
        titleBar.ButtonHoverBackgroundColor = ToColor(
            WorkbenchAppearance.CaptionHoverBackgroundArgb(scheme));
        titleBar.ButtonHoverForegroundColor = ToColor(
            WorkbenchAppearance.CaptionGlyphArgb(scheme));
        titleBar.ButtonPressedBackgroundColor = ToColor(
            WorkbenchAppearance.CaptionPressedBackgroundArgb(scheme));
        titleBar.ButtonPressedForegroundColor = ToColor(
            WorkbenchAppearance.CaptionGlyphArgb(scheme));
    }

    /// <summary>
    /// 工具区浏览器表面的外观（预绘制背景 + UA 配色），与主工作台共用同一份
    /// <see cref="WorkbenchAppearance"/> 判断。浏览器页面里的 <c>about:blank</c>
    /// 在 UA 深色下画布是 Chromium 的 #121212，浅色宿主里会显得像一块黑屏。
    /// </summary>
    private void ApplyBrowserSurfaceAppearance()
    {
        if (_browserSurfaces is null) return;
        var preference = WorkbenchAppearance.PreferenceFromComboIndex(ThemeBox.SelectedIndex);
        var scheme = WorkbenchAppearance.Resolve(preference, Root.ActualTheme == ElementTheme.Dark);
        _browserSurfaces.ApplyAppearance(
            WorkbenchAppearance.BackgroundArgb(scheme),
            ToWebView2Scheme(WorkbenchAppearance.PreferredColorSchemeFor(preference)));
    }

    /// <summary>ARGB → UI 颜色（避免在 Foundation 里引入 UI 类型）。</summary>
    private static Windows.UI.Color ToColor(uint argb)
    {
        var (alpha, red, green, blue) = WorkbenchAppearance.ToArgbParts(argb);
        return Windows.UI.Color.FromArgb(alpha, red, green, blue);
    }

    /// <summary>Foundation 的配色意图 → WebView2 的 UA 配色（唯一映射点）。</summary>
    private static CoreWebView2PreferredColorScheme ToWebView2Scheme(
        WorkbenchPreferredColorScheme scheme) => scheme switch
        {
            WorkbenchPreferredColorScheme.Light => CoreWebView2PreferredColorScheme.Light,
            WorkbenchPreferredColorScheme.Dark => CoreWebView2PreferredColorScheme.Dark,
            _ => CoreWebView2PreferredColorScheme.Auto,
        };
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

            // ── Right tool workspace ─────────────────────────────────────────────
            Navigation.SelectedItem = Navigation.MenuItems[0];
            await Task.Delay(150);
            if (_toolExpanded) throw new Exception("Tool workspace must start collapsed");
            SetToolExpanded(true);
            await Task.Delay(150);
            if (ToolPanel.Visibility != Visibility.Visible) throw new Exception("Tool workspace did not expand");
            if (_toolTabs.Find(ToolTabIdentity.Home) is null) throw new Exception("Tool home tab missing after expand");
            if (_browser is null) throw new Exception("Agent browser controller was not mounted");

            var page = await _browser.CreatePageAsync(new Uri(_coordinator.CoreAddress!, "/health").ToString(), true, _lifetime.Token);
            await Task.Delay(200);
            var browserTabId = ToolTabIdentity.Browser(page.Value);
            if (_toolTabs.Find(browserTabId) is null) throw new Exception("Browser page did not open an instance tab");
            if (_toolTabs.ActiveTabId != browserTabId) throw new Exception("Focused browser page did not select its tab");
            await _browser.AssignAgentTargetAsync(page, _lifetime.Token);
            var other = await _browser.CreatePageAsync("about:blank", true, _lifetime.Token);
            await Task.Delay(150);
            if (_browser.AgentTargetPageId != page) throw new Exception("Visible tab changed Agent target");
            if (_toolTabs.Find(ToolTabIdentity.Browser(other.Value)) is null) throw new Exception("Second browser page did not open a tab");
            if (browserTabId == ToolTabIdentity.Browser(other.Value)) throw new Exception("Browser identities collided");
            await _browser.ActivateAsync(page, _lifetime.Token);
            await Task.Delay(150);
            if (_toolTabs.ActiveTabId != browserTabId) throw new Exception("Selecting a browser page did not focus its tab");
            await _browser.SetUserTakeoverAsync(true, _lifetime.Token);
            await _browser.SetUserTakeoverAsync(false, _lifetime.Token);
            checks.Add("instance tabs per browser page; Agent target stayed stable across tab switches");

            var smokeOutput = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "smoke-output.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(smokeOutput)!);
            await File.WriteAllTextAsync(smokeOutput, "pudding tool workspace smoke\n");
            await OpenOutputAsync(smokeOutput);
            var outputId = _toolTabs.Tabs.FirstOrDefault(tab => tab.Kind == ToolTabKind.Output)?.Id;
            if (outputId is null) throw new Exception("Output tab did not open");
            if (_toolTabs.Tabs.First(tab => tab.Id == outputId).Availability != ToolTabAvailability.Ready)
                throw new Exception("Output tab is not marked ready");
            await CloseToolTabAsync(outputId);
            if (_toolTabs.Find(outputId) is not null) throw new Exception("Output tab did not close");
            var terminalTab = OpenTool(new ToolTabDescriptor
            {
                Id = ToolTabIdentity.Terminal("smoke"),
                Kind = ToolTabKind.Terminal,
                Title = "终端 · 新会话",
                // IMG11：与工具首页卡片标签同源，smoke 因此顺带验证「真源 → 标签页」这条线
                Availability = ToolAvailabilityCatalog.For(ToolTabKind.Terminal),
            });
            if (terminalTab.Availability != ToolTabAvailability.Deferred || terminalTab.IsRunning)
                throw new Exception("Deferred terminal tab must not claim to be running");
            await CloseToolTabAsync(terminalTab.Id);
            checks.Add("output tab opens a real file; deferred tools report deferred instead of faking execution");

            // Divider: ratio-based width, double-click reset, minimums.
            var wide = WorkbenchPane.ActualWidth;
            var initial = _toolLayout.Resolve(wide, true).ToolRegionWidth;
            _toolLayout = _toolLayout.WithToolWidth(wide, initial + 120);
            ApplyToolLayout();
            if (Math.Abs(_toolLayout.Resolve(wide, true).ToolRegionWidth - (initial + 120)) > 1) throw new Exception("Divider drag did not resize the workspace");
            _toolLayout = _toolLayout.ResetWidth();
            ApplyToolLayout();
            if (Math.Abs(_toolLayout.WidthRatio - ToolWorkspaceLayout.DefaultWidthRatio) > 0.0001) throw new Exception("Double-click reset did not restore the default ratio");
            if (_toolLayout.Resolve(wide, true).ToolRegionWidth < ToolWorkspaceLayout.MinimumToolWidth - 1) throw new Exception("Tool workspace fell below its minimum width");
            checks.Add("divider drag persisted as a ratio; double-click restored the default split");

            // Narrow window: the workspace overlays the chat instead of squeezing it.
            AppWindow.Resize(new Windows.Graphics.SizeInt32(860, 640));
            await Task.Delay(300);
            if (!_toolLayout.Resolve(WorkbenchPane.ActualWidth, true).SpansContent) throw new Exception("Narrow window did not switch to the overlay layout");
            if (ToolOverlayScrim.Visibility != Visibility.Visible) throw new Exception("Overlay scrim missing in narrow layout");
            if (ToolPanel.ActualWidth <= 0 || ToolPanel.ActualWidth >= WorkbenchPane.ActualWidth)
                throw new Exception("Overlay tool panel must keep its resolved width instead of stretching");
            SetToolExpanded(false);
            await Task.Delay(120);
            if (ToolPanel.Visibility != Visibility.Collapsed || _browser.Tabs.Count != 2)
                throw new Exception("Collapsing must hide the panel without destroying browser pages");
            checks.Add("narrow window overlays the chat; collapse keeps pages alive");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 920));
            await Task.Delay(250);
            SetToolExpanded(true);
            await Task.Delay(150);
            await _browser.ClosePageAsync(other, _lifetime.Token);
            await _browser.ClosePageAsync(page, _lifetime.Token);
            await Task.Delay(150);
            if (_toolTabs.Tabs.Any(tab => tab.Kind == ToolTabKind.Browser)) throw new Exception("Closed browser pages left instance tabs behind");
            SetToolExpanded(false);
            checks.Add("closing a browser page removes its instance tab");

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
