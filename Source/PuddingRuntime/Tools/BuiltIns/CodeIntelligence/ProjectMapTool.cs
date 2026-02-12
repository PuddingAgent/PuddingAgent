using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 查看项目的模块概览：目录 → 核心类的映射关系。
/// 扫描文件系统，使用 Roslyn 语法分析提取每个文件中的顶层类型。
/// 不依赖预构建索引，首次调用即可返回结果。
/// </summary>
[Tool(
    id: "project_map",
    name: "Project module overview",
    description: "View a project's module overview: directory tree mapped to core types. Shows namespace → directory → key class relationships.",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 218)]
public sealed class ProjectMapTool : PuddingToolBase<ProjectMapArgs>
{
    private const int MaxFilesPerDir = 50;
    private const int MaxTopLevelTypes = 3;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        ProjectMapArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var rootPath = ResolveRootPath(args.ProjectPath);
        var depth = Math.Clamp(args.Depth ?? 2, 1, 3);

        if (!Directory.Exists(rootPath))
            return Fail($"(目录不存在: {rootPath})");

        // 构建目录树
        var root = await BuildDirectoryNodeAsync(rootPath, rootPath, depth, 0, ct);

        // 格式化文本输出
        var sb = new StringBuilder();
        sb.AppendLine(rootPath);
        AppendTreeText(sb, root, 0, depth);

        var text = sb.ToString().TrimEnd();

        // 格式化 JSON 输出
        var treeJson = ToJsonNode(root);
        var json = JsonSerializer.Serialize(new
        {
            path = rootPath,
            depth,
            tree = treeJson,
        }, JsonOptions);

        var output = $"{text}\n\n---\n{json}";
        return Ok(output);
    }

    // ── 目录树构建 ──

    private static async Task<DirNode> BuildDirectoryNodeAsync(
        string rootPath,
        string currentPath,
        int maxDepth,
        int currentDepth,
        CancellationToken ct)
    {
        var node = new DirNode
        {
            Name = currentDepth == 0 ? currentPath : Path.GetFileName(currentPath),
            IsRoot = currentDepth == 0,
        };

        // 扫描 .cs 文件
        try
        {
            var csFiles = Directory.GetFiles(currentPath, "*.cs").Take(MaxFilesPerDir).ToList();
            foreach (var file in csFiles)
            {
                ct.ThrowIfCancellationRequested();
                var (types, totalTypeCount) = await GetFileTopTypesAsync(file, ct);
                var fileName = Path.GetFileName(file);
                node.Files.Add(new FileEntry
                {
                    Name = fileName,
                    Types = types,
                    TotalTypeCount = totalTypeCount,
                });
            }
            node.TotalFileCount = csFiles.Count;
        }
        catch
        {
            // 权限不足等，跳过
        }

        // 递归子目录
        if (currentDepth < maxDepth)
        {
            try
            {
                var dirs = Directory.GetDirectories(currentPath);
                // 跳过隐藏目录和常见非源码目录
                var filtered = dirs
                    .Where(d =>
                    {
                        var name = Path.GetFileName(d);
                        return !name.StartsWith('.')
                            && name != "bin"
                            && name != "obj"
                            && name != "node_modules"
                            && name != ".git";
                    })
                    .ToList();

                foreach (var dir in filtered)
                {
                    ct.ThrowIfCancellationRequested();
                    var child = await BuildDirectoryNodeAsync(rootPath, dir, maxDepth, currentDepth + 1, ct);
                    if (child.Files.Count > 0 || child.Children.Count > 0)
                        node.Children.Add(child);
                }
            }
            catch
            {
                // 权限不足等
            }
        }

        return node;
    }

    /// <summary>
    /// 使用 Roslyn 语法分析提取文件中的顶层类型名称。
    /// 不需要索引器，毫秒级响应。
    /// </summary>
    private static async Task<(List<string> Types, int TotalTypeCount)> GetFileTopTypesAsync(string filePath, CancellationToken ct)
    {
        var types = new List<string>();
        var totalTypeCount = 0;
        try
        {
            var sourceText = await File.ReadAllTextAsync(filePath, ct);
            var tree = CSharpSyntaxTree.ParseText(sourceText);
            var root = await tree.GetRootAsync(ct);

            var visitor = new OutlineSyntaxVisitor();
            visitor.Visit(root);

            totalTypeCount = visitor.RootNodes.Count;

            foreach (var node in visitor.RootNodes.Take(MaxTopLevelTypes))
            {
                var kind = node.Kind switch
                {
                    "class" => "class",
                    "interface" => "interface",
                    "struct" => "struct",
                    "enum" => "enum",
                    "record" => "record",
                    _ => node.Kind,
                };
                types.Add($"{kind} {node.Name}");
            }
        }
        catch
        {
            // 解析失败跳过
        }
        return (types, totalTypeCount);
    }

    // ── 文本格式化 ──

    private static void AppendTreeText(StringBuilder sb, DirNode node, int depth, int maxDepth)
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

    private static object? ToJsonNode(DirNode node)
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
    /// </summary>
    private static string ResolveRootPath(string? projectPath)
    {
        var cwd = Path.GetFullPath(Directory.GetCurrentDirectory());
        string resolved;

        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            var trimmed = projectPath.Trim();
            resolved = Path.IsPathRooted(trimmed)
                ? Path.GetFullPath(trimmed)
                : Path.GetFullPath(Path.Combine(cwd, trimmed));
        }
        else
        {
            resolved = cwd;
        }

        // 沙箱约束：解析后路径必须在工作区子树内
        if (!resolved.StartsWith(cwd + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(resolved, cwd, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"(目录不在工作区范围内: {projectPath ?? cwd})");
        }

        return resolved;
    }

    // ── 辅助 ──

    private static ToolExecutionResult Ok(string output) => ToolExecutionResult.Ok(output);
    private static ToolExecutionResult Fail(string error) => ToolExecutionResult.Fail(error);

    // ── 数据模型 ──

    private sealed class DirNode
    {
        public string Name { get; set; } = "";
        public bool IsRoot { get; set; }
        public List<FileEntry> Files { get; set; } = [];
        public List<DirNode> Children { get; set; } = [];
        public int TotalFileCount { get; set; }
    }

    private sealed class FileEntry
    {
        public string Name { get; set; } = "";
        public List<string> Types { get; set; } = [];
        public int TotalTypeCount { get; set; }
    }
}

public sealed record ProjectMapArgs
{
    [ToolParam("Project root directory path. Defaults to current working directory.")]
    public string? ProjectPath { get; init; }

    [ToolParam("Directory tree expansion depth (1-3). Default is 2.")]
    public int? Depth { get; init; }
}
