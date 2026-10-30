using PuddingLlmUsage.Contracts;

namespace PuddingLlmUsage;

/// <summary>一轮采样的结果（供日志与健康状态使用）。</summary>
/// <param name="ProviderId">本轮处理的 provider。</param>
/// <param name="Succeeded">是否拿到至少一条成功观测。</param>
/// <param name="ObservationCount">本轮写入的观测条数。</param>
/// <param name="Error">失败原因；成功时为 <c>null</c>。</param>
/// <param name="StoreFailed">
/// 存储本身失败（写不进库）。此时<b>本轮不再发起外部查询</b> ——
/// 查到的结果也无处安放，只会白白消耗额度。
/// </param>
public sealed record LlmSamplingOutcome(
    string ProviderId,
    bool Succeeded,
    int ObservationCount,
    string? Error,
    bool StoreFailed);

/// <summary>
/// 一轮采样：落 Started → 查余额 → 落结果。
/// <para>
/// <b>它不做的事</b>：不读时钟（时间由调用方给）、不捕获取消（取消是控制流，不是数据）、
/// 不决定什么时候跑（那是调度器的事）。这样它才能在测试里被完全确定地驱动。
/// </para>
/// <para>
/// <b>它必须做的事</b>：任何<b>非取消</b>异常都要落成一条失败记录并返回 outcome，
/// 而不是往上抛 —— 抛出去会让调度循环停摆，而缺口的代价比一次错误大得多。
/// </para>
/// </summary>
public sealed class LlmBalanceSampler(ILlmBalanceProbe probe, ILlmUsageStore store)
{
    private readonly ILlmBalanceProbe _probe = probe ?? throw new ArgumentNullException(nameof(probe));

    private readonly ILlmUsageStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>
    /// 对单个 provider 执行一轮采样。
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 被取消。</exception>
    public async Task<LlmSamplingOutcome> RunOnceAsync(
        string providerId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        long attemptId;
        try
        {
            // 先落盘再联网：进程若在请求途中被杀，这条 Running 会在下次启动被改成 Interrupted。
            attemptId = await _store.BeginAttemptAsync(providerId, nowUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new LlmSamplingOutcome(
                providerId, Succeeded: false, ObservationCount: 0,
                Error: $"无法写入采样起点，跳过本轮（不发起外部查询）：{exception.Message}",
                StoreFailed: true);
        }

        IReadOnlyList<LlmBalanceObservation> observations;
        try
        {
            observations = await _probe.QueryAsync(providerId, nowUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 端口约定不该抛，但"约定"不是保证 —— 兜住，别让调度循环停摆。
            observations =
            [
                LlmBalanceObservation.Failure(providerId, UnknownCurrency.Value, exception.Message, nowUtc),
            ];
        }

        try
        {
            await _store.CompleteAttemptAsync(attemptId, nowUtc, observations, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new LlmSamplingOutcome(
                providerId,
                Succeeded: observations.Any(o => o.Succeeded),
                ObservationCount: observations.Count,
                Error: $"查询完成但结果写入失败（该次会被下次启动标记为 Interrupted）：{exception.Message}",
                StoreFailed: true);
        }

        var succeeded = observations.Any(o => o.Succeeded);
        var error = succeeded
            ? null
            : observations.FirstOrDefault(o => !string.IsNullOrWhiteSpace(o.Error))?.Error ?? "未取得任何余额观测。";

        return new LlmSamplingOutcome(providerId, succeeded, observations.Count, error, StoreFailed: false);
    }

    /// <summary>
    /// 对配置中的每个 provider 顺序执行一轮。<b>串行</b>而不是并发：
    /// 并发打同一个上游端点没有收益，只会让失败更难归因。
    /// </summary>
    /// <param name="options">必须已 <see cref="LlmUsageSamplingOptions.Validate"/> 过。</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> 被取消。</exception>
    public async Task<IReadOnlyList<LlmSamplingOutcome>> RunRoundAsync(
        LlmUsageSamplingOptions options,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
            return [];

        var results = new List<LlmSamplingOutcome>(options.ProviderIds.Count);

        foreach (var providerId in options.ProviderIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RunOnceAsync(providerId, nowUtc, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }
}
