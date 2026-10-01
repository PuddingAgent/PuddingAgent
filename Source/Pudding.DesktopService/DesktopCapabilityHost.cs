using System.Collections.Concurrent;
using Pudding.Contracts;
using Pudding.DesktopConnection;

namespace Pudding.DesktopService;

/// <summary>能力传输选择（迁移期开关，启动时二选一；不做单命令故障后的跨传输自动重试）。</summary>
public enum DesktopCapabilityTransportMode
{
    /// <summary>既有认证 WebSocket Bridge（迁移基线与回退路径）。</summary>
    LegacyWebSocketBridge,

    /// <summary>Desktop 主动拨入的 gRPC 能力通道（本方案）。</summary>
    GrpcCapabilityChannel,
}

public sealed record DesktopCapabilityHostOptions
{
    public required DesktopInstanceId DesktopId { get; init; }

    /// <summary>启动时选定的传输。选择旧 Bridge 时宿主<b>拒绝启动</b>而不是悄悄并存两套。</summary>
    public DesktopCapabilityTransportMode Mode { get; init; } = DesktopCapabilityTransportMode.GrpcCapabilityChannel;

    /// <summary>停止时等待监督循环退出的上限。</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>同一个 DesktopId 是否只允许一个活动能力传输（计划 §8 切片 D）。</summary>
    public bool EnforceSingleActiveTransport { get; init; } = true;

    internal void Validate()
    {
        if (StopTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(StopTimeout), StopTimeout, "Stop timeout must be positive.");
        }
    }
}

/// <summary>
/// 桌面能力宿主：把「监督循环」与「能力服务」装配成可启停的一个单元。
///
/// 边界：
/// · 只依赖 <see cref="IDesktopConnectionSupervisor"/> 端口与 <see cref="DesktopService"/>，因此
///   生命周期/单实例/停止语义可以无端点单测；真实监督器由 <see cref="CreateGrpcSupervisorFactory"/> 构造；
/// · 不拥有 UI 线程与 WebView2（那是 <c>IDesktopUiDispatcher</c>/<c>IDesktopUiSurface</c> 实现方）；
/// · 不做跨传输回退：传输在启动时选定，选旧 Bridge 就拒绝启动本宿主。
/// </summary>
public sealed class DesktopCapabilityHost : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, byte> ActiveTransports = new(StringComparer.Ordinal);

    private readonly Func<IDesktopConnectionSupervisor> _supervisorFactory;
    private readonly DesktopCapabilityHostOptions _options;
    private readonly object _sync = new();

    private CancellationTokenSource? _lifetime;
    private Task? _loop;
    private IDesktopConnectionSupervisor? _supervisor;
    private bool _transportClaimed;
    private int _disposed;

    public DesktopCapabilityHost(
        DesktopCapabilityHostOptions options,
        Func<IDesktopConnectionSupervisor> supervisorFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _supervisorFactory = supervisorFactory ?? throw new ArgumentNullException(nameof(supervisorFactory));
    }

    /// <summary>监督循环的状态变化（转发自监督器）。</summary>
    public event Action<DesktopConnectionState>? StateChanged;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _loop is { IsCompleted: false };
            }
        }
    }

    public DesktopConnectionState State
    {
        get
        {
            lock (_sync)
            {
                return _supervisor?.State ?? DesktopConnectionState.Idle;
            }
        }
    }

    public ConnectionGeneration Generation
    {
        get
        {
            lock (_sync)
            {
                return _supervisor?.Generation ?? ConnectionGeneration.None;
            }
        }
    }

    public int AttemptCount
    {
        get
        {
            lock (_sync)
            {
                return _supervisor?.AttemptCount ?? 0;
            }
        }
    }

    public DesktopCapabilityError? LastError
    {
        get
        {
            lock (_sync)
            {
                return _supervisor?.LastError;
            }
        }
    }

    /// <summary>启动监督循环。返回时循环已在运行（不等待握手完成）。</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (_options.Mode != DesktopCapabilityTransportMode.GrpcCapabilityChannel)
        {
            // 迁移期明确二选一：不因为「gRPC 没起来」就静默切回旧 Bridge。
            throw new InvalidOperationException(
                "The capability channel is disabled because the legacy WebSocket bridge was selected at startup.");
        }

        lock (_sync)
        {
            if (_loop is { IsCompleted: false })
            {
                throw new InvalidOperationException("The capability host is already running.");
            }

            if (_options.EnforceSingleActiveTransport && !TryClaimTransport(_options.DesktopId.Value))
            {
                throw new InvalidOperationException(
                    $"Another capability transport is already active for desktop '{_options.DesktopId.Value}'.");
            }

            _transportClaimed = _options.EnforceSingleActiveTransport;

            var supervisor = _supervisorFactory();
            supervisor.StateChanged += OnSupervisorStateChanged;
            _supervisor = supervisor;

            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _lifetime = lifetime;
            _loop = Task.Run(() => supervisor.RunAsync(lifetime.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    /// <summary>等待状态到达（用于「宿主已就绪」告示；超时返回 false）。</summary>
    public async Task<bool> WaitForStateAsync(
        DesktopConnectionState state, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (State == state)
            {
                return true;
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        return State == state;
    }

    /// <summary>停止监督循环并释放单实例占用。可重复调用。</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        IDesktopConnectionSupervisor? supervisor;
        Task? loop;
        CancellationTokenSource? lifetime;

        lock (_sync)
        {
            supervisor = _supervisor;
            loop = _loop;
            lifetime = _lifetime;
            _supervisor = null;
            _loop = null;
            _lifetime = null;
        }

        if (lifetime is not null)
        {
            try
            {
                await lifetime.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // 已经释放。
            }
        }

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(_options.StopTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // 监督器没有在期限内退出：仍要释放占用，避免永久锁死（由其自身取消源兜底）。
            }
            catch (OperationCanceledException)
            {
                // 调用方取消停止等待：占用照常释放。
            }
        }

        if (supervisor is not null)
        {
            supervisor.StateChanged -= OnSupervisorStateChanged;
            try
            {
                await supervisor.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 释放失败不影响状态收敛。
            }
        }

        try
        {
            lifetime?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        ReleaseTransport();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>用真实监督器（gRPC 双向流 + 退避重连）构造宿主。</summary>
    public static Func<IDesktopConnectionSupervisor> CreateGrpcSupervisorFactory(
        IDesktopChannelStreamFactory streamFactory,
        IDesktopCapabilityExecutor executor,
        DesktopConnectionOptions connectionOptions,
        TimeSpan? initialBackoff = null,
        TimeSpan? maxBackoff = null,
        double jitter = 0.2) =>
        () => new DesktopConnectionRunner(
            streamFactory,
            executor,
            connectionOptions,
            initialBackoff,
            maxBackoff,
            jitter);

    private void OnSupervisorStateChanged(DesktopConnectionState state)
    {
        try
        {
            StateChanged?.Invoke(state);
        }
        catch
        {
            // 订阅者异常不得影响监督循环。
        }
    }

    private static bool TryClaimTransport(string desktopId) => ActiveTransports.TryAdd(desktopId, 0);

    private void ReleaseTransport()
    {
        if (!_transportClaimed)
        {
            return;
        }

        _transportClaimed = false;
        ActiveTransports.TryRemove(_options.DesktopId.Value, out _);
    }
}
