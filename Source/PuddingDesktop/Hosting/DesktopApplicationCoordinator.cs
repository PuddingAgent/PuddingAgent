using PuddingCode.Configuration;
using PuddingDesktop.Configuration;
using PuddingDesktop.Core;

namespace PuddingDesktop.Hosting;

/// <summary>
/// Coordinates the always-available WPF launcher and an isolated ASP.NET Core child process.
/// </summary>
public sealed class DesktopApplicationCoordinator : IAsyncDisposable
{
    private readonly ICoreProcessSupervisor _supervisor;
    private readonly IDesktopBootstrapSettingsStore _bootstrapStore;
    private readonly ISystemConfigurationService _systemConfigService;
    private readonly IDesktopControlTokenService _tokenService;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    private MainWindow? _mainWindow;
    private CancellationTokenSource? _lifetimeCts;
    private CancellationTokenSource? _startCts;
    private DesktopBootstrapSettings? _bootstrapSettings;
    private DesktopStartupState _state = DesktopStartupState.NeedsDataRoot;
    private Uri? _coreAddress;
    private string? _lastError;
    private int _disposeState;

    public DesktopStartupState State => _state;
    public Uri? CoreAddress => _coreAddress;
    public string? DataRoot => _bootstrapSettings?.DataRoot;
    public CoreProcessLogBuffer CoreLogBuffer => _supervisor.LogBuffer;

    public event EventHandler<DesktopStateChangedEventArgs>? StateChanged;

    public DesktopApplicationCoordinator()
    {
        _bootstrapStore = new FileDesktopBootstrapSettingsStore();
        _systemConfigService = new SystemConfigurationService();
        _tokenService = new DesktopControlTokenService(_systemConfigService);
        _supervisor = new CoreProcessSupervisor();

        _supervisor.UnexpectedExit += OnSupervisorUnexpectedExit;
    }

