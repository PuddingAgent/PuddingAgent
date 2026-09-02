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

    public ExecutionUsageBudgetDecision Record(TokenUsageDto? usage)
    {
        if (budget is null)
            return ExecutionUsageBudgetDecision.Continue;

        if (usage is null)
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
            MaxInputTokens = Remaining(budget.MaxInputTokens, InputTokens),
            MaxOutputTokens = Remaining(budget.MaxOutputTokens, OutputTokens),
            MaxCost = Remaining(budget.MaxCost, Cost),
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

        if (budget.MaxInputTokens > 0 && InputTokens >= budget.MaxInputTokens)
        {
            return Stop(
                TerminalErrorCodes.WorkUnitBudgetExhausted,
                $"WorkUnit input Token budget exhausted ({InputTokens}/{budget.MaxInputTokens}).");
        }

        if (budget.MaxOutputTokens > 0 && OutputTokens >= budget.MaxOutputTokens)
        {
            return Stop(
                TerminalErrorCodes.WorkUnitBudgetExhausted,
                $"WorkUnit output Token budget exhausted ({OutputTokens}/{budget.MaxOutputTokens}).");
        }

        if (budget.MaxCost > 0 && Cost >= budget.MaxCost)
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

    private static long Remaining(long limit, long consumed)
        => limit > 0 ? Math.Max(1, limit - consumed) : 0;

    private static decimal Remaining(decimal limit, decimal consumed)
        => limit > 0 ? Math.Max(0.0000000001m, limit - consumed) : 0;

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
