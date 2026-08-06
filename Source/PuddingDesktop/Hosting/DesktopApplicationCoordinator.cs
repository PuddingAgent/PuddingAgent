using PuddingCode.Configuration;
using PuddingDesktop.Bootstrap;
using PuddingDesktop.Browser;
using PuddingDesktop.Configuration;
using PuddingDesktop.Core;
using PuddingDesktop.Diagnostics;
using PuddingDesktop.Runtime;

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
    private readonly IDesktopRuntimeOrchestrator _runtime;
    private readonly DesktopBackgroundModeService _backgroundMode = new();
    private readonly BrowserBridgeCommandDispatcher _bridgeDispatcher = new();
    private readonly DesktopBrowserBridgeClient _bridgeClient;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly SemaphoreSlim _bridgeOperationLock = new(1, 1);

    private MainWindow? _mainWindow;
    private CancellationTokenSource? _lifetimeCts;
    private CancellationTokenSource? _startCts;
    private DesktopBootstrapSettings? _bootstrapSettings;
    private CoreProcessStartOptions? _runtimeOptions;
    private DesktopStartupState _state = DesktopStartupState.NeedsDataRoot;
    private Uri? _coreAddress;
    private string? _lastError;
    private long _bridgeIntentVersion;
    private int _disposeState;
    private DesktopBootstrapSignalService? _bootstrapSignalService;

    public DesktopStartupState State => _state;
    public Uri? CoreAddress => _coreAddress;
    public string? DataRoot => _bootstrapSettings?.DataRoot;
    public CoreProcessLogBuffer CoreLogBuffer => _supervisor.LogBuffer;
    public DesktopRuntimeSnapshot RuntimeSnapshot => _runtime.Snapshot;
    public string? CoreExecutablePath => _runtimeOptions?.ExecutablePath;
    public DesktopBackgroundModeService BackgroundMode => _backgroundMode;
    public BrowserBridgeCommandDispatcher BridgeDispatcher => _bridgeDispatcher;
    public IDesktopBrowserBridgeClient BridgeClient => _bridgeClient;

    public event EventHandler<DesktopStateChangedEventArgs>? StateChanged;
    public event EventHandler<DesktopRuntimeChangedEventArgs>? RuntimeChanged;

    public DesktopApplicationCoordinator()
    {
        _bootstrapStore = new FileDesktopBootstrapSettingsStore();
        _systemConfigService = new SystemConfigurationService();
        _tokenService = new DesktopControlTokenService(_systemConfigService);
        _supervisor = new CoreProcessSupervisor();
        _runtime = new DesktopRuntimeOrchestrator(_supervisor);
        _bridgeClient = new DesktopBrowserBridgeClient(_bridgeDispatcher);
        _runtime.Changed += OnRuntimeChanged;
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
        _backgroundMode.Configure(_bootstrapSettings.CloseBehavior);
        if (args.Any(arg => string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase)))
            _mainWindow?.Hide();
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

        // Agent Browser is a Desktop capability. It becomes available as soon as
        // DataRoot and system configuration are ready, independently of Core.
        await TryInitializeBrowserWorkspaceAsync(dataRoot, _lifetimeCts.Token);

        // Guided bootstrap signal service: zero behavior change when disabled or
        // when no signal file ever appears.
        StartBootstrapSignalService(dataRoot, systemResult.Config.Desktop.Bootstrap, _lifetimeCts.Token);

        if (systemResult.Config.Desktop.Core.AutoStart)
            _ = TryStartCoreAsync(_lifetimeCts.Token);
        else
            TransitionTo(DesktopStartupState.CoreStopped);
    }

    private void StartBootstrapSignalService(
        string dataRoot,
        PuddingDesktopBootstrapConfig bootstrapConfig,
        CancellationToken cancellationToken)
    {
        if (!bootstrapConfig.Enabled)
            return;

        try
        {
            var service = new DesktopBootstrapSignalService(this, dataRoot, bootstrapConfig, _tokenService);
            service.Start(cancellationToken);
            _bootstrapSignalService = service;
        }
        catch (Exception ex)
        {
            DesktopDiagnosticLog.Write("BootstrapSignalStart", ex);
        }
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

            operationCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCts?.Token ?? CancellationToken.None);
            _startCts = operationCts;
            await TryInitializeBrowserWorkspaceAsync(dataRoot, operationCts.Token);
            await ConfigureRuntimeAsync(
                _bootstrapSettings,
                dataRoot,
                systemResult.Config.Desktop.Core,
                operationCts.Token);
            await _runtime.StartAsync(operationCts.Token);
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

            await _runtime.StopAsync(cancellationToken);
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
        _startCts?.Cancel();
        await _operationLock.WaitAsync(cancellationToken);
        CancellationTokenSource? operationCts = null;
        try
        {
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

            operationCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCts?.Token ?? CancellationToken.None);
            _startCts = operationCts;
            await TryInitializeBrowserWorkspaceAsync(dataRoot, operationCts.Token);
            await ConfigureRuntimeAsync(
                _bootstrapSettings,
                dataRoot,
                systemResult.Config.Desktop.Core,
                operationCts.Token);
            await _runtime.RestartAsync(operationCts.Token);
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

    public async Task SetAutoRestartAsync(bool enabled, CancellationToken cancellationToken)
    {
        _bootstrapSettings = await _bootstrapStore.LoadAsync(cancellationToken);
        if (!TryValidateDataRoot(_bootstrapSettings, out var dataRoot))
            return;

        await _systemConfigService.UpdateDesktopCoreSettingsAsync(
            dataRoot,
            current => current with { AutoRestart = enabled },
            cancellationToken);
        await _runtime.SetAutoRestartAsync(enabled, cancellationToken);
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

        _ = CreateRestartPolicy(config).Validate();
    }

    private async Task ConfigureRuntimeAsync(
        DesktopBootstrapSettings bootstrapSettings,
        string dataRoot,
        PuddingDesktopCoreConfig coreConfig,
        CancellationToken cancellationToken)
    {
        ValidateCoreConfiguration(coreConfig);
        var controlToken = coreConfig.ControlToken
            ?? await _tokenService.GetOrCreateAsync(dataRoot, cancellationToken);

        _runtimeOptions = new CoreProcessStartOptions
        {
            ExecutablePath = CoreExecutableResolver.Resolve(bootstrapSettings.CoreExecutablePath),
            DataRoot = dataRoot,
            Port = coreConfig.Port,
            ParentProcessId = Environment.ProcessId,
            ControlToken = controlToken,
            StartupTimeout = TimeSpan.FromSeconds(coreConfig.StartupTimeoutSeconds),
            ShutdownTimeout = TimeSpan.FromSeconds(coreConfig.ShutdownTimeoutSeconds),
        };
        _runtime.Configure(_runtimeOptions, CreateRestartPolicy(coreConfig));
    }

    private async Task TryInitializeBrowserWorkspaceAsync(
        string dataRoot,
        CancellationToken cancellationToken)
    {
        var window = _mainWindow;
        if (window is null) return;

        try
        {
            if (window.Dispatcher.CheckAccess())
            {
                await window.InitializeBrowserWorkspaceAsync(dataRoot, cancellationToken);
                return;
            }

            var nestedTask = await window.Dispatcher.InvokeAsync(
                () => window.InitializeBrowserWorkspaceAsync(dataRoot, cancellationToken));
            await nestedTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Desktop shutdown owns cancellation.
        }
        catch
        {
            // Browser initialization failure is shown in its own page and never
            // blocks the Launcher or Core process supervisor.
        }
    }

    private static CoreRestartPolicy CreateRestartPolicy(PuddingDesktopCoreConfig config) => new()
    {
        Enabled = config.AutoRestart,
        MaxAttempts = config.RestartMaxAttempts,
        WindowSeconds = config.RestartWindowSeconds,
        InitialDelaySeconds = config.RestartInitialDelaySeconds,
        MaxDelaySeconds = config.RestartMaxDelaySeconds,
    };

    private async Task TryStartCoreAsync(CancellationToken cancellationToken)
    {
        try { await StartCoreAsync(cancellationToken); }
        catch (OperationCanceledException) { }
    }

    private void OnRuntimeChanged(object? sender, DesktopRuntimeChangedEventArgs e)
    {
        void Apply()
        {
            switch (e.Current.State)
            {
                case DesktopRuntimeState.Starting:
                    TransitionTo(DesktopStartupState.CoreStarting);
                    break;
                case DesktopRuntimeState.Ready when e.Current.Session is not null:
                    _coreAddress = e.Current.Session.BaseAddress;
                    TransitionTo(DesktopStartupState.CoreReady, _coreAddress);
                    QueueBridgeIntent(connect: true, _coreAddress);
                    break;
                case DesktopRuntimeState.Stopping:
                    QueueBridgeIntent(connect: false, null);
                    TransitionTo(DesktopStartupState.CoreStopping);
                    break;
                case DesktopRuntimeState.Stopped:
                    QueueBridgeIntent(connect: false, null);
                    _coreAddress = null;
                    TransitionTo(DesktopStartupState.CoreStopped);
                    break;
                case DesktopRuntimeState.RestartScheduled:
                    QueueBridgeIntent(connect: false, null);
                    _coreAddress = null;
                    TransitionTo(DesktopStartupState.CoreRestartScheduled, error: e.Current.LastError);
                    break;
                case DesktopRuntimeState.CircuitOpen:
                    QueueBridgeIntent(connect: false, null);
                    _coreAddress = null;
                    TransitionTo(DesktopStartupState.CoreCircuitOpen, error: e.Current.LastError);
                    break;
                case DesktopRuntimeState.Failed:
                    QueueBridgeIntent(connect: false, null);
                    _coreAddress = null;
                    TransitionTo(DesktopStartupState.CoreFailed, error: e.Current.LastError);
                    break;
            }

            RuntimeChanged?.Invoke(this, e);
        }

        var application = System.Windows.Application.Current;
        if (application is null || application.Dispatcher.CheckAccess())
            Apply();
        else
            application.Dispatcher.BeginInvoke(Apply);
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

    public void ActivateMainWindow()
    {
        var window = _mainWindow;
        if (window is null)
            return;

        void Activate()
        {
            window.Show();
            if (window.WindowState == System.Windows.WindowState.Minimized)
                window.WindowState = System.Windows.WindowState.Normal;
            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
            window.Focus();
        }

        if (window.Dispatcher.CheckAccess())
            Activate();
        else
            window.Dispatcher.BeginInvoke(Activate);
    }

    public void RequestExplicitExit() => _backgroundMode.RequestExplicitExit();

    public void ApplyCloseBehavior(DesktopCloseBehavior closeBehavior)
        => _backgroundMode.Configure(closeBehavior);

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

        if (_bootstrapSignalService is not null)
        {
            try { await _bootstrapSignalService.DisposeAsync(); }
            catch { }
            _bootstrapSignalService = null;
        }

        Interlocked.Increment(ref _bridgeIntentVersion);
        await _bridgeOperationLock.WaitAsync();
        try
        {
            // Dispose Bridge before stopping Core and after any owned lifecycle
            // transition has observed Desktop cancellation.
            await _bridgeClient.DisposeAsync();
        }
        finally
        {
            _bridgeOperationLock.Release();
        }

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

        _runtime.Changed -= OnRuntimeChanged;
        await _runtime.DisposeAsync();
        await _supervisor.DisposeAsync();
        _startCts?.Dispose();
        _lifetimeCts?.Dispose();
        _bridgeOperationLock.Dispose();
        _operationLock.Dispose();
    }

    private void QueueBridgeIntent(bool connect, Uri? coreAddress)
    {
        var version = Interlocked.Increment(ref _bridgeIntentVersion);
        var lifetimeToken = _lifetimeCts?.Token ?? CancellationToken.None;
        _ = RunBridgeIntentAsync(version, connect, coreAddress, lifetimeToken);
    }

    private async Task RunBridgeIntentAsync(
        long version,
        bool connect,
        Uri? coreAddress,
        CancellationToken cancellationToken)
    {
        try
        {
            await _bridgeOperationLock.WaitAsync(cancellationToken);
            try
            {
                if (version != Interlocked.Read(ref _bridgeIntentVersion))
                    return;

                if (!connect)
                {
                    await _bridgeClient.DisconnectAsync(cancellationToken);
                    return;
                }

                var dataRoot = _bootstrapSettings?.DataRoot;
                if (dataRoot is null || coreAddress is null)
                    return;

                var token = await _tokenService.GetOrCreateAsync(dataRoot, cancellationToken);
                if (version != Interlocked.Read(ref _bridgeIntentVersion))
                    return;

                await _bridgeClient.ConnectAsync(coreAddress, token, cancellationToken);
            }
            finally
            {
                _bridgeOperationLock.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Desktop lifetime owns cancellation.
        }
        catch
        {
            // Bridge connection failure is non-fatal; the client will retry
            // while the same owned intent remains current.
        }
    }
}
