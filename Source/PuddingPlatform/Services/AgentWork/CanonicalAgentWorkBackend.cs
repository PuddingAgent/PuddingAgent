using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pudding.Api.Contracts;
using PuddingAgentWork.Contracts;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.AgentWork;

/// <summary>
/// Singleton adapter to canonical input/batch/turn/command/events. No parallel work-state table or executor.
/// The Core data-root lease permits one writer process; the gate serializes public submissions in that process.
/// </summary>
public sealed class CanonicalAgentWorkBackend(IServiceScopeFactory scopes) : IAgentWorkBackend, IDisposable
{
    private readonly SemaphoreSlim _submissionGate = new(1, 1);
    private const string Owner = "agent_work_actor";
    private const string Operation = "agent_work_operation";
    private const string Hash = "agent_work_request_hash";

    public async Task<AgentWorkReceipt> SubmitAsync(WorkSubmission submission, CancellationToken ct)
    {
        await _submissionGate.WaitAsync(ct);
        try
        {
            var existing = await LoadAsync(submission.Key, ct);
            if (existing is not null) return Replay(existing, submission);
            using var scope = scopes.CreateScope();
            var conversation = await scope.ServiceProvider.GetRequiredService<IAgentWorkTargetResolver>()
                .ResolveConversationAsync(submission.Key, ct);
            var key = submission.Key;
            var command = new SubmitTurnCommand(conversation, key.Agent.WorkspaceId, key.Caller.ActorId,
                key.WorkId, "inpt-" + key.WorkId[5..],
                new RecipientRequest { Type = "agent", AgentIds = [key.Agent.AgentId] },
                [new ContentPart { Type = "text", Text = submission.Text }],
                new Dictionary<string, string> { [Owner] = key.Caller.ActorId, [Operation] = key.OperationId, [Hash] = submission.RequestHash })
            {
                // Server-only provenance: the reserved agent_work_* markers may only enter persisted acceptance facts
                // through this in-process Work backend. HTTP ingress never maps this flag and strips those keys.
                IsTrustedCanonicalWorkIngress = true,
            };
            try
            {
                await scope.ServiceProvider.GetRequiredService<ISubmitTurnHandler>().HandleAsync(command, ct);
            }
            catch (Exception)
            {
                // The transaction might have committed before signalling/cancellation failed. Read it back on a fresh scope.
                using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                Loaded? committed;
                try { committed = await LoadAsync(key, recovery.Token); }
                catch (Exception) { throw Unknown(); }
                if (committed is not null) return Replay(committed, submission);
                throw Unknown();
            }
            var loaded = await LoadAsync(key, ct) ?? throw Unknown();
            return Replay(loaded, submission);
        }
        finally { _submissionGate.Release(); }
    }

    public async Task<AgentWorkSnapshot?> ReadAsync(WorkKey key, CancellationToken ct) => (await LoadAsync(key, ct))?.Snapshot;

