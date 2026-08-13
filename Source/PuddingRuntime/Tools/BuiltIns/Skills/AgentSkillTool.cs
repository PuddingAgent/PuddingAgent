using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services.Tools;

[Tool(
    id: "agent_skill",
    name: "Agent SKILL",
    description: "读取并管理当前 Agent 实例的运行时私有 SKILL（技能）。【何时用】查看当前 Agent 有哪些已注册技能、读取某 SKILL 的 SKILL.md 内容、新建/更新/启停/删除私有技能，或把技能打包推送到技能市场（push）时使用。【怎么用】action=list（默认）列出技能；get_index 看索引；get/read_file 需 skill_id（read_file 可选 relative_path 指定 SKILL 内文件、max_chars 控制返回长度）；create 需 skill_id+name+skill_markdown；set_enabled/enable/disable 控制启停；delete 删除；push 推送到远程（依赖 AdminBaseUrl/AdminApiKey 配置）。【坑】list/get_index 默认过滤禁用技能，要看全部需 include_disabled=true；除 list/get_index/initialize 外多数 action 都要 skill_id，缺失直接报错；delete 不可恢复，确认 skill_id 再删；push 需要管理端可达。Read and manage runtime-private SKILLs for the current Agent instance — use to list (default), inspect indexes, read_file, create, update, enable/disable, delete, or push SKILLs; list/get_index hide disabled SKILLs unless include_disabled=true, most actions require skill_id, read_file takes relative_path/max_chars, delete is irreversible, and push requires AdminBaseUrl/AdminApiKey.",
    category: ToolCategory.FileSystem,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 45)]
