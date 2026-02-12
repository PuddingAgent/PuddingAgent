using System.Text.Json;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Services;

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
}

// ═══════════════════════════════════════════════════════════════
// code_index_status
// ═══════════════════════════════════════════════════════════════

[Tool(
    id: "code_index_status",
    name: "Code index status",
    description: "Get the current indexing status for a registered code project.",
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

    public CodeIndexStatusTool(
        ICodeQueryService? queryService = null,
        ICodeIndexScopeResolver? resolver = null)
    {
        _queryService = queryService;
        _resolver = resolver;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        CodeIndexStatusArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (_queryService is null)
            return Fail("Code query tools are not available: ICodeQueryService is not registered.");

        var projectId = await CodeQueryToolHelper.ResolveAndEnsureProjectIdAsync(
            _resolver, context.WorkspaceId, args.ProjectId, args.FilePath, args.ScopePath, ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(projectId))
            return Fail("project_id is required, or provide file_path/scope_path for auto-detection.");

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
    description: "Search code symbols by name across registered projects. Results include symbol kind, file location, and signature.",
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

    public CodeSymbolSearchTool(
        ICodeQueryService? queryService = null,
        ICodeIndexScopeResolver? resolver = null)
    {
        _queryService = queryService;
        _resolver = resolver;
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

        CodeSymbolKind? kind = null;
        if (!string.IsNullOrWhiteSpace(args.Kind)
            && Enum.TryParse<CodeSymbolKind>(args.Kind.Trim(), ignoreCase: true, out var parsedKind))
        {
            kind = parsedKind;
        }

        var request = new CodeSymbolSearchRequest(
            WorkspaceId: context.WorkspaceId,
            Query: args.Query.Trim(),
            ProjectId: projectId,
            Kind: kind,
            Limit: args.Limit ?? 50,
            Skip: 0);

        var results = await _queryService.SearchSymbolsAsync(request, ct);

        var list = results.Select(r => new
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
        }).ToList();

        // 默认过滤参数和未知种类以减少噪音，除非明确要求
        if (args.IncludeParameters != true)
        {
            var filtered = list.Count;
            list = list.Where(r => r.kind != "Parameter" && r.kind != "Unknown").ToList();
            filtered -= list.Count;
            // filtered entries silently dropped (use include_parameters=true to see them)
        }

        var output = JsonSerializer.Serialize(new
        {
            workspace_id = context.WorkspaceId,
            query = args.Query.Trim(),
            kind = kind?.ToString(),
            count = list.Count,
            results = list,
        }, JsonOptions);

        // 当没有任何项目被索引或没有匹配结果时，给出有帮助的提示
        if (list.Count == 0)
        {
            var hint = projectId == null
                ? "\n\n💡 Tip: No matching symbols found across all indexed projects. " +
                  "Auto-detection works when file_path or scope_path is provided."
                : $"\n\n💡 Tip: No symbols matching '{args.Query.Trim()}' found in project '{projectId}'." +
                  " Try a different query, or use code_index_list_projects to see registered projects.";
            output += hint;
        }

        return Ok(output);
    }

    private static ToolExecutionResult Ok(string output) => ToolExecutionResult.Ok(output);
    private static ToolExecutionResult Fail(string error) => ToolExecutionResult.Fail(error);
}

public sealed record CodeSymbolSearchArgs
{
    [ToolParam("Search query matched against symbol names.")]
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
}

// ═══════════════════════════════════════════════════════════════
// code_explore
// ═══════════════════════════════════════════════════════════════

[Tool(
    id: "code_explore",
    name: "Explore code symbol",
    description: "Explore the children (contained symbols) of a code symbol such as a namespace or type.",
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
    description: "Find all symbols that call the specified symbol.",
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
    description: "Find all symbols called by the specified symbol.",
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
    description: "Compute the downstream impact of a symbol by traversing callers recursively up to the specified depth.",
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
