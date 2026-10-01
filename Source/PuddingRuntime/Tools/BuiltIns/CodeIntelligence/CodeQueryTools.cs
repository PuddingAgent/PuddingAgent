using System.Text.Json;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Services;
using PuddingCodeIndex.Contracts;

namespace PuddingRuntime.Services.Tools;

/// <summary>Shared helper for scope resolution in query tools.</summary>
internal static class CodeQueryToolHelper
{
    /// <summary>
    /// Resolve and ensure project scope. Auto-detects and registers when no
    /// existing scope matches.
    /// </summary>
    public static async Task<string?> ResolveAndEnsureProjectIdAsync(
        ICodeIndexScopeResolver? resolver,
        string workspaceId,
        string? projectId,
        string? filePath,
        string? scopePath,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(projectId))
            return projectId.Trim();

        if (resolver is null)
            return null;

        // When no hints are given, don't fall back to process working directory.
        // code_symbol_search will search all projects; other tools report "required".
        if (string.IsNullOrWhiteSpace(filePath) && string.IsNullOrWhiteSpace(scopePath))
            return null;

        var resolution = await resolver.ResolveAndEnsureAsync(
            workspaceId, filePath, scopePath, cancellationToken: ct).ConfigureAwait(false);

        return resolution.Scope?.ScopeId;
    }

    /// <summary>
    /// D1 fail-closed 结果里 <c>status</c> 的**单点定义**：该项目未在本 workspace 的注册表中登记。
    /// </summary>
    public const string NotRegisteredStatus = "not_registered";

    /// <summary>
    /// 「该项目是否属于本 workspace 的**已登记项目**」的**唯一定义**（风格对齐
    /// <c>PuddingHost/Services/CodeIndexStatusProbe.IsStale</c>：一处定义、所有判定点共用）。
    /// <para>
    /// 真源与 <c>code_index_list_projects</c> **完全一致** —— 同一个 <see cref="ICodeProjectRegistry.ListProjectsAsync"/>
    /// 结果集、同一个 workspace 口径、逐项 <see cref="StringComparison.Ordinal"/> 比对。
    /// 于是「列表工具里查不到」⇔「这里为 false」，两处口径不可能各自漂移。
    /// </para>
    /// <para>
    /// 刻意**不**做 Trim、**不**忽略大小写：<c>project_id</c> 是索引内的稳定标识，
    /// 放宽比较会把两个不同项目判成同一个，从而又把「已注销」当「已登记」放行 —— 正是 D1 要堵的洞。
    /// </para>
    /// </summary>
    public static bool IsRegistered(IReadOnlyList<CodeProjectRecord> registeredProjects, string projectId)
    {
        foreach (var registered in registeredProjects)
        {
            if (string.Equals(registered.ProjectId, projectId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 「项目未登记」的**单点文案**：D1 的 <c>code_index_status</c> 与 D2 的
    /// <c>code_symbol_search</c> 使用同一句话，避免两处口径各自漂移。
    /// </summary>
    public static string BuildNotRegisteredMessage(string workspaceId, string projectId)
        => $"Project '{projectId}' is not registered in workspace '{workspaceId}' "
           + "(it may have been unregistered); use code_index_list_projects to list the "
           + "currently registered projects.";
}

// ═══════════════════════════════════════════════════════════════
// code_index_status
// ═══════════════════════════════════════════════════════════════

[Tool(
    id: "code_index_status",
    name: "Code index status",
    description: "获取已登记代码项目的当前索引状态（indexing status）。【何时用】登记项目后、执行索引查询前，确认索引是否已完成；查询结果异常/为空时用它诊断是否索引未就绪。【怎么用】传 project_id；也可只传 file_path 或 scope_path 自动探测所属项目。【坑】项目未在注册表登记时**不返回索引状态**，而是显式返回 status=not_registered（该项目可能已注销；用 code_index_list_projects 查当前已登记的 project_id）—— 已登记项目的返回体保持逐字不变；status 为 Pending/Indexing 时查询类工具结果不完整，等 Completed 再查；索引数据随源码变更会过期，重大改动后可重新登记触发重索引。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 210)]
public sealed class CodeIndexStatusTool : PuddingToolBase<CodeIndexStatusArgs>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ICodeQueryService? _queryService;
    private readonly ICodeIndexScopeResolver? _resolver;
    private readonly ICodeProjectRegistry? _registry;

    public CodeIndexStatusTool(
        ICodeQueryService? queryService = null,
        ICodeIndexScopeResolver? resolver = null,
        ICodeProjectRegistry? registry = null)
    {
        _queryService = queryService;
        _resolver = resolver;
        _registry = registry;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        CodeIndexStatusArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (_queryService is null)
            return Fail("Code query tools are not available: ICodeQueryService is not registered.");

        // D1 fail-closed：注册表不可用时与同目录既有工具（CodeProjectManagementTools）用**同一句**文案，
        // 不得静默降级成「当它已登记」。
        if (_registry is null)
            return Fail("Code project tools are not available: ICodeProjectRegistry is not registered.");

        var projectId = await CodeQueryToolHelper.ResolveAndEnsureProjectIdAsync(
            _resolver, context.WorkspaceId, args.ProjectId, args.FilePath, args.ScopePath, ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(projectId))
            return Fail("project_id is required, or provide file_path/scope_path for auto-detection.");

        // D1：注册表是唯一真源。未登记（可能已注销）⇒ 不得返回索引视图里的陈旧 Completed，
        // 也不得泄露陈旧的完成时间。fail-closed，但仍以**成功结果**返回（不抛异常、不 500）。
        if (!CodeQueryToolHelper.IsRegistered(
                await _registry.ListProjectsAsync(context.WorkspaceId, ct).ConfigureAwait(false),
                projectId))
        {
            return Ok(JsonSerializer.Serialize(new
            {
                workspace_id = context.WorkspaceId,
                project_id = projectId,
                status = CodeQueryToolHelper.NotRegisteredStatus,
                message = CodeQueryToolHelper.BuildNotRegisteredMessage(context.WorkspaceId, projectId),
                started_at_utc = (DateTimeOffset?)null,
                completed_at_utc = (DateTimeOffset?)null,
            }, JsonOptions));
        }

        var result = await _queryService.GetProjectIndexStatusAsync(
            context.WorkspaceId,
            projectId,
            ct);

        return Ok(JsonSerializer.Serialize(new
        {
            workspace_id = result.WorkspaceId,
            project_id = result.ProjectId,
            status = result.Status.ToString(),
            message = result.Message,
            started_at_utc = result.StartedAtUtc,
            completed_at_utc = result.CompletedAtUtc,
        }, JsonOptions));
    }

    private static ToolExecutionResult Ok(string output) => ToolExecutionResult.Ok(output);
    private static ToolExecutionResult Fail(string error) => ToolExecutionResult.Fail(error);
}

public sealed record CodeIndexStatusArgs
{
    [ToolParam("Project identifier. If omitted, auto-detected from file_path or scope_path.")]
    public string? ProjectId { get; init; }

    [ToolParam("Optional file path to detect project scope from.")]
    public string? FilePath { get; init; }

    [ToolParam("Optional directory path to detect project scope from.")]
    public string? ScopePath { get; init; }
}

// ═══════════════════════════════════════════════════════════════
// code_symbol_search
// ═══════════════════════════════════════════════════════════════

[Tool(
    id: "code_symbol_search",
    name: "Search code symbols",
    description: "按名称在已登记项目中搜索代码符号（symbol search），结果包含符号种类、文件位置与签名。【何时用】定位某个类/方法/属性的定义与签名时使用；也是 code_callers/code_callees/code_impact 的前置步骤——先用它拿到 symbol_id。【怎么用】传 query（符号名关键词）；可选 project_id 限定项目（不传则跨全部已登记项目搜索）、kind 过滤符号种类、limit 控制数量（默认50）、include_parameters=true 可包含参数符号。【坑】依赖项目已登记且索引完成，否则搜不到；未登记项目直接 fail-closed（status=not_registered）；索引命中会校验文件存在且落在登记项目根目录内，失效命中被计入 stale_skipped 并从结果中剔除——此时 results 为空不等于“符号不存在”，需重登记重建索引或在当前仓库用 search_grep 实时搜索；不传 project_id 时跨项目搜索，重名结果较多；参数与未知符号默认被过滤。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 211)]
public sealed class CodeSymbolSearchTool : PuddingToolBase<CodeSymbolSearchArgs>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ICodeQueryService? _queryService;
    private readonly ICodeIndexScopeResolver? _resolver;
    private readonly ICodeProjectRegistry? _registry;

    public CodeSymbolSearchTool(
        ICodeQueryService? queryService = null,
        ICodeIndexScopeResolver? resolver = null,
        ICodeProjectRegistry? registry = null)
    {
        _queryService = queryService;
        _resolver = resolver;
        _registry = registry;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        CodeSymbolSearchArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (_queryService is null)
            return Fail("Code query tools are not available: ICodeQueryService is not registered. " +
                "Projects are auto-detected when file_path or scope_path is provided.");

        if (string.IsNullOrWhiteSpace(args.Query))
            return Fail("query is required.");

        var projectId = await CodeQueryToolHelper.ResolveAndEnsureProjectIdAsync(
            _resolver, context.WorkspaceId, args.ProjectId, args.FilePath, args.ScopePath, ct)
            .ConfigureAwait(false);

        // D2：注册表是唯一真源。显式项目未登记（可能已注销）⇒ 不得返回陈旧索引里的命中。
        IReadOnlyList<CodeProjectRecord> registeredProjects = _registry is null
            ? Array.Empty<CodeProjectRecord>()
            : await _registry.ListProjectsAsync(context.WorkspaceId, ct).ConfigureAwait(false);

        if (projectId is not null
            && _registry is not null
            && !CodeQueryToolHelper.IsRegistered(registeredProjects, projectId))
        {
            return Fail(CodeQueryToolHelper.BuildNotRegisteredMessage(context.WorkspaceId, projectId));
        }

        CodeSymbolKind? kind = null;
        if (!string.IsNullOrWhiteSpace(args.Kind)
            && Enum.TryParse<CodeSymbolKind>(args.Kind.Trim(), ignoreCase: true, out var parsedKind))
        {
            kind = parsedKind;
        }

        if (!TryParseMatchTarget(args.MatchTarget, out var matchTarget))
            return Fail($"Unknown match_target '{args.MatchTarget}'. Valid values: name, signature, container, all (default).");

        var request = new CodeSymbolSearchRequest(
            WorkspaceId: context.WorkspaceId,
            Query: args.Query.Trim(),
            ProjectId: projectId,
            Kind: kind,
            Limit: args.Limit ?? 50,
            Skip: 0,
            MatchTarget: matchTarget,
            FileExtensions: ParseFileExtensions(args.FileExtensions));

        var results = await _queryService.SearchSymbolsAsync(request, ct);

        // D2 陈旧路径校验：索引可能仍持有旧机器/旧路径的项目（例如仓库已从 E: 迁到 D:）。
        // 命中路径必须真实存在且归属其登记项目根目录，否则不得当权威结果返回。
        var includeStale = args.IncludeStale == true;
        var stale = new List<(CodeSymbolDetail Detail, string Reason)>();
        var rescoped = new List<CodeSymbolDetail>();
        foreach (var detail in results)
        {
            var reason = ClassifyHit(detail.Symbol.FilePath, detail.Symbol.ProjectId, registeredProjects, _registry);
            if (reason is null)
                rescoped.Add(detail);
            else
                stale.Add((detail, reason));
        }

        // 默认过滤参数和未知种类以减少噪音，除非明确要求
        var visible = args.IncludeParameters == true
            ? rescoped
            : rescoped.Where(r => r.Symbol.Kind.ToString() is not ("Parameter" or "Unknown")).ToList();

        // 统一的投影：可信命中 stale_reason=null；include_stale=true 时陈旧命中带原因一并返回。
        object Project(CodeSymbolDetail r, string? staleReason) => new
        {
            symbol_id = r.Symbol.SymbolId,
            name = r.Symbol.Name,
            kind = r.Symbol.Kind.ToString(),
            signature = r.Symbol.Signature,
            container = r.Symbol.Container,
            file_path = r.Symbol.FilePath,
            start_line = r.Symbol.StartLine,
            end_line = r.Symbol.EndLine,
            display_name = r.DisplayName,
            project_id = r.Symbol.ProjectId,
            stale_reason = staleReason,
        };

        var list = visible.Select(r => Project(r, null)).ToList();
        if (includeStale)
            list.AddRange(stale.Select(s => Project(s.Detail, s.Reason)));

        var staleExamples = stale
            .Take(5)
            .Select(s => new { file_path = s.Detail.Symbol.FilePath, project_id = s.Detail.Symbol.ProjectId, reason = s.Reason })
            .ToList();

        var output = JsonSerializer.Serialize(new
        {
            workspace_id = context.WorkspaceId,
            query = args.Query.Trim(),
            kind = kind?.ToString(),
            // 覆盖范围与完整性：让调用方在"没找到"与"没搜到"之间做区分。
            searched_scope = projectId ?? "(all registered projects in workspace)",
            registered_project_count = _registry is null ? (int?)null : registeredProjects.Count,
            complete = _registry is not null,
            count = list.Count,
            stale_skipped = stale.Count,
            stale_examples = staleExamples,
            results = list,
            results_include_stale = includeStale,
        }, JsonOptions);

        if (stale.Count > 0)
        {
            output += $"\n\n⚠️ {stale.Count} index hit(s) were rejected as stale (file missing or outside the "
                + "registered project root)"
                + (includeStale ? " and are marked with `stale_reason`." : " and are NOT part of the authoritative results.")
                + " The index may belong to an older checkout; re-register the project "
                + "(code_index_register_project) to rebuild it, or run a live `search_grep` in the current repository.";
        }

        // 当没有任何项目被索引或没有匹配结果时，给出有帮助的提示
        if (visible.Count == 0)
        {
            var hint = stale.Count > 0
                ? $"\n\n💡 Tip: All {stale.Count} hit(s) for '{args.Query.Trim()}' were stale; this is not evidence "
                  + "that the symbol is absent from the current checkout."
                : projectId == null
                    ? "\n\n💡 Tip: No matching symbols found across all indexed projects. " +
                      "Auto-detection works when file_path or scope_path is provided."
                    : $"\n\n💡 Tip: No symbols matching '{args.Query.Trim()}' found in project '{projectId}'." +
                      " Try a different query, or use code_index_list_projects to see registered projects.";
            output += hint;
        }

        return Ok(output);
    }

    /// <summary>
    /// D2：判定一条索引命中是否可信。返回 null 表示可信；否则返回拒绝原因。
    /// 判定只看两条可证伪的事实：文件是否存在、路径是否落在其登记项目根目录内。
    /// 注册表不可用时（降级组合）不做归属判定，只做存在性判定。
    /// </summary>
    private static string? ClassifyHit(
        string? filePath,
        string? hitProjectId,
        IReadOnlyList<CodeProjectRecord> registeredProjects,
        ICodeProjectRegistry? registry)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return "missing_file_path";

        if (!File.Exists(filePath))
            return "file_not_found";

        if (registry is null)
            return null;

        var owner = registeredProjects.FirstOrDefault(p =>
            string.Equals(p.ProjectId, hitProjectId, StringComparison.Ordinal));
        if (owner is null)
            return "project_not_registered";

        var root = owner.ProjectPath;
        if (string.IsNullOrWhiteSpace(root))
            return "project_root_unknown";

        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var normalizedHit = Path.GetFullPath(filePath);
            var underRoot = normalizedHit.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
            return underRoot ? null : "outside_registered_root";
        }
        catch (Exception)
        {
            // 路径不是合法路径时同样不可信：不做猜测。
            return "invalid_path";
        }
    }

    /// <summary>
    /// ADR-089 §2.3：解析匹配域（逗号/分号分隔）。未知取值返回 false ⇒ 调用方 fail-closed，
    /// 不猜、不静默当成全开（静默会把"筛错了"变成"看起来筛过了"）。
    /// </summary>
    private static bool TryParseMatchTarget(string? raw, out CodeSymbolMatchTarget target)
    {
        target = CodeSymbolMatchTarget.All;
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        var parsed = CodeSymbolMatchTarget.None;
        foreach (var part in raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "all": return true;
                case "name": parsed |= CodeSymbolMatchTarget.Name; break;
                case "signature": parsed |= CodeSymbolMatchTarget.Signature; break;
                case "container": parsed |= CodeSymbolMatchTarget.Container; break;
                default: return false;
            }
        }

        target = parsed == CodeSymbolMatchTarget.None ? CodeSymbolMatchTarget.All : parsed;
        return true;
    }

    /// <summary>
    /// ADR-089 §2.3「文件类型」过滤面：解析逗号/分号分隔的扩展名（前导点可选，归一化交给存储层）。
    /// 省略或全空白 ⇒ 返回 <c>null</c>（不过滤，与历史行为一致）。
    /// </summary>
    private static IReadOnlyList<string>? ParseFileExtensions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var parts = raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : parts;
    }

    private static ToolExecutionResult Ok(string output) => ToolExecutionResult.Ok(output);
    private static ToolExecutionResult Fail(string error) => ToolExecutionResult.Fail(error);
}

public sealed record CodeSymbolSearchArgs
{
    [ToolParam("Search query text. By default it matches symbol name, signature AND container (pre-existing behaviour); use match_target to narrow the match domain.")]
    public required string Query { get; init; }

    [ToolParam("Optional project to scope search to. Auto-detected from file_path/scope_path if omitted.")]
    public string? ProjectId { get; init; }

    [ToolParam("Optional file path to detect project scope from.")]
    public string? FilePath { get; init; }

    [ToolParam("Optional directory path to detect project scope from.")]
    public string? ScopePath { get; init; }

    [ToolParam("Optional symbol kind filter: Namespace, Class, Method, Property, Field, etc.")]
    public string? Kind { get; init; }

    [ToolParam("Maximum results to return. Default 50.")]
    public int? Limit { get; init; }

    [ToolParam("是否包含参数 (Parameter) 和未知 (Unknown) 符号种类。默认 false，过滤以减少噪音。")]
    public bool? IncludeParameters { get; init; }

    [ToolParam("可选：匹配域，逗号分隔。name / signature / container / all（默认 all = 三列全开，与历史行为一致）。"
        + "name 只匹配符号名（例：查 Conf 只返回名字含 Conf 的符号，不再返回签名里提到它的构造器）。")]
    public string? MatchTarget { get; init; }

    [ToolParam("可选：文件类型（扩展名）过滤，逗号分隔，前导点可选、大小写不敏感，如 \"cs\" 或 \".cs,.ts\"。"
        + "只返回这些扩展名文件里定义的符号；省略 = 不过滤（跨全部语言，与历史行为一致）。")]
    public string? FileExtensions { get; init; }

    [ToolParam("默认 false：索引命中必须通过校验（文件存在且落在其登记项目根目录内），"
        + "失效命中会被拒绝并计入 stale_skipped，不会出现在 results 里。"
        + "仅在需要排查陈旧索引时设为 true，把原始行一并返回。")]
    public bool? IncludeStale { get; init; }
}

// ═══════════════════════════════════════════════════════════════
// code_explore
// ═══════════════════════════════════════════════════════════════

[Tool(
    id: "code_explore",
    name: "Explore code symbol",
    description: "探索代码符号（如命名空间或类型）的子符号（contained symbols）。【何时用】想查看某命名空间/类/接口下包含哪些成员（方法、属性、嵌套类型）、快速了解结构时使用；也是符号检索后的下钻步骤。【怎么用】项目必须先 code_index_register_project 登记且 code_index_status 为 Completed；传 symbol_id（必填，来自 code_symbol_search/code_explore 结果），可选 project_id 或 file_path/scope_path 自动探测项目。【坑】未登记/未索引完成会报 project_id is required 或结果为空；symbol_id 是索引内稳定标识，不能直接传符号名。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 212)]
public sealed class CodeExploreTool : PuddingToolBase<CodeExploreArgs>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ICodeQueryService? _queryService;
    private readonly ICodeIndexScopeResolver? _resolver;

    public CodeExploreTool(
        ICodeQueryService? queryService = null,
        ICodeIndexScopeResolver? resolver = null)
    {
        _queryService = queryService;
        _resolver = resolver;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        CodeExploreArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (_queryService is null)
            return Fail("Code query tools are not available: ICodeQueryService is not registered.");

        if (string.IsNullOrWhiteSpace(args.SymbolId))
            return Fail("symbol_id is required.");

        var projectId = await CodeQueryToolHelper.ResolveAndEnsureProjectIdAsync(
            _resolver, context.WorkspaceId, args.ProjectId, args.FilePath, args.ScopePath, ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(projectId))
            return Fail("project_id is required, or provide file_path/scope_path for auto-detection.");

        var results = await _queryService.ExploreAsync(
            context.WorkspaceId,
            projectId,
            args.SymbolId.Trim(),
            ct);

        var list = results.Select(r => new
        {
            symbol_id = r.SymbolId,
            name = r.Name,
            kind = r.Kind.ToString(),
            signature = r.Signature,
            file_path = r.FilePath,
            start_line = r.StartLine,
            end_line = r.EndLine,
            container = r.Container,
        }).ToList();

        return Ok(JsonSerializer.Serialize(new
        {
            workspace_id = context.WorkspaceId,
            project_id = projectId,
            symbol_id = args.SymbolId.Trim(),
            count = list.Count,
            children = list,
        }, JsonOptions));
    }

    private static ToolExecutionResult Ok(string output) => ToolExecutionResult.Ok(output);
    private static ToolExecutionResult Fail(string error) => ToolExecutionResult.Fail(error);
}

public sealed record CodeExploreArgs
{
    [ToolParam("Project identifier. Auto-detected from file_path/scope_path if omitted.")]
    public string? ProjectId { get; init; }

    [ToolParam("Symbol identifier to explore.")]
    public required string SymbolId { get; init; }

    [ToolParam("Optional file path to detect project scope from.")]
    public string? FilePath { get; init; }

    [ToolParam("Optional directory path to detect project scope from.")]
    public string? ScopePath { get; init; }
}

// ═══════════════════════════════════════════════════════════════
// code_callers
// ═══════════════════════════════════════════════════════════════

[Tool(
    id: "code_callers",
    name: "Find callers",
    description: "查找所有调用指定符号（symbol）的调用方（callers）。【何时用】修改/删除某函数或方法前评估谁在调用它、追踪调用链与重构影响时使用。【怎么用】先用 code_symbol_search 定位符号拿到 symbol_id，再传 symbol_id；可选 project_id，或传 file_path/scope_path 自动探测。【坑】依赖项目已登记且索引完成；symbol_id 是索引中的稳定标识，需从搜索/探索结果获取，不能直接传符号名；仅覆盖已索引项目内的调用，外部引用查不到。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 213)]
public sealed class CodeCallersTool : PuddingToolBase<CodeCallersArgs>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ICodeQueryService? _queryService;
    private readonly ICodeIndexScopeResolver? _resolver;

    public CodeCallersTool(
        ICodeQueryService? queryService = null,
        ICodeIndexScopeResolver? resolver = null)
    {
        _queryService = queryService;
        _resolver = resolver;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        CodeCallersArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (_queryService is null)
            return Fail("Code query tools are not available: ICodeQueryService is not registered.");

        if (string.IsNullOrWhiteSpace(args.SymbolId))
            return Fail("symbol_id is required.");

        var projectId = await CodeQueryToolHelper.ResolveAndEnsureProjectIdAsync(
            _resolver, context.WorkspaceId, args.ProjectId, args.FilePath, args.ScopePath, ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(projectId))
            return Fail("project_id is required, or provide file_path/scope_path for auto-detection.");

        var results = await _queryService.GetCallersAsync(
            context.WorkspaceId,
            projectId,
            args.SymbolId.Trim(),
            ct);

        var list = results.Select(r => new
        {
            source_symbol_id = r.SourceSymbolId,
            target_symbol_id = r.TargetSymbolId,
            kind = r.Kind.ToString(),
            source_file = r.SourceFilePath,
            source_line = r.SourceLine,
        }).ToList();

        return Ok(JsonSerializer.Serialize(new
        {
            workspace_id = context.WorkspaceId,
            project_id = projectId,
            symbol_id = args.SymbolId.Trim(),
            count = list.Count,
            callers = list,
        }, JsonOptions));
    }

    private static ToolExecutionResult Ok(string output) => ToolExecutionResult.Ok(output);
    private static ToolExecutionResult Fail(string error) => ToolExecutionResult.Fail(error);
}

public sealed record CodeCallersArgs
{
    [ToolParam("Project identifier. Auto-detected from file_path/scope_path if omitted.")]
    public string? ProjectId { get; init; }

    [ToolParam("Symbol identifier to find callers for.")]
    public required string SymbolId { get; init; }

    [ToolParam("Optional file path to detect project scope from.")]
    public string? FilePath { get; init; }

    [ToolParam("Optional directory path to detect project scope from.")]
    public string? ScopePath { get; init; }
}

// ═══════════════════════════════════════════════════════════════
// code_callees
// ═══════════════════════════════════════════════════════════════

[Tool(
    id: "code_callees",
    name: "Find callees",
    description: "查找指定符号（symbol）调用的所有被调用方（callees）。【何时用】理解某函数/方法调用了哪些其他符号、梳理依赖面与调用链时使用。【怎么用】先用 code_symbol_search 拿到 symbol_id，再传 symbol_id；可选 project_id 或 file_path/scope_path。【坑】依赖项目已登记且索引完成；symbol_id 需从搜索/探索结果获取；只返回直接调用的被调用方，多层传递可配合 code_impact 或逐层展开。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 214)]
public sealed class CodeCalleesTool : PuddingToolBase<CodeCalleesArgs>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ICodeQueryService? _queryService;
    private readonly ICodeIndexScopeResolver? _resolver;

    public CodeCalleesTool(
        ICodeQueryService? queryService = null,
        ICodeIndexScopeResolver? resolver = null)
    {
        _queryService = queryService;
        _resolver = resolver;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        CodeCalleesArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (_queryService is null)
            return Fail("Code query tools are not available: ICodeQueryService is not registered.");

        if (string.IsNullOrWhiteSpace(args.SymbolId))
            return Fail("symbol_id is required.");

        var projectId = await CodeQueryToolHelper.ResolveAndEnsureProjectIdAsync(
            _resolver, context.WorkspaceId, args.ProjectId, args.FilePath, args.ScopePath, ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(projectId))
            return Fail("project_id is required, or provide file_path/scope_path for auto-detection.");

        var results = await _queryService.GetCalleesAsync(
            context.WorkspaceId,
            projectId,
            args.SymbolId.Trim(),
            ct);

        var list = results.Select(r => new
        {
            source_symbol_id = r.SourceSymbolId,
            target_symbol_id = r.TargetSymbolId,
            kind = r.Kind.ToString(),
            source_file = r.SourceFilePath,
            source_line = r.SourceLine,
        }).ToList();

        return Ok(JsonSerializer.Serialize(new
        {
            workspace_id = context.WorkspaceId,
            project_id = projectId,
            symbol_id = args.SymbolId.Trim(),
            count = list.Count,
            callees = list,
        }, JsonOptions));
    }

    private static ToolExecutionResult Ok(string output) => ToolExecutionResult.Ok(output);
    private static ToolExecutionResult Fail(string error) => ToolExecutionResult.Fail(error);
}

public sealed record CodeCalleesArgs
{
    [ToolParam("Project identifier. Auto-detected from file_path/scope_path if omitted.")]
    public string? ProjectId { get; init; }

    [ToolParam("Symbol identifier to find callees for.")]
    public required string SymbolId { get; init; }

    [ToolParam("Optional file path to detect project scope from.")]
    public string? FilePath { get; init; }

    [ToolParam("Optional directory path to detect project scope from.")]
    public string? ScopePath { get; init; }
}

// ═══════════════════════════════════════════════════════════════
// code_impact
// ═══════════════════════════════════════════════════════════════

[Tool(
    id: "code_impact",
    name: "Code impact analysis",
    description: "通过递归遍历调用方，计算符号（symbol）的下游影响（impact），直到指定深度。【何时用】改动核心/公共符号前评估影响面大小与波及范围，用于变更风险分级与回归范围圈定。【怎么用】先用 code_symbol_search 拿到 symbol_id，再传 symbol_id；max_depth 控制递归深度（默认3，范围1-10，越大结果越全也越慢）。【坑】依赖项目已登记且索引完成；必须传 project_id 或 file_path/scope_path 确定项目范围；深度过大可能返回大量符号，建议从默认深度开始再逐步加深。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 215)]
public sealed class CodeImpactTool : PuddingToolBase<CodeImpactArgs>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly ICodeQueryService? _queryService;
    private readonly ICodeIndexScopeResolver? _resolver;

    public CodeImpactTool(
        ICodeQueryService? queryService = null,
        ICodeIndexScopeResolver? resolver = null)
    {
        _queryService = queryService;
        _resolver = resolver;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        CodeImpactArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (_queryService is null)
            return Fail("Code query tools are not available: ICodeQueryService is not registered.");

        var projectId = await CodeQueryToolHelper.ResolveAndEnsureProjectIdAsync(
            _resolver, context.WorkspaceId, args.ProjectId, args.FilePath, args.ScopePath, ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(projectId))
            return Fail("project_id is required.");
        if (string.IsNullOrWhiteSpace(args.SymbolId))
            return Fail("symbol_id is required.");

        var maxDepth = args.MaxDepth ?? 3;
        if (maxDepth < 1) maxDepth = 1;
        if (maxDepth > 10) maxDepth = 10;

        var results = await _queryService.GetImpactAsync(
            context.WorkspaceId,
            projectId,
            args.SymbolId.Trim(),
            maxDepth,
            ct);

        var list = results.Select(r => new
        {
            symbol_id = r.SymbolId,
            name = r.Name,
            kind = r.Kind.ToString(),
            signature = r.Signature,
            file_path = r.FilePath,
            start_line = r.StartLine,
            container = r.Container,
        }).ToList();

        return Ok(JsonSerializer.Serialize(new
        {
            workspace_id = context.WorkspaceId,
            project_id = projectId,
            symbol_id = args.SymbolId.Trim(),
            max_depth = maxDepth,
            count = list.Count,
            impacted = list,
        }, JsonOptions));
    }

    private static ToolExecutionResult Ok(string output) => ToolExecutionResult.Ok(output);
    private static ToolExecutionResult Fail(string error) => ToolExecutionResult.Fail(error);
}

public sealed record CodeImpactArgs
{
    [ToolParam("Project identifier. Auto-detected from file_path/scope_path if omitted.")]
    public string? ProjectId { get; init; }

    [ToolParam("Symbol identifier to analyze impact for.")]
    public required string SymbolId { get; init; }

    [ToolParam("Maximum traversal depth. Default 3, clamped between 1 and 10.")]
    public int? MaxDepth { get; init; }

    [ToolParam("Optional file path to detect project scope from.")]
    public string? FilePath { get; init; }

    [ToolParam("Optional directory path to detect project scope from.")]
    public string? ScopePath { get; init; }
}
