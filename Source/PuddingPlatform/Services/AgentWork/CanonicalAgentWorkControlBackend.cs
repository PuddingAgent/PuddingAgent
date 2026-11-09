using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pudding.Api.Contracts;
using PuddingAgentWork.Contracts;
using PuddingCode.Platform;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.AgentWork;

/// <summary>
/// PA-02 I2 canonical Observe/Cancel: Observe pages committed conversation events for the exact cached receipt; Cancel
/// goes through the existing execution control service (the same authority as chat cancel). It never writes a parallel
/// work state and never claims the provider stopped.
/// </summary>
public sealed class CanonicalAgentWorkControlBackend(
    IServiceScopeFactory scopes,
    IAgentWorkBackend work) : IAgentWorkControlBackend
{
    public async Task<WorkObservation> ObserveAsync(WorkCaller caller, AgentRef agent, string operationId,
        long afterSequence, int maxCount, CancellationToken ct)
    {
        var key = new WorkKey(caller, agent, operationId, WorkId(caller, agent, operationId));
        var snapshot = await work.ReadAsync(key, ct).ConfigureAwait(false)
            ?? throw new AgentWorkControlException("resource.not_found", "The operation was not accepted.");
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var conversationId = snapshot.Receipt.ConversationId;
        var oldest = await db.ConversationEvents.AsNoTracking()
            .Where(e => e.ConversationId == conversationId)
            .Select(e => (long?)e.Sequence).MinAsync(ct).ConfigureAwait(false) ?? 0;
        if (oldest > 0 && afterSequence < oldest - 1)
            return new(operationId, snapshot.Status.ToString(), [], afterSequence, false, true, oldest);
        var rows = await db.ConversationEvents.AsNoTracking()
            .Where(e => e.ConversationId == conversationId && e.Sequence > afterSequence)
            .OrderBy(e => e.Sequence).Take(maxCount).ToListAsync(ct).ConfigureAwait(false);
        var events = rows.Select(e => new WorkEvent(e.Sequence, e.Type, ParseTime(e.OccurredAt), null, null, null))
            .ToImmutableArray();
        var next = events.IsEmpty ? afterSequence : events[^1].Sequence;
        var terminal = snapshot.Status is AgentWorkStatus.Completed or AgentWorkStatus.Failed
            or AgentWorkStatus.Cancelled;
        return new(operationId, snapshot.Status.ToString(), events, next, terminal, false, oldest);
    }

    public async Task<WorkCancelBackendResult> CancelAsync(WorkCaller caller, AgentRef agent, string operationId,
        string targetOperationId, long expectedRevision, string reason, CancellationToken ct)
    {
        var key = new WorkKey(caller, agent, targetOperationId, WorkId(caller, agent, targetOperationId));
        var snapshot = await work.ReadAsync(key, ct).ConfigureAwait(false);
        if (snapshot is null) return new("not_found", 0, null);
        if (snapshot.Status is AgentWorkStatus.Completed or AgentWorkStatus.Failed or AgentWorkStatus.Cancelled)
            return new("terminal", snapshot.Receipt.AcceptedSequence, snapshot.Status.ToString());
        if (expectedRevision != snapshot.Receipt.AcceptedSequence)
            throw new AgentWorkControlException("revision.conflict", "The work revision changed.");
        using var scope = scopes.CreateScope();
        var control = scope.ServiceProvider.GetRequiredService<IExecutionControlService>();
        await control.SubmitAsync(new ExecutionControlCommand(snapshot.Receipt.ConversationId,
            snapshot.Receipt.TurnId, ControlMessageKind.CancelRequested, Sanitize(reason),
            caller.ActorId, Priority: 1000), ct).ConfigureAwait(false);
        return new("requested", snapshot.Receipt.AcceptedSequence, snapshot.Status.ToString());
    }

    private static DateTimeOffset ParseTime(string value)
        => DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : DateTimeOffset.MinValue;

    private static string Sanitize(string reason) => string.IsNullOrWhiteSpace(reason) || reason.Length > 64
        ? "user.cancelled"
        : reason;

    private static string WorkId(WorkCaller caller, AgentRef agent, string operationId)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new[] { "agent-work-v1", caller.ActorId, agent.WorkspaceId,
            agent.AgentId, operationId });
        return "work-" + Convert.ToHexStringLower(SHA256.HashData(json))[..59];
    }
}
