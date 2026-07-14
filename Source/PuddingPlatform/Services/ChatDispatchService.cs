using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Services;
using PuddingCode.Tools;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services;

public sealed class ChatDispatchService
{
    private readonly PlatformDbContext _db;
    private readonly WorkspaceAgentFileService _workspaceAgentFileService;
    private readonly AgentTemplateFileService _templateFileService;
    private readonly MinioStorageService _minio;
    private readonly IPuddingToolCatalogService _toolCatalog;
    private readonly IToolPermissionPolicyService _toolPermissionPolicy;
    private readonly ILlmConfigService _llmConfigService;
    private readonly SessionTitleService _sessionTitleService;
    private readonly ILogger<ChatDispatchService> _logger;

    public ChatDispatchService(
        PlatformDbContext db,
        WorkspaceAgentFileService workspaceAgentFileService,
        AgentTemplateFileService templateFileService,
        MinioStorageService minio,
        IPuddingToolCatalogService toolCatalog,
        IToolPermissionPolicyService toolPermissionPolicy,
        ILlmConfigService llmConfigService,
        SessionTitleService sessionTitleService,
        ILogger<ChatDispatchService> logger)
    {
        _db = db;
        _workspaceAgentFileService = workspaceAgentFileService;
        _templateFileService = templateFileService;
        _minio = minio;
        _toolCatalog = toolCatalog;
        _toolPermissionPolicy = toolPermissionPolicy;
        _llmConfigService = llmConfigService;
        _sessionTitleService = sessionTitleService;
        _logger = logger;
    }

