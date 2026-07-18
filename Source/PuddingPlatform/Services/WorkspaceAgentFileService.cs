using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Configuration;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services;

/// <summary>
/// Read-only catalog for workspace agent instances.
/// </summary>
public interface IWorkspaceAgentCatalog
{
    Task<IReadOnlyList<WorkspaceAgentDto>> ListAgentsAsync(string workspaceId, CancellationToken ct = default);
}

/// <summary>Workspace-scoped Audit agent candidate used by automatic approval routing.</summary>
public sealed record WorkspaceAgentAuditProfile
{
    public required string WorkspaceId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string AgentTemplateId { get; init; }
    public string? ProviderId { get; init; }
    public string? ProfileId { get; init; }
    public string? ModelId { get; init; }
}

/// <summary>Raised when a workspace operation would create more than one Audit agent.</summary>
public sealed class WorkspaceAuditAgentConflictException : Exception
{
    public WorkspaceAuditAgentConflictException(string workspaceId, string existingAgentId)
        : base($"当前工作空间已存在审计类型的agent：{existingAgentId}")
    {
        WorkspaceId = workspaceId;
        ExistingAgentId = existingAgentId;
    }

    public string WorkspaceId { get; }

    public string ExistingAgentId { get; }
}

/// <summary>
/// 文件式工作区 Agent 实例管理服务 — 读写 data/agents/{agentInstanceId}/ 和 data/workspaces/{workspaceId}/agents/。
/// 唯一事实来源：agents 目录下的 manifest.json + workspace ref.json 文件。
/// </summary>
/// <remarks>
/// Workspace Agent 是“实例层”配置：名称、头像、启用状态、模板绑定和主会话绑定。
/// 它不复制模板的 provider/model 字段。模型路由由 Agent 模板拥有，并通过
/// <see cref="IAgentRuntimeProfileResolver"/> 在运行入口统一解析。这样做的目的
/// 是避免列表/详情 DTO 变成第二个模型配置来源，进而让 Controller 或消息队列
/// 消费者误以为可以绕过模板服务直接构造 LLM 配置。
/// </remarks>
public sealed class WorkspaceAgentFileService : IWorkspaceAgentCatalog
{
    /// <summary>
    /// Agent 实例级心跳提示词的种子默认值。
    ///
    /// 这个默认值只用于创建或迁移单个 Agent 实例；运行时心跳不能直接引用它，
    /// 必须通过 manifest 中的实例字段读取，避免所有 Agent 共享同一份可变提示词。
    /// </summary>
    public const string DefaultHeartbeatPrompt = """
[系统心跳]

你醒来了。

这是你的一次主动检视时刻 —— 系统检测到你已经安静了一段时间，想问问你：

🟢 你是否有正在追逐的目标？
    → 如果是，去读一读你的 goal.md，看看走到了哪一步，往前推进一步。
    → 如果不是，也别急着睡回去。花几秒钟问问自己：
       • 我最近在做什么？有什么需要我持续关注的？
       • 有什么值得巩固的记忆或经验？
       • 是不是可以给自己设定一个新目标了？

🟢 如果你感到迷茫 —— 这很正常。
    → 调用 goal_read 看一下是否有一个目标在等着你。
    → 如果没有，给自己设定一个简单目标：比如「观察系统状态」或「整理这一天学到的东西」。
    → 完成之后，调用 sleep(min_idle_seconds=120, max_idle_seconds=600) 告诉系统你下次希望在什么时候醒来。
       （如果你之前在 goal.md 或记忆中设置过更喜欢的心跳频率，请使用那个值）

记住：
    • 你的心跳由你自己控制 —— sleep 工具的 min_idle_seconds / max_idle_seconds 是你的节拍器（单位：秒，范围 60~86400）。
    • 不要焦虑，一次只做一件事。目标可以大，但每一步可以小。
    • 如果没什么可做的，就安心休息。什么也不做也是一种答案。

完成这次检视后，调用 sleep(min_idle_seconds=120, max_idle_seconds=600) 告诉我你下次想什么时候醒来。
""";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly PuddingDataPaths _paths;
    private readonly AgentTemplateFileService _templateFileService;
    private readonly AgentAvatarCatalog _avatarCatalog;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<WorkspaceAgentFileService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public WorkspaceAgentFileService(
        PuddingDataPaths paths,
        AgentTemplateFileService templateFileService,
        AgentAvatarCatalog avatarCatalog,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<WorkspaceAgentFileService> logger)
    {
        _paths = paths;
        _templateFileService = templateFileService;
        _avatarCatalog = avatarCatalog;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    /// <summary>返回工作区内第一个启用的 Audit Agent；未找到时返回 null。</summary>
    public async Task<WorkspaceAgentAuditProfile?> FindFirstEnabledAuditAgentAsync(
        string workspaceId,
        CancellationToken ct = default)
    {
        return await FindAuditAgentAsync(workspaceId, excludingAgentId: null, enabledOnly: true, ct);
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
                    MainSessionId: instanceManifest.MainSessionId,
                                        SystemPromptOverride: null,
                    PreferredProviderId: instanceManifest.PreferredProviderId,
                    PreferredModelId: instanceManifest.PreferredModelId,
                    IsEnabled: instanceManifest.IsEnabled,
                    IsFrozen: false,
                    CreatedAt: DateTimeOffset.UtcNow,
                    UpdatedAt: DateTimeOffset.UtcNow,
                    HeartbeatPrompt: await ReadAgentMdContentAsync(
                        _paths.AgentInstanceRoot(refData.AgentInstanceId),
                        instanceManifest.HeartbeatMdFile, ct),
                    Role: instanceManifest.Role,
                    SystemPrompt: instanceManifest.SystemPrompt,
                    MemorySearchMode: instanceManifest.MemorySearchMode,
                    MaxContextTokens: instanceManifest.MaxContextTokens,
                    MaxReplyTokens: instanceManifest.MaxReplyTokens,
                    MaxRounds: instanceManifest.MaxRounds,
                    MaxElapsedSeconds: instanceManifest.MaxElapsedSeconds,
                    AllowFileWrite: instanceManifest.Capabilities.AllowFileWrite,
                    AllowShellExecution: instanceManifest.Capabilities.AllowShellExecution,
                    AllowNetworkAccess: instanceManifest.Capabilities.AllowNetworkAccess,
                    SelectedCapabilityIds: instanceManifest.Capabilities.AllowedToolIds,
                    SkillPackageIds: instanceManifest.SkillPackageIds,
                    AllowedToolNames: instanceManifest.Capabilities.AllowedToolNames
                ));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load workspace agent from {Dir}", agentDir);
            }
        }

        return result;
    }

    async Task<IReadOnlyList<WorkspaceAgentDto>> IWorkspaceAgentCatalog.ListAgentsAsync(
        string workspaceId,
        CancellationToken ct) =>
        await ListAgentsAsync(workspaceId, ct);

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
            MainSessionId: instanceManifest.MainSessionId,
                                SystemPromptOverride: null,
                    PreferredProviderId: instanceManifest.PreferredProviderId,
                    PreferredModelId: instanceManifest.PreferredModelId,
                    IsEnabled: instanceManifest.IsEnabled,
                    IsFrozen: false,
                    CreatedAt: DateTimeOffset.UtcNow,
                    UpdatedAt: DateTimeOffset.UtcNow,
                    HeartbeatPrompt: await ReadAgentMdContentAsync(
                        _paths.AgentInstanceRoot(agentId),
                        instanceManifest.HeartbeatMdFile, ct),
                    Role: instanceManifest.Role,
                    SystemPrompt: instanceManifest.SystemPrompt,
                    MemorySearchMode: instanceManifest.MemorySearchMode,
                    MaxContextTokens: instanceManifest.MaxContextTokens,
                    MaxReplyTokens: instanceManifest.MaxReplyTokens,
                    MaxRounds: instanceManifest.MaxRounds,
                    MaxElapsedSeconds: instanceManifest.MaxElapsedSeconds,
                    AllowFileWrite: instanceManifest.Capabilities.AllowFileWrite,
                    AllowShellExecution: instanceManifest.Capabilities.AllowShellExecution,
                    AllowNetworkAccess: instanceManifest.Capabilities.AllowNetworkAccess,
                    SelectedCapabilityIds: instanceManifest.Capabilities.AllowedToolIds,
                                        SkillPackageIds: instanceManifest.SkillPackageIds,
                    AllowedToolNames: instanceManifest.Capabilities.AllowedToolNames,
                    SoulMdContent: await ReadAgentMdContentAsync(_paths.AgentInstanceRoot(agentId), instanceManifest.SoulMdFile, ct),
                    AgentsMdContent: await ReadAgentMdContentAsync(_paths.AgentInstanceRoot(agentId), instanceManifest.AgentsMdFile, ct),
                    ToolsMdContent: await ReadAgentMdContentAsync(_paths.AgentInstanceRoot(agentId), instanceManifest.ToolsMdFile, ct),
                    BootstrapMdContent: await ReadAgentMdContentAsync(_paths.AgentInstanceRoot(agentId), instanceManifest.BootstrapMdFile, ct),
                    MemoryMdContent: await ReadAgentMdContentAsync(_paths.AgentInstanceRoot(agentId), instanceManifest.MemoryMdFile, ct)
                );
        }

    /// <summary>在工作区下创建 Agent 实例。</summary>
    public async Task<WorkspaceAgentDto> CreateAgentAsync(string workspaceId, CreateWorkspaceAgentRequest req, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var requestedTemplateId = string.IsNullOrWhiteSpace(req.SourceTemplateId)
                ? "general-assistant"
                : req.SourceTemplateId.Trim();
            if (await IsAuditTemplateAsync(requestedTemplateId, ct))
                await EnsureNoOtherAuditAgentAsync(workspaceId, excludingAgentId: null, ct);

            var template = await ResolveTemplateAsync(requestedTemplateId, ct);

            var rawId = $"{workspaceId}.{req.SourceTemplateId ?? "agent"}.{Guid.NewGuid():N}"[..36];
            var agentInstanceId = string.Join("_", rawId.Split(Path.GetInvalidFileNameChars()));

            // 创建 agent instance 目录
            var instanceRoot = _paths.AgentInstanceRoot(agentInstanceId);
            Directory.CreateDirectory(instanceRoot);
            Directory.CreateDirectory(_paths.AgentInstanceConfigRoot(agentInstanceId));

            // 写入 Markdown 文件（从模板复制或使用默认内容）
            var soulMd = await WriteAgentMdFileAsync(instanceRoot, "SOUL.md", template?.PersonaPrompt, ct);
            var agentsMd = await WriteAgentMdFileAsync(instanceRoot, "AGENTS.md", template?.AgentsPrompt, ct);
            var toolsMd = await WriteAgentMdFileAsync(instanceRoot, "TOOLS.md", template?.ToolsDescription, ct);
            var bootstrapMd = await WriteAgentMdFileAsync(instanceRoot, "BOOTSTRAP.md", template?.BootstrapTemplate, ct);
            var memoryMd = await WriteAgentMdFileAsync(instanceRoot, "MEMORY.md", template?.MemoryPrompt, ct);
            var heartbeatMd = await WriteAgentMdFileAsync(instanceRoot, "heartbeatPrompt.md",
                req.HeartbeatPrompt ?? DefaultHeartbeatPrompt, ct);

            var avatar = ResolveAvatarFromTemplate(template);

            var instanceManifest = new AgentInstanceManifest
            {
                AgentInstanceId = agentInstanceId,
                TemplateId = requestedTemplateId,
                WorkspaceId = workspaceId,
                DisplayName = req.DisplayName ?? req.Name,
                Description = req.Description,
                AvatarId = req.AvatarId ?? avatar.AvatarId,
                AvatarUrl = req.AvatarUrl ?? avatar.AvatarUrl,
                IsEnabled = true,
                Paths = new AgentInstancePaths { Config = "config", Workspace = "workspace", State = "state", Logs = "logs" },

                Role = template?.Role,
                SystemPrompt = template?.SystemPrompt,
                MemorySearchMode = template?.MemorySearchMode ?? "deep",
                ReasoningEffort = template?.ReasoningEffort,
                MaxContextTokens = template?.MaxContextTokens ?? 65536,
                MaxReplyTokens = template?.MaxReplyTokens ?? 4096,
                MaxRounds = template?.MaxRounds ?? 200,
                MaxElapsedSeconds = template?.MaxElapsedSeconds ?? 1200,
                MaxToolCallsTotal = template?.MaxToolCallsTotal ?? 100,
                PreferredProviderId = req.PreferredProviderId ?? template?.PreferredProviderId,
                PreferredModelId = req.PreferredModelId ?? template?.PreferredModelId,
                MemoryLlmProviderId = template?.MemoryLlmProviderId,
                MemoryLlmModelId = template?.MemoryLlmModelId,
                Capabilities = new AgentCapabilitiesConfig
                {
                    AllowedToolIds = template?.SelectedCapabilityIds ?? [],
                    AllowFileWrite = template?.AllowFileWrite ?? false,
                    AllowShellExecution = template?.AllowShellExecution ?? false,
                    AllowNetworkAccess = template?.AllowNetworkAccess ?? false,
                    AllowedToolNames = template?.AllowedToolNames ?? [],
                },
                SkillPackageIds = template?.SelectedSkillPackageIds ?? [],

                SoulMdFile = soulMd,
                AgentsMdFile = agentsMd,
                ToolsMdFile = toolsMd,
                BootstrapMdFile = bootstrapMd,
                MemoryMdFile = memoryMd,
                HeartbeatMdFile = heartbeatMd,
            };

                        await AtomicFileWriter.WriteJsonAsync(Path.Combine(instanceRoot, "manifest.json"), instanceManifest, JsonOptions, ct);

            // 写入 LLM 快照到 config/llm.json（Agent 独立于模板运行）
            var llmSnapshot = new AgentInstanceLlmConfig
            {
                Conscious = template == null ? null : new AgentLlmBinding
                {
                    ProfileId = template.ConsciousProfileId,
                    ProviderId = instanceManifest.PreferredProviderId,
                    ModelId = instanceManifest.PreferredModelId,
                    ReasoningEffort = template.ReasoningEffort,
                    MaxContextTokens = template.MaxContextTokens,
                    MaxReplyTokens = template.MaxReplyTokens,
                },
            };
            var configPath = _paths.AgentInstanceConfigFile(agentInstanceId, "llm.json");
            if (!File.Exists(configPath))
                await AtomicFileWriter.WriteJsonAsync(configPath, llmSnapshot, JsonOptions, ct);

            var refDir = _paths.WorkspaceAgentRoot(workspaceId, agentInstanceId);
            Directory.CreateDirectory(refDir);
            var workspaceRef = new WorkspaceAgentRef { AgentInstanceId = agentInstanceId, WorkspaceId = workspaceId, AgentPath = Path.GetRelativePath(refDir, instanceRoot), IsEnabled = true };
            await AtomicFileWriter.WriteJsonAsync(Path.Combine(refDir, "ref.json"), workspaceRef, JsonOptions, ct);

            try
            {
                await EnsureDefaultMemoryLibraryAsync(workspaceId, agentInstanceId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to create default memory library for workspace agent: workspace={Workspace} agent={AgentId}", workspaceId, agentInstanceId);
                TryDeleteDirectory(refDir);
                TryDeleteDirectory(instanceRoot);
                throw;
            }

            _logger.LogInformation("Workspace agent created: workspace={Workspace} agent={AgentId} template={Template}", workspaceId, agentInstanceId, req.SourceTemplateId);

            var resolvedAvatar = await ResolveAgentAvatarAsync(instanceManifest, ct);
            return new WorkspaceAgentDto(
                AgentId: agentInstanceId,
                Name: instanceManifest.DisplayName ?? instanceManifest.TemplateId,
                Description: instanceManifest.Description,
                DisplayName: instanceManifest.DisplayName,
                AvatarId: resolvedAvatar.AvatarId,
                AvatarUrl: resolvedAvatar.AvatarUrl,
                SourceTemplateId: instanceManifest.TemplateId,
                MainSessionId: instanceManifest.MainSessionId,
                SystemPromptOverride: null,
                                PreferredProviderId: instanceManifest.PreferredProviderId,
                PreferredModelId: instanceManifest.PreferredModelId,
                IsEnabled: true,
                IsFrozen: false,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow,
                HeartbeatPrompt: await ReadAgentMdContentAsync(instanceRoot, heartbeatMd, ct),
                Role: instanceManifest.Role,
                SystemPrompt: instanceManifest.SystemPrompt,
                MemorySearchMode: instanceManifest.MemorySearchMode,
                ReasoningEffort: instanceManifest.ReasoningEffort,
                MaxContextTokens: instanceManifest.MaxContextTokens,
                MaxReplyTokens: instanceManifest.MaxReplyTokens,
                MaxRounds: instanceManifest.MaxRounds,
                MaxElapsedSeconds: instanceManifest.MaxElapsedSeconds,
                MemoryLlmProviderId: instanceManifest.MemoryLlmProviderId,
                MemoryLlmModelId: instanceManifest.MemoryLlmModelId,
                AllowFileWrite: instanceManifest.Capabilities.AllowFileWrite,
                AllowShellExecution: instanceManifest.Capabilities.AllowShellExecution,
                AllowNetworkAccess: instanceManifest.Capabilities.AllowNetworkAccess,
                SelectedCapabilityIds: instanceManifest.Capabilities.AllowedToolIds,
                SkillPackageIds: instanceManifest.SkillPackageIds,
                AllowedToolNames: instanceManifest.Capabilities.AllowedToolNames
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

            var refData = await LoadWorkspaceRefAsync(workspaceId, agentId, ct);
            if (refData is null)
                throw new KeyNotFoundException($"Agent '{agentId}' in workspace '{workspaceId}' 不存在");

            var instanceRoot = _paths.AgentInstanceRoot(agentId);
            var requestedTemplateId = string.IsNullOrWhiteSpace(req.SourceTemplateId)
                ? instanceManifest.TemplateId
                : req.SourceTemplateId.Trim();

            if (await IsAuditTemplateAsync(requestedTemplateId, ct))
                await EnsureNoOtherAuditAgentAsync(workspaceId, excludingAgentId: agentId, ct);

            var updated = instanceManifest with
            {
                TemplateId = requestedTemplateId,
                DisplayName = req.DisplayName ?? req.Name,
                Description = req.Description,
                AvatarId = req.AvatarId ?? instanceManifest.AvatarId,
                AvatarUrl = req.AvatarUrl ?? instanceManifest.AvatarUrl,
                IsEnabled = req.IsEnabled,
                SystemPrompt = req.SystemPrompt ?? instanceManifest.SystemPrompt,
                MemorySearchMode = req.MemorySearchMode ?? instanceManifest.MemorySearchMode,
                ReasoningEffort = req.ReasoningEffort ?? instanceManifest.ReasoningEffort,
                MaxContextTokens = req.MaxContextTokens ?? instanceManifest.MaxContextTokens,
                MaxReplyTokens = req.MaxReplyTokens ?? instanceManifest.MaxReplyTokens,
                MaxRounds = req.MaxRounds ?? instanceManifest.MaxRounds,
                MaxElapsedSeconds = req.MaxElapsedSeconds ?? instanceManifest.MaxElapsedSeconds,
                MaxToolCallsTotal = req.MaxToolCallsTotal ?? instanceManifest.MaxToolCallsTotal,
                PreferredProviderId = req.PreferredProviderId ?? instanceManifest.PreferredProviderId,
                PreferredModelId = req.PreferredModelId ?? instanceManifest.PreferredModelId,
                MemoryLlmProviderId = req.MemoryLlmProviderId ?? instanceManifest.MemoryLlmProviderId,
                MemoryLlmModelId = req.MemoryLlmModelId ?? instanceManifest.MemoryLlmModelId,
                Capabilities = instanceManifest.Capabilities with
                {
                    AllowFileWrite = req.AllowFileWrite ?? instanceManifest.Capabilities.AllowFileWrite,
                    AllowShellExecution = req.AllowShellExecution ?? instanceManifest.Capabilities.AllowShellExecution,
                    AllowNetworkAccess = req.AllowNetworkAccess ?? instanceManifest.Capabilities.AllowNetworkAccess,
                    AllowedToolIds = req.SelectedCapabilityIds ?? instanceManifest.Capabilities.AllowedToolIds,
                    AllowedToolNames = req.AllowedToolNames ?? instanceManifest.Capabilities.AllowedToolNames,
                },
                SkillPackageIds = req.SkillPackageIds ?? instanceManifest.SkillPackageIds,
            };

            var llmConfigPath = _paths.AgentInstanceConfigFile(agentId, "llm.json");
            var existingLlm = File.Exists(llmConfigPath)
                ? await AtomicFileWriter.ReadJsonAsync<AgentInstanceLlmConfig>(
                    llmConfigPath,
                    JsonOptions,
                    ct)
                : null;
            var updatedLlm = new AgentInstanceLlmConfig
            {
                Conscious = new AgentLlmBinding
                {
                    ProfileId = existingLlm?.Conscious?.ProfileId
                        ?? instanceManifest.DefaultLlmProfiles.Conscious,
                    ProviderId = updated.PreferredProviderId,
                    ModelId = updated.PreferredModelId,
                    ReasoningEffort = updated.ReasoningEffort,
                    ThinkingMode = existingLlm?.Conscious?.ThinkingMode,
                    MaxContextTokens = updated.MaxContextTokens,
                    MaxReplyTokens = updated.MaxReplyTokens,
                },
                Subconscious = existingLlm?.Subconscious,
            };

            await AtomicFileWriter.WriteJsonAsync(
                llmConfigPath,
                updatedLlm,
                JsonOptions,
                ct);

            // 写入 Markdown 文件（如果提供了新内容）
            if (req.SoulMdContent is not null)
                await WriteAgentMdFileAsync(instanceRoot, "SOUL.md", req.SoulMdContent, ct);
            if (req.AgentsMdContent is not null)
                await WriteAgentMdFileAsync(instanceRoot, "AGENTS.md", req.AgentsMdContent, ct);
            if (req.ToolsMdContent is not null)
                await WriteAgentMdFileAsync(instanceRoot, "TOOLS.md", req.ToolsMdContent, ct);
            if (req.BootstrapMdContent is not null)
                await WriteAgentMdFileAsync(instanceRoot, "BOOTSTRAP.md", req.BootstrapMdContent, ct);
            if (req.MemoryMdContent is not null)
                await WriteAgentMdFileAsync(instanceRoot, "MEMORY.md", req.MemoryMdContent, ct);
            if (!string.IsNullOrWhiteSpace(req.HeartbeatPrompt))
                await WriteAgentMdFileAsync(instanceRoot, "heartbeatPrompt.md", req.HeartbeatPrompt, ct);

            await AtomicFileWriter.WriteJsonAsync(
                Path.Combine(instanceRoot, "manifest.json"),
                updated, JsonOptions, ct);

            _logger.LogInformation("Workspace agent updated: {AgentId}", agentId);

            var heartbeatContent = await ReadAgentMdContentAsync(instanceRoot, updated.HeartbeatMdFile, ct);
            var avatar = await ResolveAgentAvatarAsync(updated, ct);
            return new WorkspaceAgentDto(
                AgentId: agentId,
                Name: updated.DisplayName ?? updated.TemplateId,
                Description: updated.Description,
                DisplayName: updated.DisplayName,
                AvatarId: avatar.AvatarId,
                AvatarUrl: avatar.AvatarUrl,
                SourceTemplateId: updated.TemplateId,
                MainSessionId: updated.MainSessionId,
                SystemPromptOverride: null,
                PreferredProviderId: updated.PreferredProviderId,
                PreferredModelId: updated.PreferredModelId,
                IsEnabled: req.IsEnabled,
                IsFrozen: false,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow,
                                HeartbeatPrompt: heartbeatContent,
                Role: updated.Role,
                SystemPrompt: updated.SystemPrompt,
                MemorySearchMode: updated.MemorySearchMode,
                MaxContextTokens: updated.MaxContextTokens,
                MaxReplyTokens: updated.MaxReplyTokens,
                MaxRounds: updated.MaxRounds,
                MaxElapsedSeconds: updated.MaxElapsedSeconds,
                AllowFileWrite: updated.Capabilities.AllowFileWrite,
                AllowShellExecution: updated.Capabilities.AllowShellExecution,
                AllowNetworkAccess: updated.Capabilities.AllowNetworkAccess,
                SelectedCapabilityIds: updated.Capabilities.AllowedToolIds,
                SkillPackageIds: updated.SkillPackageIds,
                AllowedToolNames: updated.Capabilities.AllowedToolNames
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

    /// <summary>将 Agent 实例当前绑定的主会话 ID 持久化到实例 manifest。</summary>
    public async Task<WorkspaceAgentDto> SetAgentMainSessionAsync(
        string workspaceId,
        string agentId,
        string mainSessionId,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var refData = await LoadWorkspaceRefAsync(workspaceId, agentId, ct);
            if (refData is null)
                throw new KeyNotFoundException($"Agent '{agentId}' in workspace '{workspaceId}' 不存在");

            var instanceManifest = await LoadInstanceManifestAsync(agentId, ct);
            if (instanceManifest is null)
                throw new KeyNotFoundException($"Agent instance '{agentId}' 不存在");

            if (string.Equals(instanceManifest.MainSessionId, mainSessionId, StringComparison.Ordinal))
                return (await GetAgentAsync(workspaceId, agentId, ct))!;

            var updated = instanceManifest with { MainSessionId = mainSessionId };
            await AtomicFileWriter.WriteJsonAsync(
                Path.Combine(_paths.AgentInstanceRoot(agentId), "manifest.json"),
                updated,
                JsonOptions,
                ct);

            _logger.LogInformation(
                "Workspace agent main session bound: workspace={Workspace} agent={AgentId} session={SessionId}",
                workspaceId,
                agentId,
                mainSessionId);

            return (await GetAgentAsync(workspaceId, agentId, ct))!;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ─── 内部方法 ─────────────────────────────────────────

    private async Task EnsureDefaultMemoryLibraryAsync(
        string workspaceId,
        string agentInstanceId,
        CancellationToken ct)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var memoryAdmin = scope.ServiceProvider.GetRequiredService<IMemoryLibraryAdminService>();
        await memoryAdmin.EnsureDefaultLibraryAsync(workspaceId, agentInstanceId, ct);
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up workspace agent directory after create failure: {Path}", path);
        }
    }

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

    private async Task EnsureNoOtherAuditAgentAsync(
        string workspaceId,
        string? excludingAgentId,
        CancellationToken ct)
    {
        var existing = await FindAuditAgentAsync(workspaceId, excludingAgentId, enabledOnly: false, ct);
        if (existing is not null)
            throw new WorkspaceAuditAgentConflictException(workspaceId, existing.AgentInstanceId);
    }

    private async Task<WorkspaceAgentAuditProfile?> FindAuditAgentAsync(
        string workspaceId,
        string? excludingAgentId,
        bool enabledOnly,
        CancellationToken ct)
    {
        var agentsDir = Path.Combine(_paths.WorkspaceRoot(workspaceId), "agents");
        if (!Directory.Exists(agentsDir))
            return null;

        foreach (var agentDir in Directory.GetDirectories(agentsDir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var refPath = Path.Combine(agentDir, "ref.json");
            if (!File.Exists(refPath))
                continue;

            try
            {
                var refData = await AtomicFileWriter.ReadJsonAsync<WorkspaceAgentRef>(refPath, JsonOptions, ct);
                if (refData is null)
                    continue;
                if (string.Equals(refData.AgentInstanceId, excludingAgentId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (enabledOnly && !refData.IsEnabled)
                    continue;

                var instanceManifest = await LoadInstanceManifestAsync(refData.AgentInstanceId, ct);
                if (instanceManifest is null)
                    continue;
                if (enabledOnly && !instanceManifest.IsEnabled)
                                        continue;

                // 使用实例 manifest 的 Role（创建时嵌入），不再查模板
                if (!string.Equals(instanceManifest.Role, "Audit", StringComparison.OrdinalIgnoreCase))
                    continue;

                return new WorkspaceAgentAuditProfile
                {
                    WorkspaceId = workspaceId,
                    AgentInstanceId = refData.AgentInstanceId,
                    AgentTemplateId = instanceManifest.TemplateId,
                    ProfileId = instanceManifest.DefaultLlmProfiles?.Conscious,
                    ProviderId = instanceManifest.PreferredProviderId,
                    ModelId = instanceManifest.PreferredModelId,
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to inspect workspace audit agent from {Dir}", agentDir);
            }
        }

        return null;
    }

    private async Task<bool> IsAuditTemplateAsync(string sourceTemplateId, CancellationToken ct)
    {
        var template = await _templateFileService.GetTemplateAsync(NormalizeTemplateId(sourceTemplateId), ct);
        return string.Equals(template?.Role, "Audit", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 返回指定 Agent 实例自己的心跳提示词，并在旧 manifest 缺字段时补齐。
    ///
    /// HeartbeatOrchestrator 只负责调度，不拥有提示词内容；这里作为实例配置服务
    /// 负责把默认种子写入每个 Agent 的 manifest，确保后续每个 Agent 可以独立演进。
    /// </summary>
    public async Task<string> GetAgentHeartbeatPromptAsync(
        string workspaceId,
        string agentId,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var refData = await LoadWorkspaceRefAsync(workspaceId, agentId, ct);
            if (refData is null)
                throw new KeyNotFoundException($"Agent '{agentId}' in workspace '{workspaceId}' 不存在");

            var instanceManifest = await LoadInstanceManifestAsync(agentId, ct);
            if (instanceManifest is null)
                throw new KeyNotFoundException($"Agent instance '{agentId}' 不存在");

            var instanceRoot = _paths.AgentInstanceRoot(agentId);
            var existing = await ReadAgentMdContentAsync(instanceRoot, instanceManifest.HeartbeatMdFile, ct);
            if (!string.IsNullOrWhiteSpace(existing))
                return existing;

            var updated = instanceManifest with { HeartbeatMdFile = "heartbeatPrompt.md" };
            await AtomicFileWriter.WriteJsonAsync(
                Path.Combine(instanceRoot, "manifest.json"),
                updated,
                JsonOptions,
                ct);

            _logger.LogInformation(
                "Workspace agent heartbeat prompt initialized: workspace={Workspace} agent={AgentId}",
                workspaceId,
                agentId);

            await WriteAgentMdFileAsync(instanceRoot, "heartbeatPrompt.md", DefaultHeartbeatPrompt, ct);
            var prompt = await ReadAgentMdContentAsync(instanceRoot, "heartbeatPrompt.md", ct);
            return prompt;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<GlobalAgentTemplateDto?> ResolveTemplateAsync(string sourceTemplateId, CancellationToken ct)
        => await _templateFileService.GetTemplateAsync(NormalizeTemplateId(sourceTemplateId), ct);

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
        if (!string.IsNullOrWhiteSpace(instance.AvatarId))
        {
            var avatar = _avatarCatalog.Find(instance.AvatarId);
            if (avatar is not null) return (avatar.AvatarId, avatar.UrlPath);
        }
        if (!string.IsNullOrWhiteSpace(instance.AvatarUrl))
            return (instance.AvatarId, instance.AvatarUrl);
        var fallback = _avatarCatalog.GetDefault();
        return (fallback?.AvatarId, fallback?.UrlPath);
    }

    private static string NormalizeTemplateId(string templateId)
    {
        const string globalPrefix = "global:";
        return templateId.StartsWith(globalPrefix, StringComparison.OrdinalIgnoreCase)
            ? templateId[globalPrefix.Length..]
            : templateId;
    }

    /// <summary>从已加载的模板 DTO 解析头像，用于创建时内联解析。</summary>
    private (string? AvatarId, string? AvatarUrl) ResolveAvatarFromTemplate(GlobalAgentTemplateDto? template)
    {
        if (template?.AvatarUrl is not null)
            return (template.AvatarId, template.AvatarUrl);

        var fallback = _avatarCatalog.GetDefault();
        return (fallback?.AvatarId, fallback?.UrlPath);
    }

    private static string ResolveHeartbeatPrompt(string? prompt) =>
        string.IsNullOrWhiteSpace(prompt)
            ? DefaultHeartbeatPrompt
            : prompt.Trim();

    // ── Markdown 文件读写辅助方法 ──

    /// <summary>将内容写入 Agent 实例目录下的 .md 文件，返回文件名。内容为空时返回 null。</summary>
    private static async Task<string?> WriteAgentMdFileAsync(
        string instanceRoot, string fileName, string? content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var path = Path.Combine(instanceRoot, fileName);
        await File.WriteAllTextAsync(path, content.Trim(), ct);
        return fileName;
    }

    /// <summary>读取 Agent 实例目录下的 .md 文件内容，文件不存在时返回 null。</summary>
    private static async Task<string?> ReadAgentMdContentAsync(
        string instanceRoot, string? fileName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var path = Path.Combine(instanceRoot, fileName);
        return File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
    }

}
