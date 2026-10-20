using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using PuddingBrowser.Abstractions;
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
    /// <summary>接管/暂停的执行闸门集合（唯一写入点由浏览器工作区控制器驱动）。</summary>
    private BrowserAutomationGateSync? _automationGates;
    private DesktopTrayIcon? _tray;
    private Uri? _webOrigin;
    private long _webGeneration;
    private bool _ready, _closing, _closed;
    private bool _restartRequired, _loadingAppearance;
    private string? _stateError;

    // ── 启动状态面板（设计稿：temp/design/desktop-startup/design-spec.md）────────
    // 面板文案/步骤/按钮全部来自 Foundation.StartupPanelPresenter；这里只做
    // 「状态机 → 输入」「计时器 → 刷新」「按钮 → 协调器动作」三件事。
    private readonly DispatcherTimer _startupTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Stopwatch _startupWatch = new();
    private StartupPanelModel? _startupModel;
    private StartupPanelPhase _lastStartupPhase = StartupPanelPhase.Unknown;
    private bool _startupStateKnown, _startupStartPending, _startupCancelRequested, _rawErrorExpanded;
    private string? _startupNotice;
    private int _startupNoticeVersion;
    private TimeSpan? _coreStartupDuration;
    private CancellationTokenSource? _workbenchWatchdog;
    private readonly PuddingDesktop.Foundation.SkeletonSettingsStore _appearance = new(Path.Combine(App.StateRoot, "appearance"));

    // ── 能力通道接线所需的「宿主事实」（切片 C-3）─────────────────────────────
    // 这里只暴露**只有 Shell 知道**的事实；判定、装配与生命周期都在
    // DesktopApplicationCoordinator（组合根）与 Pudding.DesktopService（组件）里，
    // 本窗口不写任何能力通道的业务判断。
    internal WebView2BrowserRuntime? BrowserRuntime => _browserRuntime;

    internal BrowserWorkspaceController? BrowserWorkspace => _browser;

    /// <summary>接管/暂停的执行闸门集合；能力通道启用时由协调器再挂一个闸门。</summary>
    internal BrowserAutomationGateSync? AutomationGates => _automationGates;

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
        // 启动态（启动中/停止中/加载工作台/等待重启）需要 0.1 秒级刷新；
        // 只有这些相位才启动该计时器，静止状态不空转。
        _startupTimer.Tick += (_, _) => UpdateStartupPanel();
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
        _startupStateKnown = true;
        _stateError = e.Error;
        switch (e.Current)
        {
            case DesktopStartupState.CoreStarting:
                _coreStartupDuration = null;
                if (!_startupWatch.IsRunning) _startupWatch.Restart();
                break;
            case DesktopStartupState.CoreReady:
            case DesktopStartupState.WorkbenchReady:
                if (_startupWatch.IsRunning)
                {
                    _coreStartupDuration ??= _startupWatch.Elapsed;
                    _startupWatch.Stop();
                }
                break;
            default:
                _startupWatch.Stop();
                break;
        }
        if (e.Current is DesktopStartupState.WorkbenchReady or DesktopStartupState.WorkbenchFailed)
            CancelWorkbenchWatchdog();
        RefreshStatus();
        if (e.Current == DesktopStartupState.CoreReady && e.WorkbenchAddress is { } address)
            _ = LoadWorkbenchAsync(address);
        if (e.Current is DesktopStartupState.CoreStopped or DesktopStartupState.CoreFailed
            or DesktopStartupState.CoreStopping or DesktopStartupState.CoreRestartScheduled or DesktopStartupState.CoreCircuitOpen)
        {
            _webGeneration++;
            _webOrigin = null;
            CancelWorkbenchWatchdog();
            _web?.CoreWebView2?.Navigate("about:blank");
        }
    });

    private void RefreshStatus(string? error = null)
    {
        if (error is not null) _stateError = error;
        var runtime = _coordinator.RuntimeSnapshot;
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
        if (!string.IsNullOrWhiteSpace(_stateError ?? runtime.LastError)) RuntimeText.Text += $"\n{_stateError ?? runtime.LastError}";
        if (runtime.State != DesktopRuntimeState.Ready) UpdateMetrics(null);
        UpdateStartupPanel();
    }

    // ── 启动状态面板 ────────────────────────────────────────────────────────

    /// <summary>
    /// 把协调器的 13 态启动状态映射成面板相位。优化过的「立即进入启动中」
    /// （点击后到状态机回推 CoreStarting 之间的空窗）也在这里折叠，避免点击无反馈。
    /// </summary>
    private StartupPanelPhase ResolveStartupPhase()
    {
        if (!_startupStateKnown) return StartupPanelPhase.Unknown;
        if (_startupCancelRequested) return StartupPanelPhase.Cancelling;
        if (_startupStartPending && _coordinator.State is not (
                DesktopStartupState.CoreStarting or DesktopStartupState.CoreReady
                or DesktopStartupState.WebViewInitializing or DesktopStartupState.WorkbenchReady))
            return StartupPanelPhase.Starting;
        return _coordinator.State switch
        {
            DesktopStartupState.NeedsDataRoot => StartupPanelPhase.NeedsDataRoot,
            DesktopStartupState.InvalidConfiguration => StartupPanelPhase.InvalidConfiguration,
            DesktopStartupState.CoreStopped => StartupPanelPhase.Idle,
            DesktopStartupState.CoreStarting => StartupPanelPhase.Starting,
            DesktopStartupState.CoreStopping => StartupPanelPhase.Cancelling,
            DesktopStartupState.CoreReady or DesktopStartupState.WebViewInitializing => StartupPanelPhase.WorkbenchLoading,
            DesktopStartupState.WorkbenchReady => StartupPanelPhase.Ready,
            DesktopStartupState.WorkbenchFailed => StartupPanelPhase.WorkbenchFailed,
            DesktopStartupState.CoreFailed => StartupPanelPhase.CoreFailed,
            DesktopStartupState.CoreRestartScheduled => StartupPanelPhase.RestartScheduled,
            DesktopStartupState.CoreCircuitOpen => StartupPanelPhase.CircuitOpen,
            DesktopStartupState.DebugFailed => StartupPanelPhase.DebugFailed,
            _ => StartupPanelPhase.Unknown,
        };
    }

    private void UpdateStartupPanel()
    {
        if (!_ready) return;
        var phase = ResolveStartupPhase();
        var runtime = _coordinator.RuntimeSnapshot;
        var input = new StartupPanelInput
        {
            Phase = phase,
            Elapsed = _startupWatch.Elapsed,
            CoreStartupDuration = _coreStartupDuration,
            Error = _stateError ?? runtime.LastError,
            Notice = _startupNotice,
            CoreAddress = _coordinator.CoreAddress?.Authority,
            RestartAttempt = runtime.RestartAttemptsInWindow > 0 ? runtime.RestartAttemptsInWindow : null,
            NextRestartIn = runtime.NextRestartAt is { } next && next > DateTimeOffset.UtcNow
                ? next - DateTimeOffset.UtcNow
                : null,
        };
        // 真实阶段只从 Core 的诊断打点取：这些行已经在 CoreLogBuffer 里，
        // 解析/分组逻辑放在 Foundation（可单测），UI 不直接碰日志格式。
        if (phase == StartupPanelPhase.Starting)
        {
            var (activity, detail) = StartupPhaseActivity.Read(_coordinator.CoreLogBuffer.Snapshot());
            input = input with { Activity = activity, Detail = detail };
        }

        var model = StartupPanelPresenter.Present(input);
        _startupModel = model;
        ApplyStartupModel(model, phase);

        // 只有需要秒级刷新的相位才让精确计时器空转。
        var ticking = phase is StartupPanelPhase.Unknown or StartupPanelPhase.Starting
            or StartupPanelPhase.Cancelling or StartupPanelPhase.WorkbenchLoading
            or StartupPanelPhase.RestartScheduled;
        if (ticking && !_startupTimer.IsEnabled) _startupTimer.Start();
        else if (!ticking && _startupTimer.IsEnabled) _startupTimer.Stop();
    }

    private void ApplyStartupModel(StartupPanelModel model, StartupPanelPhase phase)
    {
        if (phase != _lastStartupPhase)
        {
            _rawErrorExpanded = false;
            _lastStartupPhase = phase;
        }
        LoadingPanel.Visibility = model.PanelVisible ? Visibility.Visible : Visibility.Collapsed;
        StartupTitle.Text = model.Title;
        StartupRing.Visibility = model.RingVisible ? Visibility.Visible : Visibility.Collapsed;
        var ringActive = model.RingVisible && model.PanelVisible;
        if (StartupRing.IsActive != ringActive) StartupRing.IsActive = ringActive;
        StartupErrorIcon.Visibility = model.ErrorIconVisible ? Visibility.Visible : Visibility.Collapsed;
        SetStartupText(StartupPhase, model.PhaseText, model.PhaseVisible);
        StartupStepsPanel.Visibility = model.StepsVisible ? Visibility.Visible : Visibility.Collapsed;
        if (model.StepsVisible)
        {
            for (var index = 0; index < 3; index++)
            {
                var step = index < model.Steps.Count
                    ? model.Steps[index]
                    : new StartupPanelStep(string.Empty, StartupStepState.Pending);
                ApplyStartupStep(index, step);
            }
        }
        SetStartupText(StartupMeta, model.MetaText, model.MetaVisible && !model.MetaIsWarning);
        SetStartupText(StartupMetaWarning, model.MetaText, model.MetaVisible && model.MetaIsWarning);
        SetStartupText(StartupDetail, model.DetailText, model.DetailVisible);
        SetStartupText(StartupError, model.ErrorText, model.ErrorVisible);
        SetStartupText(StartupHint, model.HintText, model.HintVisible);
        StartupPrimaryButton.Visibility = model.PrimaryVisible ? Visibility.Visible : Visibility.Collapsed;
        if (model.PrimaryVisible)
        {
            StartupPrimaryButton.Content = model.PrimaryText;
            // 取消是「撤退」而不是主 CTA：用次级按钮样式，避免把橙色主按钮的含义
            // 花在打断启动上（设计稿里取消就是默认按钮）。
            var styleKey = model.PrimaryAction == StartupPanelAction.CancelStartup
                ? "StartupSecondaryButtonStyle"
                : "StartupPrimaryButtonStyle";
            if (Root.Resources[styleKey] is Style style && !ReferenceEquals(StartupPrimaryButton.Style, style))
                StartupPrimaryButton.Style = style;
        }
        StartupRuntimeLink.Visibility = model.RuntimeLinkVisible ? Visibility.Visible : Visibility.Collapsed;
        if (model.RuntimeLinkVisible) StartupRuntimeLink.Content = model.RuntimeLinkText;
        StartupSettingsLink.Visibility = model.SettingsLinkVisible ? Visibility.Visible : Visibility.Collapsed;
        StartupRawErrorLink.Visibility = model.RawErrorLinkVisible ? Visibility.Visible : Visibility.Collapsed;
        if (model.RawErrorLinkVisible)
        {
            StartupRawErrorLink.Content = _rawErrorExpanded ? "收起错误详情" : "查看错误详情（原始输出）";
            StartupRawErrorText.Text = model.RawErrorText;
            StartupRawErrorText.Visibility = _rawErrorExpanded ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            StartupRawErrorText.Visibility = Visibility.Collapsed;
        }
        StatusText.Text = model.StatusBarText;
        SetStartupStatusDot(model.StatusBarTone);
    }

    private static void SetStartupText(TextBlock target, string text, bool visible)
    {
        target.Text = text;
        target.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 步骤的四种视觉在 XAML 里预置（颜色走 ThemeResource），这里只切 Visibility，
    /// 不在代码里拼主题色 —— 否则主题切换后代码写入的颜色不会重解析。
    /// </summary>
    private void ApplyStartupStep(int index, StartupPanelStep step)
    {
        var (pending, active, done, failed, mutedLabel, activeLabel, failedLabel) = index switch
        {
            0 => (StartupStep1Pending, StartupStep1Active, StartupStep1Done, StartupStep1Failed,
                  StartupStep1LabelMuted, StartupStep1LabelActive, StartupStep1LabelFailed),
            1 => (StartupStep2Pending, StartupStep2Active, StartupStep2Done, StartupStep2Failed,
                  StartupStep2LabelMuted, StartupStep2LabelActive, StartupStep2LabelFailed),
            _ => (StartupStep3Pending, StartupStep3Active, StartupStep3Done, StartupStep3Failed,
                  StartupStep3LabelMuted, StartupStep3LabelActive, StartupStep3LabelFailed),
        };
        pending.Visibility = step.State == StartupStepState.Pending ? Visibility.Visible : Visibility.Collapsed;
        active.Visibility = step.State == StartupStepState.Active ? Visibility.Visible : Visibility.Collapsed;
        done.Visibility = step.State == StartupStepState.Done ? Visibility.Visible : Visibility.Collapsed;
        failed.Visibility = step.State == StartupStepState.Failed ? Visibility.Visible : Visibility.Collapsed;
        mutedLabel.Text = activeLabel.Text = failedLabel.Text = step.Label;
        mutedLabel.Visibility = step.State is StartupStepState.Pending or StartupStepState.Done
            ? Visibility.Visible : Visibility.Collapsed;
        activeLabel.Visibility = step.State == StartupStepState.Active ? Visibility.Visible : Visibility.Collapsed;
        failedLabel.Visibility = step.State == StartupStepState.Failed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStartupStatusDot(StartupPanelTone tone)
    {
        StatusDotNeutral.Visibility = tone == StartupPanelTone.Normal ? Visibility.Visible : Visibility.Collapsed;
        StatusDotBusy.Visibility = tone == StartupPanelTone.Busy ? Visibility.Visible : Visibility.Collapsed;
        StatusDotWarning.Visibility = tone == StartupPanelTone.Warning ? Visibility.Visible : Visibility.Collapsed;
        StatusDotError.Visibility = tone == StartupPanelTone.Error ? Visibility.Visible : Visibility.Collapsed;
        StatusDotSuccess.Visibility = tone == StartupPanelTone.Success ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnStartupPrimary(object sender, RoutedEventArgs e)
    {
        switch (_startupModel?.PrimaryAction ?? StartupPanelAction.None)
        {
            case StartupPanelAction.StartCore:
                await StartCoreFromPanelAsync();
                break;
            case StartupPanelAction.CancelStartup:
                await CancelStartupAsync();
                break;
            case StartupPanelAction.Retry:
                await RetryFromPanelAsync();
                break;
            case StartupPanelAction.OpenSettings:
                NavigateToSettings();
                break;
            case StartupPanelAction.OpenRuntimeCenter:
                NavigateToRuntimeCenter();
                break;
        }
    }

    private void OnStartupRuntimeLink(object sender, RoutedEventArgs e) => NavigateToRuntimeCenter();

    private void OnStartupSettingsLink(object sender, RoutedEventArgs e) => NavigateToSettings();

    private void OnToggleStartupRawError(object sender, RoutedEventArgs e)
    {
        _rawErrorExpanded = !_rawErrorExpanded;
        UpdateStartupPanel();
    }

    private async Task StartCoreFromPanelAsync()
    {
        if (_restartRequired)
        {
            RefreshStatus("数据目录已改变，请退出并重开 Desktop。");
            return;
        }
        // 先乐观进入「启动中」：从点击到状态机回推 CoreStarting 之间有断言空窗，
        // 不这样做用户会看到按钮点了没反应（旧实现的实际问题）。
        _startupStartPending = true;
        _startupNotice = null;
        _startupWatch.Restart();
        _coreStartupDuration = null;
        if (!_startupTimer.IsEnabled) _startupTimer.Start();
        UpdateStartupPanel();
        try { await RunAsync(_coordinator.StartCoreAsync); }
        finally
        {
            _startupStartPending = false;
            UpdateStartupPanel();
        }
    }

    private async Task CancelStartupAsync()
    {
        if (_startupCancelRequested) return;
        _startupCancelRequested = true;
        if (!_startupTimer.IsEnabled) _startupTimer.Start();
        UpdateStartupPanel();
        try { await RunAsync(_coordinator.StopCoreAsync); }
        finally { _startupCancelRequested = false; }
        _startupNotice = "已取消启动。";
        _startupNoticeVersion++;
        UpdateStartupPanel();
        _ = ClearStartupNoticeAsync(_startupNoticeVersion);
    }

    private async Task ClearStartupNoticeAsync(int version)
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (version != _startupNoticeVersion || !_ready) return;
        _startupNotice = null;
        UpdateStartupPanel();
    }

    private async Task RetryFromPanelAsync()
    {
        if (ResolveStartupPhase() == StartupPanelPhase.WorkbenchFailed
            || _coordinator.RuntimeSnapshot.State == DesktopRuntimeState.Ready)
        {
            await ReloadWorkbenchAsync();
            return;
        }
        await StartCoreFromPanelAsync();
    }

    /// <summary>
    /// 重新加载 Web 工作台（不是重启 Core）。旧 OnStart 的 Ready 分支直接
    /// Close/清空 WebView，绕过 _webGate；所有重载路径统一走这里，避免与
    /// LoadWorkbenchAsync 并发创建 WebView2。
    /// </summary>
    private async Task ReloadWorkbenchAsync()
    {
        if (_coordinator.WorkbenchAddress is not { } address)
        {
            RefreshStatus("Core 尚未就绪，无法加载工作台。");
            return;
        }
        try
        {
            await _webGate.WaitAsync(_lifetime.Token);
            try
            {
                _web?.Close();
                _web = null;
                _webOrigin = null;
                WorkbenchHost.Children.Clear();
            }
            finally { _webGate.Release(); }
            await LoadWorkbenchAsync(address);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { RefreshStatus(ex.Message); }
    }

    private void NavigateToRuntimeCenter()
    {
        if (ReferenceEquals(Navigation.SelectedItem, RuntimeNavigationItem))
        {
            RefreshRuntimePanel();
            return;
        }
        Navigation.SelectedItem = RuntimeNavigationItem;
    }

    private void NavigateToSettings()
    {
        if (ReferenceEquals(Navigation.SelectedItem, SettingsNavigationItem))
        {
            _ = LoadSettingsAsync();
            return;
        }
        Navigation.SelectedItem = SettingsNavigationItem;
    }

    /// <summary>
    /// WebView2 阶段看门狗：NavigationCompleted 成功/失败都会取消；20 秒仍未完成
    /// 就把状态降级为 WorkbenchFailed（旧实现没有超时，面板会无限期停在加载态）。
    /// </summary>
    private void StartWorkbenchWatchdog()
    {
        CancelWorkbenchWatchdog();
        var watchdog = new CancellationTokenSource();
        _workbenchWatchdog = watchdog;
        _ = WatchWorkbenchAsync(watchdog);
    }

    private void CancelWorkbenchWatchdog()
    {
        var watchdog = _workbenchWatchdog;
        if (watchdog is null) return;
        _workbenchWatchdog = null;
        try { watchdog.Cancel(); } catch (ObjectDisposedException) { }
        watchdog.Dispose();
    }

    private async Task WatchWorkbenchAsync(CancellationTokenSource watchdog)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), watchdog.Token); }
        catch (OperationCanceledException) { return; }
        if (_closing || !ReferenceEquals(_workbenchWatchdog, watchdog)) return;
        UiThread.Post(() =>
        {
            if (_coordinator.State != DesktopStartupState.WebViewInitializing) return;
            _webOrigin = null;
            _stateError = "Web 工作台在 20 秒内没有加载完成。";
            _coordinator.NotifyWorkbenchFailed(_stateError);
            UpdateStartupPanel();
        });
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
        // 口径必须与任务管理器一致：任务管理器「内存」列是专用工作集（私有页），
        // 而 WorkingSet64 含共享页（映像/映射文件/共享 JIT 代码），同一时刻可高出一倍。
        // 专用工作集不可读时才回退到工作集，并在副标题写明当前用的是哪个口径。
        var memory = CoreMemoryDisplay.Format(metrics);
        RuntimeMemoryText.Text = memory.Value;
        RuntimeMemoryDetailText.Text = memory.Detail;
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
        ToolTabKind.Panel => "交互面板尚未接入：这里用来展示 Agent 生成的 Markdown、设计图纸等媒体资源；渲染通道还没建立，因此暂时不提供内容区。",
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
        ToolTabKind.Document => CreateDocumentContent(tab),
        ToolTabKind.Panel => CreatePanelContent(tab),
        _ => CreateDeferredContent(tab),
    };

    /// <summary>
    /// 文档预览标签页：Shell **只负责把 WebView2 指到 /admin/doc-preview**。
    ///
    /// 内容不由 Shell 读：路径校验、根目录、大小上限与 Markdown 渲染全在 Core 侧
    /// （DocumentPreviewPathPolicy + doc-preview.tsx）。这样 Shell 不需要知道工作区在哪，
    /// 也不必为每种文档格式各写一个渲染器。
    /// </summary>
    private FrameworkElement CreateDocumentContent(ToolTab tab)
    {
        var web = new Microsoft.UI.Xaml.Controls.WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        var host = new Grid();
        host.Children.Add(web);

        // 初始化是异步的，而 CreateToolContent 必须同步返回 ⇒ 先返回控件，再后台接线。
        _ = LoadDocumentTabAsync(web, tab);
        return host;
    }

    private async Task LoadDocumentTabAsync(Microsoft.UI.Xaml.Controls.WebView2 web, ToolTab tab)
    {
        try
        {
            _webEnvironment ??= await CoreWebView2Environment.CreateWithOptionsAsync(null,
                Path.Combine(_coordinator.DataRoot!, "browser", "workbench", "user-data"), null);
            await web.EnsureCoreWebView2Async(_webEnvironment);

            if (_coordinator.WorkbenchAddress is not { } address)
            {
                ShowDocumentTabMessage(web, "工作台尚未就绪，文档预览暂不可用。");
                return;
            }

            var path = tab.ResourceKey ?? string.Empty;
            // Shell 目前只跑在默认工作区；多工作区落地时由 tab.Subtitle 承载。
            var workspace = "default";
            var url = new Uri(
                address,
                $"/admin/doc-preview?path={Uri.EscapeDataString(path)}&ws={Uri.EscapeDataString(workspace)}");
            web.CoreWebView2.Navigate(url.ToString());
        }
        catch (Exception ex)
        {
            // 失败要给可读原因：空白页会被误读成“文件是空的”。
            ShowDocumentTabMessage(web, $"无法打开文档预览：{ex.Message}");
        }
    }

    private static void ShowDocumentTabMessage(Microsoft.UI.Xaml.Controls.WebView2 web, string message)
    {
        var html = "<html><body style=\"font-family:'Segoe UI',sans-serif;padding:24px;opacity:.8;\">"
            + System.Net.WebUtility.HtmlEncode(message)
            + "</body></html>";
        web.CoreWebView2?.NavigateToString(html);
    }

    /// <summary>
    /// 交互面板：**可交互的媒体画布**（用户 2026-10-20 的定位：展示 Markdown、设计图纸等资源）。
    ///
    /// 本轮只做载体与交互骨架：WebView2 渲染内置示例，「下载到本地」真实可用；
    /// 「添加到对话」需要 Core 侧的对话写入通道，暂未接入（按钮存在但如实提示，不假装成功）。
    /// Agent 推送内容的通道（panel.render）是下一步。
    /// </summary>
    private FrameworkElement CreatePanelContent(ToolTab tab)
    {
        if (tab.Kind != ToolTabKind.Panel)
        {
            throw new ArgumentException("Panel content requested for a non-panel tab.", nameof(tab));
        }

        var host = new Grid();
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 8, 6),
        };
        toolbar.Children.Add(PanelAction("添加到对话", (_, _) =>
            ToolStatusText.Text = "「添加到对话」需要 Core 的对话写入通道，尚未接入。"));
        toolbar.Children.Add(PanelAction("下载到本地", async (_, _) => await ExportPanelAsync()));
        Grid.SetRow(toolbar, 0);
        host.Children.Add(toolbar);

        var web = new Microsoft.UI.Xaml.Controls.WebView2
        {
            DefaultBackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Grid.SetRow(web, 1);
        host.Children.Add(web);

        _ = LoadPanelAsync(web);
        return host;
    }

    private async Task LoadPanelAsync(Microsoft.UI.Xaml.Controls.WebView2 web)
    {
        try
        {
            _webEnvironment ??= await CoreWebView2Environment.CreateWithOptionsAsync(null,
                Path.Combine(_coordinator.DataRoot!, "browser", "workbench", "user-data"), null);
            await web.EnsureCoreWebView2Async(_webEnvironment);
            web.CoreWebView2.NavigateToString(PanelSampleHtml);
        }
        catch (Exception ex)
        {
            // 画布起不来时不能留空白：空白会被当成“内容就是这个样子”。
            ToolStatusText.Text = $"面板画布不可用：{ex.Message}";
        }
    }

    /// <summary>导出当前画布。写固定目录而不是弹选择器：本轮先把链路做通，选择器留待与其它导出入口统一。</summary>
    private async Task ExportPanelAsync()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Pudding");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"panel-{DateTime.Now:yyyyMMdd-HHmmss}.html");
            await File.WriteAllTextAsync(file, PanelSampleHtml);
            ToolStatusText.Text = $"已导出：{file}";
        }
        catch (Exception ex)
        {
            ToolStatusText.Text = $"导出失败：{ex.Message}";
        }
    }

    private static Button PanelAction(string text, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 12,
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
        };
        button.Click += onClick;
        return button;
    }

    /// <summary>
    /// 内置示例画布：验证 WebView2 在这个位置能正常渲染、工具栏能操作。
    /// 真正的内容将由 Agent 通过 panel.render 推送，这里只是一张能看懂的占位示例。
    /// </summary>
    private const string PanelSampleHtml = """
        <!doctype html>
        <html><head><meta charset="utf-8">
        <style>
          html,body{margin:0;height:100%;font-family:'Segoe UI','Microsoft YaHei',sans-serif;background:#fff;}
          .wrap{height:100%;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:14px;padding:24px;box-sizing:border-box;}
          .legend{display:flex;gap:20px;font-size:12px;color:#555;}
          .legend i{display:inline-block;width:14px;height:3px;margin-right:6px;vertical-align:middle;border-radius:2px;}
          .canvas{width:100%;max-width:720px;}
          .hint{font-size:12px;color:#888;text-align:center;}
        </style></head>
        <body>
        <div class="wrap">
          <div class="legend">
            <span><i style="background:#2f6fd0"></i>车架系统</span>
            <span><i style="background:#d98324"></i>传动系统</span>
            <span><i style="background:#5a5a5a"></i>车轮与操控</span>
          </div>
          <svg class="canvas" viewBox="0 0 720 400" xmlns="http://www.w3.org/2000/svg">
            <circle cx="180" cy="270" r="88" fill="none" stroke="#5a5a5a" stroke-width="6"/>
            <circle cx="540" cy="270" r="88" fill="none" stroke="#5a5a5a" stroke-width="6"/>
            <path d="M180 270 L330 270 L400 130 L540 270" fill="none" stroke="#2f6fd0" stroke-width="7" stroke-linecap="round" stroke-linejoin="round"/>
            <path d="M330 270 L400 130" fill="none" stroke="#2f6fd0" stroke-width="7" stroke-linecap="round"/>
            <path d="M318 126 L382 126" stroke="#5a5a5a" stroke-width="8" stroke-linecap="round"/>
            <path d="M470 120 L532 120" stroke="#5a5a5a" stroke-width="8" stroke-linecap="round"/>
            <circle cx="330" cy="270" r="26" fill="none" stroke="#d98324" stroke-width="5"/>
            <circle cx="180" cy="270" r="12" fill="none" stroke="#d98324" stroke-width="5"/>
            <path d="M180 258 L330 244 M180 282 L330 296" stroke="#d98324" stroke-width="4"/>
            <text x="118" y="382" font-size="14" fill="#333">后轮</text>
            <text x="534" y="382" font-size="14" fill="#333">前轮</text>
            <text x="344" y="192" font-size="14" fill="#2f6fd0">车架</text>
            <text x="300" y="104" font-size="14" fill="#333">座垫</text>
            <text x="478" y="98" font-size="14" fill="#333">车把</text>
            <text x="286" y="330" font-size="14" fill="#d98324">链轮与曲柄</text>
          </svg>
          <div class="hint">Panel 画布示例：Agent 推送内容的能力通道（panel.render）尚未接入，当前展示内置示例。</div>
        </div>
        </body></html>
        """;

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

    private void OnOpenDocumentTab(object sender, RoutedEventArgs e) => OpenDocumentTool(string.Empty);

    /// <summary>
    /// 打开（或激活）文档预览标签。同路径幂等：<see cref="ToolTabIdentity.Document"/> 已把
    /// 大小写与分隔符归一，重复打开同一文件只会切到已有标签而不是又开一个。
    /// </summary>
    private void OpenDocumentTool(string path) => OpenTool(new ToolTabDescriptor
    {
        Id = ToolTabIdentity.Document(path),
        Kind = ToolTabKind.Document,
        Title = string.IsNullOrWhiteSpace(path) ? "文档预览" : Path.GetFileName(path),
        ResourceKey = path,
        Availability = ToolAvailabilityCatalog.For(ToolTabKind.Document),
    });

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
        // 画布是**单例**：Agent 推送内容时需要一个固定目标，重复点击只应激活已有标签。
        // 用 Guid 会让每点一次多一个画布（各自一份 WebView2），也说不清内容该送到哪个。
        Id = ToolTabIdentity.Panel("main"),
        Kind = ToolTabKind.Panel,
        Title = "交互面板",
        Subtitle = "内置示例画布",
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
                    CancelWorkbenchWatchdog();
                    if (e.IsSuccess)
                    {
                        LoadingPanel.Visibility = Visibility.Collapsed;
                        _coordinator.NotifyWorkbenchReady();
                    }
                    else
                    {
                        _webOrigin = null;
                        _stateError = $"Web 工作台导航失败：{e.WebErrorStatus}";
                        LoadingPanel.Visibility = Visibility.Visible;
                        _coordinator.NotifyWorkbenchFailed(_stateError);
                    }
                    UpdateStartupPanel();
                };
                _web.CoreWebView2.ProcessFailed += (_, e) =>
                {
                    if (_closing || !ReferenceEquals(_web, currentWeb)) return;
                    CancelWorkbenchWatchdog();
                    _webOrigin = null;
                    _stateError = $"WebView2 进程失败：{e.ProcessFailedKind}";
                    LoadingPanel.Visibility = Visibility.Visible;
                    _coordinator.NotifyWorkbenchFailed(_stateError);
                    UpdateStartupPanel();
                };
            }
            if (generation != _webGeneration || _closing) return;
            _webOrigin = address;
            _web.CoreWebView2.Navigate(new Uri(address, "/admin/").ToString());
            StartWorkbenchWatchdog();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _webOrigin = null;
            App.WriteDiagnostic(ex);
            CancelWorkbenchWatchdog();
            _stateError = $"Web 工作台初始化失败：{ex.Message}";
            LoadingPanel.Visibility = Visibility.Visible;
            _coordinator.NotifyWorkbenchFailed(_stateError);
            UpdateStartupPanel();
        }
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
            // 接管/暂停的**唯一写入点**：控制状态推给所有执行闸门。
            // 这里先挂 Bridge dispatcher（默认传输路径）——不挂就等于「人工接管」按钮对 Agent 无效
            // （审计 2026-10-08 结论 #1）。能力通道启用后由协调器再挂它自己的闸门。
            _automationGates = new BrowserAutomationGateSync();
            _automationGates.Register(_coordinator.BridgeDispatcher);
            // 读取基线（§145）与生命周期端口**同一份**：页面关闭时会随授权/租约一起清。
            var browser = new BrowserWorkspaceController(
                runtime, surfaces, dispatcher, _automationGates,
                _coordinator.BridgeDispatcher.Lifecycle.Readings,
            // 桥侧截图复用能力通道**同一条执行路径**（延迟求值：通道可能尚未启动）。
            () => _coordinator.CapabilityExecutor,
            // 截图/原生输入需要真实渲染：面板折叠时由 Agent 调用触发自动展开（见 PresentBrowserPageAsync）。
            PresentBrowserPageAsync);
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
            // ADR-097 D3：交页后 UI 必须能区分「等待会话确认 / 已交出 / 已失效」，不能点完按钮就显示已交出。
            _coordinator.BridgeDispatcher.SessionChanged += async (_, e) =>
            { try { await browser.ApplySessionStatusAsync(e.Status, _lifetime.Token); } catch (OperationCanceledException) { } catch (Exception ex) { App.WriteDiagnostic(ex); } };
            await browser.ApplySessionStatusAsync(
                _coordinator.BridgeDispatcher.Lifecycle.CaptureSessionStatus(), _lifetime.Token);
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

    /// <summary>
    /// Agent 请求「把这一页真正显示出来」—— 截图与原生输入的前提。
    ///
    /// 折叠的工作区会让 WebView2 表面 <c>IsVisible=false</c>，截图必然拿不到合成结果。
    /// 用户手动折叠只应抑制「后台活动自动弹面板」，不能让需要真实渲染的工具调用必然失败
    /// （否则浏览器工具就变成「面板打开才可用」）。展开后必须强制一次布局：
    /// 表面要有非零尺寸才能出图。
    /// </summary>
    private async Task PresentBrowserPageAsync(PageId pageId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await UiThread.InvokeAsync(() =>
        {
            SetToolExpanded(true);
            _toolTabs.Activate(ToolTabIdentity.Browser(pageId.Value));
            SyncToolTabItems();
            UpdateToolContentVisibility();
            ApplyBrowserSurfaceAppearance();
            ToolPanel.UpdateLayout();
            return Task.CompletedTask;
        });
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
        if (_coordinator.RuntimeSnapshot.State == DesktopRuntimeState.Ready && _coordinator.WorkbenchAddress is not null)
            await ReloadWorkbenchAsync();
        else
            await StartCoreFromPanelAsync();
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
        _startupTimer.Stop();
        CancelWorkbenchWatchdog();
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
    /// <summary>
    /// Debug smoke 证据：把当前 XAML 树保存成 PNG。RenderTargetBitmap 不含 Mica/
    /// 原生标题栏（见 Desktop-Shell-Recovery 报告），只作人工复核，不做像素门禁。
    /// </summary>
    private static async Task SavePngAsync(Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap capture, string path)
    {
        var pixels = await capture.GetPixelsAsync();
        await File.WriteAllBytesAsync(path, []);
        var imageFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        using var stream = await imageFile.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            (uint)capture.PixelWidth, (uint)capture.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
    }

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

            // 启动中面板：在 WorkbenchReady 之前抓一张，并断言面板确实在报进度。
            // 这是本面板唯一的自动化门禁（WinUI MainWindow 无法进单测）。
            var startupObserved = false;
            using (var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            {
                while (!startupTimeout.IsCancellationRequested)
                {
                    if (_coordinator.State is DesktopStartupState.CoreStarting
                        or DesktopStartupState.CoreReady or DesktopStartupState.WebViewInitializing)
                    {
                        startupObserved = true;
                        break;
                    }
                    if (_coordinator.State == DesktopStartupState.WorkbenchReady) break;
                    try { await Task.Delay(50, startupTimeout.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }
            if (!startupObserved) throw new Exception("Startup panel could not be observed before WorkbenchReady");
            await Task.Delay(250);
            if (LoadingPanel.Visibility != Visibility.Visible)
                throw new Exception("Startup panel must be visible while Core/WebView starts");
            if (!StartupRing.IsActive)
                throw new Exception("Startup progress ring must be active while starting");
            var startupCapture = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await startupCapture.RenderAsync(Root);
            await SavePngAsync(startupCapture, Path.ChangeExtension(path, ".startup.png"));
            checks.Add("startup panel visible with active progress ring before the workbench is ready");

            await WaitReady();
            if (LoadingPanel.Visibility != Visibility.Collapsed)
                throw new Exception("Startup panel must collapse once WorkbenchReady");
            if (StartupRing.IsActive)
                throw new Exception("Startup progress ring must stop once WorkbenchReady");
            checks.Add("startup panel collapsed after the workbench loaded");
            var pid = _coordinator.RuntimeSnapshot.Session!.ProcessId;
            if (pid == Environment.ProcessId) throw new Exception("Core must be a separate process");
            pids.Add(pid); checks.Add("independent Core ready and Web workbench loaded");
            if (_coordinator.RuntimeSnapshot.Session.CapabilityEndpoint is not null)
            {
                var startupLog = _coordinator.CoreLogBuffer.GetTail(300);
                if (!startupLog.Contains("capability channel preflight ok", StringComparison.Ordinal)
                    || !startupLog.Contains("能力端点已监听", StringComparison.Ordinal)
                    || startupLog.Contains('\uFFFD'))
                    throw new Exception("Capability preflight or redirected UTF-8 startup logs failed");
                checks.Add("named-pipe capability preflight passed; redirected Chinese logs decoded as UTF-8");
            }
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
            var imagePath = Path.ChangeExtension(path, ".png");
            await SavePngAsync(capture, imagePath);

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
