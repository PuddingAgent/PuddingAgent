namespace HarnessAgent.Core.Compaction;

/// <summary>Compaction tier based on context window usage ratio.</summary>
public enum CompactionTier
{
    /// <summary>Below 50% — no action needed.</summary>
    None,
    /// <summary>50-70% — soft warning, keep cache-stable prefix intact.</summary>
    Soft,
    /// <summary>70-85% — aggressive fold, shrink tail to 10% of window.</summary>
    Aggressive,
    /// <summary>Above 85% — force summarize and reset.</summary>
    Force,
}

/// <summary>The decision the compaction engine makes.</summary>
public sealed record CompactionDecision
{
    public required CompactionTier Tier { get; init; }
    public required double UsageRatio { get; init; }
    public int ContextWindowTokens { get; init; }
    public int UsedTokens { get; init; }
    public int RemainingTokens => ContextWindowTokens - UsedTokens;

    /// <summary>How many tokens the tail should be after compaction. 0 = no compaction needed.</summary>
    public int TailTokenBudget { get; init; }

    /// <summary>Human-readable recommendation.</summary>
    public string Recommendation { get; init; } = "";

    /// <summary>Whether compaction should be performed now.</summary>
    public bool ShouldCompact => Tier >= CompactionTier.Aggressive;
}

/// <summary>
/// Context compaction engine — Reasonix-style 3-tier folding.
/// Pure algorithm, no external dependencies.
/// </summary>
public sealed class CompactionEngine
{
    /// <summary>Ratio thresholds for each tier (configurable).</summary>
    public CompactionThresholds Thresholds { get; init; } = new();

    /// <summary>
    /// Evaluate the current context usage and decide what compaction action to take.
    /// </summary>
    /// <param name="usedTokens">Current estimated token usage.</param>
    /// <param name="contextWindowTokens">Maximum context window size.</param>
    /// <param name="recentTurnTokens">Estimated tokens in the most recent K turns.</param>
    public CompactionDecision Evaluate(int usedTokens, int contextWindowTokens, int recentTurnTokens = 0)
    {
        var ratio = (double)usedTokens / contextWindowTokens;
        var tier = DetermineTier(ratio);
        var tailBudget = CalculateTailBudget(tier, contextWindowTokens, usedTokens, recentTurnTokens);

        return new CompactionDecision
        {
            Tier = tier,
            UsageRatio = ratio,
            ContextWindowTokens = contextWindowTokens,
            UsedTokens = usedTokens,
            TailTokenBudget = tailBudget,
            Recommendation = BuildRecommendation(tier, ratio, tailBudget, contextWindowTokens),
        };
    }

    private CompactionTier DetermineTier(double ratio)
    {
        if (ratio >= Thresholds.ForceRatio) return CompactionTier.Force;
        if (ratio >= Thresholds.AggressiveRatio) return CompactionTier.Aggressive;
        if (ratio >= Thresholds.SoftRatio) return CompactionTier.Soft;
        return CompactionTier.None;
    }

    private int CalculateTailBudget(CompactionTier tier, int windowSize, int usedTokens, int recentTurnTokens)
    {
        switch (tier)
        {
            case CompactionTier.Force:
                // Force: keep only a minimal tail — the last few turns
                return Math.Max((int)(windowSize * Thresholds.ForceTailRatio), recentTurnTokens);

            case CompactionTier.Aggressive:
                // Aggressive: 10% of window for tail
                var aggressiveTail = (int)(windowSize * Thresholds.AggressiveTailRatio);
                return Math.Max(aggressiveTail, recentTurnTokens);

            case CompactionTier.Soft:
                // Soft: no tail constraint, just report
                return 0;

            default:
                return 0;
        }
    }

    private static string BuildRecommendation(CompactionTier tier, double ratio, int tailBudget, int windowSize)
    {
        return tier switch
        {
            CompactionTier.None =>
                $"Context healthy ({ratio:P0} used). No action needed.",

            CompactionTier.Soft =>
                $"Context growing ({ratio:P0} used). Cache-prefix still stable. " +
                $"Consider preparing a summary for upcoming compaction.",

            CompactionTier.Aggressive =>
                $"Context high ({ratio:P0} used). Aggressive fold: tail budget = {tailBudget} tokens " +
                $"({tailBudget * 100.0 / windowSize:F1}% of {windowSize / 1000}K window).",

            CompactionTier.Force =>
                $"Context critical ({ratio:P0} used). Force summarize: exit with summary, " +
                $"keeping only {tailBudget} tokens ({tailBudget * 100.0 / windowSize:F1}% of window).",

            _ => "Unknown tier.",
        };
    }
}

/// <summary>Configurable compaction threshold ratios.</summary>
public sealed record CompactionThresholds
{
    /// <summary>50% — soft warning, prefix still intact.</summary>
    public double SoftRatio { get; init; } = 0.50;

    /// <summary>70% — aggressive fold, tail = 10% of window.</summary>
    public double AggressiveRatio { get; init; } = 0.70;

    /// <summary>85% — force summarize, minimal tail.</summary>
    public double ForceRatio { get; init; } = 0.85;

    /// <summary>Tail budget ratio for aggressive fold (10% of context window).</summary>
    public double AggressiveTailRatio { get; init; } = 0.10;

    /// <summary>Tail budget ratio for force summarize (5% of context window).</summary>
    public double ForceTailRatio { get; init; } = 0.05;
}
