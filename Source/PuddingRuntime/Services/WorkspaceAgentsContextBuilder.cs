using System.Text;
using PuddingCode.Abstractions;

namespace PuddingRuntime.Services;

/// <summary>
/// Builds the workspace agent roster layer for context assembly.
/// </summary>
public sealed class WorkspaceAgentsContextBuilder
{
    private const int MaxAgentsInContext = 20;
    private readonly IAgentRosterProvider? _rosterProvider;

    public WorkspaceAgentsContextBuilder(IAgentRosterProvider? rosterProvider = null)
    {
        _rosterProvider = rosterProvider;
    }

    public async Task<string> BuildAsync(
        string? workspaceId,
        string? roomId,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- LAYER: WORKSPACE AGENTS ---");

        if (_rosterProvider is null || string.IsNullOrWhiteSpace(workspaceId))
        {
            sb.AppendLine("(No workspace agents available.)");
            return sb.ToString();
        }

        IReadOnlyList<AgentRosterItem> agents;
        try
        {
            agents = await _rosterProvider.ListAgentsAsync(
                workspaceId,
                string.IsNullOrWhiteSpace(roomId) ? "default" : roomId,
                includeBusy: true,
                includeFrozen: false,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            sb.AppendLine("(Workspace agents unavailable.)");
            return sb.ToString();
        }

        if (agents.Count == 0)
        {
            sb.AppendLine("(No workspace agents available.)");
            return sb.ToString();
        }

        sb.AppendLine("Messageable agents in this workspace:");
        foreach (var agent in agents.Take(MaxAgentsInContext))
        {
            var capabilities = agent.Capabilities.Count == 0
                ? "none"
                : string.Join(", ", agent.Capabilities.Take(8));

            // 仅保留静态字段（DisplayName/Address/Capabilities），
            // status/can_receive/currentTask 为动态字段，剔除以保证缓存前缀稳定
            sb.AppendLine(
                $"- {agent.DisplayName} address={agent.Address} capabilities=[{capabilities}]");
        }

        if (agents.Count > MaxAgentsInContext)
        {
            sb.AppendLine($"- ... {agents.Count - MaxAgentsInContext} more agents omitted. Use list_agents for the full roster.");
        }

        sb.AppendLine("Use send_message with an agent address when another agent should receive a visible room timeline message. Use list_agents to refresh the roster.");
        return sb.ToString();
    }
}
