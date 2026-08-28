using PuddingRuntime.Services.Tools;

namespace PuddingRuntime.Services.AgentLoop;

/// <summary>
/// Tracks a normalized discovery-only loop. Different search queries still belong to the same
/// progress family because discovering definitions is preparation, not task execution.
/// </summary>
internal sealed class ToolDiscoveryLoopTracker
{
    private readonly int _maxConsecutiveCalls;
    private int _consecutiveCalls;

    internal ToolDiscoveryLoopTracker(int maxConsecutiveCalls)
    {
        _maxConsecutiveCalls = Math.Max(1, maxConsecutiveCalls);
    }

    internal int ConsecutiveCalls => _consecutiveCalls;

    internal bool Observe(string? canonicalToolName)
    {
        if (!string.Equals(
                canonicalToolName,
                ToolExposurePlanner.SearchToolId,
                StringComparison.OrdinalIgnoreCase))
        {
            _consecutiveCalls = 0;
            return false;
        }

        _consecutiveCalls++;
        return _consecutiveCalls >= _maxConsecutiveCalls;
    }
}
