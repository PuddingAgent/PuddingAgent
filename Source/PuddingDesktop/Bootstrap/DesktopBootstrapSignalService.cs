using System.Diagnostics;
using System.Text;
using PuddingCode.Configuration;
using PuddingDesktop.Configuration;
using PuddingDesktop.Diagnostics;
using PuddingDesktop.Hosting;
using PuddingDesktop.Runtime;

namespace PuddingDesktop.Bootstrap;

/// <summary>
/// Guided-bootstrap signal service.
/// Polls a signal file (default &lt;DataRoot&gt;\config\rebuild.signal) every second,
/// and exposes the manual API trigger used by the loopback HTTP endpoint
/// (DesktopBootstrapHttpEndpoint) and the UI. On a valid "rebuild-restart"
/// trigger it runs the closed loop:
///   stop Core → build or accept a prebuilt artifact → transactional deployment
///   → hash verification → write yolo.signal (optional) → restart Core.
/// Every attempt (success or failure) writes a result file; malformed signals are
/// deleted to prevent a retry loop. No behavior change when the signal never appears.
/// </summary>
public sealed class DesktopBootstrapSignalService : IAsyncDisposable
{
    private const int BuildLogTailLines = 30;
    private const string CoreStopAction = "core-stop";
    private const string BuildAction = "build";
    private const string CoreStartAction = "core-start";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CoreFullyStoppedPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CoreFullyStoppedTimeout = TimeSpan.FromSeconds(30);

    private readonly DesktopApplicationCoordinator _coordinator;
    private readonly IDesktopControlTokenService _tokenService;
    private readonly string _dataRoot;
    private readonly string _signalPath;
    private readonly string _resultPath;
    private readonly string _buildLogPath;
    private readonly string _repositoryRoot;
    private readonly string _yoloSignalPath;
    private readonly string _buildProjectRelativePath;
    private readonly string _buildTarget;
    private readonly IReadOnlyList<string> _buildArguments;
    private readonly bool _autoYolo;
    private readonly string _defaultDeploymentMode;
    private readonly string _projectName;
    private readonly TimeSpan _buildTimeout;

    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;
    private int _busyFlag;

    public DesktopBootstrapSignalService(
        DesktopApplicationCoordinator coordinator,
        string dataRoot,
        PuddingDesktopBootstrapConfig config,
        IDesktopControlTokenService tokenService)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(tokenService);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        _coordinator = coordinator;
        _tokenService = tokenService;
        _dataRoot = dataRoot;

