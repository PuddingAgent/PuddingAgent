using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingRuntime;

/// <summary>
/// Tracks the runtime's most recent activity so proactive services can tell how long
/// the agent network has been idle. Fires a simple callback at a global idle threshold
/// (default 30 s). The callback recipient (HeartbeatOrchestrator) decides whether any
/// agent should be woken — this detector does NOT manage per-agent thresholds.
/// </summary>
public interface IIdleDetector
{
    /// <summary>UTC timestamp for the last observed user or tool activity.</summary>
    DateTimeOffset LastActiveAt { get; }

    /// <summary>Elapsed time since <see cref="LastActiveAt"/>.</summary>
    TimeSpan IdleDuration { get; }

    /// <summary>Records that a user message reached the runtime.</summary>
    void RecordUserMessage();

    /// <summary>Records that a tool call has completed.</summary>
    void RecordToolCompleted();

    /// <summary>Records generic runtime activity.</summary>
    void RecordActivity();

    /// <summary>
    /// Fired periodically when the runtime has been idle for at least the global
    /// threshold.  Best-effort: activity resets the timer; only fires once per
    /// idle window until activity occurs again.
    /// </summary>
    event Func<TimeSpan, CancellationToken, Task>? OnIdleThresholdReached;

    /// <summary>
    /// Resets the idle-window flag so the next idle threshold crossing fires again.
    /// Used by HeartbeatOrchestrator when multiple agents are queued.
    /// </summary>
    void ReArm();
}

/// <summary>
/// Event-backed idle detector.  Subscribes to message.deliver and tool.* events
/// via IInternalEventBus and runs a 5-second polling loop that fires
/// <see cref="OnIdleThresholdReached"/> when idle >= the global threshold.
/// </summary>
public sealed class IdleDetector : IIdleDetector, IHostedService, IDisposable
{
    private const int IdleCheckIntervalSeconds = 5;
    private static readonly TimeSpan GlobalIdleThreshold = TimeSpan.FromSeconds(30);

    private readonly IInternalEventBus? _eventBus;
    private readonly ILogger<IdleDetector> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly object _subscriptionLock = new();
    private readonly List<IEventSubscriptionHandle> _subscriptions = [];
    private long _lastActiveUtcTicks;
    private bool _firedForCurrentWindow;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private int _started;

    public IdleDetector(
        IInternalEventBus? eventBus = null,
        ILogger<IdleDetector>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _eventBus = eventBus;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<IdleDetector>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastActiveUtcTicks = _timeProvider.GetUtcNow().UtcTicks;
    }

    public DateTimeOffset LastActiveAt =>
        new(Interlocked.Read(ref _lastActiveUtcTicks), TimeSpan.Zero);

    public TimeSpan IdleDuration
    {
        get
        {
            var duration = _timeProvider.GetUtcNow() - LastActiveAt;
            return duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        }
    }

    public event Func<TimeSpan, CancellationToken, Task>? OnIdleThresholdReached;

    public void RecordUserMessage() => RecordActivity();
    public void RecordToolCompleted() => RecordActivity();

    public void RecordActivity()
    {
        Interlocked.Exchange(ref _lastActiveUtcTicks, _timeProvider.GetUtcNow().UtcTicks);
        _firedForCurrentWindow = false; // allow next idle window to fire again
    }

    /// <inheritdoc />
    public void ReArm()
    {
        _firedForCurrentWindow = false;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) == 1)
        {
            Console.WriteLine("[Startup] IdleDetector.StartAsync — already started (duplicate)");
            _logger.LogDebug("[IdleDetector] Already started; skipping duplicate StartAsync");
            return;
        }

        Console.WriteLine("[Startup] IdleDetector.StartAsync — subscribing...");
        if (_eventBus is not null)
        {
            var messageSub = await _eventBus.SubscribeAsync("message.deliver", OnUserMessageAsync, cancellationToken);
            var toolSub = await _eventBus.SubscribeAsync("tool.*", OnToolEventAsync, cancellationToken);
            lock (_subscriptionLock)
            {
                _subscriptions.Add(messageSub);
                _subscriptions.Add(toolSub);
            }

            _logger.LogInformation(
                "[IdleDetector] Subscribed message={MsgId} tool={ToolId}",
                messageSub.SubscriptionId, toolSub.SubscriptionId);
        }
        else
        {
            _logger.LogDebug("[IdleDetector] Event bus unavailable; explicit RecordActivity still works");
        }

        _loopCts = new CancellationTokenSource();
        _loopTask = RunIdleLoopAsync(_loopCts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        DisposeSubscriptions();

        var loopCts = Interlocked.Exchange(ref _loopCts, null);
        var loopTask = Interlocked.Exchange(ref _loopTask, null);
        CancelLoop(loopCts);
        if (loopTask is not null)
        {
            try { await loopTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }
        loopCts?.Dispose();
        _logger.LogInformation("[IdleDetector] Stopped");
    }

    public void Dispose()
    {
        var loopCts = Interlocked.Exchange(ref _loopCts, null);
        Interlocked.Exchange(ref _loopTask, null);
        CancelLoop(loopCts);
        loopCts?.Dispose();
        DisposeSubscriptions();
    }

    private void DisposeSubscriptions()
    {
        lock (_subscriptionLock)
        {
            foreach (var s in _subscriptions) s.Dispose();
            _subscriptions.Clear();
        }
    }

    private static void CancelLoop(CancellationTokenSource? loopCts)
    {
        if (loopCts is null)
            return;

        try
        {
            loopCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // StopAsync and Dispose can be invoked by the host during the same shutdown.
            // A disposed CTS already represents a stopped loop.
        }
    }

    /// <summary>
    /// Polls idle duration every <see cref="IdleCheckIntervalSeconds"/> seconds.
    /// Fires the callback once per idle window — reset on any activity.
    /// </summary>
    private async Task RunIdleLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(IdleCheckIntervalSeconds);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }

            var callback = OnIdleThresholdReached;
            if (callback is null) continue;

            var idle = IdleDuration;
            if (idle >= GlobalIdleThreshold && !_firedForCurrentWindow)
            {
                _firedForCurrentWindow = true;
                _logger.LogInformation(
                    "[IdleDetector] Global idle threshold reached duration={Dur}s",
                    idle.TotalSeconds.ToString("F1"));

                try { await callback(idle, ct); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[IdleDetector] Callback failed");
                }
            }
            else if (idle < GlobalIdleThreshold && _firedForCurrentWindow)
            {
                _firedForCurrentWindow = false;
            }
        }
    }

    private Task OnUserMessageAsync(InternalEvent evt)
    {
        RecordUserMessage();
        return Task.CompletedTask;
    }

    private Task OnToolEventAsync(InternalEvent evt)
    {
        if (IsToolCompletionEvent(evt.Type))
            RecordToolCompleted();
        return Task.CompletedTask;
    }

    private static bool IsToolCompletionEvent(string eventType) =>
        eventType.Equals("tool.completed", StringComparison.OrdinalIgnoreCase)
        || eventType.EndsWith(".completed", StringComparison.OrdinalIgnoreCase)
        || eventType.EndsWith(".succeeded", StringComparison.OrdinalIgnoreCase)
        || eventType.EndsWith(".failed", StringComparison.OrdinalIgnoreCase);
}
