using System.Text.Json;
using PuddingCode.Agents;
using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingPlatform.Data.Dtos;

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
    private readonly string _presetTemplatesRoot;
    private readonly AgentAvatarCatalog _avatarCatalog;
    private readonly ILogger<AgentTemplateFileService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public AgentTemplateFileService(
        PuddingDataPaths paths,
        AgentAvatarCatalog avatarCatalog,
        ILogger<AgentTemplateFileService> logger,
        string? presetTemplatesRoot = null)
    {
        _paths = paths;
        _presetTemplatesRoot = string.IsNullOrWhiteSpace(presetTemplatesRoot)
            ? Path.Combine(AppContext.BaseDirectory, "default-data", "agent-template-presets")
            : presetTemplatesRoot;
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

        return result
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>获取软件输出目录 default-data/agent-template-presets/*.json 中声明的系统预制模板。</summary>
    public async Task<List<GlobalAgentTemplateDto>> ListPresetTemplatesAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_presetTemplatesRoot))
            return [];

        var result = new List<GlobalAgentTemplateDto>();
        var idx = 1;

        foreach (var file in Directory.GetFiles(_presetTemplatesRoot, "*.json"))
        {
            try
            {
                var preset = await AtomicFileWriter.ReadJsonAsync<AgentTemplatePreset>(file, JsonOptions, ct);
                if (preset is null) continue;

                result.Add(await MapPresetToDtoAsync(idx++, NormalizePreset(preset, Path.GetFileNameWithoutExtension(file)), ct));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load agent template preset from {File}", file);
            }
        }

        return result
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>将系统预制模板导入为正式全局模板。</summary>
    public async Task<GlobalAgentTemplateDto> ImportPresetTemplateAsync(string templateId, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var preset = await LoadPresetAsync(templateId, ct)
                ?? throw new KeyNotFoundException($"Preset template '{templateId}' 不存在");

            var normalized = NormalizePreset(preset, templateId);
            var existing = await LoadManifestAsync(normalized.TemplateId, ct);
            if (existing is not null)
                throw new InvalidOperationException($"TemplateId '{normalized.TemplateId}' 已存在");

            var templateDir = _paths.AgentTemplateRoot(normalized.TemplateId);
            Directory.CreateDirectory(templateDir);

            var now = DateTimeOffset.UtcNow;
            var manifest = new AgentTemplateManifest
            {
                CreatedAt = now,
                UpdatedAt = now,
                TemplateId = normalized.TemplateId,
                Name = normalized.Name,
                Description = normalized.Description,
                Role = normalized.Role,
                DefaultLlmProfiles = new AgentDefaultLlmProfiles
                {
                    Conscious = normalized.ConsciousProfileId,
                    Subconscious = normalized.SubconsciousProfileId,
                },
                MemorySearchMode = normalized.MemorySearchMode,
                ReasoningEffort = normalized.ReasoningEffort,
                SystemPrompt = normalized.SystemPrompt,
                UserPromptTemplate = normalized.UserPromptTemplate,
                MaxContextTokens = normalized.MaxContextTokens,
                MaxReplyTokens = normalized.MaxReplyTokens,
                MaxRounds = normalized.MaxRounds,
                MaxElapsedSeconds = normalized.MaxElapsedSeconds,
                MaxToolCallsTotal = normalized.MaxToolCallsTotal,
                ContainerImage = normalized.ContainerImage,
                IsBuiltIn = true,
                IsEnabled = normalized.IsEnabled,
                SortOrder = normalized.SortOrder,
                AvatarId = normalized.AvatarId,
                PreferredProviderId = normalized.PreferredProviderId,
                PreferredModelId = normalized.PreferredModelId,
                MemoryLlmProviderId = normalized.MemoryLlmProviderId,
                MemoryLlmModelId = normalized.MemoryLlmModelId,
                SkillPackageIds = normalized.SelectedSkillPackageIds,
                Capabilities = new AgentCapabilitiesConfig
                {
                    AllowTools = true,
                    AllowedToolIds = normalized.SelectedCapabilityIds,
                },
            };

            await AtomicFileWriter.WriteJsonAsync(Path.Combine(templateDir, "manifest.json"), manifest, JsonOptions, ct);
            await WritePresetMarkdownFilesAsync(templateDir, normalized, ct);

            _logger.LogInformation("Agent template preset imported: {TemplateId}", normalized.TemplateId);
            return await MapToDtoAsync(1, manifest, templateDir, ct);
        }
        finally
        {
            _writeLock.Release();
        }
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

            var now = DateTimeOffset.UtcNow;
            var manifest = new AgentTemplateManifest
            {
                CreatedAt = now,
                UpdatedAt = now,
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
                SystemPrompt = req.SystemPrompt,
                UserPromptTemplate = req.UserPromptTemplate,
                MaxContextTokens = req.MaxContextTokens,
                MaxReplyTokens = req.MaxReplyTokens,
                MaxRounds = req.MaxRounds ?? 200,
                MaxElapsedSeconds = req.MaxElapsedSeconds ?? 1200,
                MaxToolCallsTotal = req.MaxToolCallsTotal ?? 100,
                ContainerImage = req.ContainerImage,
                IsBuiltIn = false,
                IsEnabled = req.IsEnabled,
                SortOrder = req.SortOrder,
                AvatarId = req.AvatarId,
                PreferredProviderId = req.PreferredProviderId,
                PreferredModelId = req.PreferredModelId,
                MemoryLlmProviderId = req.MemoryLlmProviderId,
                MemoryLlmModelId = req.MemoryLlmModelId,
                SkillPackageIds = req.SelectedSkillPackageIds ?? [],
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
            if (!string.IsNullOrWhiteSpace(req.AgentsPrompt))
                await File.WriteAllTextAsync(Path.Combine(templateDir, "AGENTS.md"), req.AgentsPrompt, ct);
            if (!string.IsNullOrWhiteSpace(req.MemoryPrompt))
                await File.WriteAllTextAsync(Path.Combine(templateDir, "MEMORY.md"), req.MemoryPrompt, ct);

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
                SystemPrompt = req.SystemPrompt,
                UserPromptTemplate = req.UserPromptTemplate,
                MaxContextTokens = req.MaxContextTokens,
                MaxReplyTokens = req.MaxReplyTokens,
                MaxRounds = req.MaxRounds ?? manifest.MaxRounds,
                MaxElapsedSeconds = req.MaxElapsedSeconds ?? manifest.MaxElapsedSeconds,
                MaxToolCallsTotal = req.MaxToolCallsTotal ?? manifest.MaxToolCallsTotal,
                ContainerImage = req.ContainerImage,
                IsEnabled = req.IsEnabled,
                SortOrder = req.SortOrder,
                MemorySearchMode = req.MemorySearchMode ?? manifest.MemorySearchMode,
                ReasoningEffort = req.ReasoningEffort ?? manifest.ReasoningEffort,
                AvatarId = req.AvatarId ?? manifest.AvatarId,
                PreferredProviderId = req.PreferredProviderId,
                PreferredModelId = req.PreferredModelId,
                MemoryLlmProviderId = req.MemoryLlmProviderId,
                MemoryLlmModelId = req.MemoryLlmModelId,
                SkillPackageIds = req.SelectedSkillPackageIds ?? manifest.SkillPackageIds,
                Capabilities = manifest.Capabilities with
                {
                    AllowedToolIds = req.SelectedCapabilityIds ?? manifest.Capabilities.AllowedToolIds,
                },
                DefaultLlmProfiles = new AgentDefaultLlmProfiles
                {
                    Conscious = req.ConsciousProfileId ?? manifest.DefaultLlmProfiles.Conscious,
                    Subconscious = req.SubconsciousProfileId ?? manifest.DefaultLlmProfiles.Subconscious,
                },
                CreatedAt = manifest.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            await AtomicFileWriter.WriteJsonAsync(Path.Combine(templateDir, "manifest.json"), updated, JsonOptions, ct);

            // 更新 Markdown（如果提供了新值）
            if (req.PersonaPrompt is not null)
                await File.WriteAllTextAsync(Path.Combine(templateDir, "SOUL.md"), req.PersonaPrompt, ct);
            if (req.ToolsDescription is not null)
                await File.WriteAllTextAsync(Path.Combine(templateDir, "TOOLS.md"), req.ToolsDescription, ct);
            if (req.BootstrapTemplate is not null)
                await File.WriteAllTextAsync(Path.Combine(templateDir, "BOOTSTRAP.md"), req.BootstrapTemplate, ct);
            if (req.AgentsPrompt is not null)
                await File.WriteAllTextAsync(Path.Combine(templateDir, "AGENTS.md"), req.AgentsPrompt, ct);
            if (req.MemoryPrompt is not null)
                await File.WriteAllTextAsync(Path.Combine(templateDir, "MEMORY.md"), req.MemoryPrompt, ct);

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

        var manifest = await AtomicFileWriter.ReadJsonAsync<AgentTemplateManifest>(path, JsonOptions, ct);
        if (manifest is null) return null;

        var fileInfo = new FileInfo(path);
        var createdAt = manifest.CreatedAt == default
            ? new DateTimeOffset(fileInfo.CreationTimeUtc, TimeSpan.Zero)
            : manifest.CreatedAt;
        var updatedAt = manifest.UpdatedAt == default
            ? new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero)
            : manifest.UpdatedAt;

        return manifest with
        {
            CreatedAt = createdAt,
            UpdatedAt = updatedAt == default ? createdAt : updatedAt,
        };
    }

    private async Task<AgentTemplatePreset?> LoadPresetAsync(string templateId, CancellationToken ct)
    {
        var path = Path.Combine(_presetTemplatesRoot, $"{templateId}.json");
        if (!File.Exists(path)) return null;

        return await AtomicFileWriter.ReadJsonAsync<AgentTemplatePreset>(path, JsonOptions, ct);
    }

    private async Task<GlobalAgentTemplateDto> MapPresetToDtoAsync(int id, AgentTemplatePreset preset, CancellationToken ct)
    {
        AgentAvatarDefinition? avatar = null;
        if (!string.IsNullOrWhiteSpace(preset.AvatarId))
        {
            avatar = _avatarCatalog.Find(preset.AvatarId);
        }

        avatar ??= _avatarCatalog.GetDefault();

        return new GlobalAgentTemplateDto(
            Id: id,
            TemplateId: preset.TemplateId,
            Name: preset.Name,
            Description: preset.Description,
            Role: preset.Role,
            SystemPrompt: preset.SystemPrompt,
            UserPromptTemplate: preset.UserPromptTemplate,
            PreferredProviderId: preset.PreferredProviderId,
            PreferredModelId: preset.PreferredModelId,
            MaxContextTokens: preset.MaxContextTokens,
            MaxReplyTokens: preset.MaxReplyTokens,
            ContainerImage: preset.ContainerImage,
            SelectedCapabilityIds: preset.SelectedCapabilityIds,
            SelectedSkillPackageIds: preset.SelectedSkillPackageIds,
            IsBuiltIn: true,
            IsEnabled: preset.IsEnabled,
            SortOrder: preset.SortOrder,
            CreatedAt: default,
            UpdatedAt: default,
            PersonaPrompt: preset.PersonaPrompt,
            ToolsDescription: preset.ToolsDescription,
            BootstrapTemplate: preset.BootstrapTemplate,
            AvatarEmoji: null,
            AvatarId: avatar?.AvatarId ?? preset.AvatarId,
            AvatarUrl: avatar?.UrlPath,
            AvatarName: avatar?.Name,
            MemoryLlmProviderId: preset.MemoryLlmProviderId,
            MemoryLlmModelId: preset.MemoryLlmModelId,
            MemorySearchMode: preset.MemorySearchMode,
            ReasoningEffort: preset.ReasoningEffort,
            MaxRounds: preset.MaxRounds,
            MaxElapsedSeconds: preset.MaxElapsedSeconds,
            MaxToolCallsTotal: preset.MaxToolCallsTotal,
            ConsciousProfileId: preset.ConsciousProfileId,
            SubconsciousProfileId: preset.SubconsciousProfileId,
            AgentsPrompt: preset.AgentsPrompt,
            MemoryPrompt: preset.MemoryPrompt,
            AllowFileWrite: preset.AllowFileWrite,
            AllowShellExecution: preset.AllowShellExecution,
            AllowNetworkAccess: preset.AllowNetworkAccess,
            AllowedToolNames: preset.AllowedToolNames.Count > 0 ? preset.AllowedToolNames : null
        );
    }

    private static AgentTemplatePreset NormalizePreset(AgentTemplatePreset preset, string fallbackTemplateId) =>
        preset with
        {
            TemplateId = string.IsNullOrWhiteSpace(preset.TemplateId) ? fallbackTemplateId : preset.TemplateId,
            Name = string.IsNullOrWhiteSpace(preset.Name) ? fallbackTemplateId : preset.Name,
            Role = string.IsNullOrWhiteSpace(preset.Role) ? "Service" : preset.Role,
            MemorySearchMode = string.IsNullOrWhiteSpace(preset.MemorySearchMode) ? "deep" : preset.MemorySearchMode,
            MaxContextTokens = preset.MaxContextTokens <= 0 ? 8192 : preset.MaxContextTokens,
            MaxReplyTokens = preset.MaxReplyTokens <= 0 ? 2048 : preset.MaxReplyTokens,
            MaxRounds = preset.MaxRounds <= 0 ? 200 : preset.MaxRounds,
            MaxElapsedSeconds = preset.MaxElapsedSeconds <= 0 ? 1200 : preset.MaxElapsedSeconds,
            MaxToolCallsTotal = preset.MaxToolCallsTotal <= 0 ? 100 : preset.MaxToolCallsTotal,
        };

    private static async Task WritePresetMarkdownFilesAsync(string templateDir, AgentTemplatePreset preset, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(preset.PersonaPrompt))
            await File.WriteAllTextAsync(Path.Combine(templateDir, "SOUL.md"), preset.PersonaPrompt, ct);
        if (!string.IsNullOrWhiteSpace(preset.ToolsDescription))
            await File.WriteAllTextAsync(Path.Combine(templateDir, "TOOLS.md"), preset.ToolsDescription, ct);
        if (!string.IsNullOrWhiteSpace(preset.BootstrapTemplate))
            await File.WriteAllTextAsync(Path.Combine(templateDir, "BOOTSTRAP.md"), preset.BootstrapTemplate, ct);
        if (!string.IsNullOrWhiteSpace(preset.AgentsPrompt))
            await File.WriteAllTextAsync(Path.Combine(templateDir, "AGENTS.md"), preset.AgentsPrompt, ct);
        if (!string.IsNullOrWhiteSpace(preset.MemoryPrompt))
            await File.WriteAllTextAsync(Path.Combine(templateDir, "MEMORY.md"), preset.MemoryPrompt, ct);
    }

    private async Task<GlobalAgentTemplateDto> MapToDtoAsync(int id, AgentTemplateManifest m, string templateDir, CancellationToken ct)
    {
        var soulPath = Path.Combine(templateDir, "SOUL.md");
        var toolsPath = Path.Combine(templateDir, "TOOLS.md");
        var bootstrapPath = Path.Combine(templateDir, "BOOTSTRAP.md");
        var agentsPath = Path.Combine(templateDir, "AGENTS.md");
        var memoryPath = Path.Combine(templateDir, "MEMORY.md");

        // 解析头像：优先 manifest.avatarId → manifest 内嵌的 legacy fallback → 默认头像
        AgentAvatarDefinition? avatar = null;
        if (!string.IsNullOrWhiteSpace(m.AvatarId))
        {
            avatar = _avatarCatalog.Find(m.AvatarId);
            if (avatar is null)
            {
                _logger.LogWarning(
                    "AvatarId '{AvatarId}' for template '{TemplateId}' is invalid or disabled. Falling back to default avatar.",
                    m.AvatarId,
                    m.TemplateId);
            }
        }

        avatar ??= _avatarCatalog.GetDefault();

        return new GlobalAgentTemplateDto(
            Id: id,
            TemplateId: m.TemplateId,
            Name: m.Name,
            Description: m.Description,
            Role: m.Role,
            SystemPrompt: m.SystemPrompt,
            UserPromptTemplate: m.UserPromptTemplate,
            PreferredProviderId: m.PreferredProviderId,
            PreferredModelId: m.PreferredModelId,
            MaxContextTokens: m.MaxContextTokens,
            MaxReplyTokens: m.MaxReplyTokens,
            ContainerImage: m.ContainerImage,
            SelectedCapabilityIds: m.Capabilities.AllowedToolIds,
            SelectedSkillPackageIds: m.SkillPackageIds,
            IsBuiltIn: m.IsBuiltIn,
            IsEnabled: m.IsEnabled,
            SortOrder: m.SortOrder,
            CreatedAt: m.CreatedAt,
            UpdatedAt: m.UpdatedAt,
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
            MaxRounds: m.MaxRounds,
            MaxElapsedSeconds: m.MaxElapsedSeconds,
            MaxToolCallsTotal: m.MaxToolCallsTotal,
            ConsciousProfileId: m.DefaultLlmProfiles.Conscious,
            SubconsciousProfileId: m.DefaultLlmProfiles.Subconscious,
            AgentsPrompt: File.Exists(agentsPath) ? await File.ReadAllTextAsync(agentsPath, ct) : null,
            MemoryPrompt: File.Exists(memoryPath) ? await File.ReadAllTextAsync(memoryPath, ct) : null,
            AllowFileWrite: m.Capabilities.AllowFileWrite,
            AllowShellExecution: m.Capabilities.AllowShellExecution,
            AllowNetworkAccess: m.Capabilities.AllowNetworkAccess,
            AllowedToolNames: m.Capabilities.AllowedToolNames
        );
    }

    private sealed record AgentTemplatePreset
    {
        public string TemplateId { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public string Role { get; init; } = "Service";
        public string? SystemPrompt { get; init; }
        public string? UserPromptTemplate { get; init; }
        public string? PersonaPrompt { get; init; }
        public string? ToolsDescription { get; init; }
        public string? BootstrapTemplate { get; init; }
        public string? AgentsPrompt { get; init; }
        public string? MemoryPrompt { get; init; }
        public string? PreferredProviderId { get; init; }
        public string? PreferredModelId { get; init; }
        public string? MemoryLlmProviderId { get; init; }
        public string? MemoryLlmModelId { get; init; }
        public string MemorySearchMode { get; init; } = "deep";
        public string? ReasoningEffort { get; init; }
        public string? ConsciousProfileId { get; init; }
        public string? SubconsciousProfileId { get; init; }
        public int MaxRounds { get; init; } = 200;
        public int MaxElapsedSeconds { get; init; } = 1200;
        public int MaxToolCallsTotal { get; init; } = 100;
        public int MaxContextTokens { get; init; } = 8192;
        public int MaxReplyTokens { get; init; } = 2048;
        public string? ContainerImage { get; init; }
        public List<string> SelectedCapabilityIds { get; init; } = [];
        public List<string> SelectedSkillPackageIds { get; init; } = [];
        public bool AllowFileWrite { get; init; }
        public bool AllowShellExecution { get; init; }
        public bool AllowNetworkAccess { get; init; }
        public List<string> AllowedToolNames { get; init; } = [];
        public bool IsEnabled { get; init; } = true;
        public int SortOrder { get; init; } = 100;
        public string? AvatarId { get; init; }
    }
}
