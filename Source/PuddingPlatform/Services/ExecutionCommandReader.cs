using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Goals;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// Read-only adapter for execution commands.
/// All command mutations are owned by the acceptance, lease, control and journal stores.
/// </summary>
public sealed class ExecutionCommandReader(
    IDbContextFactory<PlatformDbContext> dbFactory,
    TimeProvider? timeProvider = null) : IExecutionCommandReader
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ExecutionCommandRecord?> GetAsync(
        string commandId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ChatExecutionCommands
            .AsNoTracking()
            .FirstOrDefaultAsync(command => command.CommandId == commandId, ct);
        return entity is null ? null : await MapAsync(db, entity, ct);
    }

    public async Task<ExecutionCommandRecord?> FindByTurnIdAsync(
        string conversationId,
        string turnId,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ChatExecutionCommands
            .AsNoTracking()
            .Where(command =>
                command.SessionId == conversationId &&
                command.TurnId == turnId)
            .OrderByDescending(command => command.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return entity is null ? null : await MapAsync(db, entity, ct);
    }

    private async Task<ExecutionCommandRecord> MapAsync(
        PlatformDbContext db,
        ChatExecutionCommandEntity entity,
        CancellationToken ct)
    {
        var result = new ExecutionCommandRecord
        {
            CommandId = entity.CommandId,
            TraceId = entity.TraceId,
            WorkspaceId = entity.WorkspaceId,
            ConversationId = entity.SessionId,
            AssistantMessageId = entity.MessageId,
            UserMessageId = entity.UserMessageId,
            TurnId = entity.TurnId,
            AgentInstanceId = entity.AgentInstanceId,
            UserId = entity.UserId,
            ChannelId = entity.ChannelId,
            Status = ParseStatus(entity.Status),
            RunId = entity.RunId,
        };

        var iteration = await db.GoalIterations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.CommandId == entity.CommandId, ct);
        if (iteration is null)
            return result;

        var binding = await db.TaskGoalBindings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.GoalRunId == iteration.GoalRunId, ct);
        if (binding is null || string.IsNullOrWhiteSpace(binding.TaskPlanId))
            return result;

        var metadata = ParseMetadata(entity.MetadataJson);
        var metadataPlanId = GetRequiredMetadata(metadata, GoalContinuationMetadata.TaskPlanId);
        var metadataFingerprint = GetRequiredMetadata(
            metadata, GoalContinuationMetadata.TaskPlanFingerprint);
        var metadataNodeId = GetRequiredMetadata(metadata, GoalContinuationMetadata.TaskNodeId);
        var metadataParentNodeId = GetMetadata(metadata, GoalContinuationMetadata.ParentTaskNodeId);

        var goal = await db.GoalRuns.AsNoTracking()
            .SingleOrDefaultAsync(item => item.GoalRunId == iteration.GoalRunId, ct);
        var task = await db.WorkspaceTasks.AsNoTracking()
            .SingleOrDefaultAsync(item => item.WorkspaceId == binding.WorkspaceId
                && item.TaskId == binding.TaskId, ct);
        var reservation = string.IsNullOrWhiteSpace(binding.ReservationId)
            ? null
            : await db.AgentExecutionReservations.AsNoTracking()
                .SingleOrDefaultAsync(item => item.ReservationId == binding.ReservationId, ct);
        var plan = await db.TaskPlanRuns.AsNoTracking()
            .SingleOrDefaultAsync(item => item.PlanId == binding.TaskPlanId, ct);
        var workUnits = await db.TaskNodes.AsNoTracking()
            .Where(item => item.PlanId == binding.TaskPlanId && item.Depth == 1)
            .OrderBy(item => item.SequenceNo)
            .ToListAsync(ct);
        var workUnit = workUnits.FirstOrDefault(item =>
            item.Status is not ("Completed" or "Cancelled" or "Superseded"));
        var predecessorsCompleted = workUnit is not null
            && workUnits.Where(item => item.SequenceNo < workUnit.SequenceNo)
                .All(item => item.Status == TaskNodeStatuses.Completed.ToString());

        if (binding.Status != "active"
            || binding.WorkspaceId != entity.WorkspaceId
            || binding.AgentInstanceId != entity.AgentInstanceId
            || binding.TaskPlanId != metadataPlanId
            || string.IsNullOrWhiteSpace(binding.PlanFingerprint)
            || binding.PlanFingerprint != metadataFingerprint
            || goal is null
            || goal.Status != GoalPhase.Active
            || goal.AgentInstanceId != binding.AgentInstanceId
            || goal.CurrentConversationId != entity.SessionId
            || task is null
            || task.Version != binding.ExpectedTaskVersion
            || task.ActiveAssignmentId != binding.AssignmentId
            || task.Status is not (WorkspaceTaskStatus.Assigned or WorkspaceTaskStatus.InProgress)
            || reservation is null
            || reservation.Status != "active"
            || reservation.FencingToken != binding.ReservationFencingToken
            || reservation.LeaseUntilUtc <= _timeProvider.GetUtcNow()
            || reservation.TaskId != binding.TaskId
            || reservation.AgentId != binding.AgentInstanceId
            || reservation.GoalRunId != binding.GoalRunId
            || plan is null
            || plan.Status != TaskPlanStatuses.Active.ToString()
            || plan.WorkspaceId != binding.WorkspaceId
            || plan.WorkspaceTaskId != binding.TaskId
            // WorkspaceTaskVersion is the immutable compile-time plan input. The live
            // revision is already fenced by task.Version/binding.ExpectedTaskVersion.
            || plan.PlanFingerprint != binding.PlanFingerprint
            || workUnit is null
            || workUnit.TaskNodeId != metadataNodeId
            || workUnit.ParentTaskNodeId != metadataParentNodeId
            || workUnit.AssignedToId != binding.AgentInstanceId
            || workUnit.Status is not ("Planned" or "Assigned" or "Running")
            || !predecessorsCompleted
            || string.IsNullOrWhiteSpace(workUnit.WorkUnitKind)
            || string.IsNullOrWhiteSpace(workUnit.Objective)
            || workUnit.MaxRounds is null or <= 0
            || workUnit.MaxToolCalls is null or <= 0
            || workUnit.MaxDurationSeconds is null or <= 0
            || workUnit.MaxInputTokens is null or <= 0
            || workUnit.MaxOutputTokens is null or <= 0
            || workUnit.MaxCost is null or <= 0)
        {
            throw new InvalidOperationException(
                $"task_execution_fence_changed: command={entity.CommandId} plan={binding.TaskPlanId} node={metadataNodeId}");
        }

        return result with
        {
            WorkUnit = new ExecutionWorkUnitContext
            {
                TaskId = binding.TaskId,
                GoalRunId = binding.GoalRunId,
                PlanId = plan.PlanId,
                PlanFingerprint = plan.PlanFingerprint!,
                TaskNodeId = workUnit.TaskNodeId,
                ParentTaskNodeId = workUnit.ParentTaskNodeId,
                WorkUnitKind = workUnit.WorkUnitKind!,
                Objective = workUnit.Objective!,
                MaxRounds = workUnit.MaxRounds.Value,
                MaxToolCallsTotal = workUnit.MaxToolCalls.Value,
                MaxDurationSeconds = workUnit.MaxDurationSeconds.Value,
                MaxInputTokens = workUnit.MaxInputTokens.Value,
                MaxOutputTokens = workUnit.MaxOutputTokens.Value,
                MaxCost = workUnit.MaxCost.Value,
            },
        };
    }

    private static IReadOnlyDictionary<string, string> ParseMetadata(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json)
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOpts)
                    ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("task_execution_metadata_invalid", ex);
        }
    }

    private static string GetRequiredMetadata(
        IReadOnlyDictionary<string, string> metadata,
        string key)
        => GetMetadata(metadata, key)
           ?? throw new InvalidOperationException($"task_execution_metadata_missing: {key}");

    private static string? GetMetadata(
        IReadOnlyDictionary<string, string> metadata,
        string key)
        => metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static CommandStatus ParseStatus(string? status) =>
        status?.ToLowerInvariant() switch
        {
            "pending" => CommandStatus.Pending,
            "leased" => CommandStatus.Leased,
            "running" => CommandStatus.Running,
            "cancel_requested" => CommandStatus.CancelRequested,
            "succeeded" => CommandStatus.Succeeded,
            "failed" => CommandStatus.Failed,
            "cancelled" => CommandStatus.Cancelled,
            "lease_lost" => CommandStatus.LeaseLost,
            _ => CommandStatus.Pending,
        };
}