public sealed class AgentSkillTool(
    AgentSkillFileService skillService,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration
) : PuddingToolBase<AgentSkillArgs>
{
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        AgentSkillArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var agentInstanceId = context.AgentInstanceId;
        var action = NormalizeAction(args.Action);
        return action switch
        {
            "list" => ToolExecutionResult.Ok(ToJson(await ListAsync(agentInstanceId, args, ct))),
            "index" or "get_index" => ToolExecutionResult.Ok(ToJson(await GetIndexAsync(agentInstanceId, args, ct))),
            "get" => ToolExecutionResult.Ok(ToJson(await GetAsync(agentInstanceId, RequireSkillId(args), ct))),
            "read_file" => ToolExecutionResult.Ok(ToJson(await ReadFileAsync(agentInstanceId, args, ct))),
            "initialize" or "init" => ToolExecutionResult.Ok(ToJson(await InitializeAsync(agentInstanceId, ct))),
            "create" => ToolExecutionResult.Ok(ToJson(await CreateAsync(agentInstanceId, args, ct))),
            "update" => ToolExecutionResult.Ok(ToJson(await UpdateAsync(agentInstanceId, args, ct))),
            "set_enabled" => ToolExecutionResult.Ok(ToJson(await SetEnabledAsync(agentInstanceId, args, ct))),
            "enable" => ToolExecutionResult.Ok(ToJson(await SetEnabledAsync(agentInstanceId, args with { Enabled = true }, ct))),
            "disable" => ToolExecutionResult.Ok(ToJson(await SetEnabledAsync(agentInstanceId, args with { Enabled = false }, ct))),
            "delete" => ToolExecutionResult.Ok(ToJson(await DeleteAsync(agentInstanceId, args, ct))),
            "push" => ToolExecutionResult.Ok(ToJson(await PushAsync(agentInstanceId, args, ct))),
            "rebuild_index" => ToolExecutionResult.Ok(ToJson(await RebuildIndexAsync(agentInstanceId, ct))),
            _ => ToolExecutionResult.Fail(
                $"Unknown agent_skill action '{args.Action}'. Valid actions: list, get_index, get, read_file, initialize, create, update, set_enabled, enable, disable, delete, push, rebuild_index."),
        };
    }

    private async Task<object> ListAsync(string agentInstanceId, AgentSkillArgs args, CancellationToken ct)
    {
        var index = await skillService.GetIndexAsync(agentInstanceId, ct);
        var skills = FilterSkills(index.Skills, args.IncludeDisabled).ToList();
        return new
        {
            status = "ok",
            action = "list",
            agentInstanceId,
            count = skills.Count,
            skills,
        };
    }

    private async Task<object> GetIndexAsync(string agentInstanceId, AgentSkillArgs args, CancellationToken ct)
    {
        var index = await skillService.GetIndexAsync(agentInstanceId, ct);
        var skills = FilterSkills(index.Skills, args.IncludeDisabled).ToList();
        return new
        {
            status = "ok",
            action = "get_index",
            agentInstanceId,
            generatedAt = index.GeneratedAt,
            count = skills.Count,
            skills,
        };
    }

    private async Task<object> GetAsync(string agentInstanceId, string skillId, CancellationToken ct)
    {
        var record = await skillService.GetAsync(agentInstanceId, skillId, ct);
        return new
        {
            status = "ok",
            action = "get",
            agentInstanceId,
            skill = ToSkill(record.Manifest, record.PhysicalPath),
            physicalPath = record.PhysicalPath,
        };
    }

    private async Task<object> ReadFileAsync(string agentInstanceId, AgentSkillArgs args, CancellationToken ct)
    {
        var file = await skillService.ReadFileAsync(agentInstanceId, RequireSkillId(args), args.RelativePath, ct);
        var maxChars = args.MaxChars is > 0 ? args.MaxChars.Value : 100_000;
        var content = file.Content;
        var originalLength = content.Length;
        var truncated = content.Length > maxChars;
        if (truncated)
            content = content[..maxChars];

        return new
        {
            status = "ok",
            action = "read_file",
            agentInstanceId,
            skillId = file.SkillId,
            relativePath = file.RelativePath,
            physicalPath = file.PhysicalPath,
            content,
            originalLength,
            truncated,
        };
    }

    private async Task<object> InitializeAsync(string agentInstanceId, CancellationToken ct)
    {
        var initialized = await skillService.InitializeAsync(agentInstanceId, ct);
        return new
        {
            status = "ok",
            action = "initialize",
            agentInstanceId,
            initialized.SkillsRootPath,
            initialized.IndexPath,
        };
    }

    private async Task<object> CreateAsync(string agentInstanceId, AgentSkillArgs args, CancellationToken ct)
    {
        var skillId = RequireSkillId(args);
        if (string.IsNullOrWhiteSpace(args.Name))
            throw new ArgumentException("name is required for create action.", nameof(args));

        var created = await skillService.CreateAsync(agentInstanceId, new AgentSkillCreateRequest
        {
            SkillId = skillId,
            Name = args.Name.Trim(),
            Version = string.IsNullOrWhiteSpace(args.Version) ? "1.0.0" : args.Version.Trim(),
            Description = args.Description,
            Summary = args.Summary,
            Tags = args.Tags,
            Keywords = args.Keywords,
            SkillMarkdown = args.SkillMarkdown,
        }, ct);

        return WithRecord("create", agentInstanceId, created);
    }

    private async Task<object> UpdateAsync(string agentInstanceId, AgentSkillArgs args, CancellationToken ct)
    {
        var updated = await skillService.UpdateAsync(agentInstanceId, RequireSkillId(args), new AgentSkillUpdateRequest
        {
            Name = args.Name,
            Version = args.Version,
            Description = args.Description,
            Summary = args.Summary,
            Tags = args.Tags,
            Keywords = args.Keywords,
            SkillMarkdown = args.SkillMarkdown,
        }, ct);

        return WithRecord("update", agentInstanceId, updated);
    }

    private async Task<object> SetEnabledAsync(string agentInstanceId, AgentSkillArgs args, CancellationToken ct)
    {
        if (args.Enabled is null)
            throw new ArgumentException("enabled is required for set_enabled action.", nameof(args));

        var updated = await skillService.SetEnabledAsync(agentInstanceId, RequireSkillId(args), args.Enabled.Value, ct);
        return WithRecord("set_enabled", agentInstanceId, updated);
    }

    private async Task<object> DeleteAsync(string agentInstanceId, AgentSkillArgs args, CancellationToken ct)
    {
        var deleted = await skillService.DeleteAsync(agentInstanceId, RequireSkillId(args), ct);
        return new
        {
            status = "ok",
            action = "delete",
            agentInstanceId,
            skillId = deleted.SkillId,
            deletedPath = deleted.DeletedPath,
            count = deleted.Index.Skills.Count,
            index = deleted.Index,
        };
    }

    private async Task<object> PushAsync(string agentInstanceId, AgentSkillArgs args, CancellationToken ct)
    {
        var skillId = RequireSkillId(args);
        var record = await skillService.GetAsync(agentInstanceId, skillId, ct);
        var skillRoot = record.PhysicalPath;
        var manifest = record.Manifest;

        // 打包为 zip
        var tempDir = Path.Combine(Path.GetTempPath(), "pudding-skill-push", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var zipPath = Path.Combine(tempDir, $"{skillId}.zip");
            ZipFile.CreateFromDirectory(skillRoot, zipPath);

            // 构建 multipart form
            await using var zipStream = File.OpenRead(zipPath);
            var content = new MultipartFormDataContent();

            // skill_id 转 skillPackageId（下划线→连字符，满足 [a-z0-9\-]+）
            var packageId = skillId.Replace('_', '-');
            content.Add(new StringContent(packageId), "skillPackageId");
            content.Add(new StringContent(manifest.Name), "name");
            if (!string.IsNullOrWhiteSpace(manifest.Description))
                content.Add(new StringContent(manifest.Description), "description");
            content.Add(new StringContent(args.PushVersion ?? manifest.Version), "version");

            var fileContent = new StreamContent(zipStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            content.Add(fileContent, "file", $"{skillId}.zip");

            // 发送 HTTP
            var baseUrl = (configuration["AdminBaseUrl"] ?? "http://localhost:5000").TrimEnd('/');
            var apiKey = configuration["AdminApiKey"];

            using var httpClient = httpClientFactory.CreateClient("SkillPackagePush");
            httpClient.BaseAddress = new Uri(baseUrl);
            httpClient.Timeout = TimeSpan.FromMinutes(2);
            if (!string.IsNullOrWhiteSpace(apiKey))
                httpClient.DefaultRequestHeaders.Add("X-Admin-Api-Key", apiKey);

            var response = await httpClient.PostAsync("/api/skill-packages", content, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Push failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {responseBody}");

            return new
            {
                status = "ok",
                action = "push",
                agentInstanceId,
                skillId,
                skillPackageId = packageId,
                version = args.PushVersion ?? manifest.Version,
                serverResponse = responseBody.Length <= 2000
                    ? JsonSerializer.Deserialize<object>(responseBody)
                    : responseBody[..2000] + "...",
            };
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // 清理临时目录失败不影响主流程
            }
        }
    }

    private async Task<object> RebuildIndexAsync(string agentInstanceId, CancellationToken ct)
    {
        var index = await skillService.RebuildIndexAsync(agentInstanceId, ct);
        return new
        {
            status = "ok",
            action = "rebuild_index",
            agentInstanceId,
            count = index.Skills.Count,
            index,
        };
    }

    private static IEnumerable<AgentSkillIndexEntry> FilterSkills(
        IEnumerable<AgentSkillIndexEntry> skills,
        bool includeDisabled) =>
        includeDisabled ? skills : skills.Where(x => x.Enabled);

    private static object ToSkill(AgentSkillManifest manifest, string physicalPath) => new
    {
        manifest.SkillId,
        manifest.Name,
        manifest.Version,
        manifest.Description,
        manifest.Summary,
        manifest.Tags,
        manifest.Enabled,
        manifest.CreatedAt,
        manifest.UpdatedAt,
        manifest.ContentHash,
        physicalPath,
    };

    private static object WithRecord(string action, string agentInstanceId, AgentSkillRecord record) => new
    {
        status = "ok",
        action,
        agentInstanceId,
        skill = new
        {
            record.Manifest.SkillId,
            record.Manifest.Name,
            record.Manifest.Version,
            record.Manifest.Description,
            record.Manifest.Summary,
            record.Manifest.Tags,
            record.Manifest.Enabled,
            record.Manifest.CreatedAt,
            record.Manifest.UpdatedAt,
            record.Manifest.ContentHash,
        },
        physicalPath = record.PhysicalPath,
        count = record.Index.Skills.Count,
        index = record.Index,
    };

    private static string RequireSkillId(AgentSkillArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.SkillId))
            throw new ArgumentException("skill_id is required for this agent_skill action.", nameof(args));

        return args.SkillId.Trim();
    }

    private static string NormalizeAction(string? action) =>
        string.IsNullOrWhiteSpace(action) ? "list" : action.Trim().ToLowerInvariant();

    private static string ToJson(object value) => JsonSerializer.Serialize(value, AgentSkillToolJson.Options);
}

