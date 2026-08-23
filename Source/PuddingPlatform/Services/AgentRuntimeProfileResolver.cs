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
    ILlmConfigService llmConfigService,
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
        var manifestPath = definition.SourcePaths.GetValueOrDefault(
            "instance.manifest",
            $"data/agents/{agent.AgentId}/manifest.json");
        var llm = ResolveConsciousLlm(
            definition.Instance,
            agent.AgentId,
            manifestPath,
            llmConfigService);
        var capabilities = BuildCapabilitiesFromInstance(definition.Instance, workspaceId);
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
            VisionHelperModel = definition.Instance.VisionHelperModel,
            LlmConfig = llm.Config,
            CapabilityPolicy = capabilities.Policy,
            ToolDefinitions = capabilities.ToolDefinitions,
            SkillPackages = skillPackages,
            SystemPrompt = definition.Instance.SystemPrompt,
            MaxRounds = definition.Instance.MaxRounds,
            MaxElapsedSeconds = definition.Instance.MaxElapsedSeconds,
            MaxToolCallsTotal = definition.Instance.MaxToolCallsTotal,
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
    /// Resolve the main Agent route exclusively from its instance manifest.
    /// The provider registry only supplies the matching endpoint/credentials snapshot.
    /// </summary>
    internal static ResolvedLlmRouting ResolveConsciousLlm(
        AgentInstanceManifest manifest,
        string agentId,
        string manifestPath,
        ILlmConfigService llmConfigService)
    {
        var providerId = TrimToNull(manifest.PreferredProviderId);
        if (providerId is null)
        {
            throw new AgentConfigurationException(
                agentId,
                $"Agent '{agentId}' manifest '{manifestPath}' is missing required " +
                "'preferredProviderId'. Configure both preferredProviderId and preferredModelId.");
        }

        var modelId = TrimToNull(manifest.PreferredModelId);
        if (modelId is null)
        {
            throw new AgentConfigurationException(
                agentId,
                $"Agent '{agentId}' manifest '{manifestPath}' is missing required " +
                "'preferredModelId'. Configure both preferredProviderId and preferredModelId.");
        }

        var config = llmConfigService.Resolve(providerId, modelId);
        if (config is null)
        {
            var providerExists = llmConfigService.GetEnabledProviders().Any(provider =>
                string.Equals(provider.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            var reason = providerExists
                ? $"preferredModelId '{modelId}' is not registered as an enabled, non-deprecated model for provider '{providerId}'"
                : $"preferredProviderId '{providerId}' is not registered or is disabled";
            throw new AgentConfigurationException(
                agentId,
                $"Agent '{agentId}' manifest '{manifestPath}' is invalid: {reason} in " +
                "data/config/llm.providers.json. No fallback model was selected.");
        }

        if (!string.Equals(config.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentConfigurationException(
                agentId,
                $"Agent '{agentId}' manifest '{manifestPath}' resolved model '{config.ModelId}', " +
                $"but explicitly configured preferredModelId is '{modelId}'.");
        }

        if (config.ReasoningEffort is null
            && !string.IsNullOrWhiteSpace(manifest.ReasoningEffort))
        {
            config = config with { ReasoningEffort = manifest.ReasoningEffort.Trim() };
        }

        if (manifest.MaxReplyTokens > 0)
        {
            var modelOutputLimit = config.MaxOutputTokens is > 0
                ? config.MaxOutputTokens.Value
                : manifest.MaxReplyTokens;
            config = config with
            {
                MaxOutputTokens = Math.Min(modelOutputLimit, manifest.MaxReplyTokens),
            };
        }

        return new ResolvedLlmRouting(null, providerId, modelId, config);
    }

    /// <summary>
    /// Build capability policy and tool definitions from agent's embedded config.
    /// No longer reads template files at runtime.
    /// </summary>
    private ResolvedCapabilities BuildCapabilitiesFromInstance(
        AgentInstanceManifest instance,
        string workspaceId)
    {
        var capIds = instance.Capabilities.AllowedToolIds;
        var selectedToolDescriptors = ResolveSelectedToolDescriptors(capIds, workspaceId)
            .ToDictionary(d => d.ToolId, StringComparer.OrdinalIgnoreCase);

        // An enabled Workspace MCP server is an explicit workspace-level capability grant. Its
        // individual tools are exposed to agents in that workspace, but remain high-risk and pass
        // through the normal runtime approval gate before invocation.
        foreach (var descriptor in toolCatalog.ListTools(workspaceId)
                     .Where(d => d.SourceKind.Equals("MCP", StringComparison.OrdinalIgnoreCase)))
        {
            selectedToolDescriptors.TryAdd(descriptor.ToolId, descriptor);
        }

        if (selectedToolDescriptors.Count == 0)
            return new ResolvedCapabilities(null, null, "none", 0);

        var selectedDescriptors = selectedToolDescriptors.Values.ToList();
        var selectedToolNames = selectedDescriptors.Select(d => d.ToolId).ToList();
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
                selectedToolNames,
                workspaceId),
            BuildToolDefinitions(selectedDescriptors),
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
        IEnumerable<string> selectedCapabilityOrToolIds,
        string workspaceId)
    {
        var descriptors = toolCatalog.ListTools(workspaceId);
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
        IReadOnlyList<string> selectedToolNames,
        string workspaceId)
    {
        var tools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var descriptors = toolCatalog.ListTools(workspaceId);
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

    internal sealed record ResolvedLlmRouting(
        string? ProfileId,
        string ProviderId,
        string ModelId,
        LlmConfig Config);

    private sealed record ResolvedCapabilities(
        CapabilityPolicy? Policy,
        IReadOnlyList<LlmToolDefinition>? ToolDefinitions,
        string Source,
        int CapabilityCount);
}