    private async Task<Loaded?> LoadAsync(WorkKey key, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        // Receipt, turn and terminal must be from one database snapshot, not three moments in a running turn.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var batch = await db.AcceptanceBatches.AsNoTracking().SingleOrDefaultAsync(x =>
            x.WorkspaceId == key.Agent.WorkspaceId && x.ClientRequestId == key.WorkId, ct);
        if (batch is null) return null;
        if (batch.UserId != key.Caller.ActorId) throw InvalidReceipt();
        var commands = await db.ChatExecutionCommands.AsNoTracking().Where(x => x.BatchId == batch.BatchId).Take(2).ToListAsync(ct);
        if (batch.TurnCount != 1 || commands.Count != 1) throw InvalidReceipt();
        var command = commands[0];
        if (command.WorkspaceId != key.Agent.WorkspaceId || command.AgentInstanceId != key.Agent.AgentId
            || command.UserId != key.Caller.ActorId || command.SessionId != batch.ConversationId
            || command.ClientRequestId != key.WorkId || command.UserMessageId != batch.MessageId)
            throw InvalidReceipt();
        var input = await db.ChatMessages.AsNoTracking().SingleOrDefaultAsync(x => x.MessageId == batch.MessageId
            && x.WorkspaceId == batch.WorkspaceId && x.SessionId == batch.ConversationId, ct);
        if (input is null || input.UserId != key.Caller.ActorId || input.Role != "user") throw InvalidReceipt();
        Dictionary<string, string>? metadata;
        try { metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(input.MetadataJson ?? "{}"); }
        catch (JsonException) { throw InvalidReceipt(); }
        if (metadata is null || metadata.GetValueOrDefault(Owner) != key.Caller.ActorId
            || metadata.GetValueOrDefault(Operation) != key.OperationId || string.IsNullOrEmpty(metadata.GetValueOrDefault(Hash)))
            throw InvalidReceipt();
        var accepted = await db.ConversationEvents.AsNoTracking().SingleOrDefaultAsync(x =>
            x.ConversationId == batch.ConversationId && x.Sequence == batch.AcceptedSequence, ct);
        if (accepted is null || accepted.Type != ConversationEventTypes.TurnAccepted
            || accepted.WorkspaceId != batch.WorkspaceId || accepted.CommandId != command.CommandId
            || accepted.TurnId != command.TurnId || accepted.MessageId != batch.MessageId)
            throw InvalidReceipt();
        var receipt = new AgentWorkReceipt(key.Agent, key.OperationId, key.WorkId, batch.ConversationId,
            batch.MessageId, command.TurnId, command.CommandId, batch.AcceptedSequence,
            DateTimeOffset.FromUnixTimeMilliseconds(batch.CreatedAt));
        var turn = await db.ConversationTurns.AsNoTracking().SingleOrDefaultAsync(x =>
            x.ConversationId == batch.ConversationId && x.TurnId == command.TurnId, ct);
        var terminal = command.TerminalSequence is null ? null : await db.ConversationEvents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ConversationId == batch.ConversationId && x.Sequence == command.TerminalSequence, ct);
        return new(Project(receipt, command, turn, terminal), metadata[Hash], input.Content);
    }

    private static AgentWorkSnapshot Project(AgentWorkReceipt receipt, ChatExecutionCommandEntity command,
        ConversationTurnEntity? turn, ConversationEventEntity? terminal)
    {
        AgentWorkSnapshot UnknownState() => new(receipt, AgentWorkStatus.Unknown, null, "execution.evidence_inconsistent");
        if (turn is null || turn.CommandId != command.CommandId || turn.WorkspaceId != receipt.Agent.WorkspaceId
            || turn.AcceptedSequence != receipt.AcceptedSequence) return UnknownState();
        (string? Kind, string? Type, AgentWorkStatus Status) expected = command.Status switch
        {
            "succeeded" => ("Completed", ConversationEventTypes.TurnCompleted, AgentWorkStatus.Completed),
            "failed" => ("Failed", ConversationEventTypes.TurnFailed, AgentWorkStatus.Failed),
            "cancelled" => ("Cancelled", ConversationEventTypes.TurnCancelled, AgentWorkStatus.Cancelled),
            _ => default
        };
        if (expected.Kind is null)
        {
            if (command.TerminalSequence is not null || turn.TerminalSequence is not null
                || turn.Status is "completed" or "failed" or "cancelled") return UnknownState();
            var state = command.Status switch
            {
                "pending" or "leased" => AgentWorkStatus.Accepted,
                "running" or "cancel_requested" => AgentWorkStatus.Running,
                "waiting_child" or "waiting_for_tool" => AgentWorkStatus.Waiting,
                _ => AgentWorkStatus.Unknown
            };
            return new(receipt, state, null, state == AgentWorkStatus.Unknown ? "execution.state_unrecognized" : null);
        }
        if (terminal is null || terminal.Sequence <= receipt.AcceptedSequence || string.IsNullOrEmpty(terminal.EventId)
            || terminal.Type != expected.Type || terminal.WorkspaceId != receipt.Agent.WorkspaceId
            || terminal.CommandId != command.CommandId || terminal.TurnId != command.TurnId
            || string.IsNullOrEmpty(command.RunId) || terminal.RunId != command.RunId || terminal.MessageId != command.MessageId
            || turn.TerminalSequence != terminal.Sequence || !string.Equals(turn.TerminalKind, expected.Kind, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(turn.Status, expected.Kind, StringComparison.OrdinalIgnoreCase)) return UnknownState();
        try
        {
            using var payload = JsonDocument.Parse(terminal.Payload);
            var root = payload.RootElement;
            string? Read(string name) => root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
            if (Read("kind") != expected.Kind) return UnknownState();
            var reply = Read("reply"); var code = Read("errorCode"); var error = Read("errorMessage");
            if (expected.Status == AgentWorkStatus.Completed && (!string.IsNullOrEmpty(code) || !string.IsNullOrEmpty(error))) return UnknownState();
            return new(receipt, expected.Status, new(terminal.EventId, command.RunId, terminal.Sequence, reply, code, error), null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return UnknownState(); }
    }

    private sealed record Loaded(AgentWorkSnapshot Snapshot, string Hash, string Text);
    private static AgentWorkReceipt Replay(Loaded loaded, WorkSubmission submission)
    {
        if (loaded.Hash != submission.RequestHash || loaded.Text != submission.Text)
            throw new AgentWorkException("operation.conflict", "This operation ID was already accepted with different content.");
        return loaded.Snapshot.Receipt;
    }
    private static AgentWorkException InvalidReceipt() => new("receipt.invalid", "Canonical acceptance identity is inconsistent.", "unknown");
    private static AgentWorkException Unknown() => new("submission.unknown", "Acceptance could not be confirmed. Query or retry this same operation and text.", "unknown");
    public void Dispose() => _submissionGate.Dispose();
}
