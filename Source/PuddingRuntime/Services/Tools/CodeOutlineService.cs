using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PuddingCodeIntelligence.Contracts;

namespace PuddingRuntime.Services.Tools;

/// <summary>outline 走的是哪条路径。</summary>
public enum CodeOutlineOrigin
{
    /// <summary>Roslyn 语法分析（.cs）。</summary>
    CSharp = 0,

    /// <summary>注册表里的 outliner（其他语言 / 标记语言）。</summary>
    RegisteredOutliner = 1,
}

/// <summary>
/// outline 的**结构化**结果：只回答“有哪些节点”，不回答“怎么排版”。
/// <para>
/// 树形文本与 JSON 投影留在工具层 —— 那是展示决定，不是数据。这样
/// <c>workspace_open</c> 以后可以复用同一份结构，而不必迁就 <c>code_outline</c> 的输出格式。
/// </para>
/// <remarks>
/// <b>C# 与 outliner 两条路用的是<b>两个不同的</b> OutlineNode 类型</b>：
/// C# 侧是 <c>PuddingRuntime.Services.Tools.OutlineNode</c>（Roslyn 访问者产出），
/// outliner 侧是 <c>PuddingCodeIntelligence.Contracts.OutlineNode</c>（注册表产出）。
/// 这解释了原工具为何有两个格式化器 —— 不是重复代码，是两个类型。<b>合并类型属单独决定，本次不动。</b>
/// </remarks>
/// </summary>
public sealed record CodeOutlineOutcome(
    bool Success,
    string? Error,
    CodeOutlineOrigin Origin,
    IReadOnlyList<OutlineNode> CSharpNodes,
    IReadOnlyList<PuddingCodeIntelligence.Contracts.OutlineNode> OutlinerNodes,
    int TotalCount,
    bool Truncated,
    bool HasSyntaxErrors)
{
    /// <summary>失败：<paramref name="error"/> 必须是可直接回给调用方的文本。</summary>
    public static CodeOutlineOutcome Failed(string error) =>
        new(false, error, CodeOutlineOrigin.CSharp, [], [], 0, false, false);

    /// <summary>C# 路径成功。</summary>
    public static CodeOutlineOutcome FromCSharp(
        IReadOnlyList<OutlineNode> nodes,
        int totalCount,
        bool truncated,
        bool hasSyntaxErrors) =>
        new(true, null, CodeOutlineOrigin.CSharp, nodes, [], totalCount, truncated, hasSyntaxErrors);

    /// <summary>注册表 outliner 路径成功。</summary>
    public static CodeOutlineOutcome FromOutliner(
        IReadOnlyList<PuddingCodeIntelligence.Contracts.OutlineNode> nodes) =>
        new(true, null, CodeOutlineOrigin.RegisteredOutliner, [], nodes, nodes.Count, false, false);
}

/// <summary>
/// <c>code_outline</c> 的数据获取层：读源码、选解析路径、判定严重语法错误、决定截断。
/// <para>
/// <b>不含任何排版</b>：树形文本、JSON 字段顺序、缩进字符都在工具层。
/// </para>
/// </summary>
public interface ICodeOutlineService
{
    /// <summary>为 <paramref name="fullPath"/> 生成结构。</summary>
    /// <param name="fullPath">已规范化的绝对路径（由 <see cref="CodeOutlineTargetResolver"/> 产出）。</param>
    /// <param name="displayPath">用户原样给出的路径，只用于错误信息回显。</param>
    Task<CodeOutlineOutcome> OutlineAsync(
        string fullPath,
        string displayPath,
        CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="ICodeOutlineService"/> 的实现。逻辑逐条对齐 <c>CodeOutlineTool</c> 原有行为，
/// 由 4 条特征测试守住外部可见表现（成功与否、错误措辞、输出 JSON 顶层字段）。
/// </summary>
public sealed class CodeOutlineService(IFileOutlinerRegistry? outlinerRegistry = null) : ICodeOutlineService
{
    /// <summary>单次最多展示的顶层类型数；超出即标注截断（<c>code_outline</c> 的历史行为）。</summary>
    public const int MaxTopLevelTypes = 30;

    private readonly IFileOutlinerRegistry? _outlinerRegistry = outlinerRegistry;

    /// <inheritdoc />
    public async Task<CodeOutlineOutcome> OutlineAsync(
        string fullPath,
        string displayPath,
        CancellationToken cancellationToken)
    {
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();

        if (ext == ".cs")
        {
            return await OutlineCSharpAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }

        if (_outlinerRegistry is not null && _outlinerRegistry.IsSupported(fullPath))
        {
            return await OutlineWithRegistryAsync(fullPath, displayPath, cancellationToken).ConfigureAwait(false);
        }

        return CodeOutlineOutcome.Failed($"code_outline 不支持此文件类型: {ext}");
    }

    private static async Task<CodeOutlineOutcome> OutlineCSharpAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        string sourceText;
        try
        {
            sourceText = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return CodeOutlineOutcome.Failed($"(无法读取文件: {ex.Message})");
        }

        var syntaxTree = CSharpSyntaxTree.ParseText(sourceText);
        var root = await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);

        // 严重语法错误判定：既有错误诊断、又几乎没有可解析节点 —— 这种文件给不出有意义的 outline。
        var errors = root.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        if (errors.Count > 0 && root.DescendantNodes().Count() < 5)
        {
            var diagLines = errors.Select(d => $"  - {d}");
            return CodeOutlineOutcome.Failed(
                $"(文件包含严重语法错误，无法生成结构预览)\n语法错误诊断:\n{string.Join("\n", diagLines)}");
        }

        var visitor = new OutlineSyntaxVisitor();
        visitor.Visit(root);

        if (visitor.RootNodes.Count == 0)
        {
            return CodeOutlineOutcome.Failed("(文件中没有发现 C# 类型声明，可能是非代码文件或仅包含 using 语句)");
        }

        var truncated = visitor.RootNodes.Count > MaxTopLevelTypes;
        var displayNodes = truncated
            ? visitor.RootNodes.Take(MaxTopLevelTypes).ToList()
            : visitor.RootNodes;

        return CodeOutlineOutcome.FromCSharp(
            displayNodes,
            visitor.RootNodes.Count,
            truncated,
            visitor.HasSyntaxErrors);
    }

    private async Task<CodeOutlineOutcome> OutlineWithRegistryAsync(
        string fullPath,
        string displayPath,
        CancellationToken cancellationToken)
    {
        string sourceText;
        try
        {
            sourceText = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return CodeOutlineOutcome.Failed($"(无法读取文件: {ex.Message})");
        }

        var outliner = _outlinerRegistry!.GetOutliner(fullPath);
        var result = await outliner.OutlineAsync(fullPath, sourceText, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return CodeOutlineOutcome.Failed($"Outline failed for {displayPath}: {result.Error}");
        }

        if (result.Nodes.Count == 0)
        {
            return CodeOutlineOutcome.Failed($"(文件中没有发现结构声明: {displayPath})");
        }

        return CodeOutlineOutcome.FromOutliner(result.Nodes);
    }
}
