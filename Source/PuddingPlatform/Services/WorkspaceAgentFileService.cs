using System.Text.Json;
using PuddingCode.Configuration;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services;

/// <summary>
/// 文件式工作区 Agent 实例管理服务 — 读写 data/agents/{agentInstanceId}/ 和 data/workspaces/{workspaceId}/agents/。
/// 唯一事实来源：agents 目录下的 manifest.json + workspace ref.json 文件。
/// </summary>
public sealed class WorkspaceAgentFileService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly PuddingDataPaths _paths;
    private readonly AgentTemplateFileService _templateFileService;
    private readonly AgentAvatarCatalog _avatarCatalog;
    private readonly ILogger<WorkspaceAgentFileService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public WorkspaceAgentFileService(
        PuddingDataPaths paths,
        AgentTemplateFileService templateFileService,
        AgentAvatarCatalog avatarCatalog,
        ILogger<WorkspaceAgentFileService> logger)
    {
        _paths = paths;
        _templateFileService = templateFileService;
        _avatarCatalog = avatarCatalog;
        _logger = logger;
    }

    /// <summary>获取工作区下所有 Agent 实例。</summary>
    public async Task<List<WorkspaceAgentDto>> ListAgentsAsync(string workspaceId, CancellationToken ct = default)
    {
        var agentsDir = Path.Combine(_paths.WorkspaceRoot(workspaceId), "agents");
        if (!Directory.Exists(agentsDir))
            return [];

        var agentDirs = Directory.GetDirectories(agentsDir);
        var result = new List<WorkspaceAgentDto>();

        foreach (var agentDir in agentDirs)
        {
            var refPath = Path.Combine(agentDir, "ref.json");
            if (!File.Exists(refPath)) continue;

            try
            {
                var refData = await AtomicFileWriter.ReadJsonAsync<WorkspaceAgentRef>(refPath, JsonOptions, ct);
                if (refData is null) continue;

                var instanceManifest = await LoadInstanceManifestAsync(refData.AgentInstanceId, ct);
                if (instanceManifest is null) continue;

                var avatar = await ResolveAgentAvatarAsync(instanceManifest, ct);
                result.Add(new WorkspaceAgentDto(
                    AgentId: refData.AgentInstanceId,
                    Name: instanceManifest.DisplayName ?? instanceManifest.TemplateId,
                    Description: instanceManifest.Description,
                    DisplayName: instanceManifest.DisplayName,
                    AvatarId: avatar.AvatarId,
                    AvatarUrl: avatar.AvatarUrl,
                    SourceTemplateId: instanceManifest.TemplateId,
                    SystemPromptOverride: null,
                    PreferredProviderId: null,
                    PreferredModelId: null,
                    IsEnabled: instanceManifest.IsEnabled,
                    IsFrozen: false,
                    CreatedAt: DateTimeOffset.UtcNow,
                    UpdatedAt: DateTimeOffset.UtcNow
                ));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load workspace agent from {Dir}", agentDir);
            }
        }

        return result;
    }

    /// <summary>获取单个 Agent 实例。</summary>
    public async Task<WorkspaceAgentDto?> GetAgentAsync(string workspaceId, string agentId, CancellationToken ct = default)
    {
        var refData = await LoadWorkspaceRefAsync(workspaceId, agentId, ct);
        if (refData is null) return null;

        var instanceManifest = await LoadInstanceManifestAsync(agentId, ct);
        if (instanceManifest is null) return null;

        var avatar = await ResolveAgentAvatarAsync(instanceManifest, ct);
        return new WorkspaceAgentDto(
            AgentId: agentId,
            Name: instanceManifest.DisplayName ?? instanceManifest.TemplateId,
            Description: instanceManifest.Description,
            DisplayName: instanceManifest.DisplayName,
            AvatarId: avatar.AvatarId,
            AvatarUrl: avatar.AvatarUrl,
            SourceTemplateId: instanceManifest.TemplateId,
            SystemPromptOverride: null,
            PreferredProviderId: null,
            PreferredModelId: null,
            IsEnabled: instanceManifest.IsEnabled,
            IsFrozen: false,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow
        );
    }

    /// <summary>在工作区下创建 Agent 实例。</summary>
    public async Task<WorkspaceAgentDto> CreateAgentAsync(string workspaceId, CreateWorkspaceAgentRequest req, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var rawId = $"{workspaceId}.{req.SourceTemplateId ?? "agent"}.{Guid.NewGuid():N}"[..36];
            // 替换文件系统非法字符（Windows 不允许 : * ? " < > | 等）
            var agentInstanceId = string.Join("_", rawId.Split(Path.GetInvalidFileNameChars()));

            // 创建 agent instance 目录
            var instanceRoot = _paths.AgentInstanceRoot(agentInstanceId);
            Directory.CreateDirectory(instanceRoot);
            Directory.CreateDirectory(_paths.AgentInstanceConfigRoot(agentInstanceId));

            var instanceManifest = new AgentInstanceManifest
            {
                AgentInstanceId = agentInstanceId,
                TemplateId = req.SourceTemplateId ?? "general-assistant",
                WorkspaceId = workspaceId,
                DisplayName = req.DisplayName ?? req.Name,
                Description = req.Description,
                AvatarId = req.AvatarId,
                AvatarUrl = req.AvatarUrl,
                IsEnabled = true,
                Paths = new AgentInstancePaths
                {
                    Config = "config",
                    Workspace = "workspace",
                    State = "state",
                    Logs = "logs",
                },
            };

            await AtomicFileWriter.WriteJsonAsync(
                Path.Combine(instanceRoot, "manifest.json"),
                instanceManifest, JsonOptions, ct);

            // 写入 LLM config（如果有指定）
            if (req.PreferredProviderId is not null || req.SystemPromptOverride is not null)
            {
                var llmConfig = new AgentInstanceLlmConfig
                {
                    Conscious = new AgentLlmBinding
                    {
                        ProviderId = req.PreferredProviderId,
                        ModelId = req.PreferredModelId,
                    },
                };
                await AtomicFileWriter.WriteJsonAsync(
                    _paths.AgentInstanceConfigFile(agentInstanceId, "llm.json"),
                    llmConfig, JsonOptions, ct);
            }

            // 创建 workspace ref
            var refDir = _paths.WorkspaceAgentRoot(workspaceId, agentInstanceId);
            Directory.CreateDirectory(refDir);

            var workspaceRef = new WorkspaceAgentRef
            {
                AgentInstanceId = agentInstanceId,
                WorkspaceId = workspaceId,
                AgentPath = Path.GetRelativePath(refDir, instanceRoot),
                IsEnabled = true,
            };
            await AtomicFileWriter.WriteJsonAsync(
                Path.Combine(refDir, "ref.json"),
                workspaceRef, JsonOptions, ct);

            _logger.LogInformation(
                "Workspace agent created: workspace={Workspace} agent={AgentId} template={Template}",
                workspaceId, agentInstanceId, req.SourceTemplateId);

            var avatar = await ResolveAgentAvatarAsync(instanceManifest, ct);
            return new WorkspaceAgentDto(
                AgentId: agentInstanceId,
                Name: instanceManifest.DisplayName ?? instanceManifest.TemplateId,
                Description: instanceManifest.Description,
                DisplayName: instanceManifest.DisplayName,
                AvatarId: avatar.AvatarId,
                AvatarUrl: avatar.AvatarUrl,
                SourceTemplateId: instanceManifest.TemplateId,
                SystemPromptOverride: null,
                PreferredProviderId: req.PreferredProviderId,
                PreferredModelId: req.PreferredModelId,
                IsEnabled: true,
                IsFrozen: false,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow
            );
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>更新 Agent 实例。</summary>
    public async Task<WorkspaceAgentDto> UpdateAgentAsync(string workspaceId, string agentId, UpdateWorkspaceAgentRequest req, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var instanceManifest = await LoadInstanceManifestAsync(agentId, ct);
            if (instanceManifest is null)
                throw new KeyNotFoundException($"Agent instance '{agentId}' 不存在");

            var instanceRoot = _paths.AgentInstanceRoot(agentId);

            var updated = instanceManifest with
            {
                DisplayName = req.DisplayName ?? req.Name,
                Description = req.Description,
                AvatarId = req.AvatarId ?? instanceManifest.AvatarId,
                AvatarUrl = req.AvatarUrl ?? instanceManifest.AvatarUrl,
                IsEnabled = req.IsEnabled,
            };

            await AtomicFileWriter.WriteJsonAsync(
                Path.Combine(instanceRoot, "manifest.json"),
                updated, JsonOptions, ct);

            _logger.LogInformation("Workspace agent updated: {AgentId}", agentId);

            var avatar = await ResolveAgentAvatarAsync(updated, ct);
            return new WorkspaceAgentDto(
                AgentId: agentId,
                Name: updated.DisplayName ?? updated.TemplateId,
                Description: updated.Description,
                DisplayName: updated.DisplayName,
                AvatarId: avatar.AvatarId,
                AvatarUrl: avatar.AvatarUrl,
                SourceTemplateId: updated.TemplateId,
                SystemPromptOverride: req.SystemPromptOverride,
                PreferredProviderId: req.PreferredProviderId,
                PreferredModelId: req.PreferredModelId,
                IsEnabled: req.IsEnabled,
                IsFrozen: false,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow
            );
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>删除工作区中的 Agent 引用（硬删除文件），Agent 实例数据仍保留在 data/agents/ 中以备数据恢复。</summary>
    public async Task DeleteAgentAsync(string workspaceId, string agentId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var refData = await LoadWorkspaceRefAsync(workspaceId, agentId, ct);
            if (refData is null)
                throw new KeyNotFoundException($"Agent '{agentId}' in workspace '{workspaceId}' 不存在");

            // 删除 workspace ref 文件及其目录
            var wsAgentRoot = _paths.WorkspaceAgentRoot(workspaceId, agentId);
            if (Directory.Exists(wsAgentRoot))
            {
                Directory.Delete(wsAgentRoot, recursive: true);
            }

            _logger.LogInformation("Workspace agent deleted (ref removed): {AgentId} from workspace {WorkspaceId}", agentId, workspaceId);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ─── 内部方法 ─────────────────────────────────────────

    private async Task<AgentInstanceManifest?> LoadInstanceManifestAsync(string agentInstanceId, CancellationToken ct)
    {
        var path = Path.Combine(_paths.AgentInstanceRoot(agentInstanceId), "manifest.json");
        if (!File.Exists(path)) return null;
        return await AtomicFileWriter.ReadJsonAsync<AgentInstanceManifest>(path, JsonOptions, ct);
    }

    private async Task<WorkspaceAgentRef?> LoadWorkspaceRefAsync(string workspaceId, string agentInstanceId, CancellationToken ct)
    {
        var path = _paths.WorkspaceAgentRefFile(workspaceId, agentInstanceId);
        if (!File.Exists(path)) return null;
        return await AtomicFileWriter.ReadJsonAsync<WorkspaceAgentRef>(path, JsonOptions, ct);
    }

    /// <summary>
    /// 解析 Agent 头像，优先级：
    /// 1. AgentInstanceManifest.AvatarId
    /// 2. 对应 AgentTemplateManifest 的头像
    /// 3. AgentInstanceManifest.AvatarUrl（legacy fallback）
    /// 4. 默认启用头像
    /// 5. null（前端使用 emoji fallback）
    /// </summary>
    private async Task<(string? AvatarId, string? AvatarUrl)> ResolveAgentAvatarAsync(
        AgentInstanceManifest instance,
        CancellationToken ct)
    {
        // 1. 实例级 AvatarId
        if (!string.IsNullOrWhiteSpace(instance.AvatarId))
        {
            var avatar = await _avatarCatalog.GetRequiredEnabledAsync(instance.AvatarId);
            if (avatar is not null) return (avatar.AvatarId, avatar.UrlPath);
        }

        // 2. 模板级头像
        var template = await _templateFileService.GetTemplateAsync(instance.TemplateId, ct);
        if (template?.AvatarUrl is not null)
            return (template.AvatarId, template.AvatarUrl);

        // 3. 实例级 legacy Url
        if (!string.IsNullOrWhiteSpace(instance.AvatarUrl))
            return (instance.AvatarId, instance.AvatarUrl);

        // 4. 默认启用头像
        var fallback = await _avatarCatalog.GetDefaultAsync();
        return (fallback?.AvatarId, fallback?.UrlPath);
    }
}
