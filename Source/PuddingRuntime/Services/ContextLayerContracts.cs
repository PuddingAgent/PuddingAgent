using System.Text;
using PuddingCode.Platform;

namespace PuddingRuntime.Services;

/// <summary>
/// Contract for building a single context layer.
/// Each builder owns a logical group of layers and is invoked in a fixed order by ContextPipeline.
/// Builders must NOT depend on IServiceProvider or determine global order.
/// </summary>
public interface IContextLayerBuilder
{
    /// <summary>Layer group name for diagnostics.</summary>
    string Name { get; }

    /// <summary>
    /// Builds one or more context layers and appends them to the output.
    /// Returns the total tokens consumed.
    /// </summary>
    Task<int> BuildAsync(
        ContextBuildContext ctx,
        StringBuilder output,
        List<ContextLayerSnapshot> layers,
        List<ContextLayerInfo> layerInfos,
        CancellationToken ct);
}

/// <summary>
/// Input context available to every layer builder.
/// </summary>
public sealed class ContextBuildContext
{
    public required ContextRequest Request { get; init; }
    public required int TotalBudget { get; init; }
    public int UsedBudget { get; set; }
    public ContextPipelineCompactionLevel CompactionLevel { get; set; }
    public int AvailableBudget { get; set; }
    public bool HasMemorySummary { get; set; }
    public int InboundTokens { get; set; }
}

// Note: ContextLayerInfo is defined in PuddingCode.Platform.