public sealed record AgentSkillArgs
{
    [ToolParam("Action to run: list, get_index, get, read_file, initialize, create, update, set_enabled, enable, disable, delete, push, rebuild_index.")]
    public required string Action { get; init; }

    [ToolParam("SKILL id for get, read_file, create, update, set_enabled, enable, disable, delete, and push actions.")]
    [JsonPropertyName("skill_id")]
    public string? SkillId { get; init; }

    [ToolParam("Relative file path inside the SKILL directory. Defaults to SKILL.md.")]
    [JsonPropertyName("relative_path")]
    public string? RelativePath { get; init; }

    [ToolParam("Maximum characters to return for read_file. Default: 100000.")]
    [JsonPropertyName("max_chars")]
    public int? MaxChars { get; init; }

    [ToolParam("Include disabled SKILL entries in list and get_index actions. Default: false.")]
    [JsonPropertyName("include_disabled")]
    public bool IncludeDisabled { get; init; }

    [ToolParam("SKILL display name. Required for create.")]
    public string? Name { get; init; }

    [ToolParam("SKILL semantic version. Defaults to 1.0.0.")]
    public string? Version { get; init; }

    [ToolParam("SKILL description.")]
    public string? Description { get; init; }

    [ToolParam("Short SKILL summary used in indexes and context.")]
    public string? Summary { get; init; }

    [ToolParam("SKILL tags.")]
    public IReadOnlyList<string>? Tags { get; init; }

    [ToolParam("Keywords for auto-matching. Used by SkillEnforcer for deterministic pre-loading.")]
    [JsonPropertyName("keywords")]
    public IReadOnlyList<string>? Keywords { get; init; }

    [ToolParam("SKILL.md markdown content.")]
    [JsonPropertyName("skill_markdown")]
    public string? SkillMarkdown { get; init; }

    [ToolParam("Enabled state for set_enabled action.")]
    public bool? Enabled { get; init; }

    [ToolParam("Override version for push action. Defaults to the SKILL manifest version.")]
    [JsonPropertyName("push_version")]
    public string? PushVersion { get; init; }
}

internal static class AgentSkillToolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
}
