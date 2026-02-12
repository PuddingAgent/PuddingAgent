namespace PuddingCode.Configuration;

/// <summary>Task planning feature flags and limits.</summary>
public sealed class TaskPlanningOptions
{
    public const string SectionName = "TaskPlanning";

    public int MaxDelegationDepth { get; init; } = 2;
    public bool DefaultAllowSubDelegation { get; init; } = true;
    public bool AllowAgentCreationByLeader { get; init; } = true;
    public int MaxActiveTaskNodesPerPlan { get; init; } = 50;
}
