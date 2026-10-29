using PuddingCodeIndex.Contracts;
using PuddingCodeIntelligence.Contracts;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// <c>code_summary</c> 的**结构化**结果：只回答「这个符号是什么、谁调它、它调谁」，
/// 不回答「怎么排版」。
/// </summary>
public sealed record CodeSummaryOutcome(
    bool Success,
    string? Error,
    CodeSymbolRecord? Symbol,
    string? DisplayName,
    string? DocComment,
    IReadOnlyList<CodeRelationRecord> Callers,
    IReadOnlyList<CodeRelationRecord> Callees)
{
    /// <summary>失败：<paramref name="error"/> 必须是可直接回给调用方的文本。</summary>
    public static CodeSummaryOutcome Failed(string error) =>
        new(false, error, null, null, null, [], []);
}

/// <summary>
/// <c>code_summary</c> 的数据获取层：解析项目 → 搜符号 → 取文档注释 → 取调用关系。
/// <para>
/// <b>不含排版</b>：摘要正文与 JSON 投影留在工具层，<c>workspace_open</c> 以后复用同一份数据。
/// </para>
/// </summary>
public interface ICodeSummaryService
{
    /// <summary>汇总一个符号。</summary>
    /// <param name="symbolName">符号名（支持模糊匹配，会自动 Trim）。</param>
    /// <param name="projectId">可选项目限定；为空时由 scope 解析器给出。</param>
    /// <param name="workspaceId">工作区标识。</param>
    Task<CodeSummaryOutcome> SummarizeAsync(
        string? symbolName,
        string? projectId,
        string workspaceId,
        CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="ICodeSummaryService"/> 的实现。逐条对齐 <c>CodeSummaryTool</c> 原有行为，
/// 由 5 条 <c>code_summary</c> 特征测试守住外部可见表现。
/// </summary>
public sealed class CodeSummaryService(
    ICodeQueryService? queryService,
    ICodeIndexScopeResolver? resolver) : ICodeSummaryService
{
    /// <summary>摘要里最多展示的调用者条数（历史行为）。</summary>
    public const int MaxCallers = 5;

    /// <summary>摘要里最多展示的被调用者条数（历史行为）。</summary>
    public const int MaxCallees = 5;

    /// <summary>超过该字节数的文件不再做 AST 回退解析（历史行为，≈500 行）。</summary>
    private const long AstParseSizeLimit = 25_000;

    /// <summary>符号搜索的上限（历史行为：只取前 10 条再选第一条）。</summary>
    private const int SymbolSearchLimit = 10;

    /// <inheritdoc />
    public async Task<CodeSummaryOutcome> SummarizeAsync(
        string? symbolName,
        string? projectId,
        string workspaceId,
        CancellationToken cancellationToken)
    {
        if (queryService is null)
        {
            return CodeSummaryOutcome.Failed("ICodeQueryService is not registered.");
        }

        if (string.IsNullOrWhiteSpace(symbolName))
        {
            return CodeSummaryOutcome.Failed("symbol_name is required.");
        }

        var name = symbolName.Trim();

        var effectiveProjectId = await CodeQueryToolHelper
            .ResolveAndEnsureProjectIdAsync(resolver, workspaceId, projectId, null, null, cancellationToken)
            .ConfigureAwait(false);

        var searchResults = await queryService.SearchSymbolsAsync(
            new CodeSymbolSearchRequest(
                WorkspaceId: workspaceId,
                Query: name,
                ProjectId: effectiveProjectId,
                Kind: null,
                Limit: SymbolSearchLimit,
                Skip: 0),
            cancellationToken).ConfigureAwait(false);

        if (searchResults.Count == 0)
        {
            return CodeSummaryOutcome.Failed($"(未找到符号: {name})");
        }

        var match = searchResults[0];
        var symbol = match.Symbol;

        var docComment = await ReadDocCommentAsync(symbol, cancellationToken).ConfigureAwait(false);

        var callers = new List<CodeRelationRecord>();
        var callees = new List<CodeRelationRecord>();
        try
        {
            var effectiveId = symbol.ProjectId ?? effectiveProjectId;
            var callersTask = queryService.GetCallersAsync(workspaceId, effectiveId, symbol.SymbolId, cancellationToken);
            var calleesTask = queryService.GetCalleesAsync(workspaceId, effectiveId, symbol.SymbolId, cancellationToken);
            var results = await Task.WhenAll(callersTask, calleesTask).ConfigureAwait(false);
            callers = results[0].Take(MaxCallers).ToList();
            callees = results[1].Take(MaxCallees).ToList();
        }
        catch
        {
            // 关系图缺失不应让整个摘要失败：摘要主体是符号本身。
            // 这是**有意的降级** —— 调用方从 callers/callees 为空即可看出，不需要额外标志位。
        }

        return new CodeSummaryOutcome(
            true,
            null,
            symbol,
            match.DisplayName,
            docComment,
            callers,
            callees);
    }

    private static async Task<string?> ReadDocCommentAsync(
        CodeSymbolRecord symbol,
        CancellationToken cancellationToken)
    {
        var filePath = symbol.FilePath;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var fast = CodeSummaryDocCommentReader.ExtractDocCommentFast(filePath, symbol.StartLine);
            if (fast is not null)
            {
                return fast;
            }

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > AstParseSizeLimit)
            {
                return "(file too large for AST parse — use file_read to inspect)";
            }

            var source = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source);
            var root = await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
            return CodeSummaryDocCommentReader.ExtractDocComment(root, symbol);
        }
        catch
        {
            // 文档注释是富化信息：读不到就当没有，不能让摘要失败。
            return null;
        }
    }
}
