using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.TaskPlanning;

/// <summary>EF-backed 实现：持久化任务规划和任务树节点。</summary>
public sealed class TaskPlanStore : ITaskPlanStore
{
    private readonly PlatformDbContext _db;
    private readonly ILogger<TaskPlanStore> _logger;

    public TaskPlanStore(
        PlatformDbContext db,
        ILogger<TaskPlanStore>? logger = null)
    {
        _db = db;
        _logger = logger ?? NullLogger<TaskPlanStore>.Instance;
    }

    /// <inheritdoc />
    public async Task<TaskPlanRun> CreatePlanAsync(TaskPlanCreateRequest request, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var planId = $"plan_{Guid.NewGuid():N}";
        var rootNodeId = $"task_{Guid.NewGuid():N}";
        var maxDelegationDepth = request.MaxDelegationDepth ?? 2;

        var plan = new TaskPlanRunEntity
        {
            PlanId = planId,
            WorkspaceId = request.WorkspaceId,
            RootSessionId = request.RootSessionId,
            LeaderAgentId = request.LeaderAgentId,
            Objective = request.Objective,
            Status = TaskPlanStatuses.Draft.ToString(),
            MaxDelegationDepth = maxDelegationDepth,
            DefaultAllowSubDelegation = request.DefaultAllowSubDelegation ?? true,
            AllowAgentCreationByLeader = request.AllowAgentCreationByLeader ?? true,
            MaxActiveTaskNodesPerPlan = request.MaxActiveTaskNodesPerPlan ?? 50,
            CreatedAt = now,
            UpdatedAt = now,
            TraceId = request.TraceId,
            CorrelationId = request.CorrelationId,
        };

        var rootNode = new TaskNodeEntity
        {
            TaskNodeId = rootNodeId,
            PlanId = planId,
            ParentTaskNodeId = null,
            Depth = 0,
            Objective = request.Objective,
            Status = TaskNodeStatuses.Draft.ToString(),
            AssignedToKind = TaskAssignmentKinds.Unassigned.ToString(),
            CreatedByAgentId = request.LeaderAgentId,
            AllowSubDelegation = request.DefaultAllowSubDelegation ?? true,
            AllowAgentCreation = request.AllowAgentCreationByLeader ?? true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.TaskPlanRuns.Add(plan);
        _db.TaskNodes.Add(rootNode);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("[TaskPlanStore] Created plan {PlanId} with root node {TaskNodeId}", planId, rootNodeId);

        return ToModel(plan);
    }

    /// <inheritdoc />
    public async Task<TaskPlanRun?> GetPlanAsync(string planId, CancellationToken ct = default)
    {
        var entity = await _db.TaskPlanRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.PlanId == planId, ct);

        return entity is null ? null : ToModel(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TaskPlanRun>> QueryPlansAsync(TaskPlanQuery query, CancellationToken ct = default)
    {
        var plans = _db.TaskPlanRuns.AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.WorkspaceId))
            plans = plans.Where(item => item.WorkspaceId == query.WorkspaceId);
        if (!string.IsNullOrWhiteSpace(query.LeaderAgentId))
            plans = plans.Where(item => item.LeaderAgentId == query.LeaderAgentId);
        if (query.Status is not null)
            plans = plans.Where(item => item.Status == query.Status.Value.ToString());
        if (query.CreatedFrom is not null)
            plans = plans.Where(item => item.CreatedAt >= query.CreatedFrom.Value.ToUnixTimeMilliseconds());
        if (query.CreatedTo is not null)
            plans = plans.Where(item => item.CreatedAt <= query.CreatedTo.Value.ToUnixTimeMilliseconds());

        var limit = Math.Max(1, query.Limit);
        var items = await plans
            .OrderByDescending(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Skip(Math.Max(0, query.Offset))
            .Take(limit)
            .AsNoTracking()
            .ToListAsync(ct);

        return items.ConvertAll(ToModel);
    }

    /// <inheritdoc />
    public async Task<TaskNode> CreateNodeAsync(TaskNodeCreateRequest request, CancellationToken ct = default)
    {
        var plan = await _db.TaskPlanRuns.FirstOrDefaultAsync(item => item.PlanId == request.PlanId, ct);
        if (plan is null)
            throw new InvalidOperationException($"Plan {request.PlanId} not found.");

        if (string.IsNullOrWhiteSpace(request.ParentTaskNodeId))
            throw new InvalidOperationException("Task node parent is required for CreateNodeAsync.");
        if (request.Depth < 0)
            throw new InvalidOperationException($"Depth {request.Depth} must be non-negative.");
        if (request.Depth == 0)
            throw new InvalidOperationException("Depth 0 is reserved for plan root nodes.");
        if (request.Depth > plan.MaxDelegationDepth)
            throw new InvalidOperationException(
                $"Depth {request.Depth} exceeds max delegation depth {plan.MaxDelegationDepth} for plan {request.PlanId}.");

        var parent = await _db.TaskNodes
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.TaskNodeId == request.ParentTaskNodeId, ct);
        if (parent is null)
            throw new InvalidOperationException($"Parent task node {request.ParentTaskNodeId} not found.");
        if (!string.Equals(parent.PlanId, request.PlanId, StringComparison.Ordinal))
            throw new InvalidOperationException("Parent task node does not belong to the target plan.");
        if (request.Depth != parent.Depth + 1)
            throw new InvalidOperationException(
                $"Depth {request.Depth} does not match parent depth {parent.Depth}.");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var node = new TaskNodeEntity
        {
            TaskNodeId = $"task_{Guid.NewGuid():N}",
            PlanId = request.PlanId,
            ParentTaskNodeId = request.ParentTaskNodeId,
            Depth = request.Depth,
            Title = request.Title,
            Objective = request.Objective,
            InputContextSummary = request.InputContextSummary,
            ExpectedOutputContract = request.ExpectedOutputContract,
            AssignedToKind = request.AssignedToKind.ToString(),
            AssignedToId = request.AssignedToId,
            AssignedTemplateId = request.AssignedTemplateId,
            CreatedByAgentId = request.CreatedByAgentId,
            Status = TaskNodeStatuses.Draft.ToString(),
            AllowSubDelegation = request.AllowSubDelegation ?? plan.DefaultAllowSubDelegation,
            AllowAgentCreation = request.AllowAgentCreation ?? plan.AllowAgentCreationByLeader,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.TaskNodes.Add(node);
        await _db.SaveChangesAsync(ct);

        return ToModel(node);
    }

    /// <inheritdoc />
    public async Task<TaskNode?> GetNodeAsync(string taskNodeId, CancellationToken ct = default)
    {
        var entity = await _db.TaskNodes
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.TaskNodeId == taskNodeId, ct);

        return entity is null ? null : ToModel(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TaskNode>> QueryNodesAsync(TaskNodeQuery query, CancellationToken ct = default)
    {
        var nodes = _db.TaskNodes.AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.PlanId))
            nodes = nodes.Where(item => item.PlanId == query.PlanId);
        if (!string.IsNullOrWhiteSpace(query.ParentTaskNodeId))
            nodes = nodes.Where(item => item.ParentTaskNodeId == query.ParentTaskNodeId);
        if (query.Status is not null)
            nodes = nodes.Where(item => item.Status == query.Status.Value.ToString());
        if (query.AssignedToKind is not null)
            nodes = nodes.Where(item => item.AssignedToKind == query.AssignedToKind.Value.ToString());
        if (!string.IsNullOrWhiteSpace(query.AssignedToId))
            nodes = nodes.Where(item => item.AssignedToId == query.AssignedToId);
        if (query.Depth is not null)
            nodes = nodes.Where(item => item.Depth == query.Depth.Value);

        var limit = Math.Max(1, query.Limit);
        var items = await nodes
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Skip(Math.Max(0, query.Offset))
            .Take(limit)
            .AsNoTracking()
            .ToListAsync(ct);

        return items.ConvertAll(ToModel);
    }

