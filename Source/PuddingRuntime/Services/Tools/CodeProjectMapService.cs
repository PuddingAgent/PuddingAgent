using Microsoft.CodeAnalysis.CSharp;
using PuddingPathFiltering;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// <c>project_map</c> 的目录树模型（原先工具内的私有嵌套类，搬出来才能被服务与工具共用）。
/// </summary>
public sealed class ProjectMapNode
{
    public string Name { get; set; } = "";
    public bool IsRoot { get; set; }
    public List<ProjectMapFileEntry> Files { get; set; } = [];
    public List<ProjectMapNode> Children { get; set; } = [];
    public int TotalFileCount { get; set; }
}

public sealed class ProjectMapFileEntry
{
    public string Name { get; set; } = "";
    public List<string> Types { get; set; } = [];
    public int TotalTypeCount { get; set; }
}

/// <summary><c>project_map</c> 的结构化结果：只回答「目录里有什么」，不回答「怎么排版」。</summary>
public sealed record ProjectMapOutcome(
    bool Success,
    string? Error,
    string? RootPath,
    int Depth,
    ProjectMapNode? Root)
{
    public static ProjectMapOutcome Failed(string error) => new(false, error, null, 0, null);
}

/// <summary>
/// <c>project_map</c> 的数据获取层：解析根路径、按深度遍历目录、对每个 .cs 文件取顶层类型。
/// 排版（树形文本与 JSON 投影）留在工具层。
/// </summary>
public interface ICodeProjectMapService
{
    Task<ProjectMapOutcome> BuildAsync(
        string? projectPath,
        string? workingDirectory,
        int? depth,
        CancellationToken cancellationToken);
}

/// <summary><see cref="ICodeProjectMapService"/> 的实现，逐条对齐 <c>ProjectMapTool</c> 原有行为。</summary>
public sealed class CodeProjectMapService : ICodeProjectMapService
{
    public const int MaxFilesPerDir = 50;
    public const int MaxTopLevelTypes = 3;

    /// <inheritdoc />
    public async Task<ProjectMapOutcome> BuildAsync(
        string? projectPath,
        string? workingDirectory,
        int? depth,
        CancellationToken cancellationToken)
    {
        var rootPath = ResolveRootPath(projectPath, workingDirectory);
        var clamped = Math.Clamp(depth ?? 2, 1, 3);

        if (!Directory.Exists(rootPath))
        {
            return ProjectMapOutcome.Failed($"(目录不存在: {rootPath})");
        }

        var root = await BuildDirectoryNodeAsync(rootPath, rootPath, clamped, 0, cancellationToken)
            .ConfigureAwait(false);

        return new ProjectMapOutcome(true, null, rootPath, clamped, root);
    }

    private static async Task<ProjectMapNode> BuildDirectoryNodeAsync(
        string rootPath,
        string currentPath,
        int maxDepth,
        int currentDepth,
        CancellationToken ct)
    {
        var node = new ProjectMapNode
        {
            Name = currentDepth == 0 ? currentPath : Path.GetFileName(currentPath),
            IsRoot = currentDepth == 0,
        };

        // 扫描 .cs 文件
        try
        {
            // TotalFileCount 必须是「真实总数」。原先把 csFiles.Count 赋给它，而 csFiles 已被
            // Take(MaxFilesPerDir) 截断 ⇒ ProjectMapTool 里 `node.TotalFileCount > MaxFilesPerDir`
            // 那条 (showing 50/N files) 分支永远不会触发，调用方以为看到了全部文件。
            // ADR-089 §8.3：不得把「只列了一部分」当成「完整成功」。
            var allFiles = Directory.GetFiles(currentPath, "*.cs");
            var csFiles = allFiles.Take(MaxFilesPerDir).ToList();
            foreach (var file in csFiles)
            {
                ct.ThrowIfCancellationRequested();
                var (types, totalTypeCount) = await GetFileTopTypesAsync(file, ct);
                var fileName = Path.GetFileName(file);
                node.Files.Add(new ProjectMapFileEntry
                {
                    Name = fileName,
                    Types = types,
                    TotalTypeCount = totalTypeCount,
                });
            }
            node.TotalFileCount = allFiles.Length;
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
                // 跳过隐藏目录，以及单一真源（PathNoiseRules）里的噪声目录。
                // ADR-089 U4-4 D4: 原先是「列出 4 个名字 + 全部点目录」的内联副本。
                var filtered = dirs
                    .Where(d =>
                    {
                        var name = Path.GetFileName(d);
                        return !name.StartsWith('.')
                            && !PathNoiseRules.IsNoiseSegment(name);
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

            // OutlineSyntaxVisitor 把命名空间也记为根节点：现代 C# 文件普遍使用文件作用域命名空间，
            // 直接取 RootNodes 会得到「每个文件只有一个类型，而且那个类型是 namespace」——
            // 与本字段语义（文件的核心类型）正好相反，且 MaxTopLevelTypes 截断永远不会触发。
            // 这里把命名空间容器展开成它的成员，嵌套命名空间递归展开。
            var typeNodes = FlattenNamespaces(visitor.RootNodes);
            totalTypeCount = typeNodes.Count;

            foreach (var node in typeNodes.Take(MaxTopLevelTypes))
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

    /// <summary>
    /// 展平命名空间容器：<c>namespace X { class A {} }</c> 与文件作用域命名空间都会被展开成其成员。
    /// 只跳过命名空间这一层，其余节点（含 enum / record）原样保留并维持出现顺序。
    /// </summary>
    private static List<OutlineNode> FlattenNamespaces(IReadOnlyList<OutlineNode> roots)
    {
        var result = new List<OutlineNode>();
        foreach (var node in roots)
        {
            if (string.Equals(node.Kind, "namespace", StringComparison.Ordinal) && node.Children.Count > 0)
                result.AddRange(FlattenNamespaces(node.Children));
            else
                result.Add(node);
        }
        return result;
    }

    /// <summary>
    /// 将用户输入的路径解析为规范化绝对路径，并校验其在工作区范围内。
    /// 当前以工作目录为沙箱边界——解析后的路径必须在工作目录子树内。
    /// </summary>
        private static string ResolveRootPath(string? projectPath, string? workingDirectory)
    {
        var cwd = Path.GetFullPath(workingDirectory ?? Directory.GetCurrentDirectory());
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
}
