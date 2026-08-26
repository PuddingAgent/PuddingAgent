using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;

namespace PuddingPlatform.Services.Goals;

/// <summary>Turn terminal fact 驱动的 bounded settlement/reconciliation worker。</summary>
public sealed class GoalSettlementWorker(
    GoalSettlementStore store,
    IGoalIterationVerifier verifier,
    IOptions<GoalRunOptions> options,
    ILogger<GoalSettlementWorker> logger) : BackgroundService
{
    private readonly GoalRunOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.ContinuationEnabled)
        {
            logger.LogInformation("[GoalSettlement] disabled");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(stoppingToken);
                await Task.Delay(_options.ContinuationScanInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[GoalSettlement] scan failed");
                await Task.Delay(_options.ContinuationScanInterval, stoppingToken);
            }
        }
    }

    public async Task<int> ProcessOnceAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled || !_options.ContinuationEnabled)
            return 0;
        var candidates = await store.GetCandidatesAsync(_options.ContinuationBatchSize, ct);
        var applied = 0;
        foreach (var candidate in candidates)
        {
            var decision = await verifier.VerifyAsync(candidate.ToCapsule(), ct);
            if (await store.ApplyAsync(candidate, decision, ct))
            {
                applied++;
                logger.LogInformation(
                    "[GoalSettlement] applied goal={GoalRunId} epoch={Epoch} iteration={Iteration} turn={TurnId} verdict={Verdict}",
                    candidate.GoalRunId,
                    candidate.ActivationEpoch,
                    candidate.IterationNo,
                    candidate.TurnId,
                    decision.Verdict);
            }
        }
        return applied;
    }
}
