namespace PuddingLlmUsage.Contracts;

/// <summary>
/// 采样结果的持久化端口。
/// <para>
/// <b>为什么尝试与观测要分开记</b>：只存成功样本，采样中断在曲线上会表现为「两点之间没变化」，
/// 与「确实没花钱」无法区分。因此每次采样先落一条<b>尝试</b>（Started），结束再落结果；
/// 进程被强杀留下的 Started 由下次启动标记为
/// <see cref="LlmUsageAttemptStatus.Interrupted"/> —— 缺口因此可见，而不是被抹平。
/// </para>
/// </summary>
public interface ILlmUsageStore
{
    /// <summary>建库/建表（幂等）。</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 记一次采样<b>开始</b>，返回尝试 id。
    /// <para>必须在发起 HTTP 之前调用：先落盘再联网，才能抓住"进程在请求途中被杀"。</para>
    /// </summary>
    Task<long> BeginAttemptAsync(
        string providerId,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 结束一次尝试并写入该次的所有币种观测（单事务）。
    /// </summary>
    /// <param name="observations">
    /// 该 provider 本次的观测。为空表示"没有任何币种结果"，此时尝试按失败处理，
    /// <b>不会</b>被记成一次成功的空采样。
    /// </param>
    Task CompleteAttemptAsync(
        long attemptId,
        DateTimeOffset completedAtUtc,
        IReadOnlyList<LlmBalanceObservation> observations,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 读出某 provider 在 [fromUtc, toUtc] 内的观测，按时间升序，可直接交给
    /// <c>PuddingLlmUsage.LlmUsageAnalyzer.Analyze</c>。
    /// <para>
    /// 失败的尝试也会被还原成一条 <see cref="LlmBalanceObservation.Failure"/>（币种记为
    /// <see cref="UnknownCurrency"/>），因此分析器能看见缺口。
    /// </para>
    /// </summary>
    Task<IReadOnlyList<LlmBalanceObservation>> ReadObservationsAsync(
        string providerId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 把所有仍处于 <see cref="LlmUsageAttemptStatus.Running"/> 的尝试标记为
    /// <see cref="LlmUsageAttemptStatus.Interrupted"/>。进程启动时调用一次。
    /// </summary>
    /// <returns>被改写的行数（0 表示上次是干净退出）。</returns>
    Task<int> MarkRunningAttemptsInterruptedAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
}

/// <summary>失败观测在没有币种信息时使用的占位币种。</summary>
public static class UnknownCurrency
{
    /// <summary>占位值。<b>不得</b>参与与真实币种的金额比较。</summary>
    public const string Value = "unknown";
}

/// <summary>一次采样尝试的状态。</summary>
public enum LlmUsageAttemptStatus
{
    /// <summary>已开始、尚未结束（进程崩溃会留下这个状态）。</summary>
    Running = 1,

    /// <summary>正常结束并已写入结果。</summary>
    Succeeded = 2,

    /// <summary>结束但失败（网络/鉴权/解析等）。</summary>
    Failed = 3,

    /// <summary>进程在上一次运行中异常退出，遗留的 Running 被改写成此状态。</summary>
    Interrupted = 4,
}
