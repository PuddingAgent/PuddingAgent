using System.ComponentModel;
using System.Runtime.CompilerServices;
using PuddingDesktop.Hosting;

namespace PuddingDesktop.ViewModels;

public sealed class CoreStatusViewModel : INotifyPropertyChanged
{
    private readonly DesktopApplicationCoordinator _coordinator;

    private DesktopStartupState _state;
    private string _statusText = "就绪";
    private string _boundAddress = "127.0.0.1:0";
    private bool _isCoreRunning;
    private bool _canStart;
    private bool _canStop;
    private bool _canRestart;
    private string? _lastError;

    public CoreStatusViewModel(DesktopApplicationCoordinator coordinator)
    {
        _coordinator = coordinator;
        _state = coordinator.State;
        UpdateDerived();

        coordinator.StateChanged += OnCoordinatorStateChanged;
    }

    private void OnCoordinatorStateChanged(object? sender, DesktopStateChangedEventArgs e)
    {
        State = e.Current;
        BoundAddress = e.CoreAddress?.Authority ?? "未绑定";
        LastError = e.Error;
        OnPropertyChanged(nameof(DataRoot));
        OnPropertyChanged(nameof(StatusDescription));
    }

    public DesktopStartupState State
    {
        get => _state;
        set { _state = value; OnPropertyChanged(); UpdateDerived(); }
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public string BoundAddress
    {
        get => _boundAddress;
        set { _boundAddress = value; OnPropertyChanged(); }
    }

    public bool IsCoreRunning
    {
        get => _isCoreRunning;
        set { _isCoreRunning = value; OnPropertyChanged(); }
    }

    public bool CanStart
    {
        get => _canStart;
        set { _canStart = value; OnPropertyChanged(); }
    }

    public bool CanStop
    {
        get => _canStop;
        set { _canStop = value; OnPropertyChanged(); }
    }

    public bool CanRestart
    {
        get => _canRestart;
        set { _canRestart = value; OnPropertyChanged(); }
    }

    public string? LastError
    {
        get => _lastError;
        set { _lastError = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(_lastError);
    public string DataRoot => _coordinator.DataRoot ?? "未配置";

    public string StatusDescription => _state switch
    {
        DesktopStartupState.NeedsDataRoot => "选择数据目录后即可启动 Pudding Core。启动器仍可正常使用。",
        DesktopStartupState.InvalidConfiguration => "配置需要修正；请打开系统设置查看并保存有效参数。",
        DesktopStartupState.CoreStopped => "Core 当前已停止。桌面启动器和设置页面仍保持可用。",
        DesktopStartupState.CoreStarting => "正在启动隔离的 ASP.NET Core 子进程并等待就绪信号。",
        DesktopStartupState.CoreReady => "Core 已就绪，正在初始化本地 Workbench。",
        DesktopStartupState.CoreStopping => "正在请求 Core 优雅停止；超时后将回收子进程。",
        DesktopStartupState.CoreFailed => "Core 未能启动或已意外退出。可检查错误信息后重试。",
        DesktopStartupState.WebViewInitializing => "Core 已就绪，正在创建独立的 WebView2 Workbench 环境。",
        DesktopStartupState.WorkbenchReady => "Workbench、Core API 与桌面启动器均已就绪。",
        DesktopStartupState.WorkbenchFailed => "Core 仍可用，但 Workbench 页面未能成功加载。",
        _ => "正在准备 Pudding Desktop。",
    };

    private void UpdateDerived()
    {
        IsCoreRunning = _state is DesktopStartupState.CoreReady
            or DesktopStartupState.WebViewInitializing
            or DesktopStartupState.WorkbenchReady
            or DesktopStartupState.WorkbenchFailed;

        CanStart = _state is DesktopStartupState.CoreStopped or DesktopStartupState.CoreFailed
            or DesktopStartupState.NeedsDataRoot or DesktopStartupState.InvalidConfiguration;

        CanStop = _state is DesktopStartupState.CoreStarting
            or DesktopStartupState.CoreReady
            or DesktopStartupState.WebViewInitializing
            or DesktopStartupState.WorkbenchReady
            or DesktopStartupState.WorkbenchFailed;

        CanRestart = _state is DesktopStartupState.CoreReady or DesktopStartupState.CoreFailed
            or DesktopStartupState.WorkbenchReady or DesktopStartupState.WorkbenchFailed;

        StatusText = _state switch
        {
            DesktopStartupState.NeedsDataRoot => "需要配置数据目录",
            DesktopStartupState.InvalidConfiguration => "配置无效",
            DesktopStartupState.CoreStopped => "Core: 已停止",
            DesktopStartupState.CoreStarting => "Core: 启动中...",
            DesktopStartupState.CoreReady => "Core: 运行中",
            DesktopStartupState.CoreStopping => "Core: 停止中...",
            DesktopStartupState.CoreFailed => "Core: 启动失败",
            DesktopStartupState.WebViewInitializing => "Workbench: 加载中...",
            DesktopStartupState.WorkbenchReady => "Workbench: 就绪",
            DesktopStartupState.WorkbenchFailed => "Workbench: 加载失败",
            _ => "未知状态"
        };

        OnPropertyChanged(nameof(StatusDescription));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
