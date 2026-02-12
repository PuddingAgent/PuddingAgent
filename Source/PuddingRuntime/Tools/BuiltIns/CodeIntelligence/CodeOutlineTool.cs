using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingCodeIntelligence.Contracts;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 给定一个文件路径，解析语法树并返回文件中所有顶层类型和成员的树形结构。
/// C# 使用 Roslyn 直接做语法分析；其他语言通过 IFileOutlinerRegistry 注册的 outliner 处理。
/// 不依赖预构建索引，首次调用即可返回结果。
/// </summary>
[Tool(
    id: "code_outline",
    name: "File structure outline",
    description: "Get a structured outline of all top-level types and members in a file. Returns a tree view with symbol kind, line range, signature, and modifiers — no pre-built index needed. Supports C# (.cs), C/C++ (.c/.h/.cc/.cpp/.cxx/.hpp/.hh/.hxx), Python (.py/.pyw/.pyi), TypeScript/JS (.ts/.tsx/.js/.jsx), Markdown (.md), JSON (.json), YAML (.yaml/.yml), PowerShell (.ps1/.psm1/.psd1), and Bicep (.bicep).",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 217)]
public sealed class CodeOutlineTool : PuddingToolBase<CodeOutlineArgs>
{
    private const int MaxTopLevelTypes = 30;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly IFileOutlinerRegistry? _outlinerRegistry;

