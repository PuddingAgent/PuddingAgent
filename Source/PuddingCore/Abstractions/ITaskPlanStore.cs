using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>Persistence abstraction for planning and task nodes.</summary>
public interface ITaskPlanStore
{
    /// <summary>创建计划。</summary>
    Task<TaskPlanRun> CreatePlanAsync(TaskPlanCreateRequest request, CancellationToken ct = default);

    /// <summary>按计划 ID 获取计划。</summary>
    Task<TaskPlanRun?> GetPlanAsync(string planId, CancellationToken ct = default);

    /// <summary>按查询条件分页获取计划列表。</summary>
    Task<IReadOnlyList<TaskPlanRun>> QueryPlansAsync(TaskPlanQuery query, CancellationToken ct = default);

    /// <summary>创建任务节点。</summary>
    Task<TaskNode> CreateNodeAsync(TaskNodeCreateRequest request, CancellationToken ct = default);

    /// <summary>按任务节点 ID 获取节点。</summary>
    Task<TaskNode?> GetNodeAsync(string taskNodeId, CancellationToken ct = default);

    /// <summary>按查询条件分页获取任务节点。</summary>
    Task<IReadOnlyList<TaskNode>> QueryNodesAsync(TaskNodeQuery query, CancellationToken ct = default);

    /// <summary>更新任务节点状态。</summary>
    Task<TaskNode> UpdateNodeStatusAsync(TaskNodeStatusUpdateRequest request, CancellationToken ct = default);
}
