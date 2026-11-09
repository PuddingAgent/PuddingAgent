using System.Collections.Immutable;
using PuddingAgents.Contracts;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatform.Services.Agents;

/// <summary>
/// PA-02 I0 directory adapter over the existing Agent manifest authority. Read-only: it never creates, starts or charges.
/// Cursor is the last AgentId of the previous page; an empty truncated page is never reported as not-found.
/// </summary>
public sealed class WorkspaceAgentDirectoryStore(WorkspaceAgentFileService agents, PuddingCode.Configuration.PuddingDataPaths paths)
    : IAgentDirectoryStore
{
    public async ValueTask<AgentDirectoryQueryResult> ListAsync(string workspaceId, string? cursor, int maxCount,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || workspaceId.Length > 128 || maxCount is < 1 or > 100
            || cursor is { Length: > 128 })
            throw new AgentBackendException(AgentErrorCodes.RequestInvalid, "A bounded directory query is required.");
        var all = (await agents.ListAgentsAsync(workspaceId, ct).ConfigureAwait(false))
            .OrderBy(a => a.AgentId, StringComparer.Ordinal)
            .Where(a => cursor is null || string.CompareOrdinal(a.AgentId, cursor) > 0)
            .ToList();
        var page = all.Take(maxCount).Select(a => ToRecord(workspaceId, a)).ToImmutableArray();
        var complete = all.Count <= maxCount;
        return new(page, complete || page.IsEmpty ? null : page[^1].AgentId, complete);
    }

    public async ValueTask<AgentDirectoryRecord?> GetAsync(string workspaceId, string agentId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || workspaceId.Length > 128
            || string.IsNullOrWhiteSpace(agentId) || agentId.Length > 128)
            throw new AgentBackendException(AgentErrorCodes.RequestInvalid, "A bounded agent reference is required.");
        var agent = await agents.GetAgentAsync(workspaceId, agentId, ct).ConfigureAwait(false);
        return agent is null ? null : ToRecord(workspaceId, agent);
    }

    private AgentDirectoryRecord ToRecord(string workspaceId, WorkspaceAgentDto agent) => new(
        workspaceId, agent.AgentId, agent.DisplayName ?? agent.Name, agent.SourceTemplateId ?? "", 1,
        Revision(agent.AgentId), agent.IsFrozen || !agent.IsEnabled, agent.UpdatedAt);

    private long Revision(string agentId)
    {
        var manifest = Path.Combine(paths.AgentInstanceRoot(agentId), "manifest.json");
        return File.Exists(manifest) ? File.GetLastWriteTimeUtc(manifest).Ticks : 0;
    }
}
