using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Pudding.Api.Contracts;
using PuddingAgentConfiguration.Contracts;

namespace PuddingAgentConfiguration;

/// <summary>One update pipeline for SDK, HTTP and trusted in-process consumers. No storage or host dependency.</summary>
public sealed class AgentConfigurationService(
    IAgentConfigurationStore store, IAgentConfigurationAuthorizer authorizer, TimeProvider? timeProvider = null)
{
    // Never evict dedup receipts silently: a retry after eviction could repeat a committed operation.
    public const int MaxRecordedOperations = 1024;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public IAgentConfigurationApi ForCaller(ConfigurationCaller caller)
    {
        if (string.IsNullOrWhiteSpace(caller.ActorId) || caller.ActorId.Length > 256)
            throw Error("permission.denied", "A trusted caller identity is required.");
        return new BoundApi(this, caller);
    }

    private async Task AuthorizeAsync(ConfigurationCaller caller, AgentRef agent,
        ConfigurationAccess access, CancellationToken ct)
    {
        ValidateReference(agent);
        ct.ThrowIfCancellationRequested();
        if (!await authorizer.IsAllowedAsync(caller, agent, access, ct))
            throw Error("permission.denied", "The caller cannot perform this operation on this Agent.");
    }

    public static void ValidateReference(AgentRef agent)
    {
        if (agent is null || !ValidId(agent.WorkspaceId) || !ValidId(agent.AgentId))
            throw Error("request.invalid", "Workspace and Agent IDs must be safe, nonempty identifiers (maximum 128 characters).");
    }

    private static bool ValidId(string? value) => value is { Length: > 0 and <= 128 }
        && value is not "." and not ".." && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private async Task<AgentConfigurationSnapshot> GetAsync(ConfigurationCaller caller, AgentRef agent, CancellationToken ct)
    {
        await AuthorizeAsync(caller, agent, ConfigurationAccess.Read, ct);
        var record = await store.ReadAsync(agent, ct);
        EnsureIdentity(record, agent);
        return record.Snapshot with { ObservedAt = _clock.GetUtcNow() };
    }

    private async Task<ConfigurationChangeReceipt> UpdateAsync(ConfigurationCaller caller, AgentRef agent,
        AgentConfigurationPatch patch, ConfigurationUpdateOptions options, CancellationToken ct)
    {
        // Check authorization even for a replay, before revealing an old receipt.
        await AuthorizeAsync(caller, agent, ConfigurationAccess.Update, ct);
        if (patch is null || options is null || string.IsNullOrWhiteSpace(options.ExpectedRevision)
            || options.ExpectedRevision.Length > 256 || string.IsNullOrWhiteSpace(options.OperationId)
            || options.OperationId.Length > 128 || options.OperationId.Any(char.IsControl))
            throw Error("request.invalid", "ExpectedRevision and a bounded OperationId are required.");
        ValidateField(patch.DisplayName, 256, allowBlank: false);
        ValidateField(patch.Description, 16_000, allowBlank: true);
        if (!patch.DisplayName.IsSpecified && !patch.Description.IsSpecified)
            throw Error("request.invalid", "At least one supported field must be specified.");
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { agent, patch, options.ExpectedRevision })));
        return await store.TransactAsync(agent, state =>
        {
            ct.ThrowIfCancellationRequested();
            EnsureIdentity(state, agent);
            var previous = state.Operations.FirstOrDefault(x => x.ActorId == caller.ActorId && x.OperationId == options.OperationId);
            if (previous is not null)
            {
                if (previous.RequestHash != hash) throw Error("operation.conflict", "OperationId was already used for a different request.");
                return new ConfigurationTransaction<ConfigurationChangeReceipt>(state, previous.Receipt, false);
            }
            if (state.Snapshot.Revision != options.ExpectedRevision)
                throw Error("revision.conflict", "Configuration changed; read it again before applying a new update.");
            if (state.Operations.Length >= MaxRecordedOperations)
                throw Error("resource.limit", "The configuration operation journal is full; no receipts were evicted and no change was made.");
            var name = patch.DisplayName.IsSpecified ? patch.DisplayName.Value?.Trim() : state.Snapshot.DisplayName;
            var description = patch.Description.IsSpecified ? patch.Description.Value : state.Snapshot.Description;
            var fields = ImmutableArray.CreateBuilder<string>();
            if (name != state.Snapshot.DisplayName) fields.Add("displayName");
            if (description != state.Snapshot.Description) fields.Add("description");
            var changed = fields.Count != 0;
            var now = _clock.GetUtcNow();
            var revision = changed ? Guid.NewGuid().ToString("N") : state.Snapshot.Revision;
            long? sequence = changed ? checked((state.Changes.IsEmpty ? 0 : state.Changes[^1].Sequence) + 1) : null;
            var receipt = new ConfigurationChangeReceipt(options.OperationId, agent, state.Snapshot.Revision,
                revision, revision, "configuration-source", changed, sequence, now);
            var changes = changed ? state.Changes.Add(new(sequence!.Value, Guid.NewGuid().ToString("N"),
                options.OperationId, agent, state.Snapshot.Revision, revision, fields.ToImmutable(), now)) : state.Changes;
            var next = state with
            {
                Snapshot = state.Snapshot with { DisplayName = name, Description = description, Revision = revision, ObservedAt = now },
                Operations = state.Operations.Add(new(caller.ActorId, options.OperationId, hash, receipt)), Changes = changes
            };
            return new ConfigurationTransaction<ConfigurationChangeReceipt>(next, receipt, true);
        }, ct);
    }

    private async Task<ConfigurationChangePage> ReadChangesAsync(ConfigurationCaller caller, AgentRef agent,
        long cursor, int limit, CancellationToken ct)
    {
        await AuthorizeAsync(caller, agent, ConfigurationAccess.ReadChanges, ct);
        if (cursor < 0 || limit is < 1 or > 200) throw Error("request.invalid", "Cursor must be nonnegative and limit must be 1..200.");
        var record = await store.ReadAsync(agent, ct);
        EnsureIdentity(record, agent);
        var last = record.Changes.IsEmpty ? 0 : record.Changes[^1].Sequence;
        if (cursor > last) throw Error("cursor.invalid", "Cursor is ahead of this Agent's change stream.");
        var items = record.Changes.Where(x => x.Sequence > cursor).Take(limit).ToImmutableArray();
        var next = items.IsEmpty ? cursor : items[^1].Sequence;
        return new(items, next, next == last, record.Snapshot.Revision);
    }

    private static void ValidateField(ConfigurationField<string> field, int maxLength, bool allowBlank)
    {
        if ((!field.IsSpecified && field.Value is not null) || field.Value?.Length > maxLength
            || (!allowBlank && field.IsSpecified && field.Value is not null && string.IsNullOrWhiteSpace(field.Value)))
            throw Error("request.invalid", "Invalid configuration field value or presence marker.");
    }

    private static void EnsureIdentity(AgentConfigurationRecord record, AgentRef requested)
    {
        if (record.Snapshot.Agent != requested)
            throw Error("store.invalid", "The configuration store returned a different resource.");
    }

    private static ConfigurationApiException Error(string code, string message) => new(code, message);

    private sealed class BoundApi(AgentConfigurationService owner, ConfigurationCaller caller) : IAgentConfigurationApi
    {
        public Task<AgentConfigurationSnapshot> GetAsync(AgentRef agent, CancellationToken ct = default) => owner.GetAsync(caller, agent, ct);
        public Task<ConfigurationChangeReceipt> UpdateAsync(AgentRef agent, AgentConfigurationPatch patch,
            ConfigurationUpdateOptions options, CancellationToken ct = default) => owner.UpdateAsync(caller, agent, patch, options, ct);
        public Task<ConfigurationChangePage> ReadChangesAsync(AgentRef agent, long afterSequence = 0, int limit = 100,
            CancellationToken ct = default) => owner.ReadChangesAsync(caller, agent, afterSequence, limit, ct);
    }
}
