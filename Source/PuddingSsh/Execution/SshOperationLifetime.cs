using PuddingSsh.Diagnostics;

namespace PuddingSsh.Execution;

/// <summary>
/// 操作被终止的**原因**（ADR-096 D2）。公开结果仍只用既有的 <c>ssh.cancelled</c>/<c>ssh.timeout</c>，
/// 原因以稳定 token（<c>stop_reason=&lt;wire&gt;</c>）出现在诊断里；Core 停止不得被写成操作超时。
/// </summary>
public enum SshStopReason
{
    None,
    CallerCancelled,
    HostStopping,
    OperationDeadline,
    ConnectDeadline,
}

public static class SshStopReasons
{
    public static string ToWire(this SshStopReason reason) => reason switch
    {
        SshStopReason.CallerCancelled => "caller_cancelled",
        SshStopReason.HostStopping => "host_stopping",
        SshStopReason.OperationDeadline => "operation_deadline",
        SshStopReason.ConnectDeadline => "connect_deadline",
        _ => "none",
    };

    /// <summary>稳定诊断 token：测试与审计按它断言，不解析自由文本。</summary>
    public static string ToDiagnostic(this SshStopReason reason) => "stop_reason=" + reason.ToWire();
}

/// <summary>前置围栏触发时抛出：携带终止原因，由协调器映射成公开错误码与诊断。</summary>
internal sealed class SshOperationStoppedException(SshStopReason reason)
    : Exception(reason.ToDiagnostic())
{
    public SshStopReason Reason { get; } = reason;
}

/// <summary>阶段等待结果：<see cref="Completed"/> 为 false 表示已终止（原因见 <see cref="Reason"/>）。</summary>
internal readonly record struct SshStageOutcome<T>(bool Completed, T? Result, SshStopReason Reason);

/// <summary>
/// 单次操作的 life：**受理时算一次**绝对预算，阶段只拿剩余额度（ADR-096 D2）。
/// <list type="bullet">
///   <item>预算 = min(请求, 主机上限, 宿主 UTC deadline 折算) —— 由协调器在构造前算好。</item>
///   <item>计时用注入的 <see cref="TimeProvider"/>（生产为系统时钟），剩余额度用**单调时间戳**算，
///         因此系统时间调整不会凭空给出第二份完整预算。</item>
///   <item>阶段晚返回由 <see cref="WaitStageAsync"/> 交给 owner 收尾：迟到的身份只销毁材料、
///         迟到的连接只回收，绝不允许开启下一阶段。</item>
/// </list>
/// </summary>
internal sealed class SshOperationLifetime : IDisposable
{
    private readonly TimeProvider _time;
    private readonly long _startTimestamp;
    private readonly TimeSpan _budget;
    private readonly CancellationTokenSource _cts;
    private readonly ITimer? _budgetTimer;
    private readonly CancellationToken _callerToken;
    private readonly CancellationToken _shutdownToken;
    private int _lateSuccesses;
    private int _lateFailures;

