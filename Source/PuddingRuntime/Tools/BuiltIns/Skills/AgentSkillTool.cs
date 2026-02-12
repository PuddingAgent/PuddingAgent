using System.Text.Json;
using System.Text.Json.Serialization;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services.Tools;

[Tool(
    id: "agent_skill",
    name: "Agent SKILL",
    description: "Read and manage runtime-private SKILLs for the current Agent instance.",
    category: ToolCategory.FileSystem,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 45)]
public sealed class AgentSkillTool(AgentSkillFileService skillService) : PuddingToolBase<AgentSkillArgs>
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
            "rebuild_index" => ToolExecutionResult.Ok(ToJson(await RebuildIndexAsync(agentInstanceId, ct))),
            _ => ToolExecutionResult.Fail(
                $"Unknown agent_skill action '{args.Action}'. Valid actions: list, get_index, get, read_file, initialize, create, update, set_enabled, enable, disable, delete, rebuild_index."),
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
    [ToolParam("Action to run: list, get_index, get, read_file, initialize, create, update, set_enabled, enable, disable, delete, rebuild_index.")]
    public required string Action { get; init; }

    [ToolParam("SKILL id for get, read_file, create, update, set_enabled, enable, disable, and delete actions.")]
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
}

internal static class AgentSkillToolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
}
