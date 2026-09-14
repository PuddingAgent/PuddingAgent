using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

/// <summary>
/// Per-dispatch WorkUnit Token/cost ledger. The provider call is the smallest
/// enforceable boundary: a completed call is accounted once, then no tool or
/// subsequent LLM round may start after a limit is reached.
/// </summary>
internal sealed class ExecutionUsageBudgetTracker(ExecutionUsageBudget? budget)
{
    private const decimal TokensPerMillion = 1_000_000m;

    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public long CacheHitTokens { get; private set; }
    public decimal Cost { get; private set; }

    /// <summary>本执行实际模型请求的最大输入；委派累计 usage 不构成单轮样本。</summary>
    public long PeakRoundInputTokens { get; private set; }

    public ExecutionUsageBudgetDecision EvaluateBeforeRound()
    {
        if (budget is null)
            return ExecutionUsageBudgetDecision.Continue;

        if (budget.MaxCost > 0 && !budget.PricingKnown)
        {
            return Stop(
                TerminalErrorCodes.WorkUnitPricingUnavailable,
                "WorkUnit cost budget cannot be enforced because the selected model has no frozen pricing snapshot.");
        }

        return EvaluateLimits();
    }

    public ExecutionUsageBudgetDecision RecordInvocationUsage(TokenUsageDto? usage)
        => RecordUsage(usage, isInvocation: true);

    public ExecutionUsageBudgetDecision RecordDelegatedUsage(TokenUsageDto? usage)
        => RecordUsage(usage, isInvocation: false);

    private ExecutionUsageBudgetDecision RecordUsage(TokenUsageDto? usage, bool isInvocation)
    {
        if (budget is null)
            return ExecutionUsageBudgetDecision.Continue;

        if (usage is null
            || (budget.MaxInputTokens > 0 || budget.HasCostLimit) && usage.PromptTokens is null
            || (budget.HasOutputLimit || budget.HasCostLimit) && usage.CompletionTokens is null)
        {
            return Stop(
                TerminalErrorCodes.WorkUnitUsageUnavailable,
                "WorkUnit Token/cost budget cannot be enforced because the provider returned no usage payload.");
        }

        var input = Math.Max(0, usage.PromptTokens ?? 0);
        var output = Math.Max(0, usage.CompletionTokens ?? 0);
        var cacheHit = Math.Clamp(usage.PromptCacheHitTokens ?? 0, 0, input);
        var cacheMiss = input - cacheHit;

        InputTokens = SaturatingAdd(InputTokens, input);
        OutputTokens = SaturatingAdd(OutputTokens, output);
        CacheHitTokens = SaturatingAdd(CacheHitTokens, cacheHit);
        if (isInvocation)
            PeakRoundInputTokens = Math.Max(PeakRoundInputTokens, input);

        if (budget.PricingKnown)
        {
            Cost += ((cacheMiss * budget.InputPricePer1MTokens)
                     + (cacheHit * budget.CacheHitPricePer1MTokens)
                     + (output * budget.OutputPricePer1MTokens))
                    / TokensPerMillion;
        }

        return EvaluateLimits();
    }

    public ExecutionUsageBudget? CreateRemainingBudget()
    {
        if (budget is null)
            return null;

        return budget with
        {
            MaxInputTokens = budget.MaxInputTokens,
            MaxOutputTokens = Remaining(budget.MaxOutputTokens, OutputTokens),
            MaxCost = Remaining(budget.MaxCost, Cost),
            OutputLimitEnabled = budget.HasOutputLimit,
            CostLimitEnabled = budget.HasCostLimit,
            IsDerivedRemainder = true,
            PeakRoundInputTokens = PeakRoundInputTokens,
        };
    }

    public TokenUsageDto? CreateUsageSnapshot()
    {
        if (budget is null)
            return null;

        var prompt = ClampToInt(InputTokens);
        var completion = ClampToInt(OutputTokens);
        var cacheHit = Math.Min(prompt, ClampToInt(CacheHitTokens));
        return new TokenUsageDto
        {
            PromptTokens = prompt,
            CompletionTokens = completion,
            TotalTokens = prompt > int.MaxValue - completion
                ? int.MaxValue
                : prompt + completion,
            PromptCacheHitTokens = cacheHit,
            PromptCacheMissTokens = prompt - cacheHit,
        };
    }

    private ExecutionUsageBudgetDecision EvaluateLimits()
    {
        if (budget is null)
            return ExecutionUsageBudgetDecision.Continue;

        if (budget.MaxInputTokens > 0 && PeakRoundInputTokens > budget.MaxInputTokens)
        {
            return Stop(
                TerminalErrorCodes.WorkUnitBudgetExhausted,
                $"WorkUnit per-request input Token capacity exceeded ({PeakRoundInputTokens}/{budget.MaxInputTokens}); cumulative input={InputTokens}.");
        }

        if (budget.HasOutputLimit && OutputTokens >= budget.MaxOutputTokens)
        {
            return Stop(
                TerminalErrorCodes.WorkUnitBudgetExhausted,
                $"WorkUnit output Token budget exhausted ({OutputTokens}/{budget.MaxOutputTokens}).");
        }

        if (budget.HasCostLimit && Cost >= budget.MaxCost)
        {
            return Stop(
                TerminalErrorCodes.WorkUnitBudgetExhausted,
                $"WorkUnit cost budget exhausted ({Cost:F6}/{budget.MaxCost:F6}).");
        }

        return ExecutionUsageBudgetDecision.Continue;
    }

    private ExecutionUsageBudgetDecision Stop(string errorCode, string message) => new(
        ShouldStop: true,
        ErrorCode: errorCode,
        Message: message,
        InputTokens: InputTokens,
        OutputTokens: OutputTokens,
        CacheHitTokens: CacheHitTokens,
        Cost: Cost);

    private static long SaturatingAdd(long left, int right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    // 剩余值必须诚实归零，由各累计轴的启用标志消除「0 = 未设上限」的歧义。
    // 禁止回到 Math.Max(1, …) 伪造非零——那会派发一个首轮即死的子代理。
    private static long Remaining(long limit, long consumed)
        => limit > 0 ? Math.Max(0, limit - consumed) : 0;

    private static decimal Remaining(decimal limit, decimal consumed)
        => limit > 0 ? Math.Max(0m, limit - consumed) : 0;

    private static int ClampToInt(long value)
        => value >= int.MaxValue ? int.MaxValue : (int)Math.Max(0, value);
}

internal sealed record ExecutionUsageBudgetDecision(
    bool ShouldStop,
    string? ErrorCode,
    string? Message,
    long InputTokens,
    long OutputTokens,
    long CacheHitTokens,
    decimal Cost)
{
    public static ExecutionUsageBudgetDecision Continue { get; } = new(
        false,
        null,
        null,
        0,
        0,
        0,
        0m);
}