    public SshOperationLifetime(
        TimeProvider time,
        TimeSpan budget,
        CancellationToken callerToken,
        CancellationToken shutdownToken)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _callerToken = callerToken;
        _shutdownToken = shutdownToken;
        _budget = budget > TimeSpan.Zero ? budget : TimeSpan.Zero;
        _startTimestamp = time.GetTimestamp();
        StartedAtUtc = time.GetUtcNow();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken, shutdownToken);
        if (_budget > TimeSpan.Zero)
        {
            // 用注入的 TimeProvider 建计时器（而不是 CancelAfter）：生产是系统时钟，测试可替换。
            _budgetTimer = time.CreateTimer(
                static state =>
                {
                    try
                    {
                        ((CancellationTokenSource)state!).Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // 已经释放：无需再取消。
                    }
                },
                _cts,
                _budget,
                Timeout.InfiniteTimeSpan);
        }
        else
        {
            _cts.Cancel();
        }
    }

    public DateTimeOffset StartedAtUtc { get; }

    public CancellationToken Token => _cts.Token;

    public bool IsStopped => _cts.IsCancellationRequested;

    /// <summary>迟到的阶段结果被收尾的次数（测试用它证明「晚返回有人负责」）。</summary>
    public int LateSuccessesObserved => Volatile.Read(ref _lateSuccesses);

    public int LateFailuresObserved => Volatile.Read(ref _lateFailures);

    /// <summary>终止原因；未终止时为 <see cref="SshStopReason.None"/>。</summary>
    public SshStopReason StopReason
    {
        get
        {
            if (!_cts.IsCancellationRequested)
            {
                return SshStopReason.None;
            }

            // Core 停止优先于调用方取消：它是更强的权威，也不能被写成超时。
            if (_shutdownToken.IsCancellationRequested)
            {
                return SshStopReason.HostStopping;
            }

            if (_callerToken.IsCancellationRequested)
            {
                return SshStopReason.CallerCancelled;
            }

            return SshStopReason.OperationDeadline;
        }
    }

    /// <summary>剩余操作额度（可用 cap 进一步收窄，例如连接阶段的 connectTimeout）。</summary>
    public TimeSpan Remaining(TimeSpan? cap = null)
    {
        var remaining = _budget - _time.GetElapsedTime(_startTimestamp);
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        return cap is { } value && value < remaining ? value : remaining;
    }

    public TimeSpan RemainingOperation => Remaining();

    public TimeSpan RemainingConnect(TimeSpan connectTimeout) => Remaining(connectTimeout);

    public TimeSpan RemainingCleanup(TimeSpan cleanupGrace) => Remaining(cleanupGrace);

    /// <summary>副作用之前的前置围栏：已终止即抛，绝不「带着已取消的 token 继续往下走」。</summary>
    public void CheckBeforeSideEffect()
    {
        if (IsStopped)
        {
            throw new SshOperationStoppedException(StopReason);
        }
    }

    /// <summary>
    /// 有界等待一个阶段。到期或终止时：返回终止事实并把迟到结果交给 owner 收尾
    /// （异常被读取即视为已观察），调用方**不得**再进入下一阶段。
    /// </summary>
    public async Task<SshStageOutcome<T>> WaitStageAsync<T>(
        Task<T> stage,
        TimeSpan stageBudget,
        SshStopReason stageBudgetReason,
        Action<T>? onLateSuccess = null,
        Action<Exception>? onLateFailure = null)
    {
        ArgumentNullException.ThrowIfNull(stage);

        if (IsStopped)
        {
            ObserveLate(stage, onLateSuccess, onLateFailure);
            return new SshStageOutcome<T>(false, default, StopReason);
        }

        var budget = stageBudget > TimeSpan.Zero ? stageBudget : TimeSpan.Zero;
        var stageDeadline = budget > TimeSpan.Zero ? Task.Delay(budget, _time) : Task.CompletedTask;
        var stopped = Task.Delay(Timeout.InfiniteTimeSpan, _cts.Token);

        var winner = await Task.WhenAny(stage, stageDeadline, stopped).ConfigureAwait(false);
        if (winner == stage)
        {
            T result;
            try
            {
                result = await stage.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 阶段自身的失败照样是「已观察」；调用方按异常类型分类。
                Volatile.Write(ref _lateFailures, LateFailuresObserved + 1);
                onLateFailure?.Invoke(ex);
                throw;
            }

            return new SshStageOutcome<T>(true, result, SshStopReason.None);
        }

        ObserveLate(stage, onLateSuccess, onLateFailure);
        var reason = IsStopped ? StopReason : stageBudgetReason;
        return new SshStageOutcome<T>(false, default, reason);
    }

    private void ObserveLate<T>(Task<T> stage, Action<T>? onSuccess, Action<Exception>? onFailure)
    {
        if (stage.IsCompletedSuccessfully)
        {
            // 已完成但没被等待：直接收尾，避免为已结束的任务留 continuation。
            InvokeSuccess(stage.Result, onSuccess);
            return;
        }

        if (stage.IsFaulted || stage.IsCanceled)
        {
            InvokeFailure(stage, onFailure);
            return;
        }

        _ = stage.ContinueWith(
            static (completed, state) =>
            {
                var owner = (LateOwner<T>)state!;
                if (completed.IsFaulted || completed.IsCanceled)
                {
                    owner.Lifetime.InvokeFailure(completed, owner.OnFailure);
                    return;
                }

                owner.Lifetime.InvokeSuccess(completed.Result, owner.OnSuccess);
            },
            new LateOwner<T>(this, onSuccess, onFailure),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void InvokeSuccess<T>(T result, Action<T>? onSuccess)
    {
        Interlocked.Increment(ref _lateSuccesses);
        try
        {
            onSuccess?.Invoke(result);
        }
        catch (Exception)
        {
            // 收尾失败不得变成未观察异常：生命周期不因回收问题崩溃。
        }
    }

    private void InvokeFailure<T>(Task<T> stage, Action<Exception>? onFailure)
    {
        Interlocked.Increment(ref _lateFailures);

        // 读取 Exception 即标记为已观察（否则会以 UnobservedTaskException 形式泄漏）。
        var observed = stage.Exception?.Flatten().InnerExceptions.FirstOrDefault();
        try
        {
            onFailure?.Invoke(observed ?? new OperationCanceledException());
        }
        catch (Exception)
        {
            // 同上：观察者的异常不得外溢。
        }
    }

    public void Dispose()
    {
        _budgetTimer?.Dispose();
        _cts.Dispose();
    }

    private sealed record LateOwner<T>(
        SshOperationLifetime Lifetime,
        Action<T>? OnSuccess,
        Action<Exception>? OnFailure);
}