    /// <inheritdoc />
    public async Task<TaskNode> UpdateNodeStatusAsync(TaskNodeStatusUpdateRequest request, CancellationToken ct = default)
    {
        var node = await _db.TaskNodes.FirstOrDefaultAsync(item => item.TaskNodeId == request.TaskNodeId, ct);
        if (node is null)
            throw new InvalidOperationException($"Task node {request.TaskNodeId} not found.");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        node.Status = request.Status.ToString();
        node.ResultSummary = request.ResultSummary;
        node.ResultArtifactRef = request.ResultArtifactRef;
        node.ErrorMessage = request.ErrorMessage;
        node.UpdatedAt = now;

        if (request.Status is TaskNodeStatuses.Running && node.StartedAt is null)
            node.StartedAt = now;

        if (request.Status is TaskNodeStatuses.Completed or TaskNodeStatuses.Failed or TaskNodeStatuses.Cancelled)
        {
            if (node.CompletedAt is null)
                node.CompletedAt = now;
        }
        else
        {
            node.CompletedAt = null;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[TaskPlanStore] Updated node status {TaskNodeId} -> {Status}",
            request.TaskNodeId,
            request.Status);

        return ToModel(node);
    }

    private TaskPlanRun ToModel(TaskPlanRunEntity entity) => new()
    {
        PlanId = entity.PlanId,
        WorkspaceId = entity.WorkspaceId,
        RootSessionId = entity.RootSessionId,
        LeaderAgentId = entity.LeaderAgentId,
        Objective = entity.Objective,
        Status = ParsePlanStatus(entity.Status, entity.PlanId),
        MaxDelegationDepth = entity.MaxDelegationDepth,
        DefaultAllowSubDelegation = entity.DefaultAllowSubDelegation,
        AllowAgentCreationByLeader = entity.AllowAgentCreationByLeader,
        MaxActiveTaskNodesPerPlan = entity.MaxActiveTaskNodesPerPlan,
        CreatedAt = FromUnixMilliseconds(entity.CreatedAt),
        UpdatedAt = FromUnixMilliseconds(entity.UpdatedAt),
        CompletedAt = FromUnixMillisecondsNullable(entity.CompletedAt),
        ResultSummary = entity.ResultSummary,
        ErrorMessage = entity.ErrorMessage,
        TraceId = entity.TraceId,
        CorrelationId = entity.CorrelationId,
    };

