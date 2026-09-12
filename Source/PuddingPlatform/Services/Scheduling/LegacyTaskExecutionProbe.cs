using Microsoft.EntityFrameworkCore;
using PuddingCode.Scheduling;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>Shared read-only classification; repairs must invoke it again inside their transaction.</summary>
internal sealed record LegacyTaskExecutionProbe(
    TaskExecutionTrackingVerdict Verdict, string Code, ExecutionRunEntity? Run = null)
{
    internal static async Task<(ExecutionRunEntity? Run, ChatExecutionCommandEntity? Command)> ResolveRunAsync(
        PlatformDbContext db, string claim, CancellationToken ct)
    {
        var claimedRun = await db.ExecutionRuns.AsNoTracking().SingleOrDefaultAsync(r => r.RunId == claim, ct);
        var commandId = claimedRun?.CommandId ?? claim;
        var command = await db.ChatExecutionCommands.AsNoTracking().SingleOrDefaultAsync(c => c.CommandId == commandId, ct);
        var latestRun = await db.ExecutionRuns.AsNoTracking().Where(r => r.CommandId == commandId)
            .OrderByDescending(r => r.Attempt).ThenByDescending(r => r.FencingToken).FirstOrDefaultAsync(ct);
        return (latestRun, command);
    }

    internal static async Task<LegacyTaskExecutionProbe> ReadAsync(
        PlatformDbContext db, string workspaceId, string agentId, TaskExecutionBindingEntity binding,
        MessageDeliveryEntity delivery, DateTimeOffset? lastProgress, DateTimeOffset now,
        TimeSpan grace, CancellationToken ct)
    {
        var claim = string.IsNullOrWhiteSpace(binding.ExecutionId) ? delivery.ClaimedByExecutionId : binding.ExecutionId;
        var nowMs = now.ToUnixTimeMilliseconds();
        bool Overdue(DateTimeOffset? progress) => progress is not null && now - progress.Value > grace;
        LegacyTaskExecutionProbe Result(TaskExecutionTrackingVerdict verdict, string code, ExecutionRunEntity? run = null)
            => new(verdict, code, run);
        if (delivery.WorkspaceId != workspaceId || delivery.TargetId != agentId)
            return Result(TaskExecutionTrackingVerdict.Inconsistent, "legacy_execution_scope_mismatch");

        ExecutionRunEntity? run = null;
        if (!string.IsNullOrWhiteSpace(claim))
        {
            var resolved = await ResolveRunAsync(db, claim, ct);
            run = resolved.Run;
            var command = resolved.Command;
            if (command is not null)
            {
                if (command.WorkspaceId != workspaceId || command.AgentInstanceId != agentId
                    || (!string.IsNullOrWhiteSpace(binding.SessionId) && command.SessionId != binding.SessionId))
                    return Result(TaskExecutionTrackingVerdict.Inconsistent, "legacy_execution_scope_mismatch");
                // A claim may point to an old attempt. Never release ownership while its command retries.
                if (command.Status is "pending" or "running" or "leased" or "cancel_requested"
                    && run?.Status is not ("leased" or "running" or "cancel_requested"))
                    return Result(TaskExecutionTrackingVerdict.Waiting, "legacy_execution_command_pending", run);
            }
            else if (run is not null)
                return Result(TaskExecutionTrackingVerdict.Inconsistent, "legacy_execution_command_missing", run);
        }

        if (run is not null)
        {
            if (run.Status is "leased" or "running" or "cancel_requested")
                return Result(run.LeaseUntil > nowMs ? TaskExecutionTrackingVerdict.Healthy : TaskExecutionTrackingVerdict.Stalled,
                    run.LeaseUntil > nowMs ? "legacy_execution_active" : "legacy_execution_lease_expired", run);
            if (run.Status is not ("succeeded" or "failed" or "cancelled" or "lease_lost") || run.CompletedAt is null)
                return Result(TaskExecutionTrackingVerdict.Inconsistent, "legacy_execution_terminal_evidence_missing", run);
            var terminalAt = DateTimeOffset.FromUnixTimeMilliseconds(run.CompletedAt.Value);
            var progress = lastProgress > terminalAt ? lastProgress : terminalAt;
            return Result(Overdue(progress) ? TaskExecutionTrackingVerdict.CleanupRequired : TaskExecutionTrackingVerdict.Waiting,
                Overdue(progress) ? "legacy_execution_terminal_without_task_settlement" : "legacy_execution_terminal_pending_settlement", run);
        }

        // Session identity alone is not execution evidence, but unrelated live work is a safety veto.
        if (!string.IsNullOrWhiteSpace(binding.SessionId)
            && await db.ChatExecutionCommands.AsNoTracking().AnyAsync(c => c.WorkspaceId == workspaceId
                && c.AgentInstanceId == agentId && c.SessionId == binding.SessionId
                && (c.Status == "pending" || c.Status == "running" || c.Status == "leased" || c.Status == "cancel_requested"), ct))
            return Result(TaskExecutionTrackingVerdict.Inconsistent, "legacy_execution_claim_ambiguous");
        if (!string.IsNullOrWhiteSpace(claim) || !string.IsNullOrWhiteSpace(binding.SessionId))
            return Result(Overdue(lastProgress) ? TaskExecutionTrackingVerdict.CleanupRequired : TaskExecutionTrackingVerdict.Waiting,
                Overdue(lastProgress) ? "legacy_execution_claim_orphaned" : "legacy_assignment_waiting_execution");
        if (delivery.Status is "dead_letter" or "failed" or "cancelled")
            return Result(TaskExecutionTrackingVerdict.CleanupRequired, "legacy_delivery_terminal_without_execution");
        if (delivery.Status == "delivered" && Overdue(lastProgress))
            return Result(TaskExecutionTrackingVerdict.CleanupRequired, "legacy_assignment_execution_missing");
        return Result(TaskExecutionTrackingVerdict.Waiting, "legacy_assignment_waiting_execution");
    }
}