    public async Task<ChatAgentDispatch> ResolveChatAgentDispatchAsync(
        string workspaceId,
        int workspacePk,
        string agentId,
        CancellationToken ct)
    {
        string? agentTemplateId = null;
        string? preferredProviderId = null;
        string? preferredModelId = null;
        string? displayName = null;
        string? avatarUrl = null;

        var fileAgent = await _workspaceAgentFileService.GetAgentAsync(workspaceId, agentId, ct);
        var agent = fileAgent is null
            ? await _db.WorkspaceAgents.AsNoTracking()
                .FirstOrDefaultAsync(a => a.WorkspaceEntityId == workspacePk && a.AgentId == agentId && a.IsEnabled, ct)
            : null;
        string resolveSource = "none";
        if (fileAgent is not null && fileAgent.IsEnabled)
        {
            agentTemplateId = fileAgent.SourceTemplateId;
            preferredProviderId = fileAgent.PreferredProviderId;
            preferredModelId = fileAgent.PreferredModelId;
            displayName = fileAgent.DisplayName ?? fileAgent.Name;
            avatarUrl = fileAgent.AvatarUrl;
            resolveSource = "file";
        }
        else if (agent is not null)
        {
            agentTemplateId = agent.SourceTemplateId;
            resolveSource = "db";
            displayName = agent.DisplayName ?? agent.Name;
            avatarUrl = agent.AvatarUrl;
        }

        if (string.IsNullOrWhiteSpace(preferredProviderId) && !string.IsNullOrWhiteSpace(agentTemplateId))
        {
            var template = await ResolveAgentTemplateAsync(agentTemplateId, ct);
            if (template is not null)
            {
                preferredProviderId = template.PreferredProviderId;
                preferredModelId = template.PreferredModelId;
                resolveSource = $"{resolveSource}+template";
            }
        }

        _logger.LogInformation(
            "[Chat:Dispatch] Agent config resolved agent={AgentId} template={Template} provider={Provider} model={Model} source={Source}",
            agentId, agentTemplateId ?? "(none)",
            preferredProviderId ?? "(null)", preferredModelId ?? "(null)", resolveSource);

        var resolved = await ResolveCapabilitiesAsync(workspaceId, agentTemplateId, ct);
        _logger.LogDebug(
            "[Chat:Tools] Platform resolved tool definitions workspace={WorkspaceId} agent={AgentId} template={TemplateId} source={Source} capabilityCount={CapabilityCount} toolCount={ToolCount} tools={Tools} allowedToolCount={AllowedToolCount} defaultToolCount={DefaultToolCount} grantToolCount={GrantToolCount}",
            workspaceId,
            agentId,
            agentTemplateId ?? "",
            resolved.Source,
            resolved.CapabilityCount,
            resolved.ToolDefinitions?.Count ?? 0,
            SummarizeToolDefinitions(resolved.ToolDefinitions),
            resolved.Policy?.AllowedToolNames.Count ?? 0,
            resolved.Policy?.DefaultToolNames.Count ?? 0,
            resolved.Policy?.RequiresGrantToolNames.Count ?? 0);

        var skillPackages = await ResolveSkillPackagesAsync(agentTemplateId, ct);
        LlmConfig? llmConfig = null;
        if (preferredProviderId is not null)
        {
            var normalizedModelId = NormalizePreferredModelId(preferredProviderId, preferredModelId);
            llmConfig = _llmConfigService.Resolve(preferredProviderId, normalizedModelId);
            if (llmConfig is not null)
            {
                var templateReasoningEffort = await ResolveReasoningEffortAsync(workspaceId, agentTemplateId, ct);
                if (!string.IsNullOrWhiteSpace(templateReasoningEffort))
                    llmConfig = llmConfig with { ReasoningEffort = templateReasoningEffort };

                _logger.LogInformation(
                    "[Chat] LlmConfig resolved from agent template: agentId={AgentId} provider={ProviderId} model={ModelId} rawModel={RawModelId} endpoint={Endpoint} hasKeyVaultRef={HasKeyVaultRef}",
                    agentId,
                    preferredProviderId,
                    llmConfig.ModelId ?? "(none)",
                    preferredModelId ?? "(none)",
                    llmConfig.Endpoint,
                    !string.IsNullOrWhiteSpace(llmConfig.KeyVaultId));
            }
            else
            {
                _logger.LogWarning(
                    "[Chat] LlmConfig NOT resolved: agentId={AgentId} provider={ProviderId} model={ModelId} not found/disabled in file config",
                    agentId, preferredProviderId, normalizedModelId ?? "(none)");
            }
        }
        else
        {
            _logger.LogInformation(
                "[Chat] agent={AgentId} has no PreferredProviderId; runtime will require explicit file config",
                agentId);
        }

        _logger.LogInformation(
            "[Chat] Agent dispatch resolved: agentId={AgentId} templateId={TemplateId} hasCapability={HasCapability} allowShell={AllowShell}",
            agentId,
            agentTemplateId ?? "(none)",
            resolved.Policy is not null,
            resolved.Policy?.AllowShellExecution == true);

        return new ChatAgentDispatch(
            AgentId: agentId,
            DisplayName: displayName ?? agentId,
            AvatarUrl: avatarUrl,
            AgentTemplateId: agentTemplateId,
            PreferredProviderId: preferredProviderId,
            LlmConfig: llmConfig,
            CapabilityPolicy: resolved.Policy,
            ToolDefinitions: resolved.ToolDefinitions,
            SkillPackages: skillPackages);
    }

    public async Task<string?> ResolveInitialSessionTitleAsync(
        string workspaceId,
        AdminChatRequest req,
        ChatAgentDispatch dispatch,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(req.SessionId) && !req.ForceNewSession)
            return null;

        var agentTemplateId = string.IsNullOrWhiteSpace(dispatch.AgentTemplateId)
            ? dispatch.AgentId
            : dispatch.AgentTemplateId;

