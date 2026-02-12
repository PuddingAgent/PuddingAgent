using System.Text.Json;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingCodeIntelligence.Contracts;

namespace PuddingRuntime.Services.Tools;

// ═══════════════════════════════════════════════════════════════
// code_index_register_project
// ═══════════════════════════════════════════════════════════════

[Tool(
    id: "code_index_register_project",
    name: "Register project for indexing",
    description: "Register a local project directory in Pudding's code-index registry. Low-risk index-state change — does NOT modify or delete any source files. Index data can always be rebuilt. When index=true, triggers semantic indexing after registration.",
    category: ToolCategory.FileSystem,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 200)]
public sealed class CodeProjectAddTool : PuddingToolBase<CodeProjectAddArgs>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ICodeProjectRegistry? _registry;
    private readonly ICodeWorkspaceResolver? _resolver;
    private readonly ICodeIndexScheduler? _scheduler;

    public CodeProjectAddTool(
        ICodeProjectRegistry? registry = null,
        ICodeWorkspaceResolver? resolver = null,
        ICodeIndexScheduler? scheduler = null)
    {
        _registry = registry;
        _resolver = resolver;
        _scheduler = scheduler;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        CodeProjectAddArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (_registry is null)
            return Fail("Code project tools are not available: ICodeProjectRegistry is not registered.");

        if (string.IsNullOrWhiteSpace(args.ProjectPath))
            return Fail("project_path is required.");

        var projectPath = Path.GetFullPath(args.ProjectPath.Trim());
        if (!Directory.Exists(projectPath))
            return Fail($"Project path does not exist: {projectPath}");

        // 工作区边界检查（YOLO 模式跳过）
        if (!context.IsYoloMode
            && !HostFileToolPaths.TryResolveInsideWorkspace(projectPath, out _, out var wsError))
        {
            return Fail(wsError + " Use /yolo to bypass workspace boundary.");
        }

        var request = new CodeProjectAddRequest(
            WorkspaceId: context.WorkspaceId,
            ProjectPath: projectPath,
            ProjectId: args.ProjectId?.Trim(),
            DisplayName: args.DisplayName?.Trim());

        var result = await _registry.AddProjectAsync(request, ct);

        if (!result.Success)
            return Fail(result.Message ?? "Failed to register project.");

        var output = new Dictionary<string, object?>
        {
            ["status"] = "registered",
            ["workspace_id"] = result.WorkspaceId,
            ["project_id"] = result.ProjectId,
            ["project_path"] = projectPath,
            ["index_status"] = result.Status.ToString(),
        };

        if (args.Index is true && _scheduler is not null)
        {
            _scheduler.Enqueue(result.WorkspaceId!, result.ProjectId!);
            output["indexed"] = false;
            output["index_message"] = "Indexing enqueued for background processing.";
            output["index_status"] = "Pending";
        }

        return Ok(JsonSerializer.Serialize(output, JsonOptions));
    }

    private static ToolExecutionResult Ok(string output) => ToolExecutionResult.Ok(output);
    private static ToolExecutionResult Fail(string error) => ToolExecutionResult.Fail(error);
}

public sealed record CodeProjectAddArgs
{
    [ToolParam("Absolute or relative path to the project directory.")]
    public required string ProjectPath { get; init; }

    [ToolParam("Optional stable project identifier. Auto-generated if omitted.")]
    public string? ProjectId { get; init; }

    [ToolParam("Optional display name for the project.")]
    public string? DisplayName { get; init; }

    [ToolParam("Whether to trigger semantic indexing after registration.")]
    public bool? Index { get; init; }
}

// ═══════════════════════════════════════════════════════════════
// code_index_unregister_project
// ═══════════════════════════════════════════════════════════════

[Tool(
    id: "code_index_unregister_project",
    name: "Unregister project from indexing",
    description: "Remove a project from Pudding's code-index registry. Low-risk index-state change — does NOT delete source files or directories; only clears the index registry entry and associated index data.",
    category: ToolCategory.FileSystem,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 201)]
public sealed class CodeProjectRemoveTool : PuddingToolBase<CodeProjectRemoveArgs>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ICodeProjectRegistry? _registry;

    public CodeProjectRemoveTool(ICodeProjectRegistry? registry = null)
    {
        _registry = registry;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        CodeProjectRemoveArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (_registry is null)
            return Fail("Code project tools are not available: ICodeProjectRegistry is not registered.");

        if (string.IsNullOrWhiteSpace(args.ProjectId))
            return Fail("project_id is required.");

        var request = new CodeProjectRemoveRequest(
            WorkspaceId: context.WorkspaceId,
            ProjectId: args.ProjectId.Trim(),
            RemoveIndexData: args.RemoveIndexData ?? true);

        var result = await _registry.RemoveProjectAsync(request, ct);

        if (!result.Success)
            return Fail(result.Message ?? "Failed to remove project.");

        return Ok(JsonSerializer.Serialize(new
        {
            status = "removed",
            workspace_id = result.WorkspaceId,
            project_id = result.ProjectId,
            remove_index_data = args.RemoveIndexData ?? true,
        }, JsonOptions));
    }

    private static ToolExecutionResult Ok(string output) => ToolExecutionResult.Ok(output);
    private static ToolExecutionResult Fail(string error) => ToolExecutionResult.Fail(error);
}

public sealed record CodeProjectRemoveArgs
{
    [ToolParam("Project identifier to remove.")]
    public required string ProjectId { get; init; }

    [ToolParam("Whether to remove associated index data. Defaults to true.")]
    public bool? RemoveIndexData { get; init; }
}

// ═══════════════════════════════════════════════════════════════
// code_index_list_projects
// ═══════════════════════════════════════════════════════════════

[Tool(
    id: "code_index_list_projects",
    name: "List registered projects",
    description: "List all projects registered in Pudding's code-index registry for the current workspace.",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 202)]
public sealed class CodeProjectListTool : PuddingToolBase<CodeProjectListArgs>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ICodeProjectRegistry? _registry;

    public CodeProjectListTool(ICodeProjectRegistry? registry = null)
    {
        _registry = registry;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        CodeProjectListArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (_registry is null)
            return Fail("Code project tools are not available: ICodeProjectRegistry is not registered.");

        var projects = await _registry.ListProjectsAsync(context.WorkspaceId, ct);

        var list = projects.Select(p => new
        {
            workspace_id = p.WorkspaceId,
            project_id = p.ProjectId,
            display_name = p.DisplayName,
            project_path = p.ProjectPath,
            status = p.Status.ToString(),
            added_at_utc = p.AddedAtUtc,
            updated_at_utc = p.UpdatedAtUtc,
        }).ToList();

        var output = JsonSerializer.Serialize(new
        {
            workspace_id = context.WorkspaceId,
            count = list.Count,
            projects = list,
        }, JsonOptions);

        if (list.Count == 0)
            output += "\n\n💡 Tip: No projects registered. Use project_map to discover project directories, then code_index_register_project to index them.";

        return Ok(output);
    }

    private static ToolExecutionResult Ok(string output) => ToolExecutionResult.Ok(output);
    private static ToolExecutionResult Fail(string error) => ToolExecutionResult.Fail(error);
}

public sealed record CodeProjectListArgs
{
}