    public async Task StartAsync(string[] args, CancellationToken cancellationToken)
    {
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _mainWindow = new MainWindow(this);
            System.Windows.Application.Current.MainWindow = _mainWindow;
            _mainWindow.Show();
        });

        _bootstrapSettings = await _bootstrapStore.LoadAsync(_lifetimeCts.Token);
        if (!TryValidateDataRoot(_bootstrapSettings, out var dataRoot))
            return;

        var systemResult = await _systemConfigService.LoadAsync(dataRoot, _lifetimeCts.Token);
        if (!systemResult.Success || systemResult.Config is null)
        {
            TransitionTo(
                DesktopStartupState.InvalidConfiguration,
                error: $"系统配置加载失败: {string.Join("; ", systemResult.Errors)}");
            return;
        }

        try
        {
            await _tokenService.GetOrCreateAsync(dataRoot, _lifetimeCts.Token);
        }
        catch (Exception ex)
        {
            TransitionTo(DesktopStartupState.InvalidConfiguration, error: $"控制令牌写入失败: {ex.Message}");
            return;
        }

        if (systemResult.Config.Desktop.Core.AutoStart)
            _ = TryStartCoreAsync(_lifetimeCts.Token);
        else
            TransitionTo(DesktopStartupState.CoreStopped);
    }

    public async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        CancellationTokenSource? operationCts = null;
        try
        {
            if (_state is DesktopStartupState.CoreReady
                or DesktopStartupState.CoreStarting
                or DesktopStartupState.CoreStopping
                or DesktopStartupState.WebViewInitializing
                or DesktopStartupState.WorkbenchReady)
            {
                return;
            }

            _bootstrapSettings = await _bootstrapStore.LoadAsync(cancellationToken);
            if (!TryValidateDataRoot(_bootstrapSettings, out var dataRoot))
                return;

            var systemResult = await _systemConfigService.LoadAsync(dataRoot, cancellationToken);
            if (!systemResult.Success || systemResult.Config is null)
            {
                TransitionTo(
                    DesktopStartupState.InvalidConfiguration,
                    error: $"系统配置加载失败: {string.Join("; ", systemResult.Errors)}");
                return;
            }

            var coreConfig = systemResult.Config.Desktop.Core;
            ValidateCoreConfiguration(coreConfig);

            var controlToken = coreConfig.ControlToken
                ?? await _tokenService.GetOrCreateAsync(dataRoot, cancellationToken);
            var executablePath = CoreExecutableResolver.Resolve(_bootstrapSettings.CoreExecutablePath);

            operationCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCts?.Token ?? CancellationToken.None);
            _startCts = operationCts;
            TransitionTo(DesktopStartupState.CoreStarting);

            var session = await _supervisor.StartAsync(
                new CoreProcessStartOptions
                {
                    ExecutablePath = executablePath,
                    DataRoot = dataRoot,
                    Port = coreConfig.Port,
                    ParentProcessId = Environment.ProcessId,
                    ControlToken = controlToken,
                    StartupTimeout = TimeSpan.FromSeconds(coreConfig.StartupTimeoutSeconds),
                    ShutdownTimeout = TimeSpan.FromSeconds(coreConfig.ShutdownTimeoutSeconds),
                },
                operationCts.Token);

            _coreAddress = session.BaseAddress;
            TransitionTo(DesktopStartupState.CoreReady, _coreAddress);
        }
        catch (OperationCanceledException) when (
            operationCts?.IsCancellationRequested == true || cancellationToken.IsCancellationRequested)
        {
            _coreAddress = null;
            TransitionTo(DesktopStartupState.CoreStopped);
        }
        catch (Exception ex)
        {
            _coreAddress = null;
            TransitionTo(DesktopStartupState.CoreFailed, error: ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_startCts, operationCts))
                _startCts = null;
            operationCts?.Dispose();
            _operationLock.Release();
        }
    }

    public async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        _startCts?.Cancel();
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (_state is DesktopStartupState.CoreStopped
                or DesktopStartupState.NeedsDataRoot
                or DesktopStartupState.InvalidConfiguration)
            {
                return;
            }

            TransitionTo(DesktopStartupState.CoreStopping);
            await _supervisor.StopAsync(cancellationToken);
            _coreAddress = null;
            TransitionTo(DesktopStartupState.CoreStopped);
        }
        catch (Exception ex)
        {
            _coreAddress = null;
            TransitionTo(DesktopStartupState.CoreFailed, error: ex.Message);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task RestartCoreAsync(CancellationToken cancellationToken)
    {
        await StopCoreAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await StartCoreAsync(cancellationToken);
    }

    private bool TryValidateDataRoot(DesktopBootstrapSettings settings, out string dataRoot)
    {
        dataRoot = settings.DataRoot?.Trim() ?? string.Empty;
        if (dataRoot.Length == 0)
        {
            TransitionTo(DesktopStartupState.NeedsDataRoot, error: "请前往「系统设置」设置数据目录");
            return false;
        }

        if (!Directory.Exists(dataRoot))
        {
            TransitionTo(
                DesktopStartupState.InvalidConfiguration,
                error: $"数据目录不存在: {dataRoot}");
            return false;
        }

        return true;
    }

    private static void ValidateCoreConfiguration(PuddingDesktopCoreConfig config)
    {
        if (config.Port is < 0 or > 65535)
            throw new InvalidOperationException("Core 端口必须为 0 到 65535；0 表示动态端口。");
        if (config.StartupTimeoutSeconds is < 1 or > 600)
            throw new InvalidOperationException("Core 启动超时必须为 1 到 600 秒。");
        if (config.ShutdownTimeoutSeconds is < 1 or > 120)
            throw new InvalidOperationException("Core 停止超时必须为 1 到 120 秒。");
    }

    private async Task TryStartCoreAsync(CancellationToken cancellationToken)
    {
        try { await StartCoreAsync(cancellationToken); }
        catch (OperationCanceledException) { }
    }

    private void OnSupervisorUnexpectedExit(object? sender, CoreProcessExitedEventArgs e)
    {
        var error = $"Core 进程意外退出 (PID: {e.ProcessId}, exit code: {e.ExitCode})";
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _coreAddress = null;
            TransitionTo(DesktopStartupState.CoreFailed, error: error);
        });
    }

    public void BeginWebViewInitialization()
    {
        if (_state == DesktopStartupState.CoreReady)
            TransitionTo(DesktopStartupState.WebViewInitializing, _coreAddress);
    }

    public void NotifyWorkbenchReady()
    {
        if (_state == DesktopStartupState.WebViewInitializing)
            TransitionTo(DesktopStartupState.WorkbenchReady, _coreAddress);
    }

    public void NotifyWorkbenchFailed(string error)
    {
        if (_state == DesktopStartupState.WebViewInitializing)
            TransitionTo(DesktopStartupState.WorkbenchFailed, _coreAddress, error);
    }

    private void TransitionTo(
        DesktopStartupState newState,
        Uri? coreAddress = null,
        string? error = null)
    {
        var previous = _state;
        _state = newState;
        if (coreAddress is not null)
            _coreAddress = coreAddress;
        _lastError = error;

        StateChanged?.Invoke(
            this,
            new DesktopStateChangedEventArgs(previous, newState, _coreAddress, _lastError));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _lifetimeCts?.Cancel();
        _startCts?.Cancel();
        await StopCoreAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        _lifetimeCts?.Cancel();
        _startCts?.Cancel();
        await _operationLock.WaitAsync();
        try
        {
            try { await _supervisor.StopAsync(CancellationToken.None); }
            catch { }
        }
        finally
        {
            _operationLock.Release();
        }

        _supervisor.UnexpectedExit -= OnSupervisorUnexpectedExit;
        await _supervisor.DisposeAsync();
        _startCts?.Dispose();
        _lifetimeCts?.Dispose();
        _operationLock.Dispose();
    }
}
