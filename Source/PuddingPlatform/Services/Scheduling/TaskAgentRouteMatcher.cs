using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Scheduling;

public sealed record TaskAgentRouteMatch(bool Compatible, string Code, string Fingerprint);

/// <summary>
/// Deterministic structured route matcher. Task titles and descriptions are
/// deliberately excluded: only persisted routing metadata and canonical Agent
/// template capabilities/provider/model participate in selection.
/// </summary>
public static class TaskAgentRouteMatcher
{
    public static TaskAgentRouteMatch Evaluate(
        WorkspaceTaskEntity task,
        WorkspaceAgentDto agent,
        TaskTypeRouteOptions? typeRoute = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(agent);

        var fingerprint = Fingerprint(task, agent, typeRoute);
        if (!agent.IsEnabled)
            return new(false, "agent_disabled", fingerprint);
        if (agent.IsFrozen)
            return new(false, "agent_frozen", fingerprint);

        if (typeRoute is { AllowedRoles.Length: > 0 }
            && !typeRoute.AllowedRoles.Any(role => string.Equals(
                role, agent.Role, StringComparison.OrdinalIgnoreCase)))
            return new(false, "role_mismatch", fingerprint);

        if (!task.AllowAgentFallback
            && !string.IsNullOrWhiteSpace(task.PreferredAgentId)
            && !string.Equals(task.PreferredAgentId, agent.AgentId, StringComparison.Ordinal))
            return new(false, "preferred_agent_exclusive", fingerprint);

        var requiredProvider = FirstNonBlank(task.RequiredProviderId, typeRoute?.RequiredProviderId);
        var requiredModel = FirstNonBlank(task.RequiredModelId, typeRoute?.RequiredModelId);
        if (!string.IsNullOrWhiteSpace(requiredProvider)
            && !string.Equals(requiredProvider, agent.PreferredProviderId, StringComparison.OrdinalIgnoreCase))
            return new(false, "provider_mismatch", fingerprint);
        if (!string.IsNullOrWhiteSpace(requiredModel)
            && !string.Equals(requiredModel, agent.PreferredModelId, StringComparison.OrdinalIgnoreCase))
            return new(false, "model_mismatch", fingerprint);

        var agentCapabilities = AgentCapabilities(agent);
        var missing = RequiredCapabilities(task, typeRoute)
            .FirstOrDefault(required => !agentCapabilities.Contains(required));
        if (missing is not null)
            return new(false, $"capability_missing:{missing}", fingerprint);

        return new(true,
            string.Equals(task.PreferredAgentId, agent.AgentId, StringComparison.Ordinal)
                ? "preferred_agent"
                : "compatible_agent",
            fingerprint);
    }

    public static string Fingerprint(
        WorkspaceTaskEntity task,
        WorkspaceAgentDto agent,
        TaskTypeRouteOptions? typeRoute = null)
    {
        var canonical = string.Join('\n',
            Normalize(task.TaskType),
            string.Join(',', RequiredCapabilities(task, typeRoute)),
            Normalize(FirstNonBlank(task.RequiredProviderId, typeRoute?.RequiredProviderId)),
            Normalize(FirstNonBlank(task.RequiredModelId, typeRoute?.RequiredModelId)),
            string.Join(',', (typeRoute?.AllowedRoles ?? [])
                .Select(Normalize)
                .OrderBy(value => value, StringComparer.Ordinal)),
            Normalize(task.PreferredAgentId),
            task.AllowAgentFallback ? "1" : "0",
            task.AutoDispatchEnabled ? "1" : "0",
            Normalize(agent.AgentId),
            Normalize(agent.PreferredProviderId),
            Normalize(agent.PreferredModelId),
            string.Join(',', AgentCapabilities(agent)),
            agent.IsEnabled ? "1" : "0",
            agent.IsFrozen ? "1" : "0");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static SortedSet<string> AgentCapabilities(WorkspaceAgentDto agent)
    {
        var capabilities = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var value in agent.SelectedCapabilityIds ?? [])
            Add(capabilities, value);
        foreach (var value in agent.AllowedToolNames ?? [])
            Add(capabilities, $"tool:{value}");
        if (agent.AllowFileWrite) capabilities.Add("runtime:file_write");
        if (agent.AllowShellExecution) capabilities.Add("runtime:shell_execution");
        if (agent.AllowNetworkAccess) capabilities.Add("runtime:network_access");
        return capabilities;
    }

    private static IReadOnlyList<string> RequiredCapabilities(
        WorkspaceTaskEntity task,
        TaskTypeRouteOptions? typeRoute)
    {
        try
        {
            return (JsonSerializer.Deserialize<string[]>(task.RequiredCapabilitiesJson) ?? [])
                .Concat(typeRoute?.RequiredCapabilityIds ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(Normalize)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException)
        {
            return ["invalid_required_capabilities_json"];
        }
    }

    private static void Add(ISet<string> target, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            target.Add(Normalize(value));
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    private static string? FirstNonBlank(string? primary, string? fallback)
        => !string.IsNullOrWhiteSpace(primary) ? primary : fallback;
}
