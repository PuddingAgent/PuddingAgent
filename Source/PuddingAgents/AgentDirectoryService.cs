using System.Collections.Immutable;
using PuddingAgents.Contracts;
using Pudding.Api.Contracts;

namespace PuddingAgents;

/// <summary>
/// PA-02A directory reads: borrowed handles only. Pagination reports Cursor/Complete; a truncated page is never
/// reported as "not found", and a workspace mismatch is rejected before the store is touched.
/// </summary>
public sealed class AgentDirectoryService
{
    private readonly IAgentDirectoryStore _store;

    public AgentDirectoryService(IAgentDirectoryStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    public async ValueTask<AgentDirectoryPage> ListAsync(AgentBinding binding, AgentListQuery query,
        CancellationToken ct = default)
        => await ListAsync(new AgentReadBinding(binding.WorkspaceId, binding.ActorId,
            binding.AllowedCapabilities), query, ct).ConfigureAwait(false);

    public async ValueTask<AgentDirectoryPage> ListAsync(AgentReadBinding binding, AgentListQuery query,
        CancellationToken ct = default)
    {
        AgentOwnershipPolicy.EnsureWorkspace(binding, query?.WorkspaceId);
        if (query.MaxCount is < 1 or > 100) throw Error(AgentErrorCodes.RequestInvalid, "The page size is out of bounds.");
        if (query.Cursor is { Length: > 128 }) throw Error(AgentErrorCodes.RequestInvalid, "The cursor is out of bounds.");
        var result = await _store.ListAsync(query.WorkspaceId, query.Cursor, query.MaxCount, ct).ConfigureAwait(false);
        return new(result.Agents.Select(ToPublic).ToImmutableArray(), result.Cursor, result.Complete);
    }

    public async ValueTask<AgentDirectoryEntry?> GetAsync(AgentBinding binding, AgentRef agent,
        CancellationToken ct = default)
        => await GetAsync(new AgentReadBinding(binding.WorkspaceId, binding.ActorId,
            binding.AllowedCapabilities), agent, ct).ConfigureAwait(false);

    public async ValueTask<AgentDirectoryEntry?> GetAsync(AgentReadBinding binding, AgentRef agent,
        CancellationToken ct = default)
    {
        AgentOwnershipPolicy.EnsureWorkspace(binding, agent?.WorkspaceId);
        if (!AgentBinding.Valid(agent.AgentId)) throw Error(AgentErrorCodes.RequestInvalid, "A bounded agent id is required.");
        var record = await _store.GetAsync(agent.WorkspaceId, agent.AgentId, ct).ConfigureAwait(false);
        return record is null ? null : ToPublic(record);
    }

    private static AgentDirectoryEntry ToPublic(AgentDirectoryRecord record) => new(record.WorkspaceId,
        record.AgentId, record.DisplayName, record.TemplateId, record.TemplateVersion, record.Revision,
        record.Archived ? "Archived" : "Active", record.UpdatedAt);

    private static AgentBackendException Error(string code, string message) => new(code, message);
}
