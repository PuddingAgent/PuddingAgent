namespace PuddingChat;

/// <summary>
/// One row of the host-owned role sidebar: an agent from a workspace, with the authoritative status
/// Core reported for it (<see cref="AgentStatus"/> is null when Core returned none — the row must not
/// invent a status).
/// </summary>
public sealed record RoleNavigationItem(Workspace Workspace, Agent Agent, AgentStatus? Status)
{
    public RoleKey Role => new(Workspace.WorkspaceId, Agent.AgentId);

    public string Label => Agent.Label;

    /// <summary>Freeze and disable are separate authoritative states; the sidebar keeps the row visible but not selectable.</summary>
    public bool CanSelect => RoleNavigation.CanSelect(Agent);
}

/// <summary>
/// Snapshot handed to the host sidebar. <see cref="DuplicateAgentsDropped"/> is reported instead of
/// hidden: two rows selecting the same role would make selection ambiguous.
/// </summary>
public sealed record RoleNavigationSnapshot(
    IReadOnlyList<RoleNavigationItem> Items,
    int DuplicateAgentsDropped)
{
    public static RoleNavigationSnapshot Empty { get; } = new([], 0);
}

/// <summary>
/// Pure ordering/assembly rules for the host-owned role sidebar. No Core call, no UI, no persistence.
/// The desktop shell renders this; the native chat no longer draws its own navigation.
/// </summary>
public static class RoleNavigation
{
    /// <summary>
    /// Flattens workspaces and their agents into sidebar order. The caller's order is preserved —
    /// the sidebar must not re-sort authoritative lists, so the first row is the first agent Core
    /// returned for the first workspace Core returned.
    /// </summary>
    public static RoleNavigationSnapshot Build(
        IReadOnlyList<Workspace> workspaces,
        IReadOnlyDictionary<string, IReadOnlyList<Agent>> agentsByWorkspace,
        IReadOnlyDictionary<string, IReadOnlyList<AgentStatus>> statusesByWorkspace)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(agentsByWorkspace);
        ArgumentNullException.ThrowIfNull(statusesByWorkspace);

        var items = new List<RoleNavigationItem>();
        var seen = new HashSet<RoleKey>();
        var dropped = 0;

        foreach (var workspace in workspaces)
        {
            if (!agentsByWorkspace.TryGetValue(workspace.WorkspaceId, out var agents) || agents is null)
                continue;

            statusesByWorkspace.TryGetValue(workspace.WorkspaceId, out var statuses);
            // Match by agent id only; an unknown or absent entry stays null rather than borrowing another row's status.
            var statusById = new Dictionary<string, AgentStatus>(StringComparer.Ordinal);
            if (statuses is not null)
                foreach (var status in statuses)
                    statusById[status.AgentId] = status;

            foreach (var agent in agents)
            {
                var role = new RoleKey(workspace.WorkspaceId, agent.AgentId);
                if (!seen.Add(role))
                {
                    dropped++;
                    continue;
                }

                items.Add(new RoleNavigationItem(
                    workspace,
                    agent,
                    statusById.GetValueOrDefault(agent.AgentId)));
            }
        }

        return new RoleNavigationSnapshot(items, dropped);
    }

    /// <summary>
    /// Frozen and disabled agents stay listed (the user must be able to see them) but cannot be selected.
    /// </summary>
    public static bool CanSelect(Agent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return agent.IsEnabled && !agent.IsFrozen;
    }
}
