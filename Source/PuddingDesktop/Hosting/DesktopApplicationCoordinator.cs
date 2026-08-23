using PuddingCode.Configuration;
using PuddingDesktop.Bootstrap;
using PuddingDesktop.Browser;
using PuddingDesktop.Configuration;
using PuddingDesktop.Core;
using PuddingDesktop.Debug;
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
    private readonly SemaphoreSlim _debugComponentsLock = new(1, 1);

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
    private DesktopBootstrapHttpEndpoint? _bootstrapHttpEndpoint;
    private DesktopDebugSettings? _debugSettings;
    private FrontendDevSupervisor? _frontendSupervisor;
    private DesktopReverseProxy? _debugProxy;
    private string? _debugFailure;
    private Uri? _workbenchAddress;

    public DesktopStartupState State => _state;
    public Uri? CoreAddress => _coreAddress;
    public Uri? WorkbenchAddress => _workbenchAddress;
    public string? DataRoot => _bootstrapSettings?.DataRoot;
    public CoreProcessLogBuffer CoreLogBuffer => _supervisor.LogBuffer;
    public DesktopRuntimeSnapshot RuntimeSnapshot => _runtime.Snapshot;
    public string? CoreExecutablePath => _runtimeOptions?.ExecutablePath;
    public DesktopBackgroundModeService BackgroundMode => _backgroundMode;
    public BrowserBridgeCommandDispatcher BridgeDispatcher => _bridgeDispatcher;
    public IDesktopBrowserBridgeClient BridgeClient => _bridgeClient;

    public Task<string> GetDesktopControlTokenAsync(CancellationToken cancellationToken)
    {
        var dataRoot = _bootstrapSettings?.DataRoot;
        return string.IsNullOrWhiteSpace(dataRoot)
            ? Task.FromException<string>(new InvalidOperationException("尚未配置数据目录。"))
            : _tokenService.GetOrCreateAsync(dataRoot, cancellationToken);
    }

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

        // Guided bootstrap services: the loopback HTTP control endpoint (default
        // on) plus the opt-in signal-file polling loop (default off).
        StartBootstrapServices(dataRoot, systemResult.Config.Desktop.Bootstrap, _lifetimeCts.Token);

        if (systemResult.Config.Desktop.Core.AutoStart)
            _ = TryStartCoreAsync(_lifetimeCts.Token);
        else
            TransitionTo(DesktopStartupState.CoreStopped);
    }

    /// <summary>
    /// Starts the bootstrap services. The signal service object is always
    /// created (it powers both the API/UI trigger and the polling loop); the
    /// polling loop only starts when config.Enabled, and the loopback HTTP
    /// endpoint only when config.HttpEnabled. Failures are logged and never
    /// block Desktop startup.
    /// </summary>
    private void StartBootstrapServices(
        string dataRoot,
        PuddingDesktopBootstrapConfig bootstrapConfig,
        CancellationToken cancellationToken)
    {
        try
        {
            var service = new DesktopBootstrapSignalService(this, dataRoot, bootstrapConfig, _tokenService);
            _bootstrapSignalService = service;

            if (bootstrapConfig.Enabled)
                service.Start(cancellationToken);
        }
        catch (Exception ex)
        {
            DesktopDiagnosticLog.Write("BootstrapSignalStart", ex);
            _bootstrapSignalService = null;
        }

        if (bootstrapConfig.HttpEnabled && _bootstrapSignalService is not null)
        {
            try
            {
                var endpoint = new DesktopBootstrapHttpEndpoint(
                    _bootstrapSignalService, _tokenService, dataRoot, bootstrapConfig.HttpPort,
                    () => RuntimeSnapshot.State.ToString());
                endpoint.Start(cancellationToken);
                _bootstrapHttpEndpoint = endpoint;
            }
            catch (Exception ex)
            {
                DesktopDiagnosticLog.Write("BootstrapHttpStart", ex);
            }
        }
    }

    /// <summary>
    /// Manually triggers one rebuild-restart cycle (used by the UI button).
    /// Delegates to the bootstrap signal service; throws InvalidOperationException
    /// when another bootstrap is already running.
    /// </summary>
    public Task<DesktopBootstrapResult> TriggerBootstrapAsync(
        string? requestedBy, bool yolo, CancellationToken ct = default)
    {
        var service = _bootstrapSignalService
            ?? throw new InvalidOperationException("bootstrap signal service is not available.");
        return service.TriggerRebuildRestartAsync(requestedBy, yolo, ct);
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
            var debugExecutablePath = await BuildDebugBackendAsync(
                _bootstrapSettings, systemResult.Config.Desktop.Core, operationCts.Token);
            await ConfigureRuntimeAsync(
                _bootstrapSettings,
                dataRoot,
                systemResult.Config.Desktop.Core,
                operationCts.Token,
                debugExecutablePath);
            if (debugExecutablePath is not null)
                _ = StartDebugComponentsAsync(_lifetimeCts?.Token ?? CancellationToken.None);
            await _runtime.StartAsync(operationCts.Token);
        }
        catch (OperationCanceledException) when (
            operationCts?.IsCancellationRequested == true || cancellationToken.IsCancellationRequested)
        {
            _coreAddress = null;
            TransitionTo(DesktopStartupState.CoreStopped);
        }
        catch (DebugComponentsFailedException)
        {
            // The debug pipeline already transitioned to DebugFailed with the
            // underlying error; keep it instead of masking it as CoreFailed.
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
            var debugExecutablePath = await BuildDebugBackendAsync(
                _bootstrapSettings, systemResult.Config.Desktop.Core, operationCts.Token);
            await ConfigureRuntimeAsync(
                _bootstrapSettings,
                dataRoot,
                systemResult.Config.Desktop.Core,
                operationCts.Token,
                debugExecutablePath);
            if (debugExecutablePath is not null)
                _ = StartDebugComponentsAsync(_lifetimeCts?.Token ?? CancellationToken.None);
            await _runtime.RestartAsync(operationCts.Token);
        }
        catch (OperationCanceledException) when (
            operationCts?.IsCancellationRequested == true || cancellationToken.IsCancellationRequested)
        {
            _coreAddress = null;
            TransitionTo(DesktopStartupState.CoreStopped);
        }
        catch (DebugComponentsFailedException)
        {
            // The debug pipeline already transitioned to DebugFailed with the
            // underlying error; keep it instead of masking it as CoreFailed.
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
        if (config.Port is < 1 or > 65535)
            throw new InvalidOperationException("Core 固定监听端口必须为 1 到 65535。");
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
        CancellationToken cancellationToken,
        string? debugExecutablePath = null)
    {
        ValidateCoreConfiguration(coreConfig);
        var controlToken = coreConfig.ControlToken
            ?? await _tokenService.GetOrCreateAsync(dataRoot, cancellationToken);

        _runtimeOptions = new CoreProcessStartOptions
        {
            ExecutablePath = debugExecutablePath
                ?? CoreExecutableResolver.Resolve(bootstrapSettings.CoreExecutablePath),
            DataRoot = dataRoot,
            Port = coreConfig.Port,
            ParentProcessId = Environment.ProcessId,
            ControlToken = controlToken,
            StartupTimeout = TimeSpan.FromSeconds(coreConfig.StartupTimeoutSeconds),
            ShutdownTimeout = TimeSpan.FromSeconds(coreConfig.ShutdownTimeoutSeconds),
            EnvironmentName = debugExecutablePath is null ? null : "Development",
        };
        _runtime.Configure(_runtimeOptions, CreateRestartPolicy(coreConfig));
    }

    /// <summary>
    /// Debug pipeline stage 1: validate debug settings and build the backend
    /// from source. Returns the built exe path, or null when debug mode is
    /// off. Failures transition to DebugFailed and rethrow a marker that the
    /// Start/Restart catch blocks translate to a no-op.
    /// </summary>
    private async Task<string?> BuildDebugBackendAsync(
        DesktopBootstrapSettings bootstrapSettings,
        PuddingDesktopCoreConfig coreConfig,
        CancellationToken cancellationToken)
    {
        var debug = bootstrapSettings.Debug;
        if (!debug.Enabled)
        {
            _debugSettings = null;
            return null;
        }

        ValidateDebugConfiguration(debug, coreConfig);
        _debugSettings = debug;
        _debugFailure = null;

        try
        {
            var backendProjectPath = DebugRepositoryResolver.ResolveBackendProjectPath(debug);
            var executablePath = await new DebugBackendLauncher().BuildAsync(
                backendProjectPath,
                TimeSpan.FromSeconds(debug.BackendBuildTimeoutSeconds),
                _supervisor.LogBuffer,
                cancellationToken);
            return executablePath;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _debugFailure = ex.Message;
            TransitionOnDispatcher(DesktopStartupState.DebugFailed, error: ex.Message);
            throw new DebugComponentsFailedException(ex.Message, ex);
        }
    }

    /// <summary>
    /// Debug pipeline stage 2 (fire-and-forget, parallel to Core startup):
    /// starts the loopback reverse proxy and the pnpm frontend dev server.
    /// Core keeps its own supervision; frontend readiness re-fires CoreReady
    /// so the Workbench binds to the proxy once everything is up.
    /// </summary>
    private async Task StartDebugComponentsAsync(CancellationToken cancellationToken)
    {
        // Re-entrancy guard: Start/Restart can both fire this while a previous
        // frontend bring-up (up to minutes) is still in flight.
        if (!await _debugComponentsLock.WaitAsync(0))
            return;

        try
        {
            var debug = _debugSettings;
            if (debug is null)
                return;

            if (_debugProxy is null)
            {
                var backendBase = new Uri(
                    $"http://127.0.0.1:{_runtimeOptions?.Port ?? PuddingDesktopCoreConfig.DefaultPort}");
                var frontendBase = new Uri($"http://127.0.0.1:{debug.FrontendPort}");
                var proxy = new DesktopReverseProxy(backendBase, frontendBase, debug.ProxyPort);
                try
                {
                    proxy.Start();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"调试代理无法监听 127.0.0.1:{debug.ProxyPort}（可能被占用，请先停止 dev-up 或检查 IIS）：{ex.Message}",
                        ex);
                }

                _debugProxy = proxy;
                _supervisor.LogBuffer.Append($"[Debug] Reverse proxy listening: {proxy.BaseAddress}");
            }

            if (_frontendSupervisor is null)
            {
                var supervisor = new FrontendDevSupervisor();
                supervisor.StateChanged += OnFrontendStateChanged;
                _frontendSupervisor = supervisor;
            }

            if (_frontendSupervisor.State is FrontendDevState.Idle
                or FrontendDevState.Failed
                or FrontendDevState.Stopped)
            {
                await _frontendSupervisor.StartAsync(new FrontendDevStartOptions
                {
                    WorkingDirectory = DebugRepositoryResolver.ResolveFrontendWorkingDirectory(debug),
                    Port = debug.FrontendPort,
                    StartupTimeout = TimeSpan.FromSeconds(debug.FrontendStartupTimeoutSeconds),
                }, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Desktop shutdown owns cancellation.
        }
        catch (Exception ex)
        {
            _debugFailure = ex.Message;
            TransitionOnDispatcher(DesktopStartupState.DebugFailed, error: ex.Message);
        }
        finally
        {
            _debugComponentsLock.Release();
        }
    }

    private void OnFrontendStateChanged(object? sender, FrontendDevState state)
    {
        if (state == FrontendDevState.Ready)
        {
            // Re-fire CoreReady so the Workbench sees a non-null
            // WorkbenchAddress now that every debug component is up.
            if (_state == DesktopStartupState.CoreReady)
                TransitionOnDispatcher(DesktopStartupState.CoreReady);
        }
        else if (state == FrontendDevState.Failed)
        {
            _debugFailure = _frontendSupervisor?.LastError ?? "前端开发服务器运行失败。";
            TransitionOnDispatcher(DesktopStartupState.DebugFailed, error: _debugFailure);
        }
    }

    private static void ValidateDebugConfiguration(
        DesktopDebugSettings debug,
        PuddingDesktopCoreConfig coreConfig)
    {
        if (debug.FrontendPort is < 1 or > 65535)
            throw new InvalidOperationException("调试前端端口必须为 1 到 65535。");
        if (debug.ProxyPort is < 1 or > 65535)
            throw new InvalidOperationException("调试代理端口必须为 1 到 65535。");
        if (debug.FrontendStartupTimeoutSeconds is < 10 or > 3600)
            throw new InvalidOperationException("调试前端启动超时必须为 10 到 3600 秒。");
        if (debug.BackendBuildTimeoutSeconds is < 10 or > 3600)
            throw new InvalidOperationException("调试后端构建超时必须为 10 到 3600 秒。");
        if (debug.ProxyPort == debug.FrontendPort)
            throw new InvalidOperationException("调试代理端口不能与前端端口相同。");
        if (debug.ProxyPort == coreConfig.Port)
            throw new InvalidOperationException($"调试代理端口不能与 Core 端口（{coreConfig.Port}）相同。");
        if (debug.FrontendPort == coreConfig.Port)
            throw new InvalidOperationException($"调试前端端口不能与 Core 端口（{coreConfig.Port}）相同。");
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
                    TransitionTo(
                        _debugFailure is null
                            ? DesktopStartupState.CoreReady
                            : DesktopStartupState.DebugFailed,
                        _coreAddress);
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
        _workbenchAddress = GetWorkbenchAddress();

        StateChanged?.Invoke(
            this,
            new DesktopStateChangedEventArgs(previous, newState, _coreAddress, _workbenchAddress, _lastError));
    }

    /// <summary>
    /// The origin the Workbench must load: the debug reverse proxy once every
    /// debug component is up, otherwise the Core child itself.
    /// </summary>
    private Uri? GetWorkbenchAddress()
    {
        if (_debugSettings is null)
            return _coreAddress;

        return _debugFailure is null
            && _debugProxy is not null
            && _frontendSupervisor?.State == FrontendDevState.Ready
            ? _debugProxy.BaseAddress
            : null;
    }

    /// <summary>
    /// Transitions a state raised from background threads (debug build,
    /// frontend probes) onto the WPF dispatcher, mirroring OnRuntimeChanged.
    /// </summary>
    private void TransitionOnDispatcher(DesktopStartupState newState, string? error = null)
    {
        var application = System.Windows.Application.Current;
        if (application is null || application.Dispatcher.CheckAccess())
        {
            TransitionTo(newState, error: error);
            return;
        }

        application.Dispatcher.BeginInvoke(() => TransitionTo(newState, error: error));
    }

    /// <summary>
    /// Marker for debug pipeline failures that already transitioned the
    /// coordinator into DebugFailed; caught and swallowed by the Start/Restart
    /// catch blocks so the error is not masked as CoreFailed.
    /// </summary>
    private sealed class DebugComponentsFailedException : Exception
    {
        public DebugComponentsFailedException(string message, Exception inner)
            : base(message, inner)
        {
        }
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

        if (_debugProxy is not null)
        {
            try { await _debugProxy.DisposeAsync(); }
            catch { }
            _debugProxy = null;
        }

        if (_frontendSupervisor is not null)
        {
            _frontendSupervisor.StateChanged -= OnFrontendStateChanged;
            try { await _frontendSupervisor.DisposeAsync(); }
            catch { }
            _frontendSupervisor = null;
        }

        if (_bootstrapHttpEndpoint is not null)
        {
            try { await _bootstrapHttpEndpoint.DisposeAsync(); }
            catch { }
            _bootstrapHttpEndpoint = null;
        }

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
        _debugComponentsLock.Dispose();
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
