using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingPathFiltering;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 查看项目的模块概览：目录 → 核心类的映射关系。
/// 扫描文件系统，使用 Roslyn 语法分析提取每个文件中的顶层类型。
/// 不依赖预构建索引，首次调用即可返回结果。
/// </summary>
[Tool(
    id: "project_map",
    name: "Project module overview",
    description: "查看项目的模块总览（project map）：目录树映射到核心类型，展示命名空间→目录→关键类关系。【何时用】刚接触一个项目时快速建立目录→模块→核心类的整体认知，再决定深入哪里；也可用它发现项目目录，辅助决定 code_index_register_project 登记哪个路径。【怎么用】传 project_path（默认当前工作目录）；depth 控制目录树深度（默认2，范围1-3）。【坑】不依赖预构建索引，纯文件系统扫描，首次调用即可返回；只展示每个文件中的顶层类型，嵌套/私有类型不展开；超大目录有数量上限（每目录最多50文件、每文件最多3个顶层类型）。View a project's module overview: directory tree mapped to core types — use to orient in an unfamiliar codebase (dir → module → core classes) and to discover paths for code_index_register_project; pass project_path, depth 1-3 (default 2); works without a prebuilt index (filesystem scan); shows top-level types only, large dirs are capped (50 files/dir, 3 types/file).",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 218)]
public sealed class ProjectMapTool : PuddingToolBase<ProjectMapArgs>
{
    // 与 CodeProjectMapService 共用同一组上限（单一定义点，避免两处漂移）。
    private const int MaxFilesPerDir = CodeProjectMapService.MaxFilesPerDir;
    private const int MaxTopLevelTypes = CodeProjectMapService.MaxTopLevelTypes;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ICodeProjectMapService _mapService = new CodeProjectMapService();

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        ProjectMapArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var outcome = await _mapService.BuildAsync(args.ProjectPath, context.WorkingDirectory, args.Depth, ct);
        if (!outcome.Success)
            return Fail(outcome.Error!);

        // 排版留在工具层：树形文本与 JSON 投影回答的是「怎么显示」，不是「有什么」。
        var sb = new StringBuilder();
        sb.AppendLine(outcome.RootPath!);
        AppendTreeText(sb, outcome.Root!, 0, outcome.Depth);

        var text = sb.ToString().TrimEnd();

        var treeJson = ToJsonNode(outcome.Root!);
        var json = JsonSerializer.Serialize(new
        {
            path = outcome.RootPath,
            depth = outcome.Depth,
            tree = treeJson,
        }, JsonOptions);

        var output = $"{text}\n\n---\n{json}";
        return Ok(output);
    }

    // ── 文本格式化 ──

    private static void AppendTreeText(StringBuilder sb, ProjectMapNode node, int depth, int maxDepth)
    {
        if (node.IsRoot)
        {
            foreach (var child in node.Children)
                AppendTreeText(sb, child, depth, maxDepth);
            return;
        }

        var indent = new string(' ', depth * 4);
        var prefix = depth == 0 ? "" : "├── ";
        var dirName = node.Name;
        if (node.TotalFileCount > MaxFilesPerDir)
            dirName += $" (showing {MaxFilesPerDir}/{node.TotalFileCount} files)";

        if (depth == 1)
            sb.AppendLine($"{indent}{prefix}{dirName}/");
        else
            sb.AppendLine($"{indent}{prefix}{dirName}/");

        // 输出文件
        for (int i = 0; i < node.Files.Count; i++)
        {
            var file = node.Files[i];
            var fileIndent = new string(' ', (depth + 1) * 4);
            var filePrefix = "├── ";

            if (file.Types.Count > 0)
            {
                var typeDesc = string.Join(", ", file.Types.Take(MaxTopLevelTypes));
                if (file.TotalTypeCount > MaxTopLevelTypes) typeDesc += "...";
                sb.AppendLine($"{fileIndent}{filePrefix}{file.Name}    {typeDesc}");
            }
            else
            {
                sb.AppendLine($"{fileIndent}{filePrefix}{file.Name}");
            }
        }

        // 递归子目录
        foreach (var child in node.Children)
            AppendTreeText(sb, child, depth + 1, maxDepth);
    }

    // ── JSON 序列化 ──

    private static object? ToJsonNode(ProjectMapNode node)
    {
        if (node.IsRoot)
        {
            return node.Children.Select(ToJsonNode).Where(n => n is not null).ToList();
        }

        return new
        {
            name = node.Name,
            type = "directory",
            children = node.Children.Count > 0
                ? node.Children.Select(ToJsonNode).Where(n => n is not null).ToList()
                : null,
            files = node.Files.Select(f => new
            {
                name = f.Name,
                types = f.Types,
            }).ToList(),
        };
    }

    // ── 路径解析 ──

    /// <summary>
    /// 将用户输入的路径解析为规范化绝对路径，并校验其在工作区范围内。
    /// 当前以工作目录为沙箱边界——解析后的路径必须在工作目录子树内。
    // ── 辅助 ──

    private static ToolExecutionResult Ok(string output) => ToolExecutionResult.Ok(output);
    private static ToolExecutionResult Fail(string error) => ToolExecutionResult.Fail(error);

}

public sealed record ProjectMapArgs
{
    [ToolParam("Project root directory path. Defaults to current working directory.")]
    public string? ProjectPath { get; init; }

    [ToolParam("Directory tree expansion depth (1-3). Default is 2.")]
    public int? Depth { get; init; }
}
