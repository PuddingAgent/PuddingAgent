using System.Text.Json;
using PuddingCode.Configuration;
using PuddingCode.SubAgents;

namespace PuddingCode.Agents;

public sealed class AgentProfileProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly PuddingDataPaths _paths;

    public AgentProfileProvider(PuddingDataPaths paths)
    {
        _paths = paths;
    }

    /// <summary>
    /// 加载 Agent 实例完整 profile。模板配置已嵌入实例 manifest，不再跨目录读取。
    /// </summary>
    public async Task<AgentFileProfile> LoadAsync(string agentInstanceId, CancellationToken ct = default)
    {
        var instanceRoot = _paths.AgentInstanceRoot(agentInstanceId);
        var instancePath = Path.Combine(instanceRoot, "manifest.json");
        var instance = await ReadRequiredJsonAsync<AgentInstanceManifest>(instancePath, ct);
        if (string.IsNullOrWhiteSpace(instance.AgentInstanceId))
            instance = instance with { AgentInstanceId = agentInstanceId };

        // 模板配置已嵌入实例 manifest，从实例字段构建（不再跨目录读模板文件）
        var template = BuildTemplateFromInstance(instance);

        // LLM 配置（实例级覆盖）
        var llmPath = _paths.AgentInstanceConfigFile(agentInstanceId, "llm.json");
        var llmConfig = File.Exists(llmPath)
            ? await ReadRequiredJsonAsync<AgentInstanceLlmConfig>(llmPath, ct)
            : new AgentInstanceLlmConfig();

        // 权限配置（从实例目录加载）
        var permissions = await LoadPermissionsOrDefaultAsync(instanceRoot, ct);

        var sourcePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["instance.manifest"] = instancePath,
        };
        if (File.Exists(llmPath))
            sourcePaths["instance.config.llm"] = llmPath;

        // Markdown 从实例目录下的文件读取（通过 manifest 中的文件名引用）
        var markdown = new AgentProfileMarkdown
        {
            Soul = ReadMarkdownFile(instanceRoot, instance.SoulMdFile, sourcePaths, "instance.SOUL.md"),
            Agents = ReadMarkdownFile(instanceRoot, instance.AgentsMdFile, sourcePaths, "instance.AGENTS.md"),
            Tools = ReadMarkdownFile(instanceRoot, instance.ToolsMdFile, sourcePaths, "instance.TOOLS.md"),
            Bootstrap = ReadMarkdownFile(instanceRoot, instance.BootstrapMdFile, sourcePaths, "instance.BOOTSTRAP.md"),
            Memory = ReadMarkdownFile(instanceRoot, instance.MemoryMdFile, sourcePaths, "instance.MEMORY.md"),
        };

        return new AgentFileProfile
        {
            Instance = instance,
            Template = template,
            LlmConfig = llmConfig,
            Markdown = markdown,
            Permissions = permissions,
            SourcePaths = sourcePaths,
        };
    }

    /// <summary>
    /// 从 AgentInstanceManifest 构建 AgentTemplateManifest（向后兼容）。
    /// 模板配置在创建时已嵌入实例 manifest，此方法提供 Template 视图供现有消费方使用。
    /// </summary>
    private static AgentTemplateManifest BuildTemplateFromInstance(AgentInstanceManifest instance)
        => new()
        {
            TemplateId = instance.TemplateId,
            Name = instance.DisplayName ?? instance.TemplateId,
            Description = instance.Description,
            Role = instance.Role ?? "Service",
            SystemPrompt = instance.SystemPrompt,
            MemorySearchMode = instance.MemorySearchMode ?? "deep",
            ReasoningEffort = instance.ReasoningEffort,
            MaxContextTokens = instance.MaxContextTokens,
            MaxReplyTokens = instance.MaxReplyTokens,
            MaxRounds = instance.MaxRounds,
            MaxElapsedSeconds = instance.MaxElapsedSeconds,
            MaxToolCallsTotal = instance.MaxToolCallsTotal,
            PreferredProviderId = instance.PreferredProviderId,
            PreferredModelId = instance.PreferredModelId,
            MemoryLlmProviderId = instance.MemoryLlmProviderId,
            MemoryLlmModelId = instance.MemoryLlmModelId,
            Capabilities = instance.Capabilities,
            SkillPackageIds = instance.SkillPackageIds,
            IsEnabled = instance.IsEnabled,
        };

    /// <summary>
    /// 从目录加载 permissions.json；如果文件不存在，返回默认权限。
    /// </summary>
    internal async Task<SubAgentPermissions> LoadPermissionsOrDefaultAsync(string directory, CancellationToken ct)
    {
        var permissionsPath = Path.Combine(directory, "permissions.json");
        if (!File.Exists(permissionsPath))
            return new SubAgentPermissions();

        try
        {
            await using var stream = File.OpenRead(permissionsPath);
            var permissions = await JsonSerializer.DeserializeAsync<SubAgentPermissions>(stream, JsonOptions, ct);
            return permissions ?? new SubAgentPermissions();
        }
        catch
        {
            return new SubAgentPermissions();
        }
    }

    private static async Task<T> ReadRequiredJsonAsync<T>(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Agent profile file not found: {path}", path);

        await using var stream = File.OpenRead(path);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
        return value ?? throw new InvalidOperationException($"Agent profile file is empty or invalid: {path}");
    }

    /// <summary>
    /// 读取实例目录下的 Markdown 文件。directory 或 fileName 为空时返回 null。
    /// </summary>
    private static string? ReadMarkdownFile(
        string? directory,
        string? fileName,
        IDictionary<string, string> sourcePaths,
        string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
            return null;

        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
            return null;

        sourcePaths[sourceKey] = path;
        return File.ReadAllText(path);
    }

    // ── 向后兼容方法（模板目录读取，用于 AgentTemplateProvider 等场景）──

    /// <summary>
    /// 获取模板 manifest.json 的完整路径。
    /// </summary>
    public string GetTemplateManifestPath(string templateId)
    {
        return _paths.AgentTemplateFile(templateId, "manifest.json");
    }

    /// <summary>
    /// 只读取模板 manifest（不需要 agent instance），用于 AgentTemplateProvider 文件回退。
    /// </summary>
    public async Task<AgentTemplateManifest?> LoadTemplateManifestAsync(string templateId, CancellationToken ct = default)
    {
        var path = GetTemplateManifestPath(templateId);
        if (!File.Exists(path)) return null;
        return await ReadRequiredJsonAsync<AgentTemplateManifest>(path, ct);
    }

    /// <summary>
    /// 获取模板的 Markdown 文件（不记录 sourcePaths）。
    /// </summary>
    public AgentProfileMarkdown GetMarkdown(string templateId)
    {
        var templateRoot = _paths.AgentTemplateRoot(templateId);
        return new AgentProfileMarkdown
        {
            Soul = File.Exists(Path.Combine(templateRoot, "SOUL.md")) ? File.ReadAllText(Path.Combine(templateRoot, "SOUL.md")) : null,
            Agents = File.Exists(Path.Combine(templateRoot, "AGENTS.md")) ? File.ReadAllText(Path.Combine(templateRoot, "AGENTS.md")) : null,
            Tools = File.Exists(Path.Combine(templateRoot, "TOOLS.md")) ? File.ReadAllText(Path.Combine(templateRoot, "TOOLS.md")) : null,
            Bootstrap = File.Exists(Path.Combine(templateRoot, "BOOTSTRAP.md")) ? File.ReadAllText(Path.Combine(templateRoot, "BOOTSTRAP.md")) : null,
            Memory = File.Exists(Path.Combine(templateRoot, "MEMORY.md")) ? File.ReadAllText(Path.Combine(templateRoot, "MEMORY.md")) : null,
        };
    }
}

public sealed record AgentFileProfile
{
    public required AgentInstanceManifest Instance { get; init; }
    public required AgentTemplateManifest Template { get; init; }
    public required AgentInstanceLlmConfig LlmConfig { get; init; }
    public required AgentProfileMarkdown Markdown { get; init; }
    public required SubAgentPermissions Permissions { get; init; }
    public required IReadOnlyDictionary<string, string> SourcePaths { get; init; }
}

public sealed record AgentProfileMarkdown
{
    public string? Soul { get; init; }
    public string? Agents { get; init; }
    public string? Tools { get; init; }
    public string? Bootstrap { get; init; }
    public string? Memory { get; init; }
}
