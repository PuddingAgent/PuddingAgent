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

    public async Task<AgentFileProfile> LoadAsync(string agentInstanceId, CancellationToken ct = default)
    {
        var instancePath = Path.Combine(_paths.AgentInstanceRoot(agentInstanceId), "manifest.json");
        var instance = await ReadRequiredJsonAsync<AgentInstanceManifest>(instancePath, ct);
        if (string.IsNullOrWhiteSpace(instance.AgentInstanceId))
            instance = instance with { AgentInstanceId = agentInstanceId };

        if (string.IsNullOrWhiteSpace(instance.TemplateId))
            throw new InvalidOperationException($"Agent instance '{agentInstanceId}' has empty templateId.");

        var templateRoot = _paths.AgentTemplateRoot(instance.TemplateId);
        var templatePath = Path.Combine(templateRoot, "manifest.json");
        var template = await ReadRequiredJsonAsync<AgentTemplateManifest>(templatePath, ct);

        var llmPath = _paths.AgentInstanceConfigFile(agentInstanceId, "llm.json");
        var llmConfig = File.Exists(llmPath)
            ? await ReadRequiredJsonAsync<AgentInstanceLlmConfig>(llmPath, ct)
            : new AgentInstanceLlmConfig();

        // 加载权限配置 — 如果模板目录存在 permissions.json 则加载，否则返回默认权限（禁止写 config/databases）
        var permissions = await LoadPermissionsOrDefaultAsync(templateRoot, ct);

        var sourcePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["instance.manifest"] = instancePath,
            ["template.manifest"] = templatePath,
        };
        if (File.Exists(llmPath))
            sourcePaths["instance.config.llm"] = llmPath;

        var markdown = new AgentProfileMarkdown
        {
            Soul = ReadMarkdown(templateRoot, "SOUL.md", sourcePaths, "template.SOUL.md"),
            Agents = ReadMarkdown(templateRoot, "AGENTS.md", sourcePaths, "template.AGENTS.md"),
            Tools = ReadMarkdown(templateRoot, "TOOLS.md", sourcePaths, "template.TOOLS.md"),
            Bootstrap = ReadMarkdown(templateRoot, "BOOTSTRAP.md", sourcePaths, "template.BOOTSTRAP.md"),
            Memory = ReadMarkdown(templateRoot, "MEMORY.md", sourcePaths, "template.MEMORY.md"),
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
    /// 从模板目录加载 permissions.json；如果文件不存在，返回默认权限（禁止写 config/databases）。
    /// </summary>
    internal async Task<SubAgentPermissions> LoadPermissionsOrDefaultAsync(string templateRoot, CancellationToken ct)
    {
        var permissionsPath = Path.Combine(templateRoot, "permissions.json");
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
            // 权限文件损坏或格式错误时返回默认权限，不阻断 agent 加载
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

    private static string? ReadMarkdown(
        string directory,
        string fileName,
        IDictionary<string, string> sourcePaths,
        string sourceKey)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
            return null;

        sourcePaths[sourceKey] = path;
        return File.ReadAllText(path);
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
