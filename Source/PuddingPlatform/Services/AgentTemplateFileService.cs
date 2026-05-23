using System.Text.Json;
using PuddingCode.Agents;
using PuddingCode.Configuration;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// 文件式 Agent 模板管理服务 — 读写 data/agent-templates/{templateId}/。
/// 唯一事实来源：agent-templates 目录下的 manifest.json + Markdown 文件。
/// </summary>
public sealed class AgentTemplateFileService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly PuddingDataPaths _paths;
    private readonly AgentAvatarCatalog _avatarCatalog;
    private readonly ILogger<AgentTemplateFileService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public AgentTemplateFileService(
        PuddingDataPaths paths,
        AgentAvatarCatalog avatarCatalog,
        ILogger<AgentTemplateFileService> logger)
    {
        _paths = paths;
        _avatarCatalog = avatarCatalog;
        _logger = logger;
    }

    /// <summary>获取所有全局 Agent 模板列表。</summary>
    public async Task<List<GlobalAgentTemplateDto>> ListTemplatesAsync(bool? enabledOnly = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(_paths.AgentTemplatesRoot))
            return [];

        var templateDirs = Directory.GetDirectories(_paths.AgentTemplatesRoot);
        var result = new List<GlobalAgentTemplateDto>();
        var idx = 1;

        foreach (var dir in templateDirs)
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath)) continue;

            try
            {
                var manifest = await AtomicFileWriter.ReadJsonAsync<AgentTemplateManifest>(manifestPath, JsonOptions, ct);
                if (manifest is null) continue;

                if (enabledOnly == true && !manifest.IsEnabled) continue;

                result.Add(await MapToDtoAsync(idx++, manifest, dir, ct));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load agent template from {Dir}", dir);
            }
        }

        return result;
    }

    /// <summary>获取单个模板详情。</summary>
    public async Task<GlobalAgentTemplateDto?> GetTemplateAsync(string templateId, CancellationToken ct = default)
    {
        var manifest = await LoadManifestAsync(templateId, ct);
        if (manifest is null) return null;

        var templateDir = _paths.AgentTemplateRoot(templateId);
        var allTemplates = await ListTemplatesAsync(ct: ct);
        var idx = allTemplates.Count + 1;
        return await MapToDtoAsync(idx, manifest, templateDir, ct);
    }

    /// <summary>创建模板。</summary>
    public async Task<GlobalAgentTemplateDto> CreateTemplateAsync(UpsertGlobalAgentTemplateRequest req, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var existing = await LoadManifestAsync(req.TemplateId, ct);
            if (existing is not null)
                throw new InvalidOperationException($"TemplateId '{req.TemplateId}' 已存在");

            var templateDir = _paths.AgentTemplateRoot(req.TemplateId);
            Directory.CreateDirectory(templateDir);

            var manifest = new AgentTemplateManifest
            {
                TemplateId = req.TemplateId,
                Name = req.Name,
                Description = req.Description,
                Role = req.Role,
                DefaultLlmProfiles = new AgentDefaultLlmProfiles
                {
                    Conscious = req.ConsciousProfileId,
                    Subconscious = req.SubconsciousProfileId,
                },
                MemorySearchMode = req.MemorySearchMode ?? "deep",
                ReasoningEffort = req.ReasoningEffort,
                MaxContextTokens = req.MaxContextTokens,
                MaxReplyTokens = req.MaxReplyTokens,
                IsBuiltIn = false,
                IsEnabled = req.IsEnabled,
                AvatarId = req.AvatarId,
                PreferredProviderId = req.PreferredProviderId,
                PreferredModelId = req.PreferredModelId,
                MemoryLlmProviderId = req.MemoryLlmProviderId,
                MemoryLlmModelId = req.MemoryLlmModelId,
                Capabilities = new AgentCapabilitiesConfig
                {
                    AllowTools = true,
                    AllowedToolIds = req.SelectedCapabilityIds ?? [],
                },
            };

            await AtomicFileWriter.WriteJsonAsync(Path.Combine(templateDir, "manifest.json"), manifest, JsonOptions, ct);

            // 写入 Markdown 文件（如果有值）
            if (!string.IsNullOrWhiteSpace(req.PersonaPrompt))
                await File.WriteAllTextAsync(Path.Combine(templateDir, "SOUL.md"), req.PersonaPrompt, ct);
            if (!string.IsNullOrWhiteSpace(req.ToolsDescription))
                await File.WriteAllTextAsync(Path.Combine(templateDir, "TOOLS.md"), req.ToolsDescription, ct);
            if (!string.IsNullOrWhiteSpace(req.BootstrapTemplate))
                await File.WriteAllTextAsync(Path.Combine(templateDir, "BOOTSTRAP.md"), req.BootstrapTemplate, ct);

            _logger.LogInformation("Agent template created: {TemplateId}", req.TemplateId);

            var allTemplates = await ListTemplatesAsync(ct: ct);
            return await MapToDtoAsync(allTemplates.Count + 1, manifest, templateDir, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>更新模板。</summary>
    public async Task<GlobalAgentTemplateDto> UpdateTemplateAsync(string templateId, UpsertGlobalAgentTemplateRequest req, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var templateDir = _paths.AgentTemplateRoot(templateId);
            var manifest = await LoadManifestAsync(templateId, ct);
            if (manifest is null)
                throw new KeyNotFoundException($"Template '{templateId}' 不存在");

            if (!Directory.Exists(templateDir))
                Directory.CreateDirectory(templateDir);

            var updated = manifest with
            {
                Name = req.Name,
                Description = req.Description,
                Role = req.Role,
                MaxContextTokens = req.MaxContextTokens,
                MaxReplyTokens = req.MaxReplyTokens,
                IsEnabled = req.IsEnabled,
                MemorySearchMode = req.MemorySearchMode ?? manifest.MemorySearchMode,
                ReasoningEffort = req.ReasoningEffort ?? manifest.ReasoningEffort,
                AvatarId = req.AvatarId ?? manifest.AvatarId,
                PreferredProviderId = req.PreferredProviderId,
                PreferredModelId = req.PreferredModelId,
                MemoryLlmProviderId = req.MemoryLlmProviderId,
                MemoryLlmModelId = req.MemoryLlmModelId,
                DefaultLlmProfiles = new AgentDefaultLlmProfiles
                {
                    Conscious = req.ConsciousProfileId ?? manifest.DefaultLlmProfiles.Conscious,
                    Subconscious = req.SubconsciousProfileId ?? manifest.DefaultLlmProfiles.Subconscious,
                },
            };

            await AtomicFileWriter.WriteJsonAsync(Path.Combine(templateDir, "manifest.json"), updated, JsonOptions, ct);

            // 更新 Markdown（如果提供了新值）
            if (req.PersonaPrompt is not null)
                await File.WriteAllTextAsync(Path.Combine(templateDir, "SOUL.md"), req.PersonaPrompt, ct);
            if (req.ToolsDescription is not null)
                await File.WriteAllTextAsync(Path.Combine(templateDir, "TOOLS.md"), req.ToolsDescription, ct);
            if (req.BootstrapTemplate is not null)
                await File.WriteAllTextAsync(Path.Combine(templateDir, "BOOTSTRAP.md"), req.BootstrapTemplate, ct);

            _logger.LogInformation("Agent template updated: {TemplateId}", templateId);

            return await MapToDtoAsync(1, updated, templateDir, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>删除模板。</summary>
    public async Task DeleteTemplateAsync(string templateId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var manifest = await LoadManifestAsync(templateId, ct);
            if (manifest is null)
                throw new KeyNotFoundException($"Template '{templateId}' 不存在");

            if (manifest.IsBuiltIn)
                throw new InvalidOperationException("系统内置模板不允许删除");

            var templateDir = _paths.AgentTemplateRoot(templateId);
            if (Directory.Exists(templateDir))
                Directory.Delete(templateDir, recursive: true);

            _logger.LogInformation("Agent template deleted: {TemplateId}", templateId);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ─── 内部方法 ─────────────────────────────────────────

    private async Task<AgentTemplateManifest?> LoadManifestAsync(string templateId, CancellationToken ct)
    {
        var path = _paths.AgentTemplateFile(templateId, "manifest.json");
        if (!File.Exists(path)) return null;

        return await AtomicFileWriter.ReadJsonAsync<AgentTemplateManifest>(path, JsonOptions, ct);
    }

    private async Task<GlobalAgentTemplateDto> MapToDtoAsync(int id, AgentTemplateManifest m, string templateDir, CancellationToken ct)
    {
        var soulPath = Path.Combine(templateDir, "SOUL.md");
        var toolsPath = Path.Combine(templateDir, "TOOLS.md");
        var bootstrapPath = Path.Combine(templateDir, "BOOTSTRAP.md");

        // 解析头像：优先 manifest.avatarId → manifest 内嵌的 legacy fallback → 默认头像
        AgentAvatarEntity? avatar = null;
        if (!string.IsNullOrWhiteSpace(m.AvatarId))
            avatar = await _avatarCatalog.GetRequiredEnabledAsync(m.AvatarId);
        avatar ??= await _avatarCatalog.GetDefaultAsync();

        return new GlobalAgentTemplateDto(
            Id: id,
            TemplateId: m.TemplateId,
            Name: m.Name,
            Description: m.Description,
            Role: m.Role,
            SystemPrompt: null,
            UserPromptTemplate: null,
            PreferredProviderId: m.PreferredProviderId,
            PreferredModelId: m.PreferredModelId,
            MaxContextTokens: m.MaxContextTokens,
            MaxReplyTokens: m.MaxReplyTokens,
            ContainerImage: null,
            SelectedCapabilityIds: m.Capabilities.AllowedToolIds,
            SelectedSkillPackageIds: [],
            IsBuiltIn: m.IsBuiltIn,
            IsEnabled: m.IsEnabled,
            SortOrder: 0,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            PersonaPrompt: File.Exists(soulPath) ? await File.ReadAllTextAsync(soulPath, ct) : null,
            ToolsDescription: File.Exists(toolsPath) ? await File.ReadAllTextAsync(toolsPath, ct) : null,
            BootstrapTemplate: File.Exists(bootstrapPath) ? await File.ReadAllTextAsync(bootstrapPath, ct) : null,
            AvatarEmoji: null,
            AvatarId: avatar?.AvatarId ?? m.AvatarId,
            AvatarUrl: avatar?.UrlPath,
            AvatarName: avatar?.Name,
            MemoryLlmProviderId: m.MemoryLlmProviderId,
            MemoryLlmModelId: m.MemoryLlmModelId,
            MemorySearchMode: m.MemorySearchMode,
            ReasoningEffort: m.ReasoningEffort,
            MaxRounds: 200,
            MaxElapsedSeconds: 1200,
            MaxToolCallsTotal: 100,
            ConsciousProfileId: m.DefaultLlmProfiles.Conscious,
            SubconsciousProfileId: m.DefaultLlmProfiles.Subconscious
        );
    }
}
