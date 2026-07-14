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
                    PreferredProviderId: null,
                    PreferredModelId: null,
                    IsEnabled: instanceManifest.IsEnabled,
                    IsFrozen: false,
                    CreatedAt: DateTimeOffset.UtcNow,
                    UpdatedAt: DateTimeOffset.UtcNow,
                    HeartbeatPrompt: ResolveHeartbeatPrompt(instanceManifest.HeartbeatPrompt)
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
            PreferredProviderId: null,
            PreferredModelId: null,
            IsEnabled: instanceManifest.IsEnabled,
            IsFrozen: false,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            HeartbeatPrompt: ResolveHeartbeatPrompt(instanceManifest.HeartbeatPrompt)
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
                TemplateId = requestedTemplateId,
                WorkspaceId = workspaceId,
                DisplayName = req.DisplayName ?? req.Name,
                Description = req.Description,
                AvatarId = req.AvatarId,
                AvatarUrl = req.AvatarUrl,
                HeartbeatPrompt = ResolveHeartbeatPrompt(req.HeartbeatPrompt),
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

            try
            {
                await EnsureDefaultMemoryLibraryAsync(workspaceId, agentInstanceId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    ex,
                    "Failed to create default memory library for workspace agent: workspace={Workspace} agent={AgentId}",
                    workspaceId,
                    agentInstanceId);
                TryDeleteDirectory(refDir);
                TryDeleteDirectory(instanceRoot);
                throw;
            }

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
                MainSessionId: instanceManifest.MainSessionId,
                SystemPromptOverride: null,
                PreferredProviderId: null,
                PreferredModelId: null,
                IsEnabled: true,
                IsFrozen: false,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow,
                HeartbeatPrompt: ResolveHeartbeatPrompt(instanceManifest.HeartbeatPrompt)
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
                HeartbeatPrompt = req.HeartbeatPrompt is null
                    ? instanceManifest.HeartbeatPrompt
                    : ResolveHeartbeatPrompt(req.HeartbeatPrompt),
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
                MainSessionId: updated.MainSessionId,
                SystemPromptOverride: null,
                PreferredProviderId: null,
                PreferredModelId: null,
                IsEnabled: req.IsEnabled,
                IsFrozen: false,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow,
                HeartbeatPrompt: ResolveHeartbeatPrompt(updated.HeartbeatPrompt)
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

                var templateId = NormalizeTemplateId(instanceManifest.TemplateId);
                var template = await _templateFileService.GetTemplateAsync(templateId, ct);
                if (template is null
                    || (enabledOnly && !template.IsEnabled)
                    || !string.Equals(template.Role, "Audit", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return new WorkspaceAgentAuditProfile
                {
                    WorkspaceId = workspaceId,
                    AgentInstanceId = refData.AgentInstanceId,
                    AgentTemplateId = template.TemplateId,
                    ProfileId = template.ConsciousProfileId,
                    ProviderId = template.PreferredProviderId,
                    ModelId = template.PreferredModelId,
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

            var prompt = ResolveHeartbeatPrompt(instanceManifest.HeartbeatPrompt);
            if (!string.IsNullOrWhiteSpace(instanceManifest.HeartbeatPrompt))
                return prompt;

            var updated = instanceManifest with { HeartbeatPrompt = prompt };
            await AtomicFileWriter.WriteJsonAsync(
                Path.Combine(_paths.AgentInstanceRoot(agentId), "manifest.json"),
                updated,
                JsonOptions,
                ct);

            _logger.LogInformation(
                "Workspace agent heartbeat prompt initialized: workspace={Workspace} agent={AgentId}",
                workspaceId,
                agentId);

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
        // 1. 实例级 AvatarId
        if (!string.IsNullOrWhiteSpace(instance.AvatarId))
        {
            var avatar = await _avatarCatalog.GetRequiredEnabledAsync(instance.AvatarId);
            if (avatar is not null) return (avatar.AvatarId, avatar.UrlPath);
        }

        // 2. 模板级头像
        var template = await _templateFileService.GetTemplateAsync(NormalizeTemplateId(instance.TemplateId), ct);
        if (template?.AvatarUrl is not null)
            return (template.AvatarId, template.AvatarUrl);

        // 3. 实例级 legacy Url
        if (!string.IsNullOrWhiteSpace(instance.AvatarUrl))
            return (instance.AvatarId, instance.AvatarUrl);

        // 4. 默认启用头像
        var fallback = await _avatarCatalog.GetDefaultAsync();
        return (fallback?.AvatarId, fallback?.UrlPath);
    }

    private static string NormalizeTemplateId(string templateId)
    {
        const string globalPrefix = "global:";
        return templateId.StartsWith(globalPrefix, StringComparison.OrdinalIgnoreCase)
            ? templateId[globalPrefix.Length..]
            : templateId;
    }

    private static string ResolveHeartbeatPrompt(string? prompt) =>
        string.IsNullOrWhiteSpace(prompt)
            ? DefaultHeartbeatPrompt
            : prompt.Trim();

}