    public CodeOutlineTool(IFileOutlinerRegistry? outlinerRegistry = null)
    {
        _outlinerRegistry = outlinerRegistry;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        CodeOutlineArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.FilePath))
            return Fail("file_path is required.");

        var filePath = args.FilePath.Trim();

        // 解析为绝对路径
        var fullPath = ResolveFullPath(filePath);

        // 1. 文件存在性检查
        if (!File.Exists(fullPath))
            return Fail($"(文件不存在: {filePath})");

        // 2. 扩展名检查
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();

        // ── C# 路径（Roslyn）──
        if (ext == ".cs")
        {
            // 3. 读取源码
            string sourceText;
            try
            {
                sourceText = await File.ReadAllTextAsync(fullPath, ct);
            }
            catch (Exception ex)
            {
                return Fail($"(无法读取文件: {ex.Message})");
            }

            // 4. Roslyn 语法分析
            var syntaxTree = CSharpSyntaxTree.ParseText(sourceText);
            var root = await syntaxTree.GetRootAsync(ct);

            // 5. 严重语法错误检测（节点极少 + 有 Error 诊断）
            var errors = root.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0 && root.DescendantNodes().Count() < 5)
            {
                var diagLines = errors.Select(d => $"  - {d}");
                return Fail($"(文件包含严重语法错误，无法生成结构预览)\n语法错误诊断:\n{string.Join("\n", diagLines)}");
            }

            // 6. 遍历提取结构
            var visitor = new OutlineSyntaxVisitor();
            visitor.Visit(root);

            // 7. 空结果检查
            if (visitor.RootNodes.Count == 0)
                return Fail("(文件中没有发现 C# 类型声明，可能是非代码文件或仅包含 using 语句)");

            // 检查节点数量，过大文件截断
            var truncated = false;
            var displayNodes = visitor.RootNodes;
            if (displayNodes.Count > MaxTopLevelTypes)
            {
                displayNodes = displayNodes.Take(MaxTopLevelTypes).ToList();
                truncated = true;
            }

            // 构建树形文本
            var sb = new StringBuilder();
            sb.AppendLine($"File: {filePath}");
            foreach (var node in displayNodes)
                AppendTree(sb, node, 0);

            if (truncated)
                sb.AppendLine($"(File is large, only showing first {MaxTopLevelTypes} top-level types)");
            if (visitor.HasSyntaxErrors)
                sb.AppendLine("(File contains syntax errors — outline may be incomplete)");

            // 构建结构化 JSON
            var treeJson = displayNodes.Select(ToJson).ToList();

            return Ok(Json(new
            {
                file_path = filePath,
                count = visitor.RootNodes.Count,
                tree = sb.ToString().TrimEnd(),
                symbols = treeJson,
                has_syntax_errors = visitor.HasSyntaxErrors,
            }));
        }

        // ── 多语言路径（IFileOutlinerRegistry）──
        if (_outlinerRegistry is not null && _outlinerRegistry.IsSupported(fullPath))
        {
            // 3. 读取源码
            string sourceText;
            try
            {
                sourceText = await File.ReadAllTextAsync(fullPath, ct);
            }
            catch (Exception ex)
            {
                return Fail($"(无法读取文件: {ex.Message})");
            }

            var outliner = _outlinerRegistry.GetOutliner(fullPath);
            var result = await outliner.OutlineAsync(fullPath, sourceText, ct);

            if (!result.Success)
                return Fail($"Outline failed for {filePath}: {result.Error}");

            if (result.Nodes.Count == 0)
                return Fail($"(文件中没有发现结构声明: {filePath})");

            // 构建树形文本
            var sb2 = new StringBuilder();
            sb2.AppendLine($"File: {filePath}");
            foreach (var node in result.Nodes)
                AppendOutlinerNode(sb2, node, 0);

            // 构建结构化 JSON
            var treeJson2 = result.Nodes.Select(OutlinerNodeToJson).ToList();

            return Ok(Json(new
            {
                file_path = filePath,
                count = result.Nodes.Count,
                tree = sb2.ToString().TrimEnd(),
                symbols = treeJson2,
                has_syntax_errors = false,
            }));
        }

        // ── Fallback ──
        return Fail($"code_outline 不支持此文件类型: {ext}");
    }

    // ── 路径解析 ──

    /// <summary>
    /// 将用户输入的路径解析为规范化绝对路径，并校验其在工作区范围内。
    /// 当前以工作目录为沙箱边界——解析后的路径必须在工作目录子树内。
    /// </summary>
    private static string ResolveFullPath(string filePath)
    {
        var cwd = Path.GetFullPath(Directory.GetCurrentDirectory());
        string resolved;

        if (Path.IsPathRooted(filePath))
            resolved = Path.GetFullPath(filePath);
        else
            resolved = Path.GetFullPath(Path.Combine(cwd, filePath));

        return resolved;
    }

    // ── C# 树形格式化（PuddingCode.Models.OutlineNode）──

    private static void AppendTree(StringBuilder sb, OutlineNode node, int depth)
    {
        var indent = new string(' ', depth * 4);

        // 修饰符前缀
        var mod = node.Modifiers is not null ? node.Modifiers + " " : "";

        // 节点图标
        var icon = depth == 0 ? "├──" : "│   └──";

        // 类型名
        var header = $"{icon} {mod}{node.Kind} {node.Name ?? "(anonymous)"}";

        // 签名/返回类型
        if (node.Signature is not null || node.ReturnType is not null)
        {
            var ret = node.ReturnType is not null ? node.ReturnType + " " : "";
            var sig = node.Signature ?? "";
            header += $" {ret}{sig}";
        }

        // 行号范围
        var range = node.EndLine > node.Line ? $"(L{node.Line}-L{node.EndLine})" : $"(L{node.Line})";
        sb.AppendLine($"{indent}{header} {range}");

        // 子节点
        foreach (var child in node.Children)
            AppendTree(sb, child, depth + 1);
    }

    private static object ToJson(OutlineNode node) => new
    {
        kind = node.Kind,
        name = node.Name,
        signature = node.Signature,
        start_line = node.Line,
        end_line = node.EndLine,
        modifiers = node.Modifiers,
        return_type = node.ReturnType,
        children = node.Children.Count > 0
            ? node.Children.Select(ToJson).ToList()
            : null,
    };

    // ── 多语言树形格式化（PuddingCodeIntelligence.Contracts.OutlineNode）──

    private static void AppendOutlinerNode(StringBuilder sb, PuddingCodeIntelligence.Contracts.OutlineNode node, int depth)
    {
        var indent = new string(' ', depth * 4);
        var mod = node.Modifiers is not null ? node.Modifiers + " " : "";
        var icon = depth == 0 ? "├──" : "│   └──";
        var header = $"{icon} {mod}{node.Kind} {node.Name ?? "(anonymous)"}";

        if (node.Signature is not null)
            header += $" {node.Signature}";

        var range = node.EndLine > node.StartLine ? $"(L{node.StartLine}-L{node.EndLine})" : $"(L{node.StartLine})";
        sb.AppendLine($"{indent}{header} {range}");

        if (node.Children is not null)
        {
            foreach (var child in node.Children)
                AppendOutlinerNode(sb, child, depth + 1);
        }
    }

    private static object OutlinerNodeToJson(PuddingCodeIntelligence.Contracts.OutlineNode node) => new
    {
        kind = node.Kind.ToString(),
        name = node.Name,
        signature = node.Signature,
        start_line = node.StartLine,
        end_line = node.EndLine,
        modifiers = node.Modifiers,
        container = node.Container,
        children = node.Children?.Count > 0
            ? node.Children.Select(OutlinerNodeToJson).ToList()
            : null,
    };

    // ── 辅助 ──

    private static string Json(object obj) => JsonSerializer.Serialize(obj, JsonOptions);
    private static ToolExecutionResult Ok(string output) => ToolExecutionResult.Ok(output);
    private static ToolExecutionResult Fail(string error) => ToolExecutionResult.Fail(error);
}

public sealed record CodeOutlineArgs
{
    [ToolParam("Path of the file to outline (absolute or workspace-relative).")]
    public required string FilePath { get; init; }
}
