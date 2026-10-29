using System.Text.Json;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingCodeIndex.Contracts;
using PuddingCodeIntelligence.Contracts;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 按视图打开工作区里的一个文件或目录：<c>excerpt</c>（行切片）/ <c>outline</c>（结构树）/ <c>map</c>（目录地图）。
/// <para>
/// <b>S5 接线</b>：与 <c>code_outline</c> / <c>project_map</c> 一致，服务在构造时内部 <c>new</c>，
/// 不引入构造注入。原因见 <see cref="WorkspaceOpenService"/> 上游代码注释：
/// <c>AddPuddingToolsFromAssembly</c> 会解析**每一个**工具，多一个无默认值的构造参数
/// 就多一个「没注册就启动期失败」的坑 —— 2026-10-29 宿主启动崩溃（core exit -532462766）
/// 正是这条。可见的首个真实故障形态是 <c>Unable to resolve service for type 'IRetrievalPort'</c>。
/// </para>
/// <para>
/// <b>边界校验不在本层</b>：路径必须落在 workspace scope 根子树内的策略在服务层，
/// 工具只负责把字符串视图翻成枚举、把结构化结果翻成 JSON。
/// </para>
/// </summary>
[Tool(
    id: "workspace_open",
    name: "Open workspace file",
    description: "按视图打开工作区内的文件或目录：view=excerpt 取行切片（可给 start_line/line_count/max_bytes），view=outline 取顶层类型结构树，view=map 取目录树与每个文件的核心类型。路径必须在当前工作区内。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 216)]
public sealed class WorkspaceOpenTool : PuddingToolBase<WorkspaceOpenArgs>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly IWorkspaceOpenService _openService;

    public WorkspaceOpenTool(
        IFileOutlinerRegistry? outlinerRegistry = null,
        ICodeIndexScopeResolver? resolver = null)
    {
        // 三个服务都在内部构造：工具程序集扫描只要求工具本身可解析，不需要 DI 认识这些服务。
        _openService = new WorkspaceOpenService(
            new CodeOutlineService(outlinerRegistry),
            resolver,
            new CodeProjectMapService());
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        WorkspaceOpenArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (!TryParseView(args.View, out var view, out var viewError))
        {
            return Fail(viewError!);
        }

        if (string.IsNullOrWhiteSpace(args.Path))
        {
            return Fail("path is required（v1 只支持按路径打开）");
        }

        var request = new WorkspaceOpenRequest(
            context.WorkspaceId,
            ProjectId: null,
            args.Path,
            view,
            StartLine: args.StartLine,
            LineCount: args.LineCount,
            Depth: args.Depth,
            MaxBytes: args.MaxBytes);

        var result = await _openService.OpenAsync(request, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return Fail(result.Error!);
        }

        return Ok(Json(ToOutput(result)));
    }

    /// <summary>视图字符串 → 枚举。未知值显式报错，不静默降级到 excerpt。</summary>
    private static bool TryParseView(string? raw, out WorkspaceOpenView view, out string? error)
    {
        error = null;
        view = WorkspaceOpenView.Excerpt;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "excerpt":
                view = WorkspaceOpenView.Excerpt;
                return true;
            case "outline":
                view = WorkspaceOpenView.Outline;
                return true;
            case "map":
                view = WorkspaceOpenView.Map;
                return true;

            // 下面三个在规范 §8.3 里**已经定义**，只是 v1 没实现。
            // 必须与「拼错的名字」分开报：说「不支持的 view」会让调用方以为自己写错了，
            // 于是反复换写法试错；而真实原因是能力缺口。§8.3 要求能力声明如实，
            // 不能把「还没做」含混成「没有这个东西」。
            case "summary":
                return Deferred(raw, "按符号名取摘要请用 code_summary", out error);
            case "relations":
                return Deferred(raw, "取调用关系请用 code_callers / code_callees", out error);
            case "status":
                return Deferred(raw, "查索引状态请用 code_index_status", out error);

            default:
                // ADR-089 §8.3：不认识的视图必须报错。把它当 excerpt 返回会给出
                // 「声称是 A、内容却是 B」的结果 —— 那比失败更难发现。
                error = $"不支持的 view: {raw}（v1 支持 excerpt / outline / map）";
                return false;
        }
    }

    /// <summary>
    /// 规范已定义、v1 未实现的视图：给出「已定义但未实现 + 该用哪个工具」的可执行原因。
    /// </summary>
    private static bool Deferred(string raw, string alternative, out string? error)
    {
        error = $"view={raw.Trim().ToLowerInvariant()} 在规范 §8.3 已定义，但 v1 尚未实现（依赖 symbol_ref）。{alternative}。";
        return false;
    }

    /// <summary>结构化结果 → JSON 投影。按视图放各自的键，调用方不必按类型分支。</summary>
    private static object ToOutput(WorkspaceOpenResult result)
    {
        var head = new Dictionary<string, object?>
        {
            ["view"] = result.View.ToString().ToLowerInvariant(),
            ["resolved_path"] = result.ResolvedPath,
            ["truncated"] = result.Truncated,
            ["truncated_reason"] = result.TruncatedReason,
        };

        switch (result.Payload)
        {
            case ExcerptPayload excerpt:
                head["excerpt"] = new
                {
                    start_line = excerpt.StartLine,
                    total_line_count = excerpt.TotalLineCount,
                    line_count = excerpt.Lines.Count,
                    lines = excerpt.Lines,
                };
                break;

            case OutlinePayload outline:
                head["outline"] = new
                {
                    total_count = outline.TotalCount,
                    node_count = outline.Nodes.Count,
                    truncated = outline.Truncated,
                    nodes = outline.Nodes.Select(EntryToJson).ToList(),
                };
                break;

            case MapPayload map:
                head["map"] = new
                {
                    total_file_count = map.TotalFileCount,
                    root = EntryToJson(map.Root),
                };
                break;

            default:
                head["payload_kind"] = result.Payload?.GetType().Name ?? "(null)";
                break;
        }

        return head;
    }

    private static object EntryToJson(OutlineEntry entry) => new
    {
        name = entry.Name,
        kind = entry.Kind,
        start_line = entry.StartLine,
        end_line = entry.EndLine,
        signature = entry.Signature,
        container = entry.Container,
        children = entry.Children is { Count: > 0 }
            ? entry.Children.Select(EntryToJson).ToList()
            : null,
    };

    private static object EntryToJson(MapEntry entry) => new
    {
        name = entry.Name,
        total_file_count = entry.TotalFileCount,
        files = entry.Files.Select(f => new
        {
            name = f.Name,
            types = f.Types,
            // 真实类型总数：types 被 MaxTopLevelTypes 截断，靠它才能看出「还有更多」。
            total_type_count = f.TotalTypeCount,
        }).ToList(),
        children = entry.Children.Select(EntryToJson).ToList(),
    };

    private static string Json(object obj) => JsonSerializer.Serialize(obj, JsonOptions);
    private static ToolExecutionResult Ok(string output) => ToolExecutionResult.Ok(output);
    private static ToolExecutionResult Fail(string error) => ToolExecutionResult.Fail(error);
}

/// <summary><c>workspace_open</c> 的入参。</summary>
public sealed record WorkspaceOpenArgs
{
    [ToolParam("Workspace-relative or absolute path of the file/directory to open. Must stay inside the workspace.")]
    public required string Path { get; init; }

    [ToolParam("View: excerpt (line slice, default) | outline (top-level type tree) | map (directory tree).")]
    public string? View { get; init; }

    [ToolParam("excerpt only: 1-based first line to return (default 1).")]
    public int? StartLine { get; init; }

    [ToolParam("excerpt only: how many lines to return (default 80).")]
    public int? LineCount { get; init; }

    [ToolParam("map only: recursion depth (default 2, clamped to 1..5 by the map service).")]
    public int? Depth { get; init; }

    [ToolParam("excerpt only: hard cap on returned content bytes (default 65536).")]
    public int MaxBytes { get; init; } = 64 * 1024;
}
