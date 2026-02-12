using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>Domain service abstraction for task planning run orchestration.</summary>
public interface ITaskPlanService : ITaskPlanStore
{
    /// <summary>Activate a draft plan.</summary>
    Task<TaskPlanRun> ActivatePlanAsync(string planId, CancellationToken ct = default);

    /// <summary>Mark a running plan as completed.</summary>
    Task<TaskPlanRun> CompletePlanAsync(string planId, string? resultSummary, CancellationToken ct = default);

    /// <summary>Mark a plan as failed.</summary>
    Task<TaskPlanRun> FailPlanAsync(string planId, string? errorMessage, CancellationToken ct = default);

    /// <summary>Cancel a running or draft plan.</summary>
    Task<TaskPlanRun> CancelPlanAsync(string planId, string? reason, CancellationToken ct = default);
}
