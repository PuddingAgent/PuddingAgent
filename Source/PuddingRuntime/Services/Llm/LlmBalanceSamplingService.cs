using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuddingLlmUsage;
using PuddingLlmUsage.Contracts;

namespace PuddingRuntime.Services.Llm;

/// <summary>
/// 余额采样的宿主壳：只管"什么时候跑"，不掺任何判定或存储细节。
/// <para>
/// <b>为什么必须是独立时钟而不是 Agent 心跳</b>：心跳是尽力模式（周期会漂移、还会被批量投递），
/// 用它驱动采样会让「十分钟一条」变成「有时十分钟、有时几小时」的随机序列，
/// 那样的曲线不能用来判断消耗速率。
/// </para>
/// <para>
/// <b>失败不终止服务</b>：任何非取消异常只记日志并进入下一轮。采样是后台观测，
/// 它中断的代价（曲线上出现一个无法解释的缺口）远大于一轮采样失败。
/// </para>
/// </summary>
public sealed class LlmBalanceSamplingService(
    LlmBalanceSampler sampler,
    ILlmUsageStore store,
    LlmUsageSamplingOptions options,
    TimeProvider timeProvider,
    ILogger<LlmBalanceSamplingService> logger) : BackgroundService
{
    private readonly LlmBalanceSampler _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));

    private readonly ILlmUsageStore _store = store ?? throw new ArgumentNullException(nameof(store));

    private readonly LlmUsageSamplingOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<LlmBalanceSamplingService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 上一次运行若在请求途中被杀，会留下 Running 行。先把它们改写成 Interrupted，
        // 否则那段时间在曲线上看起来像"没变化"。
        try
        {
            var rewritten = await _store
                .MarkRunningAttemptsInterruptedAsync(_timeProvider.GetUtcNow(), stoppingToken)
                .ConfigureAwait(false);

            if (rewritten > 0)
            {
                _logger.LogWarning(
                    "LLM 余额采样：上一次运行遗留 {Count} 条未完成的采样记录，已标记为 Interrupted。",
                    rewritten);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            // 存储不可用不应阻止服务启动 —— 后面的每一轮都会各自记录失败。
            _logger.LogWarning(exception, "LLM 余额采样：启动扫描未完成采样记录失败。");
        }

        if (!_options.Enabled)
        {
            _logger.LogInformation("LLM 余额采样已禁用（Enabled=false），服务不发起任何外部请求。");
            return;
        }

        _logger.LogInformation(
            "LLM 余额采样启动：providers=[{Providers}] 间隔={Interval} 单次超时={Timeout}。",
            string.Join(", ", _options.ProviderIds),
            _options.Interval,
            _options.PerProviderTimeout);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunRoundSafelyAsync(stoppingToken).ConfigureAwait(false);

            var delay = LlmSamplingSchedule.DelayUntilNextSlot(_timeProvider.GetUtcNow(), _options.Interval);

            try
            {
                await Task.Delay(delay, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunRoundSafelyAsync(CancellationToken stoppingToken)
    {
        try
        {
            var outcomes = await _sampler
                .RunRoundAsync(_options, _timeProvider.GetUtcNow(), stoppingToken)
                .ConfigureAwait(false);

            foreach (var outcome in outcomes)
            {
                if (outcome.StoreFailed)
                {
                    _logger.LogError(
                        "LLM 余额采样：{Provider} 写入失败：{Error}", outcome.ProviderId, outcome.Error);
                }
                else if (outcome.Succeeded)
                {
                    _logger.LogDebug(
                        "LLM 余额采样：{Provider} 成功，写入 {Count} 条观测。",
                        outcome.ProviderId, outcome.ObservationCount);
                }
                else
                {
                    _logger.LogWarning(
                        "LLM 余额采样：{Provider} 未取得余额：{Error}", outcome.ProviderId, outcome.Error);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "LLM 余额采样：本轮异常，下一轮继续。");
        }
    }
}