        _signalPath = DesktopBootstrapSignalParser.ResolveSignalPath(config.SignalPath, dataRoot);
        _resultPath = DesktopBootstrapSignalParser.ResolveResultPath(_signalPath);
        _buildLogPath = DesktopBootstrapSignalParser.ResolveBuildLogPath(dataRoot);
        _repositoryRoot = DesktopBootstrapSignalParser.ResolveRepositoryRoot(
            Environment.GetEnvironmentVariable(DesktopBootstrapSignalParser.RepositoryRootEnvironmentVariable),
            AppContext.BaseDirectory);
        _yoloSignalPath = DesktopBootstrapSignalParser.ResolveYoloSignalPath(_repositoryRoot);
        _buildProjectRelativePath = string.IsNullOrWhiteSpace(config.BuildProjectRelativePath)
            ? DesktopBootstrapSignalParser.DefaultBuildProjectRelativePath
            : config.BuildProjectRelativePath;
        _buildTarget = !string.IsNullOrWhiteSpace(config.BuildProjectPath)
            ? Path.GetFullPath(config.BuildProjectPath)
            : Path.Combine(_repositoryRoot, _buildProjectRelativePath);
        _buildArguments = DesktopBootstrapSignalParser.SplitArguments(config.BuildArguments);
        _autoYolo = config.AutoYolo;
        _buildTimeout = TimeSpan.FromSeconds(
            config.BuildTimeoutSeconds > 0
                ? config.BuildTimeoutSeconds
                : DesktopBootstrapSignalParser.DefaultBuildTimeoutSeconds);
        _defaultDeploymentMode = DesktopBootstrapSignalParser.NormalizeDeploymentMode(
                                     config.DefaultDeploymentMode)
                                 ?? DesktopBootstrapSignalParser.DesktopBuildMode;
        _projectName = Path.GetFileNameWithoutExtension(_buildTarget);
    }

    /// <summary>True while a rebuild-restart attempt is in flight (guards concurrent triggers).</summary>
    public bool IsBusy => Volatile.Read(ref _busyFlag) != 0;

    /// <summary>Path of the result file (&lt;SignalPath&gt;.result.json) written after each attempt.</summary>
    public string ResultPath => _resultPath;

    /// <summary>Starts the background polling loop. Idempotent.</summary>
    public void Start(CancellationToken cancellationToken)
    {
        if (_loopTask is not null)
            return;

        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        _loopTask = Task.Run(() => RunLoopAsync(linked.Token), CancellationToken.None);
    }

    /// <summary>
    /// Runs one rebuild-restart cycle (stop Core → prepare/deploy/verify →
    /// optional yolo → restart Core) and writes the result file. Shared by the signal-file
    /// polling loop and the HTTP API / UI trigger paths.
    /// </summary>
    /// <param name="requestedBy">Optional free-form requester identity.</param>
    /// <param name="yolo">When true (and AutoYolo is enabled), write yolo.signal after a successful build.</param>
    /// <param name="ct">Cancellation token. The signal-file path cancels on shutdown; the API path passes CancellationToken.None.</param>
    /// <exception cref="InvalidOperationException">Thrown when another rebuild-restart attempt is already running.</exception>
    public async Task<DesktopBootstrapResult> TriggerRebuildRestartAsync(
        string? requestedBy, bool yolo, CancellationToken ct)
        => await TriggerRebuildRestartAsync(
            requestedBy,
            yolo,
            deploymentMode: null,
            artifactDirectory: null,
            artifactAssemblySha256: null,
            ct);

    /// <summary>
    /// Runs a deployment-aware rebuild-restart cycle. desktop-build delegates
    /// compilation to Desktop; prebuilt-artifact accepts an Agent-produced build;
    /// restart-only is an explicit no-deployment escape hatch.
    /// </summary>
    public async Task<DesktopBootstrapResult> TriggerRebuildRestartAsync(
        string? requestedBy,
        bool yolo,
        string? deploymentMode,
        string? artifactDirectory,
        string? artifactAssemblySha256,
        CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _busyFlag, 1, 0) != 0)
            throw new InvalidOperationException("bootstrap already running");

        try
        {
            var result = await RunRebuildRestartCoreAsync(
                requestedBy,
                yolo,
                deploymentMode,
                artifactDirectory,
                artifactAssemblySha256,
                ct);
            await WriteResultAndDeleteSignalAsync(result);
            return result;
        }
        finally
        {
            Interlocked.Exchange(ref _busyFlag, 0);
        }
    }

    /// <summary>
    /// Atomically stops Core and waits until the runtime reports fully stopped.
    /// No result file is written and no signal file is touched (atomic
    /// operations are independent of the signal-file protocol).
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when another bootstrap operation is already running.</exception>
    public async Task<DesktopBootstrapResult> StopCoreAtomicAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _busyFlag, 1, 0) != 0)
            throw new InvalidOperationException("bootstrap already running");

        try
        {
            var startedAt = DateTimeOffset.UtcNow;
            var errors = new List<string>();

            try
            {
                await _coordinator.StopCoreAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                errors.Add($"停止 Core 失败: {ex.Message}");
            }

            var fullyStopped = false;
            try
            {
                fullyStopped = await WaitForCoreFullyStoppedAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                errors.Add($"等待 Core 完全退出失败: {ex.Message}");
            }

            return new DesktopBootstrapResult
            {
                Success = fullyStopped,
                Action = CoreStopAction,
                StartedAt = startedAt,
                FinishedAt = DateTimeOffset.UtcNow,
                Errors = errors,
            };
        }
        finally
        {
            Interlocked.Exchange(ref _busyFlag, 0);
        }
    }

    /// <summary>
    /// Atomically runs only the dotnet build. Precondition: Core must be fully
    /// stopped (otherwise the build can fail on file locks). No sync, no yolo
    /// signal and no restart are performed.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when another bootstrap operation is already running, or when Core
    /// is still running (call StopCoreAtomicAsync / core/stop first).
    /// </exception>
    public async Task<DesktopBootstrapResult> BuildOnlyAtomicAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _busyFlag, 1, 0) != 0)
            throw new InvalidOperationException("bootstrap already running");

        try
        {
            if (!IsCoreFullyStopped(_coordinator.RuntimeSnapshot.State))
                throw new InvalidOperationException("Core 正在运行，请先调用 core/stop");

            var startedAt = DateTimeOffset.UtcNow;
            var errors = new List<string>();
            BuildRunResult? build = null;

            try
            {
                build = await RunBuildAsync(cancellationToken);
                if (build.ExitCode != 0)
                    errors.Add($"dotnet build 失败，退出码: {build.ExitCode}");
            }
            catch (Exception ex)
            {
                errors.Add($"dotnet build 异常: {ex.Message}");
                DesktopDiagnosticLog.Write("BootstrapBuild", ex);
            }

            return new DesktopBootstrapResult
            {
                Success = build is not null && build.ExitCode == 0,
                Action = BuildAction,
                StartedAt = startedAt,
                FinishedAt = DateTimeOffset.UtcNow,
                BuildExitCode = build?.ExitCode,
                BuildLogTail = build?.LogTail ?? [],
                Errors = errors,
            };
        }
        finally
        {
            Interlocked.Exchange(ref _busyFlag, 0);
        }
    }

    /// <summary>
    /// Atomically starts Core. No result file is written and no signal file is
    /// touched.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when another bootstrap operation is already running.</exception>
    public async Task<DesktopBootstrapResult> StartCoreAtomicAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _busyFlag, 1, 0) != 0)
            throw new InvalidOperationException("bootstrap already running");

        try
        {
            var startedAt = DateTimeOffset.UtcNow;
            var errors = new List<string>();
            var coreRestarted = false;

            try
            {
                await _coordinator.StartCoreAsync(cancellationToken);
                coreRestarted = _coordinator.RuntimeSnapshot.State == DesktopRuntimeState.Ready;
            }
            catch (Exception ex)
            {
                errors.Add($"启动 Core 失败: {ex.Message}");
            }

            return new DesktopBootstrapResult
            {
                Success = coreRestarted,
                Action = CoreStartAction,
                StartedAt = startedAt,
                FinishedAt = DateTimeOffset.UtcNow,
                CoreRestarted = coreRestarted,
                Errors = errors,
            };
        }
        finally
        {
            Interlocked.Exchange(ref _busyFlag, 0);
        }
    }

    /// <summary>
    /// Polls the runtime snapshot until Core is fully stopped (Stopped) or was
    /// never started (Idle — no process exists, so no file locks can be held).
    /// Polls every 500 ms for up to 30 seconds.
    /// </summary>
    private async Task<bool> WaitForCoreFullyStoppedAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + CoreFullyStoppedTimeout;
        while (true)
        {
            if (IsCoreFullyStopped(_coordinator.RuntimeSnapshot.State))
                return true;

            if (DateTimeOffset.UtcNow >= deadline)
                return false;

            await Task.Delay(CoreFullyStoppedPollInterval, cancellationToken);
        }
    }

    /// <summary>
    /// True when no Core process is running: fully stopped, or never started
    /// (Idle). Starting/Ready/Stopping/RestartScheduled keep the flag false so
    /// callers never build while a process could hold file locks.
    /// </summary>
    private static bool IsCoreFullyStopped(DesktopRuntimeState state)
        => state is DesktopRuntimeState.Stopped or DesktopRuntimeState.Idle;

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(_signalPath))
                    await ProcessSignalAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                DesktopDiagnosticLog.Write("BootstrapSignalLoop", ex);
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessSignalAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var errors = new List<string>();
        var writeResult = true;
        DesktopBootstrapResult? orchestratedResult = null;

        try
        {
            string content;
            try
            {
                content = await File.ReadAllTextAsync(_signalPath, cancellationToken);
            }
            catch (Exception ex)
            {
                errors.Add($"读取信号文件失败: {ex.Message}");
                DesktopDiagnosticLog.Write("BootstrapSignalRead", ex);
                return;
            }

            var signal = DesktopBootstrapSignalParser.TryParseSignal(content);
            if (signal is null)
            {
                errors.Add("信号文件不是合法的 JSON，已忽略并删除。");
                return;
            }

            if (!DesktopBootstrapSignalParser.IsSupportedAction(signal.Action))
            {
                errors.Add($"不支持的 action: {signal.Action ?? "(null)"}");
                return;
            }

            var expectedToken = await _tokenService.GetOrCreateAsync(_dataRoot, cancellationToken);
            if (!DesktopBootstrapSignalParser.IsTokenValid(signal.Token, expectedToken))
            {
                // Deliberately do NOT leak the expected token value.
                errors.Add("令牌校验失败，信号已拒绝。");
                return;
            }

            try
            {
                orchestratedResult = await TriggerRebuildRestartAsync(
                    signal.RequestedBy,
                    signal.Yolo,
                    signal.DeploymentMode,
                    signal.ArtifactDirectory,
                    signal.ArtifactAssemblySha256,
                    cancellationToken);
            }
            catch (InvalidOperationException) when (IsBusy)
            {
                // Another rebuild-restart (e.g. started via the HTTP endpoint)
                // is already running; reject this signal without disturbing it.
                errors.Add("另一个引导任务正在进行，信号已忽略。");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown in progress: leave the signal file in place so the
                // next launch can retry; skip result write to avoid a
                // misleading file.
                writeResult = false;
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
                DesktopDiagnosticLog.Write("BootstrapSignal", ex);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            writeResult = false;
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            DesktopDiagnosticLog.Write("BootstrapSignal", ex);
        }
        finally
        {
            if (writeResult && orchestratedResult is null)
            {
                // Rejected signal (bad JSON / action / token / busy) or an
                // unexpected failure before the orchestration produced a
                // result: write a minimal failed result and delete the signal.
                var result = new DesktopBootstrapResult
                {
                    Success = false,
                    Action = DesktopBootstrapSignalParser.RebuildRestartAction,
                    StartedAt = startedAt,
                    FinishedAt = DateTimeOffset.UtcNow,
                    Errors = errors,
                };
                await WriteResultAndDeleteSignalAsync(result);
            }
        }
    }

    /// <summary>
    /// The shared rebuild-restart orchestration: stop Core → prepare artifact →
    /// transactional deployment → hash verification → optional yolo.signal →
    /// restart Core. Preparation/deployment failures are recorded and the old
    /// Core is restarted as the fallback.
    /// </summary>
    private async Task<DesktopBootstrapResult> RunRebuildRestartCoreAsync(
        string? requestedBy,
        bool yolo,
        string? requestedDeploymentMode,
        string? artifactDirectory,
        string? artifactAssemblySha256,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var errors = new List<string>();
        var buildLogTail = new List<string>();
        int? buildExitCode = null;
        var deploymentMode = DesktopBootstrapSignalParser.NormalizeDeploymentMode(
            requestedDeploymentMode,
            _defaultDeploymentMode);
        var buildOutputDirectory = default(string);
        var deploymentDirectory = default(string);
        var preparedAssemblySha256 = default(string);
        var loadedAssemblySha256 = default(string);
        var preparedArtifactManifestSha256 = default(string);
        var loadedArtifactManifestSha256 = default(string);
        ManagedArtifactManifestResult? preparedArtifactManifest = null;
        var deploymentCopied = 0;
        var deploymentSkipped = 0;
        var preparationSucceeded = deploymentMode == DesktopBootstrapSignalParser.RestartOnlyMode;
        var deploymentVerified = false;
        var yoloSignalWritten = false;
        var coreRestarted = false;

        if (deploymentMode is null)
        {
            errors.Add($"不支持的 deploymentMode: {requestedDeploymentMode ?? "(null)"}");
            return new DesktopBootstrapResult
            {
                Success = false,
                Action = DesktopBootstrapSignalParser.RebuildRestartAction,
                StartedAt = startedAt,
                FinishedAt = DateTimeOffset.UtcNow,
                Errors = errors,
            };
        }

        if (deploymentMode == DesktopBootstrapSignalParser.PrebuiltArtifactMode)
        {
            if (string.IsNullOrWhiteSpace(artifactDirectory) || !Path.IsPathRooted(artifactDirectory))
            {
                errors.Add("prebuilt-artifact 模式要求绝对 artifactDirectory。");
            }
            else
            {
                buildOutputDirectory = Path.GetFullPath(artifactDirectory);
                if (!PuddingBuildOutputSync.IsPathWithin(buildOutputDirectory, _repositoryRoot))
                    errors.Add($"预构建产物目录必须位于仓库根目录内: {_repositoryRoot}");
                else if (!Directory.Exists(buildOutputDirectory))
                    errors.Add($"预构建产物目录不存在: {buildOutputDirectory}");
            }

            if (errors.Count > 0)
            {
                return new DesktopBootstrapResult
                {
                    Success = false,
                    Action = DesktopBootstrapSignalParser.RebuildRestartAction,
                    DeploymentMode = deploymentMode,
                    BuildOutputDirectory = buildOutputDirectory,
                    StartedAt = startedAt,
                    FinishedAt = DateTimeOffset.UtcNow,
                    Errors = errors,
                };
            }
        }

        // a) Stop Core. No-op when Core is already stopped.
        try
        {
            await _coordinator.StopCoreAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            errors.Add($"停止 Core 失败: {ex.Message}");
        }

        // a2) Hard guarantee: Core must be fully gone before the build starts,
        //     otherwise the incremental build can fail on file locks held by the
        //     running binaries. When Core was already stopped, StopCoreAsync above
        //     is a no-op and the poll passes immediately. On timeout the build and
        //     sync are skipped — step d still restarts Core with the old binary.
        var skipBuild = false;
        try
        {
            var fullyStopped = await WaitForCoreFullyStoppedAsync(cancellationToken);
            if (!fullyStopped)
            {
                errors.Add("Core 进程未完全退出，为避免文件锁跳过构建");
                skipBuild = true;
            }
        }
        catch (Exception ex)
        {
            errors.Add($"等待 Core 完全退出失败: {ex.Message}");
            skipBuild = true;
        }

        // b) Desktop-owned incremental build, unless the caller supplied a
        //    prepared artifact or explicitly requested restart-only.
        BuildRunResult? build = null;
        if (!skipBuild && deploymentMode == DesktopBootstrapSignalParser.DesktopBuildMode)
        {
            try
            {
                build = await RunBuildAsync(cancellationToken);
                buildExitCode = build.ExitCode;
                buildLogTail = build.LogTail;
                if (build.ExitCode != 0)
                    errors.Add($"dotnet build 失败，退出码: {build.ExitCode}");
                else
                    buildOutputDirectory = PuddingBuildOutputSync.TryParseBuildOutputDirectory(
                        build.FullLog,
                        _projectName);

                if (build.ExitCode == 0 && buildOutputDirectory is null)
                    errors.Add("未能从构建输出中解析 PuddingAgent 产物目录。");
            }
            catch (Exception ex)
            {
                errors.Add($"dotnet build 异常: {ex.Message}");
                DesktopDiagnosticLog.Write("BootstrapBuild", ex);
            }
        }

        // b2) Deploy into the directory of the executable Desktop will actually
        //     launch. Never use AppContext.BaseDirectory: packaged Desktop keeps
        //     Core under a side-by-side core/ directory.
        if (!skipBuild
            && deploymentMode != DesktopBootstrapSignalParser.RestartOnlyMode
            && errors.Count == 0
            && buildOutputDirectory is not null)
        {
            var preparedAssemblyPath = Path.Combine(buildOutputDirectory, "PuddingAgent.dll");
            var coreExecutablePath = _coordinator.CoreExecutablePath;
            deploymentDirectory = string.IsNullOrWhiteSpace(coreExecutablePath)
                ? null
                : Path.GetDirectoryName(Path.GetFullPath(coreExecutablePath));

            if (!File.Exists(preparedAssemblyPath))
            {
                errors.Add($"预构建产物缺少 PuddingAgent.dll: {preparedAssemblyPath}");
            }
            else if (string.IsNullOrWhiteSpace(deploymentDirectory))
            {
                errors.Add("Desktop 尚未解析实际 CoreExecutablePath，拒绝部署到不确定目录。");
            }
            else
            {
                try
                {
                    preparedAssemblySha256 = PuddingBuildOutputSync.ComputeSha256(preparedAssemblyPath);
                    preparedArtifactManifest = PuddingBuildOutputSync.ComputeManagedArtifactManifest(
                        buildOutputDirectory);
                    preparedArtifactManifestSha256 = preparedArtifactManifest.Sha256;
                    if (!string.IsNullOrWhiteSpace(artifactAssemblySha256)
                        && !string.Equals(
                            preparedAssemblySha256,
                            artifactAssemblySha256.Trim(),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(
                            $"预构建产物 SHA-256 不匹配: expected={artifactAssemblySha256.Trim()}, " +
                            $"actual={preparedAssemblySha256}");
                    }

                    var syncResult = errors.Count == 0
                        ? PuddingBuildOutputSync.DeployDirectoryTransactional(
                            buildOutputDirectory,
                            deploymentDirectory)
                        : new BuildOutputSyncResult();
                    deploymentCopied = syncResult.Copied;
                    deploymentSkipped = syncResult.Skipped;
                    if (syncResult.Failures.Count > 0)
                    {
                        errors.Add(
                            $"构建产物事务部署失败: 复制 {syncResult.Copied} 个, " +
                            $"跳过 {syncResult.Skipped} 个, 失败 {syncResult.Failures.Count} 个。");
                        foreach (var failure in syncResult.Failures)
                            errors.Add($"  产物部署失败: {failure}");
                    }

                    var deployedAssemblyPath = Path.Combine(deploymentDirectory, "PuddingAgent.dll");
                    if (errors.Count == 0 && File.Exists(deployedAssemblyPath))
                    {
                        loadedAssemblySha256 = PuddingBuildOutputSync.ComputeSha256(deployedAssemblyPath);
                        var loadedManifest = PuddingBuildOutputSync.ComputeManagedArtifactManifest(
                            deploymentDirectory,
                            preparedArtifactManifest.RelativePaths);
                        loadedArtifactManifestSha256 = loadedManifest.Sha256;
                        deploymentVerified = string.Equals(
                            preparedAssemblySha256,
                            loadedAssemblySha256,
                            StringComparison.OrdinalIgnoreCase)
                            && string.Equals(
                                preparedArtifactManifestSha256,
                                loadedArtifactManifestSha256,
                                StringComparison.OrdinalIgnoreCase);
                        if (!deploymentVerified)
                            errors.Add("部署后的托管程序集清单与预构建产物不一致。");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"部署构建产物失败: {ex.Message}");
                    DesktopDiagnosticLog.Write("BootstrapSyncOutput", ex);
                }
            }

            preparationSucceeded = deploymentVerified && errors.Count == 0;
        }

        // c) yolo.signal (only after the requested preparation succeeded).
        if (preparationSucceeded && _autoYolo && yolo)
        {
            try
            {
                await WriteYoloSignalAsync(requestedBy, cancellationToken);
                yoloSignalWritten = true;
            }
            catch (Exception ex)
            {
                errors.Add($"写入 yolo.signal 失败: {ex.Message}");
            }
        }

        // d) Restart Core with the last configured start options. On a
        //    preparation failure this restores Core with the previous binary.
        try
        {
            await _coordinator.StartCoreAsync(cancellationToken);
            coreRestarted = _coordinator.RuntimeSnapshot.State == DesktopRuntimeState.Ready;
        }
        catch (Exception ex)
        {
            errors.Add($"启动 Core 失败: {ex.Message}");
        }

        var assembliesReloaded = false;
        var launchedCoreExecutablePath = _coordinator.CoreExecutablePath;
        if (coreRestarted
            && deploymentMode != DesktopBootstrapSignalParser.RestartOnlyMode
            && preparationSucceeded
            && !string.IsNullOrWhiteSpace(deploymentDirectory)
            && !string.IsNullOrWhiteSpace(preparedAssemblySha256)
            && preparedArtifactManifest is not null)
        {
            try
            {
                var launchedDirectory = string.IsNullOrWhiteSpace(launchedCoreExecutablePath)
                    ? null
                    : Path.GetDirectoryName(Path.GetFullPath(launchedCoreExecutablePath));
                var launchDirectoryMatches = !string.IsNullOrWhiteSpace(launchedDirectory)
                    && string.Equals(
                        Path.TrimEndingDirectorySeparator(launchedDirectory),
                        Path.TrimEndingDirectorySeparator(deploymentDirectory),
                        StringComparison.OrdinalIgnoreCase);
                if (!launchDirectoryMatches)
                {
                    errors.Add("Core 重启后的实际启动目录与部署目录不一致。");
                }

                loadedAssemblySha256 = PuddingBuildOutputSync.ComputeSha256(
                    Path.Combine(launchedDirectory ?? deploymentDirectory, "PuddingAgent.dll"));
                var launchedManifest = PuddingBuildOutputSync.ComputeManagedArtifactManifest(
                    launchedDirectory ?? deploymentDirectory,
                    preparedArtifactManifest.RelativePaths);
                loadedArtifactManifestSha256 = launchedManifest.Sha256;
                assembliesReloaded = launchDirectoryMatches && string.Equals(
                    preparedAssemblySha256,
                    loadedAssemblySha256,
                    StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        preparedArtifactManifestSha256,
                        loadedArtifactManifestSha256,
                        StringComparison.OrdinalIgnoreCase);
                if (!assembliesReloaded)
                    errors.Add("Core 重启后实际托管程序集清单与部署产物不一致。");
            }
            catch (Exception ex)
            {
                errors.Add($"Core 重启后程序集校验失败: {ex.Message}");
            }
        }

        return new DesktopBootstrapResult
        {
            Success = coreRestarted
                      && errors.Count == 0
                      && (deploymentMode == DesktopBootstrapSignalParser.RestartOnlyMode || assembliesReloaded),
            Action = DesktopBootstrapSignalParser.RebuildRestartAction,
            DeploymentMode = deploymentMode,
            StartedAt = startedAt,
            FinishedAt = DateTimeOffset.UtcNow,
            BuildExitCode = buildExitCode,
            BuildLogTail = buildLogTail,
            BuildOutputDirectory = buildOutputDirectory,
            DeploymentDirectory = deploymentDirectory,
            CoreExecutablePath = launchedCoreExecutablePath,
            DeploymentCopied = deploymentCopied,
            DeploymentSkipped = deploymentSkipped,
            PreparedAssemblySha256 = preparedAssemblySha256,
            LoadedAssemblySha256 = loadedAssemblySha256,
            PreparedArtifactManifestSha256 = preparedArtifactManifestSha256,
            LoadedArtifactManifestSha256 = loadedArtifactManifestSha256,
            ManagedArtifactFileCount = preparedArtifactManifest?.FileCount ?? 0,
            AssembliesReloaded = assembliesReloaded,
            CoreRestarted = coreRestarted,
            YoloSignalWritten = yoloSignalWritten,
            Errors = errors,
        };
    }

    private async Task<BuildRunResult> RunBuildAsync(
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = _repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(_buildTarget);
        foreach (var argument in _buildArguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException("dotnet 进程启动失败。");

        var stdoutTask = ReadToEndSafelyAsync(process.StandardOutput);
        var stderrTask = ReadToEndSafelyAsync(process.StandardError);

        var timedOut = false;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_buildTimeout);
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Timeout or shutdown: kill the whole child tree so nothing lingers.
            try { process.Kill(entireProcessTree: true); }
            catch { }

            await WaitForExitSafelyAsync(process);
            timedOut = !cancellationToken.IsCancellationRequested;
        }

        string stdout;
        string stderr;
        try
        {
            stdout = await stdoutTask;
            stderr = await stderrTask;
        }
        catch
        {
            stdout = string.Empty;
            stderr = string.Empty;
        }

        var fullLog = stdout + Environment.NewLine + stderr;
        await AppendToBuildLogAsync(fullLog, CancellationToken.None);

        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);

        if (timedOut)
            throw new TimeoutException(
                $"dotnet build 超过 {_buildTimeout.TotalSeconds:0} 秒未完成，已终止。");

        return new BuildRunResult(process.ExitCode, TakeTail(fullLog), fullLog);
    }

    private async Task WriteYoloSignalAsync(string? requestedBy, CancellationToken cancellationToken)
    {
        var content = string.Join(
            Environment.NewLine,
            "writtenBy=PuddingDesktop.bootstrap",
            "action=rebuild-restart",
            $"at={DateTimeOffset.UtcNow:O}",
            $"requestedBy={requestedBy ?? "unknown"}");

        await File.WriteAllTextAsync(_yoloSignalPath, content, cancellationToken);
    }

    private async Task AppendToBuildLogAsync(string content, CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(_buildLogPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await File.AppendAllTextAsync(
                _buildLogPath,
                $"{DateTimeOffset.Now:O} [bootstrap build]{Environment.NewLine}{content}{Environment.NewLine}",
                cancellationToken);
        }
        catch
        {
            // Build log is best-effort diagnostics only.
        }
    }

    private async Task WriteResultAndDeleteSignalAsync(DesktopBootstrapResult result)
    {
        try
        {
            await DesktopBootstrapResultWriter.WriteAsync(
                _resultPath, result, CancellationToken.None);
        }
        catch (Exception ex)
        {
            DesktopDiagnosticLog.Write("BootstrapResultWrite", ex);
        }

        TryDeleteSignal();
    }

    private void TryDeleteSignal()
    {
        try
        {
            if (File.Exists(_signalPath))
                File.Delete(_signalPath);
        }
        catch (Exception ex)
        {
            DesktopDiagnosticLog.Write("BootstrapSignalDelete", ex);
        }
    }

    private static async Task<string> ReadToEndSafelyAsync(StreamReader reader)
    {
        try { return await reader.ReadToEndAsync().ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    private static async Task WaitForExitSafelyAsync(Process process)
    {
        try { await process.WaitForExitAsync().ConfigureAwait(false); }
        catch { }
    }

    private static List<string> TakeTail(string text)
        => text
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .TakeLast(BuildLogTailLines)
            .ToList();

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        if (_loopTask is not null)
        {
            try { await _loopTask; }
            catch { }
            _loopTask = null;
        }

        _cts.Dispose();
    }

    /// <summary>Result of one dotnet build invocation: exit code, log tail and the full log.</summary>
    private sealed record BuildRunResult(int ExitCode, List<string> LogTail, string FullLog);
}
