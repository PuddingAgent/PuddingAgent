using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingMemoryEngine.Data;
using PuddingRuntime.Services.Tools.Handlers;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 记忆工具编排器：只负责 action 分发，具体逻辑委托给 Handler。
/// Agent 自有 Memory 库写入属低风险自治操作，经用户指示（2026-08-27）归类 AutoAllowed 免运行时审批。
/// </summary>
[Tool(
    id: "manage_memory",
    name: "manage_memory",
    description: "管理记忆图书馆结构。action: list_books|create_book|delete_book|list_chapters|add_chapter|update_chapter|delete_chapter|add_pointer|list_pointers|add_relation|list_relations|get_related|dedup_report|merge_chapters。章节支持设置 scene（场景）/ constraints（约束）/ tags（标签）和 source_reference（引用来源），用于溯源核实和知识图谱关联。",
    category: ToolCategory.Memory,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ConcurrencySafe)]
public sealed class ManageMemoryTool : PuddingToolBase<ManageMemoryArgs>
{
    /// <summary>支持的 action 白名单，与下方 switch 分支一一对应；未知 action 一律 fail-closed。</summary>
    private static readonly string[] SupportedActions = new[]
    {
        "list_books", "create_book", "delete_book",
        "list_chapters", "add_chapter", "update_chapter", "delete_chapter",
        "add_pointer", "list_pointers",
        "add_relation", "list_relations", "get_related",
        "dedup_report", "merge_chapters",
    };

    private readonly ILogger<ManageMemoryTool> _logger;
    private readonly BookHandler _bookHandler;
    private readonly ChapterHandler _chapterHandler;
    private readonly ReferenceHandler _referenceHandler;
    private readonly GraphHandler _graphHandler;
    private readonly DedupHandler _dedupHandler;

    public ManageMemoryTool(
        IMemoryLibrary lib,
        ILogger<ManageMemoryTool> logger,
        BookHandler bookHandler,
        ChapterHandler chapterHandler,
        ReferenceHandler referenceHandler,
        GraphHandler graphHandler,
        DedupHandler dedupHandler)
    {
        _logger = logger;
        _bookHandler = bookHandler;
        _chapterHandler = chapterHandler;
        _referenceHandler = referenceHandler;
        _graphHandler = graphHandler;
        _dedupHandler = dedupHandler;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        ManageMemoryArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var root = BuildRoot(args, context);
        // T01：action 归一化（去空白 + 小写），并在进入任何写路径之前做白名单校验，
        // 未知 action 一律 fail-closed，不得以成功结果返回错误语义。
        var action = (root.GetOptionalString("action") ?? "list_books").Trim().ToLowerInvariant();
        var workspaceId = context.WorkspaceId;

        if (Array.IndexOf(SupportedActions, action) < 0)
        {
            _logger.LogWarning("[ManageMemory] Rejected unknown action={Action}", action);
            return ToolExecutionResult.Fail(
                $"Unknown memory action '{action}'. Supported actions: {string.Join(", ", SupportedActions)}.");
        }

        try
        {
            var result = action switch
            {
                // ── Book 操作 ──
                "list_books" => await _bookHandler.ListBooksAsync(workspaceId, ct),
                "create_book" => await _bookHandler.CreateBookAsync(root, workspaceId, ct),
                "delete_book" => await _bookHandler.DeleteBookAsync(
                    root.GetOptionalString("book_id") ?? "", ct),

                // ── Chapter 操作 ──
                "list_chapters" => await _chapterHandler.ListChaptersAsync(
                    root.GetOptionalString("book_id") ?? "",
                    root.GetBoolean("include_history", false), ct),
                "add_chapter" => await _chapterHandler.AddChapterAsync(
                    root, root.GetOptionalString("book_id") ?? "", ct),
                "update_chapter" => await _chapterHandler.UpdateChapterAsync(root, ct),
                "delete_chapter" => await _chapterHandler.DeleteChapterAsync(
                    root.GetOptionalString("chapter_id") ?? "", ct),

                // ── Pointer 操作 ──
                "add_pointer" => await _referenceHandler.AddPointerAsync(root, ct),
                "list_pointers" => await _referenceHandler.ListPointersAsync(root, workspaceId, ct),

                // ── Relation 操作 ──
                "add_relation" => await _graphHandler.AddRelationAsync(root, ct),
                "list_relations" => await _graphHandler.ListRelationsAsync(root, ct),
                "get_related" => await _graphHandler.GetRelatedAsync(root, ct),

                // ── Dedup 操作 ──
                "dedup_report" => await _dedupHandler.DedupReportAsync(workspaceId, ct),
                "merge_chapters" => await _dedupHandler.MergeChaptersAsync(
                    root.GetOptionalString("source_book_id") ?? "",
                    root.GetOptionalString("target_book_id") ?? "", ct),

                // ── 未知 action：上方白名单已拦截，此分支不可达 ──
                _ => throw new InvalidOperationException($"Unknown memory action '{action}'.")
            };

            return ToolExecutionResult.Ok(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // T01：取消不是业务错误，必须向上传播，不得被吞成失败结果。
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ManageMemory] Failed action={Action}", action);

            // Phase 0: 将底层异常包装为友好错误信息，避免泄露原始 SQL 错误
            var friendlyMessage = ex switch
            {
                Microsoft.Data.Sqlite.SqliteException sqliteEx =>
                    $"数据库操作失败 (action={action})。ErrorCode={sqliteEx.ErrorCode}，Message={sqliteEx.Message}。如果操作是创建或添加，请先使用 list_books / list_chapters 检查是否已存在同名记录。",
                InvalidOperationException =>
                    $"操作无效 (action={action})：{ex.Message}",
                _ =>
                    $"操作失败 (action={action})：{ex.Message}"
            };

            // T01：业务失败必须以 Success=false 返回，避免调用方把错误当成功。
            return ToolExecutionResult.Fail(friendlyMessage);
        }
    }

    private static JsonElement BuildRoot(ManageMemoryArgs args, ToolExecutionContext context)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["action"] = args.Action,
            ["book_id"] = args.BookId,
            ["include_history"] = args.IncludeHistory,
            ["library_id"] = args.LibraryId,
            ["title"] = args.Title,
            ["content"] = args.Content,
            ["summary"] = args.Summary,
            ["chapter_id"] = args.ChapterId,
            ["source_type"] = args.SourceType,
            ["source_id"] = args.SourceId,
            ["tags"] = args.Tags,
            ["chapter_order"] = args.ChapterOrder,
            ["source_reference"] = args.SourceReference,
            ["reference_type"] = args.ReferenceType,
            ["scene"] = args.Scene,
            ["constraints"] = args.Constraints,
            ["source_chapter_id"] = args.SourceChapterId,
            ["target_chapter_id"] = args.TargetChapterId,
            ["relation_type"] = args.RelationType,
            ["description"] = args.Description,
            ["weight"] = args.Weight,
            ["depth"] = args.Depth,
            ["min_weight"] = args.MinWeight,
            ["source_book_id"] = args.SourceBookId,
            ["target_book_id"] = args.TargetBookId,
        };

        if (!string.IsNullOrWhiteSpace(context.AgentInstanceId))
            values["agent_instance_id"] = context.AgentInstanceId;

        return JsonSerializer.SerializeToElement(values);
    }
}
