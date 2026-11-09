using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using PuddingEventing.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Plugins;

/// <summary>
/// PA-07A S5 durable plugin job store: Submit only persists and returns a ticket (never waits); idempotent by
/// (workspace, operation) with different-content conflict; cancellation is a separate non-blocking path; every
/// transition and cancel is fenced by the caller's plugin generation; terminal states cannot regress and a job past
/// its deadline cannot be marked succeeded.
/// </summary>
public sealed class PluginJobStore(IDbContextFactory<PlatformDbContext> dbFactory) : IPluginJobStore
{
    private const int Accepted = 0;
    private const int Running = 1;
    private const int Succeeded = 2;
    private const int Failed = 3;
    private const int Cancelled = 4;
    private const int Unknown = 5;

    public async ValueTask<PluginJobTicket> SubmitAsync(PluginJobRecord record, CancellationToken ct = default)
    {
        if (record is null || !record.IsValid)
            throw Error(JobErrorCodes.RequestInvalid, "A valid plugin job record is required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var operationKey = record.Binding.WorkspaceId + "|" + record.Request.OperationId;
        var operation = await db.PluginJobOperations.SingleOrDefaultAsync(o => o.OperationKey == operationKey, ct)
            .ConfigureAwait(false);
        if (operation is not null)
        {
            var fingerprint = Fingerprint(record);
            if (!string.Equals(operation.Fingerprint, fingerprint, StringComparison.Ordinal))
                throw Error(JobErrorCodes.Conflict, "The operation id was already used with different job content.");
            var prior = await db.PluginJobs.SingleAsync(j => j.JobId == operation.JobId, ct).ConfigureAwait(false);
            return new(prior.JobId, prior.AcceptedSequence);
        }
        var entity = new PluginJobEntity
        {
            JobId = record.JobId,
            PluginId = record.Binding.PluginId,
            PluginGeneration = record.Binding.PluginGeneration,
            WorkspaceId = record.Binding.WorkspaceId,
            OwnerWorkId = record.Binding.OwnerWorkId,
            RootBudgetId = record.Binding.RootBudgetId,
            Kind = record.Request.Kind,
            OperationId = record.Request.OperationId,
            InputRef = record.Request.InputRef,
            InputHash = record.Request.InputHash,
            TimeoutSeconds = record.Request.TimeoutSeconds,
            RequiredGrants = string.Join(',', record.Request.RequiredGrants),
            Deadline = record.Binding.Deadline,
            AcceptedAt = record.AcceptedAt,
            AcceptedSequence = record.AcceptedSequence,
            State = Accepted,
            UpdatedAt = record.AcceptedAt,
        };
        db.PluginJobs.Add(entity);
        db.PluginJobOperations.Add(new() { OperationKey = operationKey, JobId = record.JobId, Fingerprint = Fingerprint(record) });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new(record.JobId, record.AcceptedSequence);
    }

    public async ValueTask<PluginJobSnapshot> GetAsync(string jobId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return ToSnapshot(await FindAsync(db, jobId, ct).ConfigureAwait(false));
    }

    public async ValueTask<PluginJobSnapshot> CancelAsync(string jobId, long pluginGeneration, string reasonCode,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reasonCode) || reasonCode.Length > 128)
            throw Error(JobErrorCodes.RequestInvalid, "A bounded cancellation reason is required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await FindAsync(db, jobId, ct).ConfigureAwait(false);
        EnsureGeneration(entity, pluginGeneration);
        if (IsTerminal(entity.State)) throw Error(JobErrorCodes.StateInvalid, "A terminal plugin job cannot be cancelled.");
        entity.State = Cancelled;
        entity.ReasonCode = reasonCode;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToSnapshot(entity);
    }

    public async ValueTask<ImmutableArray<PluginJobSnapshot>> ListAsync(string workspaceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || workspaceId.Length > 128)
            throw Error(JobErrorCodes.RequestInvalid, "A bounded workspace id is required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.PluginJobs.AsNoTracking().Where(j => j.WorkspaceId == workspaceId)
            .OrderBy(j => j.AcceptedSequence).ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(ToSnapshot)];
    }

    public async ValueTask<PluginJobSnapshot> TransitionAsync(string jobId, long pluginGeneration,
        PluginJobState state, string? reasonCode, string? resultRef, string? resultHash,
        CancellationToken ct = default)
    {
        if (state is not (PluginJobState.Running or PluginJobState.Succeeded
            or PluginJobState.Failed or PluginJobState.Unknown))
            throw Error(JobErrorCodes.RequestInvalid, "The target job state is not a valid transition.");
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await FindAsync(db, jobId, ct).ConfigureAwait(false);
        EnsureGeneration(entity, pluginGeneration);
        if (IsTerminal(entity.State)) throw Error(JobErrorCodes.StateInvalid, "A terminal plugin job cannot change state.");
        var target = state switch
        {
            PluginJobState.Running => Running,
            PluginJobState.Succeeded => Succeeded,
            PluginJobState.Failed => Failed,
            _ => Unknown,
        };
        if (entity.State == Accepted && target != Running
            || entity.State == Running && target == Running)
            throw Error(JobErrorCodes.StateInvalid, "The job transition is not allowed.");
        if (target == Succeeded && DateTimeOffset.UtcNow > entity.Deadline)
            throw Error(JobErrorCodes.DeadlineExpired, "The plugin job deadline has expired.");
        entity.State = target;
        entity.ReasonCode = reasonCode;
        entity.ResultRef = resultRef;
        entity.ResultHash = resultHash;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        if (target == Running) entity.Attempt++;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToSnapshot(entity);
    }

    private static async Task<PluginJobEntity> FindAsync(PlatformDbContext db, string jobId, CancellationToken ct)
        => await db.PluginJobs.SingleOrDefaultAsync(j => j.JobId == jobId, ct).ConfigureAwait(false)
            ?? throw Error(JobErrorCodes.JobMissing, "The plugin job is missing.");

    private static void EnsureGeneration(PluginJobEntity entity, long pluginGeneration)
    {
        if (pluginGeneration != entity.PluginGeneration)
            throw Error(JobErrorCodes.GenerationStale, "The plugin generation does not own this job.");
    }

    private static bool IsTerminal(int state) => state is Succeeded or Failed or Cancelled;

    private static string Fingerprint(PluginJobRecord record) => string.Join('|', record.JobId, record.Binding.PluginId,
        record.Binding.PluginGeneration, record.Binding.WorkspaceId, record.Binding.OwnerWorkId, record.Binding.RootBudgetId,
        record.Binding.Deadline.ToString("O"), record.Request.Kind, record.Request.OperationId, record.Request.InputRef,
        record.Request.InputHash, record.Request.TimeoutSeconds, string.Join(',', record.Request.RequiredGrants));

    private static PluginJobSnapshot ToSnapshot(PluginJobEntity e) => new(e.JobId, e.State switch
    {
        Accepted => PluginJobState.Accepted,
        Running => PluginJobState.Running,
        Succeeded => PluginJobState.Succeeded,
        Failed => PluginJobState.Failed,
        Cancelled => PluginJobState.Cancelled,
        _ => PluginJobState.Unknown,
    }, e.PluginId, e.WorkspaceId, e.PluginGeneration, e.ReasonCode, e.ResultRef, e.ResultHash, e.UpdatedAt, e.Attempt);

    private static JobException Error(string code, string message) => new(code, message);
}
