using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>Route task nodes to execution targets without owning runtime loops.</summary>
public interface ITaskAssignmentService
{
    /// <summary>Assign a task node to the requested destination.</summary>
    Task<TaskNode> AssignAsync(TaskAssignmentRequest request, CancellationToken ct = default);
}
