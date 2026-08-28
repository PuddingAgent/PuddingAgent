using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Goals;
using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>
/// Rebuilds a conservative Agent availability projection from committed
/// configuration, Task/Goal ownership, Runtime commands, deliveries and child
/// Agent facts.  It never infers idle from an absent in-memory registry entry.
/// </summary>
public sealed class AgentAvailabilityProjectionStore(
    IDbContextFactory<PlatformDbContext> dbFactory,
    IWorkspaceAgentCatalog agentCatalog,
    TimeProvider timeProvider,
    ILogger<AgentAvailabilityProjectionStore> logger)
    : IAgentAvailabilityProjectionStore
{
    private static readonly TimeSpan ProjectionTtl = TimeSpan.FromSeconds(30);

    public async Task<AgentAvailabilitySnapshot> GetAsync(
        string workspaceId,
        string agentId,
        CancellationToken ct = default)
    {
        ValidateIdentity(workspaceId, agentId);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.AgentAvailabilityProjections
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.WorkspaceId == workspaceId && item.AgentId == agentId,
                ct);
        return entity is null
            ? AgentAvailabilitySnapshot.Unknown(workspaceId, agentId, timeProvider.GetUtcNow())
            : ToSnapshot(entity);
    }

    public async Task<AgentAvailabilitySnapshot> RebuildAsync(
        string workspaceId,
        string agentId,
        CancellationToken ct = default)
    {
        ValidateIdentity(workspaceId, agentId);
        var now = timeProvider.GetUtcNow();
        var agents = await agentCatalog.ListAgentsAsync(workspaceId, ct);
        var agent = agents.FirstOrDefault(item =>
            string.Equals(item.AgentId, agentId, StringComparison.Ordinal));

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var previous = await db.AgentAvailabilityProjections
            .SingleOrDefaultAsync(
                item => item.WorkspaceId == workspaceId && item.AgentId == agentId,
                ct);

        var computed = agent is null
            ? AvailabilityDecision.Offline(
                AgentActivityReason.ConfigurationMissing,
                "agent_configuration_missing")
            : !agent.IsEnabled
                ? AvailabilityDecision.Offline(
                    AgentActivityReason.AgentDisabled,
                    "agent_disabled",
                    agent.MainSessionId)
                : agent.IsFrozen
                    ? AvailabilityDecision.Frozen(agent.MainSessionId)
                    : await ComputeActiveDecisionAsync(
                        db,
                        workspaceId,
                        agentId,
                        agent.MainSessionId,
                        now,
                        ct);

        var entity = previous ?? new AgentAvailabilityProjectionEntity
        {
            WorkspaceId = workspaceId,
            AgentId = agentId,
        };

        entity.State = computed.State;
        entity.ActivityReason = computed.ActivityReason;
        entity.Version = (previous?.Version ?? 0) + 1;
        entity.ObservedAtUtc = now;
        entity.ValidUntilUtc = now.Add(ProjectionTtl);
        entity.IdleSinceUtc = computed.State == AgentAvailabilityState.Idle
            ? previous is { State: AgentAvailabilityState.Idle }
                ? previous.IdleSinceUtc ?? now
                : now
            : null;
        entity.MainConversationId = computed.MainConversationId;
        entity.ActiveTurnId = computed.ActiveTurnId;
        entity.ActiveExecutionId = computed.ActiveExecutionId;
        entity.ActiveTaskId = computed.ActiveTaskId;
        entity.ActiveGoalRunId = computed.ActiveGoalRunId;
        entity.ActiveSubAgentRunId = computed.ActiveSubAgentRunId;
        entity.ReservationId = computed.ReservationId;
        entity.CooldownUntilUtc = computed.CooldownUntilUtc;
        entity.ReasonCode = computed.ReasonCode;

        if (previous is null)
            db.AgentAvailabilityProjections.Add(entity);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (previous is null)
        {
            // A heartbeat, diagnostics request and shadow scan may rebuild the
            // same previously-missing projection concurrently. The unique
            // workspace/agent row is authoritative; retry once against it.
            logger.LogDebug(
                ex,
                "[AgentAvailability] concurrent initial projection workspace={WorkspaceId} agent={AgentId}; rebuilding existing row",
                workspaceId,
                agentId);
            return await RebuildExistingAfterConcurrentInsertAsync(workspaceId, agentId, ct);
        }

        logger.LogDebug(
            "[AgentAvailability] rebuilt workspace={WorkspaceId} agent={AgentId} state={State} reason={Reason} version={Version}",
            workspaceId,
            agentId,
            entity.State,
            entity.ReasonCode,
            entity.Version);
        return ToSnapshot(entity);
    }

    private async Task<AgentAvailabilitySnapshot> RebuildExistingAfterConcurrentInsertAsync(
        string workspaceId,
        string agentId,
        CancellationToken ct)
    {
        await using var verification = await dbFactory.CreateDbContextAsync(ct);
        var existing = await verification.AgentAvailabilityProjections
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.WorkspaceId == workspaceId && item.AgentId == agentId,
                ct);
        if (existing is null)
            return AgentAvailabilitySnapshot.Unknown(workspaceId, agentId, timeProvider.GetUtcNow());
        return ToSnapshot(existing);
    }

    private static async Task<AvailabilityDecision> ComputeActiveDecisionAsync(
        PlatformDbContext db,
        string workspaceId,
        string agentId,
        string? mainConversationId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var binding = await db.TaskGoalBindings
            .AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId
                && item.AgentInstanceId == agentId
                && item.Status == "active")
            .OrderBy(item => item.BindingId)
            .FirstOrDefaultAsync(ct);

        // Assignment ownership is only an active-capacity fact while it still
        // matches the Task's canonical active assignment and the Task itself is
        // non-terminal. Historical imports and administrative terminal updates
        // may leave an unreleased attempt behind; those rows remain audit data
        // but must not keep an Agent false-busy forever.
        var activeAssignment = await (
                from attempt in db.TaskAssignmentAttempts.AsNoTracking()
                join task in db.WorkspaceTasks.AsNoTracking()
                    on new { attempt.WorkspaceId, attempt.TaskId }
                    equals new { task.WorkspaceId, task.TaskId }
                where attempt.WorkspaceId == workspaceId
                    && attempt.AgentId == agentId
                    && attempt.ReleasedAtUtc == null
                    && task.ActiveAssignmentId == attempt.AttemptId
                    && task.Status != WorkspaceTaskStatus.Completed
                    && task.Status != WorkspaceTaskStatus.Failed
                    && task.Status != WorkspaceTaskStatus.Cancelled
                    && task.Status != WorkspaceTaskStatus.Archived
                orderby attempt.AttemptNumber, attempt.AttemptId
                select attempt)
            .FirstOrDefaultAsync(ct);

        var activeTaskId = binding?.TaskId ?? activeAssignment?.TaskId;
        var goal = binding is null
            ? null
            : await db.GoalRuns.AsNoTracking().FirstOrDefaultAsync(
                item => item.GoalRunId == binding.GoalRunId
                    && (item.Status == GoalPhase.Active
                        || item.Status == GoalPhase.Paused
                        || item.Status == GoalPhase.Blocked),
                ct);
        var conversationId = goal?.CurrentConversationId ?? mainConversationId;

        var child = await db.SessionSubAgents
            .AsNoTracking()
            .Where(item => item.Status == "running"
                && (item.ParentAgentId == agentId
                    || (conversationId != null && item.ParentSessionId == conversationId)))
            .OrderBy(item => item.Id)
            .FirstOrDefaultAsync(ct);
        if (child is not null)
        {
            // session_sub_agents is a current-state projection and may lag the
            // immutable run index after a crash/terminal projection fault. Do
            // not keep an Agent false-busy forever when the matching latest run
            // is already terminal. A missing/newer run remains conservatively busy.
            var latestRun = await db.SubAgentRuns
                .AsNoTracking()
                .Where(item => item.SubSessionId == child.SubSessionId)
                .OrderByDescending(item => item.Id)
                .FirstOrDefaultAsync(ct);
            if (latestRun is null
                || latestRun.Status == "running"
                || IsSpawnedAfter(child.SpawnedAt, latestRun.StartedAt))
            {
                return AvailabilityDecision.Busy(
                    AgentActivityReason.WaitingSubAgent,
                    "waiting_subagent",
                    conversationId,
                    activeTaskId,
                    goal?.GoalRunId,
                    activeSubAgentRunId: child.SubSessionId);
            }
        }

        if (binding is not null || activeAssignment is not null)
        {
            return AvailabilityDecision.Busy(
                AgentActivityReason.TaskExecution,
                "active_task_owned",
                conversationId,
                activeTaskId,
                goal?.GoalRunId);
        }

        var command = await db.ChatExecutionCommands
            .AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId
                && item.AgentInstanceId == agentId
                && (item.Status == "pending"
                    || item.Status == "leased"
                    || item.Status == "running"
                    || item.Status == "cancel_requested"))
            .OrderBy(item => item.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (command is not null)
        {
            return AvailabilityDecision.Busy(
                AgentActivityReason.RuntimeExecution,
                command.Status == "pending" ? "queued_turn" : "active_runtime_execution",
                command.SessionId,
                activeTurnId: command.TurnId,
                activeExecutionId: command.RunId);
        }

        var pendingDelivery = await db.MessageDeliveries
            .AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId
                && item.TargetKind == "agent"
                && item.TargetId == agentId
                && (item.Status == "queued"
                    || item.Status == "delivering"
                    || item.Status == "retrying"))
            .OrderBy(item => item.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (pendingDelivery is not null)
        {
            return AvailabilityDecision.Busy(
                AgentActivityReason.QueuedMessage,
                "queued_message_delivery",
                mainConversationId,
                activeExecutionId: pendingDelivery.ClaimedByExecutionId);
        }

        var reservations = await db.AgentExecutionReservations
            .AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId
                && item.AgentId == agentId
                && item.Status == "active")
            .OrderBy(item => item.FencingToken)
            .ToListAsync(ct);
        var reservation = reservations.FirstOrDefault(item => item.LeaseUntilUtc > now);
        if (reservation is not null)
        {
            return new AvailabilityDecision(
                AgentAvailabilityState.Reserved,
                AgentActivityReason.ActiveReservation,
                "active_auto_reservation",
                mainConversationId,
                ActiveTaskId: reservation.TaskId,
                ActiveGoalRunId: reservation.GoalRunId,
                ReservationId: reservation.ReservationId);
        }

        return AvailabilityDecision.Idle(mainConversationId);
    }

    private static AgentAvailabilitySnapshot ToSnapshot(AgentAvailabilityProjectionEntity entity) => new()
    {
        WorkspaceId = entity.WorkspaceId,
        AgentId = entity.AgentId,
        State = entity.State,
        ActivityReason = entity.ActivityReason,
        Version = entity.Version,
        ObservedAtUtc = entity.ObservedAtUtc,
        ValidUntilUtc = entity.ValidUntilUtc,
        IdleSinceUtc = entity.IdleSinceUtc,
        MainConversationId = entity.MainConversationId,
        ActiveTurnId = entity.ActiveTurnId,
        ActiveExecutionId = entity.ActiveExecutionId,
        ActiveTaskId = entity.ActiveTaskId,
        ActiveGoalRunId = entity.ActiveGoalRunId,
        ActiveSubAgentRunId = entity.ActiveSubAgentRunId,
        ReservationId = entity.ReservationId,
        CooldownUntilUtc = entity.CooldownUntilUtc,
        ReasonCode = entity.ReasonCode,
    };

    private static void ValidateIdentity(string workspaceId, string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
    }

    private static bool IsSpawnedAfter(string spawnedAt, string startedAt)
    {
        if (!DateTimeOffset.TryParse(spawnedAt, out var spawned))
            return true;
        if (!DateTimeOffset.TryParse(startedAt, out var started))
            return true;
        return spawned > started;
    }

    private sealed record AvailabilityDecision(
        AgentAvailabilityState State,
        AgentActivityReason ActivityReason,
        string ReasonCode,
        string? MainConversationId = null,
        string? ActiveTurnId = null,
        string? ActiveExecutionId = null,
        string? ActiveTaskId = null,
        string? ActiveGoalRunId = null,
        string? ActiveSubAgentRunId = null,
        string? ReservationId = null,
        DateTimeOffset? CooldownUntilUtc = null)
    {
        public static AvailabilityDecision Idle(string? conversationId) => new(
            AgentAvailabilityState.Idle,
            AgentActivityReason.None,
            "idle_confirmed",
            conversationId);

        public static AvailabilityDecision Offline(
            AgentActivityReason reason,
            string code,
            string? conversationId = null) => new(
            AgentAvailabilityState.Offline,
            reason,
            code,
            conversationId);

        public static AvailabilityDecision Frozen(string? conversationId) => new(
            AgentAvailabilityState.Frozen,
            AgentActivityReason.AgentFrozen,
            "agent_frozen",
            conversationId);

        public static AvailabilityDecision Busy(
            AgentActivityReason reason,
            string code,
            string? conversationId,
            string? activeTaskId = null,
            string? activeGoalRunId = null,
            string? activeSubAgentRunId = null,
            string? activeTurnId = null,
            string? activeExecutionId = null) => new(
                AgentAvailabilityState.Busy,
                reason,
                code,
                conversationId,
                activeTurnId,
                activeExecutionId,
                activeTaskId,
                activeGoalRunId,
                activeSubAgentRunId);
    }
}
