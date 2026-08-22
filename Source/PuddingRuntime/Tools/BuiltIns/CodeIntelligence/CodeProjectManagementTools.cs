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
    description: "在 Pudding 的代码索引注册表中登记本地项目目录（register project）。低风险索引状态变更——不修改或删除任何源文件。索引数据始终可重建。index=true 时在登记后触发语义索引。【何时用】所有基于索引的 CodeIntelligence 查询（code_symbol_search/code_callers/code_callees/code_impact/code_summary）之前必须先登记项目；查询报 project_id is required 或结果为空时，多半是项目未登记。【怎么用】传 project_path（本地目录，必须存在）；可选 project_id/display_name；index=true 在登记后触发后台语义索引，否则仅登记不建索引。【坑】目录不存在会失败；非 YOLO 模式受工作区边界限制；索引是异步后台任务，登记后需用 code_index_status 确认 Completed 再查询，否则查不到符号。Register a local project directory in Pudding's code-index registry — REQUIRED before any index-based query (code_symbol_search/callers/callees/impact/summary); pass project_path (+optional project_id/display_name), set index=true to trigger async semantic indexing; poll code_index_status until Completed before querying.",
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
            && !HostFileToolPaths.TryResolveInsideWorkspace(
                projectPath, out _, out var wsError,
                executionWorkingDirectory: context.WorkingDirectory))
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
    description: "从 Pudding 的代码索引注册表中移除项目（unregister project）。低风险索引状态变更——不删除源文件或目录，仅清除索引注册项与关联的索引数据。【何时用】项目不再需要代码查询/索引时清理注册表；或想重建索引时先移除再重新登记。【怎么用】传 project_id（用 code_index_list_projects 查）；remove_index_data 默认 true，同时删除关联索引数据，设 false 可仅移除注册项保留索引数据。【坑】移除后该项目立即无法被索引查询访问；remove_index_data=true 会删除索引数据（但可重建，源文件不受影响）；确认 project_id 正确再执行，属于状态变更操作。Remove a project from Pudding's code-index registry — use when a project no longer needs code querying or to rebuild its index (remove then re-register); pass project_id from code_index_list_projects; remove_index_data defaults to true and deletes index data (source files untouched, index rebuildable).",
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
    description: "列出当前工作区中已登记到 Pudding 代码索引注册表的所有项目（list projects）。【何时用】开始代码查询前先看有哪些项目已登记、拿到 project_id；查询结果为空时用它排查项目是否已登记。【怎么用】无必填参数，直接调用即可；返回每个项目的 project_id/display_name/project_path/status。【坑】只列出已登记项目，未登记的目录不会出现；status 是注册状态而非索引完成度，索引进度需用 code_index_status 查。List all projects registered in Pudding's code-index registry for the current workspace — call before index-based queries to get project_id/status; no required args; only shows registered projects; use code_index_status for indexing progress.",
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
