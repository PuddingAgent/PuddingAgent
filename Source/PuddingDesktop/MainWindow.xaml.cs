using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using PuddingBrowser.Abstractions;
using PuddingBrowser.WebView2;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
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
        // 折叠态必须在聊天区右侧留出与入口把手等宽的空槽：外壳控件不得浮在工作台网页上，
        // 否则会盖住前端 header 最右端的「字号 / 头像」（旧版就是这样重叠的）。
        // 展开态不留槽位，工具面板自己占列。
        WorkbenchHost.Margin = new Thickness(0, 0, allocation.IsVisible ? 0 : ToolEntryGutterWidth, 0);
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

    /// <summary>
    /// 折叠态入口把手占用的右侧槽宽。与 XAML 里 ToolEntryButton 的 Width + Margin 对齐：
    /// 30 + 5（右外边距）+ 1（容差）= 36。改其一时必须同步改另一个。
    /// </summary>
    private const double ToolEntryGutterWidth = 36;

    private void UpdateToolEntryButton()
    {
        var summary = _toolTabs.DescribeActivity();
        // 竖排把手只放图标 + 活动点；摘要文字移到 tooltip —— 横向 pill 放回右上角就会
        // 重新压到前端 header，而它正是这次要修的东西。
        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(new FontIcon { Glyph = "\uE76B", FontSize = 11 });
        if (summary.Length > 0)
            content.Children.Add(new Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 213, 159, 97)),
            });
        ToolEntryButton.Content = content;
        ToolTipService.SetToolTip(ToolEntryButton,
            summary.Length == 0 ? "打开工具区" : $"打开工具区 · {summary}");
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
        // ToolTabKind.Artifact 已不再走这里：制成品有自己的列表（CreateCraftLibrary）。
        // 留着一条「尚未接入」的文案会是假话——它会让人以为制品库还是空的。
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
        ToolTabKind.Artifact => CreateCraftLibrary(),
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
    /// 内容真源在 Core（<c>/api/panels/{id}</c>），Shell 定时拉取并按 revision 重绘；
    /// 能力通道推送是快路径。「下载到本地」导出的是画布**当前内容**。
    /// 「添加到对话」需要 Core 侧的对话写入通道，仍未接入（按钮存在但如实提示，不假装成功）。
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

        // 工具与标注是**开关量**，用 ToggleButton：选中态是 WinUI 自带的强调色，
        // 不需要自己画一套。用普通 Button 时用户只能靠状态栏那行字猜当前是哪个工具。
        var annotationButton = PanelToggle("标注", async (_, _) => await TogglePanelAnnotationAsync(tab));
        var toolButtons = new Dictionary<string, Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>(
            StringComparer.Ordinal);
        var undoButton = PanelAction("撤销", async (_, _) => await UndoPanelAsync(tab));

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 6),
        };
        toolbar.Children.Add(annotationButton);
        toolbar.Children.Add(PanelDivider());
        // 工具组常驻显示：先选工具再开标注，比先进标注再切更顺手。
        foreach (var (id, label) in PanelTools)
        {
            var button = PanelToggle(label, async (_, _) => await SetPanelToolAsync(tab, id));
            toolButtons[id] = button;
            toolbar.Children.Add(button);
        }
        toolbar.Children.Add(PanelDivider());
        toolbar.Children.Add(undoButton);
        toolbar.Children.Add(PanelDivider());
        toolbar.Children.Add(PanelAction("保存到制品库", async (_, _) => await SavePanelToCraftsAsync(tab), accent: true));
        toolbar.Children.Add(PanelAction("添加到对话", async (_, _) => await AddPanelToChatAsync(tab)));
        toolbar.Children.Add(PanelAction("下载到本地", async (_, _) => await ExportPanelAsync(tab)));
        _panelToolbars[tab.Id] = new PanelToolbar(annotationButton, toolButtons, undoButton);
        Grid.SetRow(toolbar, 0);
        host.Children.Add(toolbar);

        var web = new Microsoft.UI.Xaml.Controls.WebView2
        {
            DefaultBackgroundColor = ToColor(PanelCanvasTheme.PageArgb(CurrentWorkbenchScheme())),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Grid.SetRow(web, 1);
        host.Children.Add(web);

        _ = LoadPanelAsync(web, tab);
        return host;
    }

    /// <summary>每个画布 tab 的 WebView2（按 tab id）；Agent 重复推送时用它直接覆盖内容。</summary>
    private readonly Dictionary<string, Microsoft.UI.Xaml.Controls.WebView2> _panelCanvases = new(StringComparer.Ordinal);

    /// <summary>待渲染的内容（按 tab id）：标签新建时先存这里，WebView2 初始化完成后取用。</summary>
    private readonly Dictionary<string, string> _panelContent = new(StringComparer.Ordinal);

    /// <summary>
    /// 面板内容的真源在 Core：Shell 定时拉 <c>/api/panels/{id}</c>，按 revision 决定是否重绘。
    /// 超时压到 3 秒 —— 拉不到就是「Core 正在重启」，下个 tick 再试，不该拖住 UI。
    /// </summary>
    private static readonly HttpClient PanelHttp = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>每块画布一个轮询器（按 tab id）。关标签时必须停，否则定时器会一直跑。</summary>
    private readonly Dictionary<string, DispatcherTimer> _panelPollers = new(StringComparer.Ordinal);

    /// <summary>画布上当前内容的 revision；缺省表示「还没渲染过任何内容」。</summary>
    private readonly Dictionary<string, long> _panelRenderedRevision = new(StringComparer.Ordinal);

    /// <summary>防重入：一次拉取还没回来就不发第二次。</summary>
    private readonly HashSet<string> _panelPollInFlight = new(StringComparer.Ordinal);

    /// <summary>已经提示过的读取失败（按 tab id）：失败原因不该每 2 秒刷一次状态栏。</summary>
    private readonly HashSet<string> _panelFailureNotified = new(StringComparer.Ordinal);

    /// <summary>
    /// 上一次写出去的标注原文（按 tab id）。
    ///
    /// <para>
    /// 轮询每 2 秒一轮，而标注只有用户动手时才变 —— 每次比对原文，没变就不落盘：
    /// 否则每隔两秒都要重写同一个文件，既白耗磁盘，又会让文件时间戳一直在跳，
    /// 看不出“用户到底是刚才画的，还是三小时前画的”。只记写成功过的，失败下轮重试。
    /// </para>
    /// </summary>
    private readonly Dictionary<string, string> _panelAnnotationPayloads = new(StringComparer.Ordinal);

    /// <summary>已提示过的标注写入失败（按 tab id）。与读取失败分开记，两者互不掩盖。</summary>
    private readonly HashSet<string> _panelAnnotationWriteFailed = new(StringComparer.Ordinal);

    private const double PanelPollSeconds = 2;

    /// <summary>每块画布的工具栏句柄（按 tab id）。关标签时必须一起丢掉，否则字典随标签无限增长。</summary>
    private readonly Dictionary<string, PanelToolbar> _panelToolbars = new(StringComparer.Ordinal);

    /// <summary>
    /// 一块画布工具栏的选中态回显。
    ///
    /// <para>
    /// **页内是唯一真源**：Esc、字母快捷键、撤销都会在不经过宿主的情况下改变状态，
    /// 所以这里只做「页面怎么说就怎么画」，本地不自己记账 —— 记账版本在 Esc 之后会显示错。
    /// </para>
    /// </summary>
    private sealed class PanelToolbar
    {
        private readonly Microsoft.UI.Xaml.Controls.Primitives.ToggleButton _annotation;
        private readonly Dictionary<string, Microsoft.UI.Xaml.Controls.Primitives.ToggleButton> _tools;
        private readonly Button _undo;

        public PanelToolbar(
            Microsoft.UI.Xaml.Controls.Primitives.ToggleButton annotation,
            Dictionary<string, Microsoft.UI.Xaml.Controls.Primitives.ToggleButton> tools,
            Button undo)
        {
            _annotation = annotation;
            _tools = tools;
            _undo = undo;
        }

        public void Apply(PanelCanvasState state)
        {
            _annotation.IsChecked = state.IsActive;

            foreach (var (id, button) in _tools)
            {
                button.IsChecked = state.IsActive
                    && string.Equals(id, state.Tool, StringComparison.Ordinal);
            }

            // 没有可撤销的动作时灰掉：现在点它只会刷一句状态栏，看起来和按坏了没区别。
            _undo.IsEnabled = state.CanUndo;
        }
    }

    /// <summary>
    /// 面板允许读取的图片根目录。
    ///
    /// <para>
    /// 用户 2026-10-20 要求面板能加载本地绝对路径的图片。但整盘放行等于把磁盘读权限
    /// 交给 Agent 生成的 HTML（只要扩展名对就能读），所以按目录授权：
    /// 数据根（Agent 生成的图都在这里）+ 用户图片目录。
    /// 需要更多目录时在这里加，不要在 CSP 或拦截器里放开。
    /// </para>
    /// </summary>
    private IReadOnlyList<string> PanelImageRoots()
    {
        var roots = new List<string>(2);

        if (_coordinator.DataRoot is { Length: > 0 } dataRoot)
        {
            roots.Add(dataRoot);
        }

        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (!string.IsNullOrWhiteSpace(pictures))
        {
            roots.Add(pictures);
        }

        return roots;
    }

    private static string PanelIdOf(ToolTab tab)
        => string.IsNullOrWhiteSpace(tab.ResourceKey) ? "main" : tab.ResourceKey;

    private async Task LoadPanelAsync(Microsoft.UI.Xaml.Controls.WebView2 web, ToolTab tab)
    {
        try
        {
            _webEnvironment ??= await CoreWebView2Environment.CreateWithOptionsAsync(null,
                Path.Combine(_coordinator.DataRoot!, "browser", "workbench", "user-data"), null);
            await web.EnsureCoreWebView2Async(_webEnvironment);

            _panelCanvases[tab.Id] = web;

            // 本地图片通道：CSP 已放行 pudding-image:，这里负责真正读盘。
            // 每块画布各挂一次（一个 WebView2 一份），重复挂会产生重复过滤器。
            PanelImageBridge.Attach(web.CoreWebView2, PanelImageRoots());

            // 快路径：能力通道刚推过内容，先渲染它 —— 用户立刻看到，不必等一个轮询周期。
            if (_panelContent.TryGetValue(tab.Id, out var pushed))
            {
                web.CoreWebView2.NavigateToString(WrapPanelHtml(pushed));
            }

            // 慢路径：内容真源在 Core。拉一次，之后按 revision 跟随。
            StartPanelPolling(tab, web);
        }
        catch (Exception ex)
        {
            // 画布起不来时不能留空白：空白会被当成“内容就是这个样子”。
            ToolStatusText.Text = $"面板画布不可用：{ex.Message}";
        }
    }

    /// <summary>
    /// 开始跟随 Core 上的画布内容。
    ///
    /// 为什么由 Shell 拉、而不是只等能力通道推送：通道断了的时候画布会永远停在旧内容，
    /// 而且没有任何迹象。内容归 Core 之后，这条路与通道是否连通无关。
    /// </summary>
    private void StartPanelPolling(ToolTab tab, Microsoft.UI.Xaml.Controls.WebView2 web)
    {
        if (_panelPollers.ContainsKey(tab.Id))
        {
            return;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(PanelPollSeconds) };
        timer.Tick += async (_, _) => await PollPanelAsync(tab.Id);
        _panelPollers[tab.Id] = timer;
        timer.Start();

        // 立刻拉一次，不等第一个 tick 周期。
        _ = PollPanelAsync(tab.Id);
    }

    /// <summary>停掉一块画布的轮询（关标签、或标签已不存在）。</summary>
    private void StopPanelPolling(string tabId)
    {
        if (_panelPollers.Remove(tabId, out var timer))
        {
            timer.Stop();
        }

        _panelRenderedRevision.Remove(tabId);
        _panelPollInFlight.Remove(tabId);
        _panelToolbars.Remove(tabId);
        _panelFailureNotified.Remove(tabId);
        _panelAnnotationPayloads.Remove(tabId);
        _panelAnnotationWriteFailed.Remove(tabId);
    }

    private async Task PollPanelAsync(string tabId)
    {
        if (!_panelPollInFlight.Add(tabId))
        {
            return;
        }

        try
        {
            var tab = _toolTabs.Find(tabId);
            if (tab is null)
            {
                // 标签已经关了：停掉定时器，否则它会一直跑下去。
                StopPanelPolling(tabId);
                return;
            }

            if (!_panelCanvases.TryGetValue(tabId, out var web) || web.CoreWebView2 is null)
            {
                return;
            }

            if (_coordinator.WorkbenchAddress is not { } address)
            {
                return;
            }

            var result = await ReadPanelFromCoreAsync(address, PanelIdOf(tab));

            // 先交标注、再应用内容：一旦内容 revision 变了，ApplyPanelFeed 会 NavigateToString，
            // 页面重载后画布是空的 —— 那时再 take() 只会拿到一份空标注，把用户的笔迹洗干净。
            // 顺序反过来才是对的：趁页面还带着用户的笔迹，先把它们存下去。
            await PublishPanelAnnotationsAsync(tab, web, PanelIdOf(tab));

            ApplyPanelFeed(tabId, web, result);
        }
        catch (Exception ex)
        {
            // 轮询不能把异常扔到 UI 线程的定时器上：一次失败只提示一次，下个 tick 继续试。
            if (_panelFailureNotified.Add(tabId))
            {
                ToolStatusText.Text = $"面板内容读取失败：{ex.Message}";
            }
        }
        finally
        {
            _panelPollInFlight.Remove(tabId);
        }
    }

    /// <summary>
    /// 把画布上的用户标注交给 Core（落盘成 <c>panel-{id}.annotations.json</c>）。
    ///
    /// <para>
    /// 为什么走文件而不走 HTTP：Core 的接口面绑在 0.0.0.0 上，一个匿名的写入口
    /// 等于把画布交给整个局域网；而 Agent 读标注只需要「能读到磁盘上的文件」。
    /// 两个进程本来就共享数据根，文件是这里最窄的连接面：Desktop 只写自己那块画布的快照，Core 只读。
    /// </para>
    /// <para>
    /// 只在原文变化时写，且**从不抛异常** —— 它挂在 UI 定时器上，
    /// 一次脚本或序列化问题不该把整条轮询链带断。
    /// </para>
    /// </summary>
    private async Task PublishPanelAnnotationsAsync(
        ToolTab tab, Microsoft.UI.Xaml.Controls.WebView2 web, string panelId)
    {
        if (_coordinator.DataRoot is not { Length: > 0 } dataRoot)
        {
            return;
        }

        var core = web.CoreWebView2;
        if (core is null)
        {
            return;
        }

        string payload;
        try
        {
            var raw = await core.ExecuteScriptAsync(
                "window.puddingAnnotation ? puddingAnnotation.take() : ''");
            payload = JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
        }
        catch (Exception)
        {
            // 页面正在换、或脚本被拒绝执行：这一轮不写，下个 tick 再试。
            return;
        }

        // 页面还没装标注层（模版页、导出页、刚 NavigateToString 完）时脚本返回空串 ——
        // 那时绝不能写，否则会用一份空标注盖掉用户上一轮真正画过的东西。
        if (string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        if (_panelAnnotationPayloads.TryGetValue(tab.Id, out var previous)
            && string.Equals(previous, payload, StringComparison.Ordinal))
        {
            return;
        }

        if (!PanelAnnotationSnapshotWriter.TryPublish(
                Path.Combine(dataRoot, "panels"),
                panelId,
                payload,
                DateTimeOffset.Now,
                out var error))
        {
            if (_panelAnnotationWriteFailed.Add(tab.Id))
            {
                ToolStatusText.Text = $"标注没能存下来：{error}";
            }

            return;
        }

        _panelAnnotationPayloads[tab.Id] = payload;
        _panelAnnotationWriteFailed.Remove(tab.Id);
    }

    private static async Task<PanelCanvasFeedResult> ReadPanelFromCoreAsync(Uri address, string panelId)
    {
        try
        {
            using var response = await PanelHttp.GetAsync(PanelCanvasFeed.BuildUri(address, panelId));
            var status = PanelCanvasFeed.StatusForHttpCode(response.StatusCode);

            if (status == PanelCanvasFeedStatus.NotPublished)
            {
                return PanelCanvasFeedResult.NotPublished();
            }

            if (status != PanelCanvasFeedStatus.Fetched)
            {
                return PanelCanvasFeedResult.Unreachable($"HTTP {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadAsStringAsync();
            // 传入请求的 panelId：响应必须自证是这块画布的，否则宁可不渲染。
            return PanelCanvasFeed.TryParse(body, out var item, out var parseError, panelId)
                ? PanelCanvasFeedResult.Fetched(item)
                : PanelCanvasFeedResult.Unreachable(parseError);
        }
        catch (Exception ex)
        {
            // 连不上不是异常状态：Core 可能正在重启。当作一次失败的拉取，下个 tick 再试。
            return PanelCanvasFeedResult.Unreachable(ex.Message);
        }
    }

    /// <summary>
    /// 把一次拉取结果落到画布上。三条分支的取舍：
    /// ① 有内容且 revision 变过 → 重绘；没变就不重绘（重绘会打断用户在画布上的操作）；
    /// ② 还没有内容 → 先放内置示例，并记为「已渲染 -1」，这样第一次推送来了必定重绘；
    /// ③ 读不到 → 只在第一次提示，避免每 2 秒刷一次状态栏。
    /// </summary>
    private void ApplyPanelFeed(
        string tabId, Microsoft.UI.Xaml.Controls.WebView2 web, PanelCanvasFeedResult result)
    {
        var core = web.CoreWebView2;
        if (core is null)
        {
            return;
        }

        switch (result.Status)
        {
            case PanelCanvasFeedStatus.Fetched when result.Item is { } item:
                var rendered = _panelRenderedRevision.TryGetValue(tabId, out var known) ? known : long.MinValue;
                if (!PanelCanvasFeed.RequiresRerender(rendered, item.Revision))
                {
                    return;
                }

                _panelRenderedRevision[tabId] = item.Revision;
                _panelFailureNotified.Remove(tabId);
                core.NavigateToString(WrapPanelHtml(item.Content));
                _ = SyncPanelToolbarAsync(tabId);
                ToolStatusText.Text = string.IsNullOrWhiteSpace(item.Title)
                    ? $"画布已更新（rev {item.Revision}）"
                    : $"{item.Title}（rev {item.Revision}）";
                break;

            case PanelCanvasFeedStatus.NotPublished:
                if (!PanelCanvasFeed.ShouldShowSample(
                        _panelRenderedRevision.ContainsKey(tabId),
                        _panelContent.ContainsKey(tabId)))
                {
                    // 本地已有内容（例如「继续编辑」打开的制成品画布），
                    // Core 里查不到是预期：用示例覆盖会让用户以为存的图丢了。
                    return;
                }

                _panelRenderedRevision[tabId] = -1; // 有内容后必定重绘
                core.NavigateToString(PanelSampleHtml);
                _ = SyncPanelToolbarAsync(tabId);
                ToolStatusText.Text = PanelCanvasFeed.DescribeFailure(result.Status, null);
                break;

            case PanelCanvasFeedStatus.Unreachable:
                if (!_panelFailureNotified.Add(tabId))
                {
                    return;
                }

                ToolStatusText.Text = PanelCanvasFeed.DescribeFailure(result.Status, result.ErrorMessage);
                break;
        }
    }

    /// <summary>
    /// 把 Agent 内容包进**沙箱化**的文档壳。
    ///
    /// 三条约束（对应 capability 层的安全承诺）：
    /// ① `default-src 'none'` —— 无外部网络（连字体、图片、fetch 全断）；
    /// ② `script-src 'unsafe-inline'` —— 允许画布自己的交互脚本，但它运行在无网络、
    ///    **无 shellBridge** 的文档里，拿不到任何特权能力；
    /// ③ 不注入任何 native 桥：这个文档与工作台页面完全隔离。
    ///
    /// 主题由宿主注入而不是交给 `prefers-color-scheme`：那个媒体查询取自 WebView2 的
    /// Profile，和宿主外观选择不是同一件事（宿主可以在浅色系统上显式选深色）。
    /// 之前这里写死 `background:#fff`，结果所有画布在深色工具区里都是一张白纸。
    /// 现在把 <see cref="PanelCanvasTheme"/> 的变量注入 `:root`，内容用
    /// `var(--pudding-page)` 取值即可，不需要自己猜主题。
    /// </summary>
    private string WrapPanelHtml(string content) =>
        PanelCanvasDocument.WrapForEmbedding(content, CurrentWorkbenchScheme());

    /// <summary>
    /// 能力通道：把 Agent 内容推到工具区画布（由 <c>WinUiShellFacilities</c> 调用，已在 UI 线程）。
    /// 同 panelId 幂等：已有画布则覆盖内容，没有则开一个。
    /// </summary>
    public Task<CapabilityResult<DesktopPanelRenderResult>> CapabilityRenderPanelAsync(
        DesktopPanelRenderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tabId = ToolTabIdentity.Panel(request.PanelId);
        var existed = _toolTabs.Find(tabId) is not null;

        _panelContent[tabId] = request.Content;

        if (_panelCanvases.TryGetValue(tabId, out var web) && web.CoreWebView2 is not null)
        {
            // 已有画布：直接覆盖内容（WebView2 未就绪的情况交给 LoadPanelAsync 的待渲染路径）。
            web.CoreWebView2.NavigateToString(WrapPanelHtml(request.Content));
        }

        // 新建与复用都必须展开并激活：能力调用是「让用户看见」的显式动作。
        // 旧版只在新建标签时展开 ⇒ 画布已存在但工具区被折叠时，推送等于什么都没发生，
        // 而返回值仍然报已渲染，把「没显示」说成了「已渲染」。
        OpenTool(new ToolTabDescriptor
        {
            Id = tabId,
            Kind = ToolTabKind.Panel,
            Title = string.IsNullOrWhiteSpace(request.Title) ? "交互面板" : request.Title,
            Subtitle = "Agent 推送内容",
            ResourceKey = request.PanelId,
            Availability = ToolAvailabilityCatalog.For(ToolTabKind.Panel),
            StatusText = "已接收 Agent 推送内容",
        });

        return Task.FromResult(CapabilityResult<DesktopPanelRenderResult>.Success(
            new DesktopPanelRenderResult(request.PanelId,
                existed ? DesktopPanelRenderStatus.Replaced : DesktopPanelRenderStatus.Rendered)));
    }

    /// <summary>
    /// 能力通道：在工具区打开一份文档（由 <c>WinUiShellFacilities</c> 调用，已在 UI 线程）。
    /// 只传相对路径；内容由 Core 的 /admin/doc-preview 渲染，Shell 不读盘。
    /// </summary>
    public Task<CapabilityResult<DesktopDocumentPreviewResult>> CapabilityOpenDocumentAsync(
        DesktopDocumentPreviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tabId = ToolTabIdentity.Document(request.Path);
        var existed = _toolTabs.Find(tabId) is not null;

        // 已有标签也要走这一遭：OpenTool 会幂等激活它并展开工具区。
        // 旧版只在新建时打开 ⇒ 文档已在侧边但工具区被折叠时，调用完全没反应。
        OpenTool(new ToolTabDescriptor
        {
            Id = tabId,
            Kind = ToolTabKind.Document,
            Title = string.IsNullOrWhiteSpace(request.Title) ? "文档预览" : request.Title,
            Subtitle = request.Path,
            ResourceKey = request.Path,
            Availability = ToolAvailabilityCatalog.For(ToolTabKind.Document),
            StatusText = request.Path,
        });

        return Task.FromResult(CapabilityResult<DesktopDocumentPreviewResult>.Success(
            new DesktopDocumentPreviewResult(tabId, request.Path,
                existed ? DesktopDocumentPreviewStatus.AlreadyOpen : DesktopDocumentPreviewStatus.Opened)));
    }

    /// <summary>
    /// 导出画布**当前显示的内容**。内容真源在 Core，所以先拉一次；拉不到才退回
    /// 最近一次推送的副本。<b>绝不导出内置示例</b>——否则「下载到本地」拿到的和
    /// 屏幕上看到的不是同一个东西。
    ///
    /// 导出文件是给用户独立打开/分享的：不写沙箱 CSP（那是宿主容器的事），但保留
    /// <see cref="PanelCanvasTheme"/> 变量，保证在浏览器里打开时配色仍然正确。
    /// </summary>
    private async Task ExportPanelAsync(ToolTab tab)
    {
        try
        {
            var content = await ResolvePanelContentForExportAsync(tab);
            if (string.IsNullOrWhiteSpace(content))
            {
                ToolStatusText.Text = "画布还没有内容，未导出。";
                return;
            }

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Pudding");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(
                dir, $"panel-{PanelIdOf(tab)}-{DateTime.Now:yyyyMMdd-HHmmss}.html");
            await File.WriteAllTextAsync(file, BuildStandalonePanelHtml(content));
            ToolStatusText.Text = $"已导出：{file}";
        }
        catch (Exception ex)
        {
            ToolStatusText.Text = $"导出失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 取画布当前内容的导出副本：Core 是唯一真源，拉成功就用它；Core 不可达时退回
    /// 能力通道最近推送的副本（至少是用户屏幕上那份）；两者都没有则返回 null。
    /// </summary>
    private async Task<string?> ResolvePanelContentForExportAsync(ToolTab tab)
    {
        if (_coordinator.WorkbenchAddress is { } address)
        {
            var result = await ReadPanelFromCoreAsync(address, PanelIdOf(tab));
            if (result.Status == PanelCanvasFeedStatus.Fetched && result.Item is { } item
                && !string.IsNullOrWhiteSpace(item.Content))
            {
                return item.Content;
            }
        }

        return _panelContent.TryGetValue(tab.Id, out var pushed) ? pushed : null;
    }

    /// <summary>
    /// 导出用的独立文档（无沙箱 CSP、带标题）。拼装在
    /// <see cref="PanelCanvasDocument.WrapForExport"/>（可测），这里只提供当前配色。
    /// </summary>
    private string BuildStandalonePanelHtml(string content) =>
        PanelCanvasDocument.WrapForExport(content, CurrentWorkbenchScheme());

    /// <summary>
    /// 切换画布标注模式。
    ///
    /// <para>
    /// 标注层由壳注入（见 <see cref="PanelCanvasDocument"/>），默认隐藏。
    /// 这里只用 <c>ExecuteScriptAsync</c> 读取一个布尔值，不开任何消息通道：
    /// 面板内容由 Agent 生成，能主动向宿主推消息的能力越少越好。
    /// </para>
    /// </summary>
    private async Task TogglePanelAnnotationAsync(ToolTab tab)
    {
        if (!_panelCanvases.TryGetValue(tab.Id, out var web) || web.CoreWebView2 is null)
        {
            ToolStatusText.Text = "画布尚未就绪，无法标注。";
            return;
        }

        try
        {
            var raw = await web.CoreWebView2.ExecuteScriptAsync(
                "window.puddingAnnotation ? puddingAnnotation.toggle() : null");

            await SyncPanelToolbarAsync(tab.Id);

            if (string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase))
            {
                // 旧壳 / 页面未注入标注层。如实报，不要让按钮看起来按了。
                ToolStatusText.Text = "这块画布没有标注层（内容未带壳或版本较旧）。";
                return;
            }

            ToolStatusText.Text = string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
                ? "标注模式：在图片上拖框或点选，完成后点「添加到对话」"
                : "已退出标注模式";
        }
        catch (Exception ex)
        {
            ToolStatusText.Text = $"标注不可用：{ex.Message}";
        }
    }

    /// <summary>
    /// 切换画布工具（框 / 点 / 涂鸦 / 箭头 / 文字）。
    ///
    /// <para>
    /// <paramref name="toolId"/> 只来自 <see cref="PanelTools"/> 常量表，
    /// 不进用户输入，所以拼进脚本没有注入面。
    /// </para>
    /// </summary>
    private async Task SetPanelToolAsync(ToolTab tab, string toolId)
    {
        if (!_panelCanvases.TryGetValue(tab.Id, out var web) || web.CoreWebView2 is null)
        {
            ToolStatusText.Text = "画布尚未就绪。";
            return;
        }

        try
        {
            await web.CoreWebView2.ExecuteScriptAsync(
                $"window.puddingAnnotation ? puddingAnnotation.setTool('{toolId}') : null");

            await SyncPanelToolbarAsync(tab.Id);

            ToolStatusText.Text = toolId switch
            {
                "pen" => "工具：涂鸦 —— 按住拖动写下笔迹（训练模式，记得先开「标注」）",
                "arrow" => "工具：箭头 —— 从起点拖到终点",
                "text" => "工具：文字 —— 图上点一下，再输入要改什么",
                "point" => "工具：点选 —— 在目标上点一下",
                _ => "工具：框选 —— 拖出矩形",
            };
        }
        catch (Exception ex)
        {
            ToolStatusText.Text = $"切换工具失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 把页内的工具状态回显到工具栏按钮。
    ///
    /// <para>
    /// 取不到就什么都不动（不猜）。这条路径失败只意味着按钮颜色不更新，
    /// 不该往状态栏写东西盖掉真正有用的提示。
    /// </para>
    /// </summary>
    private async Task SyncPanelToolbarAsync(string tabId)
    {
        if (!_panelToolbars.TryGetValue(tabId, out var bar))
        {
            return;
        }

        if (!_panelCanvases.TryGetValue(tabId, out var web) || web.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            var raw = await web.CoreWebView2.ExecuteScriptAsync(
                "window.puddingAnnotation ? puddingAnnotation.state() : ''");

            if (PanelCanvasState.TryParse(raw, out var state))
            {
                bar.Apply(state);
            }
        }
        catch (Exception)
        {
            // 回显失败不该打断用户正在做的操作。
        }
    }

    /// <summary>撤销画布上一步。橡皮擦就是这个 —— 单独做一个「擦」会跟撤销抢职责。</summary>
    private async Task UndoPanelAsync(ToolTab tab)
    {
        if (!_panelCanvases.TryGetValue(tab.Id, out var web) || web.CoreWebView2 is null)
        {
            ToolStatusText.Text = "画布尚未就绪。";
            return;
        }

        try
        {
            var raw = await web.CoreWebView2.ExecuteScriptAsync(
                "window.puddingAnnotation ? String(puddingAnnotation.undo()) : 'false'");

            await SyncPanelToolbarAsync(tab.Id);

            // ExecuteScriptAsync 返回的是 JSON 字面量：字符串带引号，布尔不带。
            ToolStatusText.Text = raw.Trim('"') == "true" ? "已撤销上一步" : "没有可撤销的操作";
        }
        catch (Exception ex)
        {
            ToolStatusText.Text = $"撤销失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 取回画布上的**完整载荷**（标注 + 来源图）。取不到时返回 null（不建失败），
    /// 由调用方决定退化成什么。解析失败会写状态栏：静默丢掉标注等于用户白画一遍。
    /// </summary>
    private async Task<PanelCanvasPayload?> TryReadCanvasPayloadAsync(ToolTab tab)
    {
        if (!_panelCanvases.TryGetValue(tab.Id, out var web) || web.CoreWebView2 is null)
        {
            return null;
        }

        try
        {
            var raw = await web.CoreWebView2.ExecuteScriptAsync(
                "window.puddingAnnotation ? puddingAnnotation.take() : ''");

            // ExecuteScriptAsync 返回的是 JSON，字符串结果会被再编码一层。
            var payload = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            if (!PanelAnnotation.TryParseCanvas(payload, out var parsed, out var error))
            {
                ToolStatusText.Text = $"标注无法解析：{error}";
                return null;
            }

            return parsed;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 把画布烧录成一张带笔迹的 PNG（原图 + 所有标记）。
    ///
    /// <para>
    /// 形式 1（官方「任意标记 + 自然语言」）要求笔迹存在于图片像素里，而标注层是
    /// DOM 覆盖 —— 不烧录，模型收到的永远是干净原图，用户画了等于没画。
    /// </para>
    /// </summary>
    /// <returns>落盘路径；画布未就绪、无标记或失败时返回 null。</returns>
    private async Task<string?> TryComposePanelAsync(ToolTab tab)
    {
        if (!_panelCanvases.TryGetValue(tab.Id, out var web) || web.CoreWebView2 is null)
        {
            return null;
        }

        try
        {
            var raw = await web.CoreWebView2.ExecuteScriptAsync(
                "window.puddingAnnotation ? puddingAnnotation.compose() : ''");

            // ExecuteScriptAsync 把字符串结果再编码一层 JSON，这里必须反序列化。
            var dataUrl = JsonSerializer.Deserialize<string>(raw);
            // compose 默认输出 JPEG（体积小，能过脚本通道）；仍接受 png，
            // 以冠页面版本回退时整条链路直接断掉。
            const string marker = ";base64,";
            var markerIndex = dataUrl?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
            if (string.IsNullOrWhiteSpace(dataUrl)
                || !dataUrl.StartsWith("data:image/", StringComparison.Ordinal)
                || markerIndex < 0)
            {
                return null;
            }

            var bytes = Convert.FromBase64String(dataUrl[(markerIndex + marker.Length)..]);
            Directory.CreateDirectory(PanelMarkDirectory);

            var path = Path.Combine(
                PanelMarkDirectory, $"panel-marked-{DateTime.Now:yyyyMMdd-HHmmss-fff}.jpg");
            await File.WriteAllBytesAsync(path, bytes);

            // 每插一次就多一张图，不清会无限增长。
            // 但**不能盲删**：现在没有引用关系落盘，被消息引用的图一旦删掉链接就永久断。
            // 所以两道门：超过上限 **且** 早于保留期。传入 path 作 protect ——
            // 排序靠 mtime，时钟回拨时刚写的图可能被排成“最旧”。
            PanelMarkRetention.Prune(
                PanelMarkDirectory,
                minAge: TimeSpan.FromDays(7),
                protect: path);
            return path;
        }
        catch (Exception)
        {
            // 烧录失败不该阻断整个插入：坐标路径仍然可用（形式 2 不依赖烧录图）。
            return null;
        }
    }

    /// <summary>
    /// 「添加到对话」：优先把**标注**变成坐标文本插进输入框（Seedream 交互编辑协议），
    /// 没有标注时退回插入画布引用。
    ///
    /// 画布在独立 WebView2 里拿不到输入框，按钮又由原生 XAML 画 —— 两边各缺一半，
    /// 所以由这里转发一条消息给主工作台（契约见前端 <c>integration/shellBridge.ts</c>，
    /// 消费方在 <c>IntentConsole</c>）。
    /// </summary>
    private async Task AddPanelToChatAsync(ToolTab tab)
    {
        var core = _web?.CoreWebView2;
        if (core is null)
        {
            // 宁可如实报不可用，也不要弹一个看起来成功、实际什么都没发生的提示。
            ToolStatusText.Text = "工作台尚未就绪，无法插入对话。";
            return;
        }

        var payload = await TryReadCanvasPayloadAsync(tab);
        var annotations = payload?.Items ?? Array.Empty<PanelAnnotation>();
        var title = string.IsNullOrWhiteSpace(tab.Title) ? "交互面板" : tab.Title;
        var text = annotations.Count > 0
            ? PanelAnnotation.DescribeForComposer(
                annotations, payload!.SourceUri, PanelImageRoots())
            : _panelRenderedRevision.TryGetValue(tab.Id, out var revision)
                ? $"【画布：{title} rev {revision}】"
                : $"【画布：{title}】";

        string? markedPath = null;
        if (annotations.Count > 0)
        {
            // 坐标之外再给一张**带笔迹的图**：形式 1 靠它，形式 2 靠坐标，两者并存。
            markedPath = await TryComposePanelAsync(tab);
            if (markedPath is not null)
            {
                text = $"【已画标记的图】{markedPath}\n{text}";
            }
        }

        try
        {
            // 手写外层结构、只用 JsonSerializer 序列化字符串值：避开匿名类型在
            // trimming/AOT 下的元数据依赖。
            var message = "{\"type\":\"pudding.composer.insert\",\"text\":"
                + JsonSerializer.Serialize(text) + "}";
            core.PostWebMessageAsJson(message);
            ToolStatusText.Text = annotations.Count > 0
                ? $"已插入 {annotations.Count} 个标注坐标"
                : $"已插入对话输入框：{title}";

            // 插进过对话的烧录图要打「已引用」标记：它的路径现在存进了消息历史，
            // 清理再按时间/数量删它就是永久断链。打标记失败不影响插入。
            if (markedPath is not null)
            {
                PanelMarkRetention.MarkUsed(markedPath);
            }
        }
        catch (Exception ex)
        {
            ToolStatusText.Text = $"插入对话失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 制成品库。本地目录是唯一真源（<c>{DataRoot}/crafts</c>），不新增 Core API：
    /// 制成品是用户资产而不是会话状态，应当能直接备份、用资源管理器打开、
    /// 不依赖 Core 存活。
    /// </summary>
    private PanelCraftStore? _craftStore;

    /// <summary>
    /// 画布工具：id 必须与页面 <c>puddingAnnotation.setTool</c> 认的名字一致，
    /// 否则按钮点了没反应（而页面不会报错）。
    /// </summary>
    private static readonly (string Id, string Label)[] PanelTools =
    [
        ("box", "框"),
        ("point", "点"),
        ("pen", "涂鸦"),
        ("arrow", "箭头"),
        ("text", "文字"),
    ];

    /// <summary>
    /// 烧录图（原图 + 笔迹）的落盘目录。与制成品分开：
    /// 制成品是用户资产，这些是中间产物，可随时重生。
    /// </summary>
    private string PanelMarkDirectory =>
        Path.Combine(_coordinator.DataRoot!, "panel-marks");

    /// <summary>
    /// <c>_trash</c> 里的条目保留多久。7 天足够覆盖「误删后过几天才想起来」；
    /// 再长就只是占磁盘了。
    /// </summary>
    private static readonly TimeSpan TrashRetention = TimeSpan.FromDays(7);

    private PanelCraftStore CraftStore => _craftStore ??= new PanelCraftStore(_coordinator.DataRoot!);

    /// <summary>
    /// 「保存到制品库」：把当前画布上那张图存成一件制成品。
    ///
    /// <para>
    /// 只接受能还原成白名单内真实路径的图：<c>data:</c> 内嵌图、外部链接都没有
    /// 「一件可回溯的资产」可言，存下来只会是一个无法再加工的副本。
    /// </para>
    ///
    /// <para>
    /// 不强制起名：默认用画布标题。中断编辑去弹窗改名，比事后重命名更贵。
    /// </para>
    /// </summary>
    private async Task SavePanelToCraftsAsync(ToolTab tab)
    {
        var payload = await TryReadCanvasPayloadAsync(tab);

        if (payload is null
            || string.IsNullOrWhiteSpace(payload.SourceUri)
            || !PanelImageSource.TryResolve(payload.SourceUri, PanelImageRoots(), out var imagePath, out _))
        {
            ToolStatusText.Text = "这块画布没有可保存的本地图片。";
            return;
        }

        var title = string.IsNullOrWhiteSpace(tab.Title) ? "未命名" : tab.Title;

        try
        {
            // 记录图片的**原始像素**，不是页面显示尺寸：制成品列表写着 1123×1123
            // 而文件实际是 2048×2048，会让用户以为存错了图。
            // 载荷没有自然尺寸时（旧画布）回退到显示尺寸，总比记 0 好。
            var pixelWidth = payload.NaturalWidth > 0 ? payload.NaturalWidth : payload.Width;
            var pixelHeight = payload.NaturalHeight > 0 ? payload.NaturalHeight : payload.Height;

            var craft = CraftStore.Save(
                imagePath,
                title,
                (int)Math.Round(pixelWidth),
                (int)Math.Round(pixelHeight),
                new PanelCraftSource(PanelIdOf(tab), null, null, payload.Items),
                // 从「继续编辑」打开的画布，panelId 里就带着来源制成品；
                // 不是编辑画布（Agent 自己发的面板）时为 null —— 那确实没有本地血缘。
                parent: PanelCraftEditPanel.CraftIdOf(PanelIdOf(tab)),
                note: null,
                DateTimeOffset.Now);

            ToolStatusText.Text = $"已保存到制品库：{craft.Title}（{craft.Id}）";
        }
        catch (Exception ex)
        {
            // 保存失败必须说出来：默默失败会让用户以为存上了，几天后才发现没了。
            ToolStatusText.Text = $"保存到制品库失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 制成品列表：直接扫本地目录，不经过 Core。
    ///
    /// <para>
    /// 每次进入标签页都重扫一次（而不是缓存）：这是低频、低频次的操作，
    /// 而缓存与磁盘不一致时，用户看到的是“我存的图凭空消失”。
    /// </para>
    /// </summary>
    private FrameworkElement CreateCraftLibrary()
    {
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(16) };

        panel.Children.Add(new TextBlock
        {
            Text = "制成品",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        panel.Children.Add(new TextBlock
        {
            Text = CraftStore.CraftsDirectory,
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });

        IReadOnlyList<PanelCraft> crafts;
        try
        {
            crafts = CraftStore.List();
        }
        catch (Exception ex)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"无法读取制品库：{ex.Message}",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8,
            });
            return WrapInScroll(panel);
        }

        // 顺手清理过期的 _trash 条目。清理失败不影响列表：这是维护动作，
        // 不该让用户因为「旧东西没删掉」而看不到自己的制成品。
        var pruned = 0;
        try
        {
            pruned = CraftStore.PruneTrash(DateTimeOffset.Now, TrashRetention);
        }
        catch (Exception)
        {
        }

        if (pruned > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"已清理 {pruned} 个超过 {TrashRetention.Days} 天的已删除条目",
                FontSize = 11,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        if (crafts.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "还没有制成品。在交互面板里点「保存到制品库」就会出现在这里。",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
            });
            return WrapInScroll(panel);
        }

        // 每张卡片要显示「第 N 版」：深度靠整份列表算，所以在这里算一次给每张卡分用。
        var depths = PanelCraftLineage.Depths(crafts);

        foreach (var craft in crafts)
        {
            panel.Children.Add(CreateCraftCard(craft, depths));
        }

        return WrapInScroll(panel);
    }

    private static ScrollViewer WrapInScroll(FrameworkElement content) => new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Content = content,
    };

    private FrameworkElement CreateCraftCard(
        PanelCraft craft, IReadOnlyDictionary<string, int> depths)
    {
        var body = new StackPanel { Spacing = 6 };

        body.Children.Add(new TextBlock
        {
            Text = craft.Title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        var created = DateTimeOffset.FromUnixTimeMilliseconds(craft.CreatedAt).LocalDateTime;
        var meta = $"{craft.Width}×{craft.Height} · {created:MM-dd HH:mm} · {craft.Id}";
        if (craft.Parent is not null)
        {
            // 「来自 craft-xxxx」对用户没意义；版本号才是他关心的「改到第几轮」。
            var version = depths.TryGetValue(craft.Id, out var depth) ? depth : 1;
            meta += $" · 第 {version} 版";
        }

        body.Children.Add(new TextBlock
        {
            Text = meta,
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(PanelAction("继续编辑", async (_, _) => await OpenCraftForEditingAsync(craft)));
        actions.Children.Add(PanelAction("打开文件", (_, _) => OpenCraftInExplorer(craft)));
        actions.Children.Add(PanelAction("改名", async (_, _) => await RenameCraftAsync(craft)));
        actions.Children.Add(PanelAction("删除", (_, _) => DeleteCraft(craft)));
        body.Children.Add(actions);

        return new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = body,
        };
    }

    /// <summary>
    /// 「继续编辑」：把制成品重新打开成一块可标注的画布。
    ///
    /// 复用 <see cref="CapabilityRenderPanelAsync"/> 而不是另开一条渲染路径：
    /// 那条路已经处理了「同 panelId 幂等覆盖」「折叠时自动展开」等细节，
    /// 复制一份只会让两处慢慢跑偏。
    /// </summary>
    private async Task OpenCraftForEditingAsync(PanelCraft craft)
    {
        var imagePath = CraftStore.ResolveImagePath(craft);
        if (!File.Exists(imagePath))
        {
            ToolStatusText.Text = $"制成品的图片文件已丢失：{craft.FileName}";
            return;
        }

        // 来源写在 panelId 上：再保存时用它当 parent，不必另存「这个 tab 从哪件打开」。
        var panelId = PanelCraftEditPanel.IdOf(craft);
        var html = BuildCraftEditHtml(craft, imagePath);

        await CapabilityRenderPanelAsync(
            new DesktopPanelRenderRequest(panelId, html, title: craft.Title + " · 编辑"),
            CancellationToken.None);
    }

    /// <summary>
    /// 画布内容：一张图 + 出处。壳会注入主题、标注层与本地图片通道，
    /// 所以这里只需要给 <c>&lt;img&gt;</c> —— 以及一个足够大的面积（标注层按最大图定位）。
    /// </summary>
    private static string BuildCraftEditHtml(PanelCraft craft, string imagePath)
    {
        var src = PanelImageSource.BuildUri(imagePath);
        var title = System.Net.WebUtility.HtmlEncode(craft.Title);
        var created = DateTimeOffset.FromUnixTimeMilliseconds(craft.CreatedAt).LocalDateTime;

        // 把当初的标注一起画回去：没有它，「继续编辑」看到的是一张干净的图，
        // 用户上一次画的东西静默消失。无标注时是空串，不会多出 script 块。
        var restore = PanelCanvasRestore.BuildInlineScript(
            craft.Source?.Annotations ?? Array.Empty<PanelAnnotation>());

        return $"""
            <div style="padding:16px">
              <h1 style="font-size:15px;font-weight:500;margin:0 0 4px">{title}</h1>
              <p style="font-size:12px;opacity:.65;margin:0 0 12px">{craft.Width}×{craft.Height} · {created:yyyy-MM-dd HH:mm}</p>
              <img src="{src}" alt="{title}" style="max-width:100%;height:auto;border-radius:10px;display:block">
            </div>
            {restore}
            """;
    }

    private void OpenCraftInExplorer(PanelCraft craft)
    {
        var path = CraftStore.ResolveImagePath(craft);
        if (!File.Exists(path))
        {
            ToolStatusText.Text = $"文件已丢失：{craft.FileName}";
            return;
        }

        try
        {
            // 用 /select 让用户看到文件本身，而不是目录里另一个同名文件。
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ToolStatusText.Text = $"无法打开资源管理器：{ex.Message}";
        }
    }

    /// <summary>
    /// 删除制成品：只移进 <c>_trash</c>，不真删。
    ///
    /// <para>
    /// 误删一张改了很久的图，代价远大于占一点磁盘；而「先移再删」让恢复只需要一次拖拽。
    /// 状态栏报出移到了哪里 —— 不说的话用户只能去翻目录碰运气。
    /// </para>
    /// </summary>
    private void DeleteCraft(PanelCraft craft)
    {
        try
        {
            // 传 retention：制成品标签页只在用户打开时才清回收站，
            // 而这里是 _trash 唯一增长的地方 —— 不在这里清就永远不会被清。
            if (!CraftStore.Delete(craft.Id, DateTimeOffset.Now, TrashRetention))
            {
                ToolStatusText.Text = $"删除失败：制品库里找不到 {craft.Id}";
                return;
            }

            ToolStatusText.Text = $"已删除：{craft.Title}（移入 _trash，可手动恢复）";
            RefreshCraftLibraryTab();
        }
        catch (Exception ex)
        {
            ToolStatusText.Text = $"删除失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 改名：弹一个输入框，只改 title。
    ///
    /// <para>
    /// 用对话框而不是就地可编辑文本框：卡片每次刷新都会重建，
    /// 就地编辑一旦碰上列表重建，用户正在输入的内容会被抹掉。
    /// </para>
    /// </summary>
    private async Task RenameCraftAsync(PanelCraft craft)
    {
        var input = new TextBox
        {
            Text = craft.Title,
            SelectionStart = craft.Title.Length,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "重命名制成品",
            Content = input,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var updated = CraftStore.Rename(craft.Id, input.Text);
            if (updated is null)
            {
                // 空名字一概拒绝：不把「无名」写进制品库，也不假装成功了。
                ToolStatusText.Text = "重命名失败：名称不能为空。";
                return;
            }

            ToolStatusText.Text = $"已重命名为「{updated.Title}」";
            RefreshCraftLibraryTab();
        }
        catch (Exception ex)
        {
            ToolStatusText.Text = $"重命名失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 重建制成品标签页。
    ///
    /// <para>
    /// 不能就地改列表：<see cref="CreateCraftLibrary"/> 每次重建整页，局部更新会让
    /// 「已删除但仍显示」和「已新增但不显示」两种不同步同时存在。
    /// 丢弃缓存并让 <see cref="UpdateToolContentVisibility"/> 重新构建，只有一条路径。
    /// </para>
    /// </summary>
    private void RefreshCraftLibraryTab()
    {
        var tab = _toolTabs.ActiveTab;
        if (tab is null || tab.Kind != ToolTabKind.Artifact)
        {
            return;
        }

        if (_toolTabContent.Remove(tab.Id, out var stale))
        {
            ToolTabContentHost.Children.Remove(stale);
        }

        UpdateToolContentVisibility();
    }

    /// <summary>
    /// 工具栏上的普通按钮。
    ///
    /// <para>
    /// <paramref name="accent"/> 只给唯一的主操作（保存到制品库）：
    /// 十个按钮一样重的时候，「下一步该点哪个」在界面上没有答案。
    /// </para>
    /// </summary>
    private static Button PanelAction(string text, RoutedEventHandler onClick, bool accent = false)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 12,
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(6),
            BorderThickness = accent ? new Thickness(0) : new Thickness(1),
        };

        // 强调样式来自主题；取不到就退回普通按钮，绝不因为找不到资源让整个工具栏起不来。
        if (accent
            && Application.Current.Resources.TryGetValue("AccentButtonStyle", out var found)
            && found is Style accentStyle)
        {
            button.Style = accentStyle;
        }
        else
        {
            button.Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
            button.BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        }

        button.Click += onClick;
        return button;
    }

    /// <summary>
    /// 工具栏上的开关按钮（标注 / 各个工具）。
    ///
    /// <para>
    /// 点它只把意图发给页面，本地不抢先改 <c>IsChecked</c>：
    /// 否则页面拒绝时会出现「按钮显示已选中、画布上其实没生效」。
    /// 真实选中态一律由 <see cref="SyncPanelToolbarAsync"/> 从页内回显。
    /// </para>
    /// </summary>
    private static Microsoft.UI.Xaml.Controls.Primitives.ToggleButton PanelToggle(
        string text, RoutedEventHandler onClick)
    {
        var button = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
        {
            Content = text,
            FontSize = 12,
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(6),
            MinWidth = 0,
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
        };
        button.Click += onClick;
        return button;
    }

    /// <summary>工具组之间的竖线。工具栏只有一行高，用 1px Border 比分隔符控件更稳。</summary>
    private static Border PanelDivider()
    {
        var brush = Application.Current.Resources.TryGetValue("DividerStrokeColorDefaultBrush", out var found)
            && found is Brush divider
            ? divider
            : (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];

        return new Border
        {
            Width = 1,
            Height = 16,
            Background = brush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0),
        };
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
          <div class="hint">Agent 还没有向这块画布推送内容，当前展示内置示例。</div>
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
        StopPanelPolling(tabId);
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
        var scheme = CurrentWorkbenchScheme();

        // 三处宿主表面共用同一份判断：标题栏按钮、工具区里的浏览器表面、主工作台。
        // 前两处不依赖 _web，所以放在它的 null 早退之前。
        ApplyCaptionButtonColors(scheme);
        ApplyBrowserSurfaceAppearance();
        RefreshPanelCanvasAppearance();

        var web = _web;
        if (web is null) return;
        web.DefaultBackgroundColor = ToColor(WorkbenchAppearance.BackgroundArgb(scheme));

        if (web.CoreWebView2 is null) return;
        web.CoreWebView2.Profile.PreferredColorScheme = ToWebView2Scheme(
            WorkbenchAppearance.PreferredColorSchemeFor(preference));
    }

    /// <summary>
    /// 当前生效配色。宿主外观选择的**唯一**解析入口：标题栏、浏览器表面、面板画布
    /// 都从这里取，避免三处各写一份 <c>Resolve(...)</c> 而漂移。
    /// </summary>
    private WorkbenchColorScheme CurrentWorkbenchScheme() =>
        WorkbenchAppearance.Resolve(
            WorkbenchAppearance.PreferenceFromComboIndex(ThemeBox.SelectedIndex),
            Root.ActualTheme == ElementTheme.Dark);

    /// <summary>
    /// 宿主主题切换后，已经渲染过的画布必须按新主题重绘。
    ///
    /// 只改 <c>DefaultBackgroundColor</c> 不够：那只影响首帧，已加载的文档仍是旧底色。
    /// 这里清掉 revision 台账，让轮询在下一个 tick 重新拉取并重新包装
    /// （<see cref="PanelCanvasFeed.RequiresRerender"/> 只用"是否相同"判断，
    /// 台账缺失即视为需要重绘）。清 <c>_panelFailureNotified</c> 是为了让新主题下的
    /// 读取失败能再提示一次，而不是被上一轮的抑制记录永久吞掉。
    /// </summary>
    private void RefreshPanelCanvasAppearance()
    {
        var argb = PanelCanvasTheme.PageArgb(CurrentWorkbenchScheme());
        foreach (var web in _panelCanvases.Values)
        {
            web.DefaultBackgroundColor = ToColor(argb);
        }

        if (_panelCanvases.Count == 0) return;
        _panelRenderedRevision.Clear();
        _panelFailureNotified.Clear();
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
