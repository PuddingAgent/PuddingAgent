using Pudding.Contracts;
using Proto = Pudding.Rpc.Protocol.V1;

namespace Pudding.DesktopConnection;

/// <summary>
/// 连接监督：反复建立新连接，失败/断开后按指数退避 + jitter 重连（计划 §5）。
///
/// 明确<b>不</b>做：
/// · 不跨传输回退（不做「IPC 失败就切 Loopback」）；
/// · 不在新连接上重放脚本、通知、剪贴板写入等副作用命令 —— 副作用只能由 Core 在新世代重新下发。
/// </summary>
public sealed class DesktopConnectionRunner : IDesktopConnectionSupervisor, IAsyncDisposable
{
    private readonly IDesktopChannelStreamFactory _streamFactory;
    private readonly IDesktopCapabilityExecutor _executor;
    private readonly DesktopConnectionOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double> _jitterSample;
    private readonly TimeSpan _initialBackoff;
    private readonly TimeSpan _maxBackoff;
    private readonly double _jitter;
    private readonly int _maxAttempts;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateLock = new();

    private DesktopConnectionState _state = DesktopConnectionState.Idle;
    private DesktopConnection? _active;
    private long _generation;

    public DesktopConnectionRunner(
        IDesktopChannelStreamFactory streamFactory,
        IDesktopCapabilityExecutor executor,
        DesktopConnectionOptions options,
        TimeSpan? initialBackoff = null,
        TimeSpan? maxBackoff = null,
        double jitter = 0.2,
        int maxAttempts = int.MaxValue,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<double>? jitterSample = null)
    {
        _streamFactory = streamFactory ?? throw new ArgumentNullException(nameof(streamFactory));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _initialBackoff = initialBackoff ?? TimeSpan.FromSeconds(1);
        _maxBackoff = maxBackoff ?? TimeSpan.FromSeconds(30);
        _jitter = Math.Clamp(jitter, 0d, 1d);
        _maxAttempts = Math.Max(1, maxAttempts);
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        _jitterSample = jitterSample ?? Random.Shared.NextDouble;

        if (_initialBackoff <= TimeSpan.Zero || _maxBackoff < _initialBackoff)
        {
            throw new ArgumentOutOfRangeException(nameof(initialBackoff), initialBackoff, "Backoff bounds are invalid.");
        }
    }

    public event Action<DesktopConnectionState>? StateChanged;

    public DesktopConnectionState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public ConnectionGeneration Generation => ConnectionGeneration.Require(Interlocked.Read(ref _generation));

    public int AttemptCount { get; private set; }

    public DesktopCapabilityError? LastError { get; private set; }

    /// <summary>最近一次实际使用的退避时长（测试与诊断用）。</summary>
    public IReadOnlyList<TimeSpan> BackoffHistory => _backoffHistory;

    private readonly List<TimeSpan> _backoffHistory = [];

    /// <summary>运行直到取消或达到尝试上限。返回时不再有活动连接。</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var backoff = _initialBackoff;

        while (!linked.IsCancellationRequested && AttemptCount < _maxAttempts)
        {
            AttemptCount++;
            var connection = new DesktopConnection(_streamFactory, _executor, _options);
            _active = connection;

            var reachedReady = false;
            void OnStateChanged(DesktopConnectionState state)
            {
                if (state == DesktopConnectionState.Ready)
                {
                    reachedReady = true;

                    // 世代必须在握手成功当刻就对外可见：否则调用方在连接存活期间读到的是上一次的世代。
                    Interlocked.Exchange(ref _generation, connection.Generation.Value);
                }

                SetState(state);
            }

            connection.StateChanged += OnStateChanged;
            DesktopConnectionOutcome outcome;
            try
            {
                outcome = await connection.RunAsync(linked.Token).ConfigureAwait(false);
            }
            finally
            {
                connection.StateChanged -= OnStateChanged;
                await connection.DisposeAsync().ConfigureAwait(false);
                _active = null;
            }

            LastError = outcome.Error;
            if (outcome.Generation.IsLive)
            {
                // 只有成功握手过的连接才有世代；失败的尝试不得把上次的世代冲成 0。
                Interlocked.Exchange(ref _generation, outcome.Generation.Value);
            }

            if (linked.IsCancellationRequested)
            {
                break;
            }

            if (reachedReady)
            {
                // 成功握手后重置退避梯度，避免「一次长时间健康连接」把下次重试拖慢。
                backoff = _initialBackoff;
            }

            if (AttemptCount >= _maxAttempts)
            {
                // 没有下一次尝试了：不再空等一个退避周期。
                break;
            }

            var wait = NextBackoff(backoff);
            _backoffHistory.Add(wait);
            SetState(DesktopConnectionState.Disconnected);

            try
            {
                await _delay(wait, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = wait >= _maxBackoff ? _maxBackoff : TimeSpan.FromTicks(Math.Min(_maxBackoff.Ticks, backoff.Ticks * 2));
        }

        SetState(DesktopConnectionState.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_lifetime.IsCancellationRequested)
        {
            _lifetime.Cancel();
        }

        if (_active is { } active)
        {
            await active.DisposeAsync().ConfigureAwait(false);
        }
    }

    private TimeSpan NextBackoff(TimeSpan baseBackoff)
    {
        if (_jitter <= 0)
        {
            return baseBackoff > _maxBackoff ? _maxBackoff : baseBackoff;
        }

        var sample = Math.Clamp(_jitterSample(), 0d, 1d);
        var factor = 1d + (_jitter * ((2d * sample) - 1d));
        var ticks = (long)(baseBackoff.Ticks * factor);
        var jittered = TimeSpan.FromTicks(Math.Clamp(ticks, 0, _maxBackoff.Ticks));
        return jittered > _maxBackoff ? _maxBackoff : jittered;
    }

    private void SetState(DesktopConnectionState state)
    {
        lock (_stateLock)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
        }

        try
        {
            StateChanged?.Invoke(state);
        }
        catch
        {
            // 订阅者异常不得影响监督循环。
        }
    }
}