        return await _sessionTitleService.BuildDefaultTitleAsync(
            workspaceId,
            agentTemplateId,
            dispatch.DisplayName,
            ct);
    }

    public static async Task<IReadOnlyList<WorkspaceAgentDto>> LoadWorkspaceAgentsForRoutingAsync(
        PlatformDbContext db,
        WorkspaceAgentFileService workspaceAgentFileService,
        int workspacePk,
        string workspaceId,
        CancellationToken ct)
    {
        var dbAgents = await db.WorkspaceAgents.AsNoTracking()
            .Where(a => a.WorkspaceEntityId == workspacePk)
            .Select(a => new WorkspaceAgentDto(
                a.AgentId,
                a.Name,
                a.Description,
                a.DisplayName,
                a.AvatarId,
                a.AvatarUrl,
                a.SourceTemplateId,
                null,
                null,
                null,
                null,
                a.IsEnabled,
                a.IsFrozen,
                a.CreatedAt,
                a.UpdatedAt))
            .ToListAsync(ct);

        var fileAgents = await workspaceAgentFileService.ListAgentsAsync(workspaceId, ct);
        return fileAgents
            .Concat(dbAgents)
            .GroupBy(agent => agent.AgentId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static (string RawId, string GlobalId, bool IsExplicitGlobal) NormalizeTemplateId(string templateId)
    {
        const string prefix = "global:";
        return templateId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? (templateId, templateId[prefix.Length..], true)
            : (templateId, templateId, false);
    }

    private async Task<GlobalAgentTemplateDto?> ResolveAgentTemplateAsync(
        string templateId,
        CancellationToken ct)
    {
        var (_, globalId, _) = NormalizeTemplateId(templateId);
        return await _templateFileService.GetTemplateAsync(globalId, ct);
    }

    private async Task<ResolvedCapabilities> ResolveCapabilitiesAsync(
        string workspaceId,
        string? templateId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            return new ResolvedCapabilities(null, null, "none", 0);

        var (rawId, globalId, isExplicitGlobal) = NormalizeTemplateId(templateId);

        var globalTemplate = await _templateFileService.GetTemplateAsync(globalId, ct);
        if (globalTemplate is not null)
        {
            var selected = globalTemplate.SelectedCapabilityIds;
            var selectedToolNames = ResolveSelectedToolNames(selected);
            var selectedDescriptors = ResolveSelectedToolDescriptors(selected);
            var allowedToolNamesJson = (globalTemplate.AllowedToolNames is { Count: > 0 } names)
                ? JsonSerializer.Serialize(names)
                : "[]";
            return new ResolvedCapabilities(
                BuildPolicy(
                globalTemplate.AllowFileWrite,
                globalTemplate.AllowShellExecution,
                globalTemplate.AllowNetworkAccess,
                allowedToolNamesJson,
                globalTemplate.Role,
                selectedToolNames,
                _toolCatalog,
                _toolPermissionPolicy),
                BuildToolDefinitions(selectedDescriptors),
                "global-file-template",
                selectedToolNames.Count);
        }

        if (globalId.Equals("code-agent", StringComparison.OrdinalIgnoreCase)
            || globalId.Equals("workspace-task-agent", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedCapabilities(
                new CapabilityPolicy
                {
                    AllowFileWrite = true,
                    AllowShellExecution = true,
                    AllowNetworkAccess = false,
                    AllowedToolNames = ["shell", "file_read", "file_write", "file_patch"],
                    DefaultToolNames = ["file_read", "search_memory", "grep_memory",
                        "query_sessions", "http_fetch", "file_search", "search_grep",
                        "spawn_sub_agent", "manage_tasks"],
                    RequiresGrantToolNames = ["file_patch", "file_write", "shell"],
                },
                [
                    new LlmToolDefinition
                    {
                        Name = "shell",
                        Description = "Execute a host shell command",
                        Parameters = new ToolParameterSchema(
                            [
                                new ToolParameter("command", "string", "Command to execute on the host"),
                                new ToolParameter("shell", "string", "Shell mode: auto, wsl, bash, cmd, or powershell. Default: auto"),
                                new ToolParameter("working_directory", "string", "Host working directory. Default: current runtime directory"),
                                new ToolParameter("timeout_seconds", "integer", "Timeout in seconds, 1-600. Default: 30"),
                            ],
                            ["command"]),
                    }
                ],
                "fallback-code-agent",
                0);
        }

        return new ResolvedCapabilities(null, null, "none", 0);
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

    private static readonly string[] TerminalLifecycleToolIds =
    [
        "terminal_start",
        "terminal_wait",
        "terminal_read",
        "terminal_status",
        "terminal_cancel",
        "terminal_input",
    ];

    private static bool IsTerminalExecuteAlias(string value)
        => value.Equals("terminal_execute", StringComparison.OrdinalIgnoreCase)
        || value.Equals("cap-terminal-execute", StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<ToolDescriptor> ResolveSelectedToolDescriptors(
        IEnumerable<string> selectedCapabilityOrToolIds)
    {
        var descriptors = _toolCatalog.ListTools();
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
                foreach (var terminalToolId in TerminalLifecycleToolIds)
                {
                    if (byToolId.TryGetValue(terminalToolId, out var descriptor))
                        result.TryAdd(descriptor.ToolId, descriptor);
                }
                continue;
            }

            if (byCapabilityId.TryGetValue(id, out var byCap))
            {
                result.TryAdd(byCap.ToolId, byCap);
                continue;
            }

            if (byToolId.TryGetValue(id, out var byTool))
                result.TryAdd(byTool.ToolId, byTool);
        }

        return result.Values.ToList();
    }

    private IReadOnlyList<string> ResolveSelectedToolNames(
        IEnumerable<string> selectedCapabilityOrToolIds)
        => ResolveSelectedToolDescriptors(selectedCapabilityOrToolIds)
            .Select(d => d.ToolId)
            .ToList();

    private static string ToolIdToCapabilityId(string toolId)
        => $"cap-{toolId.Trim().Replace('_', '-').ToLowerInvariant()}";

    private static string SummarizeToolDefinitions(IReadOnlyList<LlmToolDefinition>? tools)
        => tools is { Count: > 0 }
            ? string.Join(",", tools.Select(t => t.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            : "";

    private static CapabilityPolicy BuildPolicy(
        bool allowFileWrite,
        bool allowShellExecution,
        bool allowNetworkAccess,
        string allowedToolNamesJson,
        string role,
        IReadOnlyList<string>? selectedToolNames = null,
        IPuddingToolCatalogService? toolCatalog = null,
        IToolPermissionPolicyService? toolPermissionPolicy = null)
    {
        var tools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var descriptors = toolCatalog?.ListTools() ?? [];
        var descriptorByTool = descriptors.ToDictionary(d => d.ToolId, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var t in JsonSerializer.Deserialize<List<string>>(allowedToolNamesJson) ?? [])
            {
                if (string.IsNullOrWhiteSpace(t))
                    continue;

                var trimmed = t.Trim();
                if (IsTerminalExecuteAlias(trimmed))
                {
                    foreach (var terminalToolId in TerminalLifecycleToolIds)
                        tools.Add(terminalToolId);
                }
                else
                {
                    tools.Add(trimmed);
                }
            }
        }
        catch
        {
            // ignore malformed JSON and continue with selected capabilities.
        }

        foreach (var toolName in selectedToolNames ?? [])
        {
            if (!string.IsNullOrWhiteSpace(toolName))
                tools.Add(toolName.Trim());
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

        if (toolPermissionPolicy is null)
            throw new InvalidOperationException("Tool permission policy service is required.");

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

    private static List<string> ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }

    private static string? NormalizePreferredModelId(string providerId, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return modelId;

        var trimmed = modelId.Trim();
        if (providerId.Equals("mimo", StringComparison.OrdinalIgnoreCase))
            return trimmed.ToLowerInvariant();

        return trimmed;
    }

    private async Task<IReadOnlyList<SkillPackageInfo>?> ResolveSkillPackagesAsync(
        string? templateId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            return null;

        var (_, globalId, _) = NormalizeTemplateId(templateId);

        var globalTemplate = await _templateFileService.GetTemplateAsync(globalId, ct);
        if (globalTemplate is null)
            return null;

        var selectedIds = globalTemplate.SelectedSkillPackageIds;
        if (selectedIds.Count == 0)
            return null;

        var packages = await _db.SkillPackages.AsNoTracking()
            .Where(s => selectedIds.Contains(s.SkillPackageId) && s.IsEnabled)
            .ToListAsync(ct);

        if (packages.Count == 0)
            return null;

        var result = new List<SkillPackageInfo>(packages.Count);
        foreach (var pkg in packages)
        {
            var url = await _minio.GetPresignedDownloadUrlAsync(pkg.ObjectKey, 86400, ct);
            result.Add(new SkillPackageInfo
            {
                SkillPackageId = pkg.SkillPackageId,
                Name           = pkg.Name,
                Description    = pkg.Description,
                Version        = pkg.Version,
                DownloadUrl    = url,
            });
        }
        return result;
    }

    private async Task<string?> ResolveReasoningEffortAsync(
        string workspaceId,
        string? templateId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            return null;

        var (rawId, globalId, isExplicitGlobal) = NormalizeTemplateId(templateId);

        var globalTemplate = await _templateFileService.GetTemplateAsync(globalId, ct);
        return globalTemplate?.ReasoningEffort;
    }
}

internal sealed record ResolvedCapabilities(
    CapabilityPolicy? Policy,
    IReadOnlyList<LlmToolDefinition>? ToolDefinitions,
    string Source,
    int CapabilityCount);
