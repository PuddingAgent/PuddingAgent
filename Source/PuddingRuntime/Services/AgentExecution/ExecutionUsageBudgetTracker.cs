using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingExecutionResources;
using PuddingExecutionResources.Contracts;

namespace PuddingRuntime.Services;

/// <summary>
/// Per-dispatch WorkUnit Token/cost ledger. The provider call is the smallest
/// enforceable boundary: a completed call is accounted once, then no tool or
/// subsequent LLM round may start after a limit is reached.
/// </summary>
internal sealed class ExecutionUsageBudgetTracker : IDisposable
{
    private readonly ExecutionUsageBudget? budget;
    private readonly ExecutionResourceScope _account;
    private readonly Lazy<IExecutionResourceScope> _resources;
    public IExecutionResourceScope Resources => _resources.Value;
    public ExecutionUsageBudgetTracker(ExecutionUsageBudget? budget, ExecutionResourceIdentity? identity = null,
        DateTimeOffset? deadline = null, CancellationToken ct = default)
    {
        var expires = deadline is { } d && d < DateTimeOffset.UtcNow.AddHours(24) ? d : DateTimeOffset.UtcNow.AddHours(24);
        this.budget = budget;
        _account = new(identity ?? new("internal", "internal", "internal", Guid.NewGuid().ToString("N")),
            new(MaxOutputTokens: budget?.HasOutputLimit == true ? budget.MaxOutputTokens : null,
                MaxCost: budget?.HasCostLimit == true ? budget.MaxCost : null,
                MaxInputTokensPerRequest: budget?.MaxInputTokens > 0 ? budget.MaxInputTokens : null),
            expires, ct);
        // A finite per-dispatch auxiliary allowance also applies to ordinary chat without a WorkUnit budget.
        // Do not throw before the existing loop's cancellation/terminal cleanup block is entered.
        _resources = new(() => _account.CreateChild(new(MaxOutputTokens: 8192, MaxCalls: 8), _account.Deadline));
    }
    public void Dispose() { if (_resources.IsValueCreated) Resources.Dispose(); _account.Dispose(); }
    private const decimal TokensPerMillion = 1_000_000m;

    public long InputTokens => _account.Snapshot().InputTokens;
    public long OutputTokens => _account.Snapshot().OutputTokens;
    public long CacheHitTokens => _account.Snapshot().CacheHitTokens;
    public decimal Cost => _account.Snapshot().Cost;

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
        if (usage is null
            || budget is not null && ((budget.MaxInputTokens > 0 || budget.HasCostLimit) && usage.PromptTokens is null
            || (budget.HasOutputLimit || budget.HasCostLimit) && usage.CompletionTokens is null))
        {
            if (budget is null) return ExecutionUsageBudgetDecision.Continue;
            _account.FailUnknown();
            return Stop(
                TerminalErrorCodes.WorkUnitUsageUnavailable,
                "WorkUnit Token/cost budget cannot be enforced because the provider returned no usage payload.");
        }

        var input = Math.Max(0, usage.PromptTokens ?? 0);
        var output = Math.Max(0, usage.CompletionTokens ?? 0);
        var cacheHit = Math.Clamp(usage.PromptCacheHitTokens ?? 0, 0, input);
        var cacheMiss = input - cacheHit;

        if (isInvocation)
            PeakRoundInputTokens = Math.Max(PeakRoundInputTokens, input);

        decimal invocationCost = 0;
        if (budget?.PricingKnown == true)
        {
            invocationCost = ((cacheMiss * budget.InputPricePer1MTokens)
                     + (cacheHit * budget.CacheHitPricePer1MTokens)
                     + (output * budget.OutputPricePer1MTokens))
                    / TokensPerMillion;
        }
        _account.RecordExternal(new(input, output, cacheHit), invocationCost);

        return EvaluateLimits();
    }

    public ExecutionUsageBudget? CreateRemainingBudget()
    {
        if (budget is null)
            return null;

        var snapshot = _account.Snapshot();
        return budget with
        {
            MaxInputTokens = budget.MaxInputTokens,
            MaxOutputTokens = Remaining(budget.MaxOutputTokens, snapshot.OutputTokens + snapshot.ReservedOutputTokens + snapshot.UnknownOutputTokens),
            MaxCost = Remaining(budget.MaxCost, snapshot.Cost + snapshot.ReservedCost + snapshot.UnknownCost),
            OutputLimitEnabled = budget.HasOutputLimit,
            CostLimitEnabled = budget.HasCostLimit,
            IsDerivedRemainder = true,
            PeakRoundInputTokens = PeakRoundInputTokens,
        };
    }

    public TokenUsageDto? CreateUsageSnapshot()
    {
        if (budget is null && (!_resources.IsValueCreated || Resources.Snapshot().Calls == 0))
            return null;

        var snapshot = _account.Snapshot();
        var prompt = ClampToInt(snapshot.InputTokens);
        var completion = ClampToInt(snapshot.OutputTokens);
        var cacheHit = Math.Min(prompt, ClampToInt(snapshot.CacheHitTokens));
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

        var resourceState = _account.Snapshot();
        if (resourceState.FailureCode is not null)
            return Stop(TerminalErrorCodes.WorkUnitUsageUnavailable, "A delegated model call has unknown or out-of-reservation usage; parent budget cannot admit further calls.");

        if (budget.MaxInputTokens > 0 && PeakRoundInputTokens > budget.MaxInputTokens)
        {
            return Stop(
                TerminalErrorCodes.WorkUnitBudgetExhausted,
                $"WorkUnit per-request input Token capacity exceeded ({PeakRoundInputTokens}/{budget.MaxInputTokens}); cumulative input={InputTokens}.");
        }

        if (budget.HasOutputLimit && resourceState.OutputTokens + resourceState.ReservedOutputTokens + resourceState.UnknownOutputTokens >= budget.MaxOutputTokens)
        {
            return Stop(
                TerminalErrorCodes.WorkUnitBudgetExhausted,
                $"WorkUnit output Token budget exhausted ({OutputTokens}/{budget.MaxOutputTokens}).");
        }

        if (budget.HasCostLimit && resourceState.Cost + resourceState.ReservedCost + resourceState.UnknownCost >= budget.MaxCost)
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