    private TaskNode ToModel(TaskNodeEntity entity) => new()
    {
        TaskNodeId = entity.TaskNodeId,
        PlanId = entity.PlanId,
        ParentTaskNodeId = entity.ParentTaskNodeId,
        Depth = entity.Depth,
        Title = entity.Title,
        Objective = entity.Objective,
        InputContextSummary = entity.InputContextSummary,
        ExpectedOutputContract = entity.ExpectedOutputContract,
        AssignedToKind = ParseAssignmentKind(entity.AssignedToKind, entity.TaskNodeId),
        AssignedToId = entity.AssignedToId,
        AssignedTemplateId = entity.AssignedTemplateId,
        CreatedByAgentId = entity.CreatedByAgentId,
        Status = ParseNodeStatus(entity.Status, entity.TaskNodeId),
        AllowSubDelegation = entity.AllowSubDelegation,
        AllowAgentCreation = entity.AllowAgentCreation,
        ResultSummary = entity.ResultSummary,
        ResultArtifactRef = entity.ResultArtifactRef,
        ErrorMessage = entity.ErrorMessage,
        SupersededByTaskNodeId = entity.SupersededByTaskNodeId,
        StartedAt = FromUnixMillisecondsNullable(entity.StartedAt),
        CompletedAt = FromUnixMillisecondsNullable(entity.CompletedAt),
        CreatedAt = FromUnixMilliseconds(entity.CreatedAt),
        UpdatedAt = FromUnixMilliseconds(entity.UpdatedAt),
    };

    private TaskPlanStatuses ParsePlanStatus(string value, string planId)
    {
        if (Enum.TryParse(value, out TaskPlanStatuses parsed))
            return parsed;

        _logger.LogWarning(
            "[TaskPlanStore] Invalid plan status value '{Status}' for plan {PlanId}; using fallback Draft.",
            value,
            planId);

        return TaskPlanStatuses.Draft;
    }

    private TaskNodeStatuses ParseNodeStatus(string value, string taskNodeId)
    {
        if (Enum.TryParse(value, out TaskNodeStatuses parsed))
            return parsed;

        _logger.LogWarning(
            "[TaskPlanStore] Invalid node status value '{Status}' for node {TaskNodeId}; using fallback Draft.",
            value,
            taskNodeId);

        return TaskNodeStatuses.Draft;
    }

    private TaskAssignmentKinds ParseAssignmentKind(string value, string taskNodeId)
    {
        if (Enum.TryParse(value, out TaskAssignmentKinds parsed))
            return parsed;

        _logger.LogWarning(
            "[TaskPlanStore] Invalid assignment kind value '{Value}' for node {TaskNodeId}; using fallback Unassigned.",
            value,
            taskNodeId);

        return TaskAssignmentKinds.Unassigned;
    }

    private static DateTimeOffset FromUnixMilliseconds(long value)
    {
        return DateTimeOffset.FromUnixTimeMilliseconds(value);
    }

    private static DateTimeOffset? FromUnixMillisecondsNullable(long? value)
    {
        if (value is null)
            return null;

        return FromUnixMilliseconds(value.Value);
    }
}
