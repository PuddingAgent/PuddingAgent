using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Agents;
using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services;

/// <summary>
/// Resolves the complete runtime profile for a workspace agent instance.
/// Template config is now embedded in the agent DTO at creation time,
/// eliminating the need for template-file lookups during execution.
/// </summary>
public sealed class AgentRuntimeProfileResolver(
    IWorkspaceAgentCatalog agentCatalog,
    AgentProfileProvider profileProvider,
    ILLMConfigResolver llmConfigResolver,
    PlatformDbContext db,
    MinioStorageService minio,
    IPuddingToolCatalogService toolCatalog,
    IToolPermissionPolicyService toolPermissionPolicy,
    ILogger<AgentRuntimeProfileResolver> logger) : IAgentRuntimeProfileResolver
{
    private static readonly string[] TerminalLifecycleToolIds =
    [
        "terminal_start",
        "terminal_wait",
        "terminal_read",
        "terminal_status",
        "terminal_cancel",
        "terminal_input",
    ];

    public async Task<AgentRuntimeProfile> ResolveAsync(
        string workspaceId,
        string agentId,
        CancellationToken ct = default)
    {
        var agent = await ResolveAgentAsync(workspaceId, agentId, ct);
        var definition = await LoadDefinitionAsync(workspaceId, agent, ct);
        var llm = await ResolveLlmAsync(
            definition.LlmConfig.Conscious,
            workspaceId,
            agent.AgentId,
            ct);
        var capabilities = BuildCapabilitiesFromInstance(definition.Instance);
        var skillPackages = await ResolveSkillPackagesFromInstanceAsync(
            definition.Instance,
            ct);

        return new AgentRuntimeProfile
        {
            WorkspaceId = workspaceId,
            AgentId = agent.AgentId,
            DisplayName = agent.DisplayName ?? agent.Name,
            AvatarUrl = agent.AvatarUrl,
            MainSessionId = agent.MainSessionId,
            SourceTemplateId = agent.SourceTemplateId,
            ConsciousProfileId = llm.ProfileId,
            PreferredProviderId = llm.ProviderId,
            PreferredModelId = llm.ModelId,
            LlmConfig = llm.Config,
            CapabilityPolicy = capabilities.Policy,
            ToolDefinitions = capabilities.ToolDefinitions,
            SkillPackages = skillPackages,
            SystemPrompt = definition.Instance.SystemPrompt,
            MaxRounds = definition.Instance.MaxRounds,
            MaxElapsedSeconds = definition.Instance.MaxElapsedSeconds,
            MaxContextTokens = definition.Instance.MaxContextTokens,
            CapabilitySource = capabilities.Source,
            CapabilityCount = capabilities.CapabilityCount,
        };
    }

    private async Task<AgentFileProfile> LoadDefinitionAsync(
        string workspaceId,
        WorkspaceAgentDto agent,
        CancellationToken ct)
    {
        AgentFileProfile definition;
        try
        {
            definition = await profileProvider.LoadAsync(agent.AgentId, ct);
        }
        catch (Exception ex) when (ex is FileNotFoundException or JsonException or InvalidOperationException)
        {
            throw new AgentConfigurationException(
                agent.AgentId,
                $"Agent '{agent.AgentId}' definition is incomplete or invalid: {ex.Message}");
        }

        if (!string.Equals(
                definition.Instance.WorkspaceId,
                workspaceId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentConfigurationException(
                agent.AgentId,
                $"Agent '{agent.AgentId}' belongs to workspace '{definition.Instance.WorkspaceId}', not '{workspaceId}'.");
        }

        return definition;
    }

    private async Task<WorkspaceAgentDto> ResolveAgentAsync(
        string workspaceId,
        string agentId,
        CancellationToken ct)
    {
        var agents = await agentCatalog.ListAgentsAsync(workspaceId, ct);
        return agents.FirstOrDefault(item =>
            item.IsEnabled
            && string.Equals(item.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Agent '{agentId}' was not found in workspace '{workspaceId}'.");
    }

    /// <summary>
    /// Resolve LLM config from the Agent instance's config/llm.json snapshot.
    /// Provider credentials and endpoint details are enriched from llm.providers.json.
    /// </summary>
    private async Task<ResolvedLlmRouting> ResolveLlmAsync(
        AgentLlmBinding? binding,
        string workspaceId,
        string agentId,
        CancellationToken ct)
    {
        if (binding is null)
        {
            throw new AgentConfigurationException(
                agentId,
                $"Agent '{agentId}' is missing config/llm.json conscious binding.");
        }

        var routing = await llmConfigResolver.ResolveAsync(binding, ct);

        var providerId = routing?.ProviderId;
        var modelId = routing?.Config?.ModelId ?? routing?.ModelId;
        if (routing?.Config is null)
        {
            logger.LogWarning(
                "[AgentRuntimeProfile] LLM config unresolved workspace={WorkspaceId} agent={AgentId} provider={ProviderId} model={ModelId}",
                workspaceId, agentId, providerId ?? "(none)", modelId ?? "(none)");
            throw new AgentConfigurationException(
                agentId,
                $"Agent '{agentId}' conscious LLM binding cannot be resolved from llm.providers.json.");
        }

        return new ResolvedLlmRouting(routing?.ProfileId, providerId, modelId, routing?.Config);
    }

    /// <summary>
    /// Build capability policy and tool definitions from agent's embedded config.
    /// No longer reads template files at runtime.
    /// </summary>
    private ResolvedCapabilities BuildCapabilitiesFromInstance(AgentInstanceManifest instance)
    {
        var capIds = instance.Capabilities.AllowedToolIds;
        if (capIds.Count == 0)
            return new ResolvedCapabilities(null, null, "none", 0);

        var selectedToolDescriptors = ResolveSelectedToolDescriptors(capIds);
        var selectedToolNames = selectedToolDescriptors.Select(d => d.ToolId).ToList();
        var allowedToolNamesJson = instance.Capabilities.AllowedToolNames is { Count: > 0 } names
            ? JsonSerializer.Serialize(names)
            : "[]";

        return new ResolvedCapabilities(
            BuildPolicy(
                instance.Capabilities.AllowFileWrite,
                instance.Capabilities.AllowShellExecution,
                instance.Capabilities.AllowNetworkAccess,
                allowedToolNamesJson,
                instance.Role ?? "Service",
                selectedToolNames),
            BuildToolDefinitions(selectedToolDescriptors),
            "agent-instance-embedded",
            selectedToolNames.Count);
    }

    /// <summary>
    /// Resolve skill packages from agent's embedded skill package IDs.
    /// </summary>
    private async Task<IReadOnlyList<SkillPackageInfo>?> ResolveSkillPackagesFromInstanceAsync(
        AgentInstanceManifest instance,
        CancellationToken ct)
    {
        var skillIds = instance.SkillPackageIds;
        if (skillIds.Count == 0)
            return null;

        var packages = await db.SkillPackages.AsNoTracking()
            .Where(p => skillIds.Contains(p.SkillPackageId) && p.IsEnabled)
            .ToListAsync(ct);

        if (packages.Count == 0)
            return null;

        var result = new List<SkillPackageInfo>(packages.Count);
        foreach (var package in packages)
        {
            var url = await minio.GetPresignedDownloadUrlAsync(package.ObjectKey, 86400, ct);
            result.Add(new SkillPackageInfo
            {
                SkillPackageId = package.SkillPackageId,
                Name = package.Name,
                Description = package.Description,
                Version = package.Version,
                DownloadUrl = url,
            });
        }

        return result;
    }

    // ── 以下方法保持不变 ──

    private IReadOnlyList<ToolDescriptor> ResolveSelectedToolDescriptors(
        IEnumerable<string> selectedCapabilityOrToolIds)
    {
        var descriptors = toolCatalog.ListTools();
        var byToolId = descriptors.ToDictionary(d => d.ToolId, StringComparer.OrdinalIgnoreCase);
        var byCapabilityId = descriptors.ToDictionary(d => ToolIdToCapabilityId(d.ToolId), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, ToolDescriptor>(StringComparer.OrdinalIgnoreCase);

        foreach (var selected in selectedCapabilityOrToolIds)
        {
            if (string.IsNullOrWhiteSpace(selected))
                continue;

            var id = selected.Trim();
            if (IsTerminalExecuteAlias(id))
            {
                AddToolDescriptors(result, byToolId, TerminalLifecycleToolIds);
                continue;
            }

            if (byCapabilityId.TryGetValue(id, out var byCapability))
            {
                result.TryAdd(byCapability.ToolId, byCapability);
                continue;
            }

            if (byToolId.TryGetValue(id, out var byTool))
                result.TryAdd(byTool.ToolId, byTool);
        }

        return result.Values.ToList();
    }

    private static IReadOnlyList<LlmToolDefinition> BuildToolDefinitions(
        IReadOnlyList<ToolDescriptor> descriptors)
    {
        var map = new Dictionary<string, LlmToolDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in descriptors)
        {
            if (map.ContainsKey(descriptor.ToolId))
                continue;
            map[descriptor.ToolId] = new LlmToolDefinition
            {
                Name = descriptor.ToolId,
                Description = descriptor.Description,
                Parameters = descriptor.Parameters,
            };
        }
        return map.Values.ToList();
    }

    private CapabilityPolicy BuildPolicy(
        bool allowFileWrite,
        bool allowShellExecution,
        bool allowNetworkAccess,
        string allowedToolNamesJson,
        string role,
        IReadOnlyList<string> selectedToolNames)
    {
        var tools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var descriptors = toolCatalog.ListTools();
        var descriptorByTool = descriptors.ToDictionary(d => d.ToolId, StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var toolName in JsonSerializer.Deserialize<List<string>>(allowedToolNamesJson) ?? [])
                AddPolicyTool(tools, toolName);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "[AgentRuntimeProfile] Ignoring malformed allowed tool list.");
        }

        foreach (var toolName in selectedToolNames)
            AddPolicyTool(tools, toolName);

        var isTaskRole = role.Equals("Task", StringComparison.OrdinalIgnoreCase);
        if (isTaskRole && tools.Count == 0)
        {
            tools.UnionWith([
                "terminal_start", "terminal_wait", "terminal_read",
                "terminal_status", "terminal_cancel", "terminal_input",
                "shell", "file_read", "list_dir", "file_write", "file_patch", "apply_patch",
            ]);
        }

        var policy = toolPermissionPolicy.BuildCapabilityPolicy(
            descriptors, tools.Where(descriptorByTool.ContainsKey), isTaskRole);

        return policy with
        {
            AllowFileWrite = allowFileWrite || policy.AllowFileWrite || isTaskRole,
            AllowShellExecution = allowShellExecution || policy.AllowShellExecution || isTaskRole,
            AllowNetworkAccess = allowNetworkAccess || policy.AllowNetworkAccess,
        };
    }

    private static string? TrimToNull(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static void AddToolDescriptors(
        Dictionary<string, ToolDescriptor> result,
        IReadOnlyDictionary<string, ToolDescriptor> byToolId,
        IEnumerable<string> toolIds)
    {
        foreach (var toolId in toolIds)
            if (byToolId.TryGetValue(toolId, out var descriptor))
                result.TryAdd(descriptor.ToolId, descriptor);
    }

    private static void AddPolicyTool(HashSet<string> tools, string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return;
        var trimmed = toolName.Trim();
        if (!IsTerminalExecuteAlias(trimmed))
        {
            tools.Add(trimmed);
            return;
        }
        foreach (var terminalToolId in TerminalLifecycleToolIds)
            tools.Add(terminalToolId);
    }

    private static bool IsTerminalExecuteAlias(string value)
        => value.Equals("terminal_execute", StringComparison.OrdinalIgnoreCase)
        || value.Equals("cap-terminal-execute", StringComparison.OrdinalIgnoreCase);

    private static string ToolIdToCapabilityId(string toolId)
        => $"cap-{toolId.Trim().Replace('_', '-').ToLowerInvariant()}";

    private sealed record ResolvedLlmRouting(
        string? ProfileId,
        string? ProviderId,
        string? ModelId,
        LlmConfig? Config);

    private sealed record ResolvedCapabilities(
        CapabilityPolicy? Policy,
        IReadOnlyList<LlmToolDefinition>? ToolDefinitions,
        string Source,
        int CapabilityCount);
}
