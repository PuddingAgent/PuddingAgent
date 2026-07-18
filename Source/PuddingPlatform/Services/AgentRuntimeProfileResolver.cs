using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services;

/// <summary>
/// Resolves the complete runtime profile for a workspace agent instance.
/// </summary>
/// <remarks>
/// Agent execution has several ingress paths: Web chat, message delivery,
/// heartbeat, connector ingress, and future automation. Those paths must not
/// independently read agent manifests, template manifests, model providers, or
/// capability policy. The profile resolver is the application-service boundary
/// that turns configuration files and runtime indexes into one execution-ready
/// snapshot.
///
/// The key design constraint is ownership: workspace agents own identity,
/// avatar, enablement, and main-session binding; source templates own model
/// routing, capability policy, and Skill selection. Keeping that rule here
/// prevents controllers and queue consumers from re-implementing configuration
/// fallbacks whenever the storage layout changes.
/// </remarks>
public sealed class AgentRuntimeProfileResolver(
    IWorkspaceAgentCatalog agentCatalog,
    ILLMConfigResolver templateLlmResolver,
    AgentTemplateFileService templateFileService,
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
        var llm = await ResolveTemplateLlmAsync(workspaceId, agent, ct);
        var capabilities = await ResolveCapabilitiesAsync(agent.SourceTemplateId, ct);
        var skillPackages = await ResolveSkillPackagesAsync(agent.SourceTemplateId, ct);

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
            CapabilitySource = capabilities.Source,
            CapabilityCount = capabilities.CapabilityCount,
        };
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

    private async Task<ResolvedLlmRouting> ResolveTemplateLlmAsync(
        string workspaceId,
        WorkspaceAgentDto agent,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(agent.SourceTemplateId))
            return new ResolvedLlmRouting(null, null, null, null);

        var templateRouting = await templateLlmResolver.ResolveConsciousAsync(
            agent.SourceTemplateId!,
            workspaceId,
            ct);

        var providerId = TrimToNull(templateRouting?.ProviderId);
        var modelId = TrimToNull(templateRouting?.Config?.ModelId ?? templateRouting?.ModelId);
        var config = templateRouting?.Config;

        if (config is null)
        {
            logger.LogWarning(
                "[AgentRuntimeProfile] Template LLM config unresolved workspace={WorkspaceId} agent={AgentId} template={TemplateId} provider={ProviderId} model={ModelId}",
                workspaceId,
                agent.AgentId,
                agent.SourceTemplateId,
                providerId ?? "(none)",
                modelId ?? "(none)");
        }

        return new ResolvedLlmRouting(
            TrimToNull(templateRouting?.ProfileId),
            providerId,
            modelId,
            config);
    }

    private async Task<ResolvedCapabilities> ResolveCapabilitiesAsync(
        string? templateId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            return new ResolvedCapabilities(null, null, "none", 0);

        var (_, globalId, _) = NormalizeTemplateId(templateId);
        var template = await templateFileService.GetTemplateAsync(globalId, ct);
        if (template is null)
        {
            logger.LogWarning(
                "[AgentRuntimeProfile] Template capability config missing template={TemplateId}; runtime will execute without template capability grants",
                templateId);
            return new ResolvedCapabilities(null, null, "missing-template", 0);
        }

        var selectedToolDescriptors = ResolveSelectedToolDescriptors(template.SelectedCapabilityIds);
        var selectedToolNames = selectedToolDescriptors.Select(descriptor => descriptor.ToolId).ToList();
        var allowedToolNamesJson = template.AllowedToolNames is { Count: > 0 } names
            ? JsonSerializer.Serialize(names)
            : "[]";

        return new ResolvedCapabilities(
            BuildPolicy(
                template.AllowFileWrite,
                template.AllowShellExecution,
                template.AllowNetworkAccess,
                allowedToolNamesJson,
                template.Role,
                selectedToolNames),
            BuildToolDefinitions(selectedToolDescriptors),
            "global-file-template",
            selectedToolNames.Count);
    }

    private async Task<IReadOnlyList<SkillPackageInfo>?> ResolveSkillPackagesAsync(
        string? templateId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            return null;

        var (_, globalId, _) = NormalizeTemplateId(templateId);
        var template = await templateFileService.GetTemplateAsync(globalId, ct);
        if (template is null || template.SelectedSkillPackageIds.Count == 0)
            return null;

        var selectedIds = template.SelectedSkillPackageIds;
        var packages = await db.SkillPackages.AsNoTracking()
            .Where(package => selectedIds.Contains(package.SkillPackageId) && package.IsEnabled)
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
        var descriptorByTool = descriptors.ToDictionary(descriptor => descriptor.ToolId, StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var toolName in JsonSerializer.Deserialize<List<string>>(allowedToolNamesJson) ?? [])
            {
                AddPolicyTool(tools, toolName);
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "[AgentRuntimeProfile] Ignoring malformed allowed tool list from template capability policy.");
        }

        foreach (var toolName in selectedToolNames)
        {
            AddPolicyTool(tools, toolName);
        }

        var isTaskRole = role.Equals("Task", StringComparison.OrdinalIgnoreCase);
        if (isTaskRole && tools.Count == 0)
        {
            tools.UnionWith([
                "terminal_start",
                "terminal_wait",
                "terminal_read",
                "terminal_status",
                "terminal_cancel",
                "terminal_input",
                "shell",
                "file_read",
                "list_dir",
                "file_write",
                "file_patch",
                "apply_patch",
            ]);
        }

        var policy = toolPermissionPolicy.BuildCapabilityPolicy(
            descriptors,
            tools.Where(descriptorByTool.ContainsKey),
            isTaskRole);

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
        {
            if (byToolId.TryGetValue(toolId, out var descriptor))
                result.TryAdd(descriptor.ToolId, descriptor);
        }
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

    private static (string RawId, string GlobalId, bool IsExplicitGlobal) NormalizeTemplateId(string templateId)
    {
        const string prefix = "global:";
        return templateId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? (templateId, templateId[prefix.Length..], true)
            : (templateId, templateId, false);
    }

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
