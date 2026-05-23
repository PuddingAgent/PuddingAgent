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
    private readonly ILogger<WorkspaceAgentFileService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public WorkspaceAgentFileService(PuddingDataPaths paths, ILogger<WorkspaceAgentFileService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>获取工作区下所有 Agent 实例。</summary>
    public async Task<List<WorkspaceAgentDto>> ListAgentsAsync(string workspaceId, CancellationToken ct = default)
    {
        var agentRefDir = _paths.WorkspaceAgentRoot(workspaceId, "");
        var parentDir = Path.GetDirectoryName(agentRefDir); // workspaces/{workspaceId}/agents/
        if (parentDir is null || !Directory.Exists(parentDir))
            return [];

        var agentDirs = Directory.GetDirectories(parentDir);
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

                result.Add(new WorkspaceAgentDto(
                    AgentId: refData.AgentInstanceId,
                    Name: instanceManifest.DisplayName ?? instanceManifest.TemplateId,
                    Description: instanceManifest.Description,
                    DisplayName: instanceManifest.DisplayName,
                    AvatarId: null,
                    AvatarUrl: null,
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

        return new WorkspaceAgentDto(
            AgentId: agentId,
            Name: instanceManifest.DisplayName ?? instanceManifest.TemplateId,
            Description: instanceManifest.Description,
            DisplayName: instanceManifest.DisplayName,
            AvatarId: null,
            AvatarUrl: null,
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

            return new WorkspaceAgentDto(
                AgentId: agentInstanceId,
                Name: instanceManifest.DisplayName ?? instanceManifest.TemplateId,
                Description: instanceManifest.Description,
                DisplayName: instanceManifest.DisplayName,
                AvatarId: null,
                AvatarUrl: null,
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
                IsEnabled = req.IsEnabled,
            };

            await AtomicFileWriter.WriteJsonAsync(
                Path.Combine(instanceRoot, "manifest.json"),
                updated, JsonOptions, ct);

            _logger.LogInformation("Workspace agent updated: {AgentId}", agentId);

            return new WorkspaceAgentDto(
                AgentId: agentId,
                Name: updated.DisplayName ?? updated.TemplateId,
                Description: updated.Description,
                DisplayName: updated.DisplayName,
                AvatarId: null,
                AvatarUrl: null,
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

    /// <summary>软删除 Agent 实例。</summary>
    public async Task DeleteAgentAsync(string workspaceId, string agentId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var refData = await LoadWorkspaceRefAsync(workspaceId, agentId, ct);
            if (refData is null)
                throw new KeyNotFoundException($"Agent '{agentId}' in workspace '{workspaceId}' 不存在");

            // 软删：设置 IsEnabled = false
            var refPath = _paths.WorkspaceAgentRefFile(workspaceId, agentId);
            var updatedRef = refData with { IsEnabled = false };
            await AtomicFileWriter.WriteJsonAsync(refPath, updatedRef, JsonOptions, ct);

            // 同时更新 instance manifest
            var instanceRoot = _paths.AgentInstanceRoot(agentId);
            var manifestPath = Path.Combine(instanceRoot, "manifest.json");
            var manifest = await AtomicFileWriter.ReadJsonAsync<AgentInstanceManifest>(manifestPath, JsonOptions, ct);
            if (manifest is not null)
            {
                await AtomicFileWriter.WriteJsonAsync(manifestPath,
                    manifest with { IsEnabled = false }, JsonOptions, ct);
            }

            _logger.LogInformation("Workspace agent soft-deleted: {AgentId}", agentId);
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
}
