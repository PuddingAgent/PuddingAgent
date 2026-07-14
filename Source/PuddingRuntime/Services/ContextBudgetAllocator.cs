using Microsoft.Extensions.Logging;

namespace PuddingRuntime.Services;

/// <summary>
/// Encapsulates token budget arithmetic: compaction level, available budget calculation,
/// and per-layer allocation percentages. Eliminates scattered budget math in AssembleAsync.
/// </summary>
public sealed class ContextBudgetAllocator
{
    private readonly ILogger _logger;

    public const int ReservedForReply = 4096;
    public const double GentleThreshold = 0.6;
    public const double CompactionThreshold = 0.8;

    public ContextBudgetAllocator(ILogger logger)
    {
        _logger = logger;
    }

    public void Initialize(ContextBuildContext ctx)
    {
        ctx.AvailableBudget = Math.Max(ctx.TotalBudget - ReservedForReply - ctx.UsedBudget, 500);
        ctx.CompactionLevel = DetermineCompactionLevel(ctx.UsedBudget, ctx.TotalBudget);
    }

    public void UpdateAvailable(ContextBuildContext ctx)
    {
        ctx.AvailableBudget = Math.Max(ctx.TotalBudget - ReservedForReply - ctx.UsedBudget, 200);
    }

    public int AllocatePercent(ContextBuildContext ctx, double percent)
    {
        return (int)(ctx.AvailableBudget * percent);
    }

    public int AllocatePercentWithFloor(ContextBuildContext ctx, double percent, int floor)
    {
        return Math.Max(AllocatePercent(ctx, percent), floor);
    }

    public ContextPipelineCompactionLevel DetermineCompactionLevel(int usedBudget, int totalBudget)
    {
        if (totalBudget <= 0) return ContextPipelineCompactionLevel.None;
        var ratio = (double)usedBudget / totalBudget;
        if (ratio >= CompactionThreshold) return ContextPipelineCompactionLevel.Aggressive;
        if (ratio >= GentleThreshold) return ContextPipelineCompactionLevel.Gentle;
        return ContextPipelineCompactionLevel.None;
    }
}
