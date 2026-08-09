using System.ComponentModel;
using System.Runtime.CompilerServices;
using PuddingDesktop.Hosting;
using PuddingDesktop.Runtime;

namespace PuddingDesktop.ViewModels;

public sealed class RuntimeCenterViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly DesktopApplicationCoordinator _coordinator;
    private readonly IDiagnosticBundleService _diagnosticBundleService;
    private DesktopStartupState _state;
    private DesktopRuntimeSnapshot _runtime;
    private string? _stateError;
    private string _coreLogText = "尚无 Core 输出。";
    private int _disposeState;

    public RuntimeCenterViewModel(DesktopApplicationCoordinator coordinator)
    {
        _coordinator = coordinator;
        _state = coordinator.State;
        _runtime = coordinator.RuntimeSnapshot;
        _diagnosticBundleService = new DiagnosticBundleService(
            () => coordinator.RuntimeSnapshot,
            coordinator.CoreLogBuffer);

        coordinator.StateChanged += OnCoordinatorStateChanged;
        coordinator.RuntimeChanged += OnRuntimeChanged;
        RefreshTransient();
    }

    public DesktopStartupState State => _state;
    public DesktopRuntimeSnapshot Runtime => _runtime;
    public string DataRoot => _coordinator.DataRoot ?? "未配置";
    public string CoreExecutablePath => _coordinator.CoreExecutablePath ?? "尚未解析";
    public string BoundAddress => _runtime.Session?.ListenAddress is { } listenAddress
        ? FormatEndpoint(listenAddress)
        : _coordinator.CoreAddress is { } controlAddress
            ? FormatEndpoint(controlAddress)
            : "未绑定";
    public string LocalControlAddress => _coordinator.CoreAddress is { } address
        ? $"Desktop 本机控制地址：{FormatEndpoint(address)}"
        : "Desktop 本机控制地址：未绑定";
    public string ProcessIdLabel => IsCoreRunning ? "进程 PID" : "最近 PID";
    public string ProcessIdText => _runtime.Session?.ProcessId.ToString() ?? _runtime.LastProcessId?.ToString() ?? "—";
    public string StartedAtText => _runtime.Session is { HasExited: false } session
        ? session.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        : "—";
    public string UptimeText => _runtime.Session is { HasExited: false } session
        ? FormatDuration(DateTimeOffset.UtcNow - session.StartedAt)
        : "—";
    public string LastExitCodeText => _runtime.LastExitCode?.ToString() ?? "—";
    public string HealthText => IsCoreRunning ? "/health/ready 正常" : "Core 未就绪";
    public bool AutoRestartEnabled => _runtime.AutoRestartEnabled;
    public int RestartAttempts => _runtime.RestartAttemptsInWindow;
    public string RestartStatusText => _runtime.State switch
    {
        DesktopRuntimeState.RestartScheduled when _runtime.NextRestartAt is not null
            => $"第 {_runtime.RestartAttemptsInWindow} 次恢复 · {_runtime.NextRestartAt.Value.ToLocalTime():HH:mm:ss}",
        DesktopRuntimeState.CircuitOpen => "自动恢复已熔断",
        _ when !_runtime.AutoRestartEnabled => "自动恢复已关闭",
        _ => "自动恢复待命",
    };

    public string StatusText => _state switch
    {
        DesktopStartupState.NeedsDataRoot => "需要配置数据目录",
        DesktopStartupState.InvalidConfiguration => "配置无效",
        DesktopStartupState.CoreStopped => "Core 已停止",
        DesktopStartupState.CoreStarting => "Core 启动中",
        DesktopStartupState.CoreReady => "Core 运行中",
        DesktopStartupState.CoreStopping => "Core 停止中",
        DesktopStartupState.CoreFailed => "Core 启动失败",
        DesktopStartupState.CoreRestartScheduled => "正在等待自动恢复",
        DesktopStartupState.CoreCircuitOpen => "自动恢复已熔断",
        DesktopStartupState.WebViewInitializing => "Workbench 加载中",
        DesktopStartupState.WorkbenchReady => "Workbench 就绪",
        DesktopStartupState.WorkbenchFailed => "Workbench 加载失败",
        _ => "未知状态",
    };

    public string StatusDescription => _state switch
    {
        DesktopStartupState.NeedsDataRoot => "选择数据目录后即可启动 Pudding Core；Desktop 仍可正常使用。",
        DesktopStartupState.InvalidConfiguration => "配置需要修正；请打开系统设置保存有效参数。",
        DesktopStartupState.CoreStopped => "Core 当前已停止，Desktop 和设置页面保持可用。",
        DesktopStartupState.CoreStarting => "正在启动隔离的 ASP.NET Core 子进程并等待 Ready 与健康检查。",
        DesktopStartupState.CoreReady => "Core 已通过健康检查，正在初始化本地 Workbench。",
        DesktopStartupState.CoreStopping => "正在请求 Core 优雅停止；超时后会回收子进程树。",
        DesktopStartupState.CoreFailed => "Core 未能启动或已意外退出，请检查最近输出。",
        DesktopStartupState.CoreRestartScheduled => "Core 意外退出，Desktop 将按退避策略自动恢复。",
        DesktopStartupState.CoreCircuitOpen => "短时间连续失败达到上限；请处理错误后手动启动或重启。",
        DesktopStartupState.WebViewInitializing => "Core 已就绪，正在创建隔离的 WebView2 Workbench。",
        DesktopStartupState.WorkbenchReady => "Desktop、Core API 与 Workbench 均已就绪。",
        DesktopStartupState.WorkbenchFailed => "Core 仍可用，但 Workbench 页面未能成功加载。",
        _ => "正在准备 Pudding Desktop。",
    };

    public bool IsCoreRunning => _state is DesktopStartupState.CoreReady
        or DesktopStartupState.WebViewInitializing
        or DesktopStartupState.WorkbenchReady
        or DesktopStartupState.WorkbenchFailed;

    public bool CanStart => _state is DesktopStartupState.CoreStopped
        or DesktopStartupState.CoreFailed
        or DesktopStartupState.CoreCircuitOpen
        or DesktopStartupState.NeedsDataRoot
        or DesktopStartupState.InvalidConfiguration;

    public bool CanStop => _state is DesktopStartupState.CoreStarting
        or DesktopStartupState.CoreReady
        or DesktopStartupState.CoreRestartScheduled
        or DesktopStartupState.WebViewInitializing
        or DesktopStartupState.WorkbenchReady
        or DesktopStartupState.WorkbenchFailed;

    public bool CanRestart => _state is DesktopStartupState.CoreReady
        or DesktopStartupState.CoreFailed
        or DesktopStartupState.CoreCircuitOpen
        or DesktopStartupState.WorkbenchReady
        or DesktopStartupState.WorkbenchFailed;

    public string? LastError => _stateError ?? _runtime.LastError;
    public bool HasError => !string.IsNullOrWhiteSpace(LastError);
    public string CoreLogText
    {
        get => _coreLogText;
        private set
        {
            if (_coreLogText == value)
                return;
            _coreLogText = value;
            OnPropertyChanged();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
        => await _coordinator.StartCoreAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken)
        => await _coordinator.StopCoreAsync(cancellationToken);

    public async Task RestartAsync(CancellationToken cancellationToken)
        => await _coordinator.RestartCoreAsync(cancellationToken);

    public async Task SetAutoRestartAsync(bool enabled, CancellationToken cancellationToken)
        => await _coordinator.SetAutoRestartAsync(enabled, cancellationToken);

    public async Task<string> CreateDiagnosticBundleAsync(CancellationToken cancellationToken)
        => await _diagnosticBundleService.CreateAsync(
            _coordinator.DataRoot ?? string.Empty,
            cancellationToken);

    public string GetLogDirectory()
    {
        var dataRoot = _coordinator.DataRoot;
        if (string.IsNullOrWhiteSpace(dataRoot))
            throw new InvalidOperationException("请先配置数据目录。");
        return Path.Combine(dataRoot, "logs");
    }

    public void RefreshTransient()
    {
        _runtime = _coordinator.RuntimeSnapshot;
        CoreLogText = _coordinator.CoreLogBuffer.GetTail(500);
        if (string.IsNullOrWhiteSpace(CoreLogText))
            CoreLogText = "尚无 Core 输出。";
        RaiseAll();
    }

    private void OnCoordinatorStateChanged(object? sender, DesktopStateChangedEventArgs e)
    {
        _state = e.Current;
        _stateError = e.Error;
        RaiseAll();
    }

    private void OnRuntimeChanged(object? sender, DesktopRuntimeChangedEventArgs e)
    {
        _runtime = e.Current;
        RaiseAll();
    }

    private void RaiseAll()
    {
        foreach (var property in new[]
        {
            nameof(State), nameof(Runtime), nameof(StatusText), nameof(StatusDescription),
            nameof(DataRoot), nameof(CoreExecutablePath), nameof(BoundAddress), nameof(LocalControlAddress), nameof(ProcessIdLabel), nameof(ProcessIdText),
            nameof(StartedAtText), nameof(UptimeText), nameof(LastExitCodeText), nameof(HealthText),
            nameof(AutoRestartEnabled), nameof(RestartAttempts), nameof(RestartStatusText),
            nameof(IsCoreRunning), nameof(CanStart), nameof(CanStop), nameof(CanRestart),
            nameof(LastError), nameof(HasError),
        })
        {
            OnPropertyChanged(property);
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;
        return duration.TotalDays >= 1
            ? $"{(int)duration.TotalDays} 天 {duration:hh\\:mm\\:ss}"
            : duration.ToString("hh\\:mm\\:ss");
    }

    private static string FormatEndpoint(Uri address) => $"{address.Host}:{address.Port}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;
        _coordinator.StateChanged -= OnCoordinatorStateChanged;
        _coordinator.RuntimeChanged -= OnRuntimeChanged;
    }
}
