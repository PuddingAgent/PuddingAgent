// ═══════════════════════════════════════════════════════════════
// save_memory — 写入/更新记忆条目
// ═══════════════════════════════════════════════════════════════

using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingMemoryEngine.Data;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 主动记忆写入 Tool：Agent 可直接将事实、偏好、摘要写入 MemoryLibrary。
/// Agent 自有 Memory 库写入属低风险自治操作，经用户指示（2026-08-27）归类 AutoAllowed 免运行时审批。
/// </summary>
[Tool(
    id: "save_memory",
    name: "save_memory",
    description: "写入或更新记忆。最小用法仅需 content——其余自动推断。完整参数：action, type, book, content, key, value。",
    category: ToolCategory.Memory,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ConcurrencySafe)]
public sealed class SaveMemoryTool : PuddingToolBase<SaveMemoryArgs>
{
    private readonly IMemoryLibraryConvenience _library;
    private readonly IMemoryLibrary _memLib;
    private readonly ILogger<SaveMemoryTool> _logger;
    private readonly IImportantMemoryService? _importantMemory;
    private readonly MemoryQualityFilter? _qualityFilter;

    public SaveMemoryTool(
        IMemoryLibraryConvenience library,
        IMemoryLibrary memLib,
        ILogger<SaveMemoryTool> logger,
        IImportantMemoryService? importantMemory = null,
        MemoryQualityFilter? qualityFilter = null)
    {
        _library = library;
        _memLib = memLib;
        _logger = logger;
        _importantMemory = importantMemory;
        _qualityFilter = qualityFilter;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SaveMemoryArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var root = BuildRoot(args, context);
        var action = NormalizeAction(root.GetOptionalString("action"));
        var type = NormalizeType(root.GetOptionalString("type"));
        var content = root.GetString("content", "");
        var book = root.GetOptionalString("book");
        var key = root.GetOptionalString("key");
        var value = root.GetOptionalString("value");
        var title = root.GetString("title", type);
        var workspaceId = context.WorkspaceId;
        var sourceRef = root.GetOptionalString("source_reference");
        var refType = root.GetOptionalString("reference_type");
        var agentInstanceId = context.AgentInstanceId;

        // T01 闭合合同：未知 action 一律 fail-closed，禁止猜成 upsert 落库。
        if (Array.IndexOf(SupportedActions, action) < 0)
        {
            _logger.LogWarning("[SaveMemory] Rejected unknown action={Action}", action);
            return ToolExecutionResult.Fail(
                $"Unknown memory action '{action}'. Supported actions: {string.Join(", ", SupportedActions)}.");
        }

        // T01-R：未知 type 不 fail-closed —— 既有合同由 MemoryQualityFilter 产出 unknown_type 警告；
        // 这里只做归一化，避免 'Preference' 通过 OrdinalIgnoreCase 校验后落到通用分支写空内容。
        // T01 参数组合前置校验：不合格组合在写路径之前拒绝，保证零写入。
        // 口径与 ToolParam 声明一致：preference 需要 key + value，其余可写类型需要 content。
        if (action == "upsert")
        {
            if (type == "preference")
            {
                if (string.IsNullOrWhiteSpace(key))
                    return ToolExecutionResult.Fail("Preference entries require 'key'.");
                if (string.IsNullOrWhiteSpace(value))
                    return ToolExecutionResult.Fail("Preference entries require 'value': the schema declares it required.");
            }
            else if (string.IsNullOrWhiteSpace(content))
            {
                return ToolExecutionResult.Fail($"'content' is required for type='{type}'.");
            }
        }

        if (action == "set_important" && string.IsNullOrWhiteSpace(content))
        {
            return ToolExecutionResult.Fail("'content' is required for set_important.");
        }


        try
        {
            string output;
            if (action == "delete")
            {
                output = await DeleteMemoryAsync(root, type, workspaceId, ct);
                return ToResult(output);
            }

            if (action == "set_important" && _importantMemory is not null)
            {
                var impContent = root.GetString("content", "");
                // T01：身份按执行上下文派生；模型不得经参数指定 agent_instance_id
                // 绕过作用域（SaveMemoryArgs 也未声明该字段）。
                var instanceId = context.AgentInstanceId;

                if (string.IsNullOrWhiteSpace(instanceId))
                    return ToolExecutionResult.Fail("agent_instance_id is not available in the execution context; important memory requires an agent identity.");

                var writeResult = await _importantMemory.WriteAsync(instanceId, impContent, ct);
                output = JsonSerializer.Serialize(new
                {
                    status = writeResult.Success ? "ok" : "error",
                    action = "set_important",
                    writeResult.Success,
                    message = writeResult.Success
                        ? $"已保存重要记忆（{writeResult.LineCount} 行，{writeResult.ByteCount} 字节）"
                        : writeResult.Error,
                    writeResult.LineCount,
                    writeResult.ByteCount,
                    max_lines = IImportantMemoryService.MaxLines,
                    max_chars = IImportantMemoryService.MaxChars,
                });
                return ToResult(output);
            }

            if (action == "get_important" && _importantMemory is not null)
            {
                var instanceId = context.AgentInstanceId;

                if (string.IsNullOrWhiteSpace(instanceId))
                    return ToolExecutionResult.Fail("agent_instance_id is not available in the execution context; important memory requires an agent identity.");

                var impContent = await _importantMemory.ReadAsync(instanceId, ct);
                output = JsonSerializer.Serialize(new
                {
                    status = "ok",
                    action = "get_important",
                    exists = impContent != null,
                    content = impContent ?? "(无重要记忆)",
                    line_count = impContent?.Split('\n').Length ?? 0,
                });
                return ToolExecutionResult.Ok(output);
            }

            if (action is "set_important" or "get_important")
            {
                output = JsonSerializer.Serialize(new
                {
                    status = "error",
                    action,
                    message = "ImportantMemoryService 未注入，set_important/get_important 不可用。",
                });
                return ToResult(output);
            }

            // ── P2 记忆质量监控：写前校验 + 脏词过滤 ──
            var qualityWarnings = new List<MemoryQualityWarning>();
            var effectiveContent = content;
            if (_qualityFilter is not null)
            {
                var qResult = _qualityFilter.Check(content, type);
                qualityWarnings.AddRange(qResult.Warnings);
                effectiveContent = qResult.FilteredContent;
            }

            // ── P2 去重检查（FTS5） ──
            if (_qualityFilter is not null && !string.IsNullOrWhiteSpace(content))
            {
                var dupWarnings = await _qualityFilter.CheckDuplicateAsync(
                    content, book ?? title ?? type, workspaceId, _memLib, ct);
                qualityWarnings.AddRange(dupWarnings);
            }

            var package = new ExperiencePackage
            {
                Title = book ?? title ?? type,
                Content = type switch
                {
                    "preference" => $"{key}: {value}",
                    _ => effectiveContent
                },
                SuggestedTags = type switch
                {
                    "fact" => ["记忆", "事实"],
                    "preference" => ["偏好", key ?? ""],
                    "summary" => ["摘要"],
                    _ => ["记忆"]
                },
                Importance = type switch
                {
                    "fact" => 0.7,
                    "preference" => 0.9,
                    "summary" => 0.5,
                    _ => 0.5
                },
                SourceReference = sourceRef,
                ReferenceType = refType,
                AgentInstanceId = agentInstanceId
            };

            var upsertResult = await _library.UpsertExperienceAsync(workspaceId, package, ct);
            _logger.LogInformation("[SaveMemory] {Action} type={Type} book={Book}", action, type, upsertResult.Book.Title);

            // ADR-028 Phase 2: 写入 SourceReference
            if (!string.IsNullOrEmpty(sourceRef) && !string.IsNullOrEmpty(refType))
            {
                try
                {
                    await _memLib.AddSourceReferenceAsync(new SourceReferenceCreateRequest(
                        workspaceId, "chapter", upsertResult.Chapter.ChapterId, refType, sourceRef,
                        Label: $"{type}: {upsertResult.Book.Title}"), ct);
                }
                catch (Exception srEx)
                {
                    _logger.LogWarning(srEx, "[SaveMemory] Failed to write SourceReference for chapter={ChapterId}", upsertResult.Chapter.ChapterId);
                }
            }

            output = JsonSerializer.Serialize(new
            {
                status = "ok",
                action,
                type,
                bookId = upsertResult.Book.BookId,
                bookTitle = upsertResult.Book.Title,
                chapterId = upsertResult.Chapter.ChapterId,
                quality = qualityWarnings.Count > 0
                    ? new { warnings = qualityWarnings.Select(w => new { w.Rule, w.Message, w.Severity }) }
                    : null
            });
            return ToolExecutionResult.Ok(output);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 取消必须向上传播，不得被降级成业务错误结果。
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SaveMemory] Failed action={Action} type={Type}", action, type);
            return ToolExecutionResult.Fail(ex.Message);
        }
    }

    private async Task<string> DeleteMemoryAsync(JsonElement root, string type, string workspaceId, CancellationToken ct)
    {
        var bookId = root.GetOptionalString("book_id");
        var chapterId = root.GetOptionalString("chapter_id");
        var pointerId = root.GetOptionalString("pointer_id");
        var book = root.GetOptionalString("book");
        var title = root.GetOptionalString("title");
        var content = root.GetOptionalString("content");
        var key = root.GetOptionalString("key");
        var value = root.GetOptionalString("value");

        var deletedBookIds = new List<string>();
        var deletedChapterIds = new List<string>();
        var deletedPointerIds = new List<string>();

        if (!string.IsNullOrWhiteSpace(pointerId))
        {
            if (await PointerBelongsToWorkspaceAsync(pointerId, workspaceId, ct)
                && await _memLib.DeletePointerAsync(pointerId, ct))
                deletedPointerIds.Add(pointerId);
            return SerializeDeleteResult(type, workspaceId, deletedBookIds, deletedChapterIds, deletedPointerIds);
        }

        if (!string.IsNullOrWhiteSpace(chapterId))
        {
            if (await ChapterBelongsToWorkspaceAsync(chapterId, workspaceId, ct)
                && await _memLib.DeleteChapterAsync(chapterId, ct))
                deletedChapterIds.Add(chapterId);
            return SerializeDeleteResult(type, workspaceId, deletedBookIds, deletedChapterIds, deletedPointerIds);
        }

        if (!string.IsNullOrWhiteSpace(bookId))
        {
            if (await BookBelongsToWorkspaceAsync(bookId, workspaceId, ct)
                && await _memLib.DeleteBookAsync(bookId, ct))
                deletedBookIds.Add(bookId);
            return SerializeDeleteResult(type, workspaceId, deletedBookIds, deletedChapterIds, deletedPointerIds);
        }

        var books = await _memLib.ListBooksScopedAsync(workspaceId, 500, ct);
        var targetBooks = books
            .Where(b => string.IsNullOrWhiteSpace(book)
                || string.Equals(b.Title, book, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (string.Equals(type, "book", StringComparison.OrdinalIgnoreCase))
        {
            var bookTitle = book ?? title;
            if (string.IsNullOrWhiteSpace(bookTitle))
                return JsonSerializer.Serialize(new { status = "error", action = "delete", type, message = "book_id or book/title is required for book delete" });

            foreach (var candidate in books.Where(b => string.Equals(b.Title, bookTitle, StringComparison.OrdinalIgnoreCase)))
            {
                if (await _memLib.DeleteBookAsync(candidate.BookId, ct))
                    deletedBookIds.Add(candidate.BookId);
            }
            return SerializeDeleteResult(type, workspaceId, deletedBookIds, deletedChapterIds, deletedPointerIds);
        }

        var targetText = string.Equals(type, "preference", StringComparison.OrdinalIgnoreCase)
            ? string.IsNullOrWhiteSpace(value) ? key : $"{key}: {value}"
            : content;
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(targetText))
        {
            return JsonSerializer.Serialize(new
            {
                status = "error",
                action = "delete",
                type,
                message = "chapter_id, or enough title/content/key data is required for memory delete"
            });
        }

        foreach (var candidateBook in targetBooks)
        {
            var chapters = await _memLib.ListChaptersAsync(candidateBook.BookId, ct);
            foreach (var chapter in chapters)
            {
                var titleMatches = string.IsNullOrWhiteSpace(title)
                    || string.Equals(chapter.Title, title, StringComparison.OrdinalIgnoreCase);
                var contentMatches = string.IsNullOrWhiteSpace(targetText)
                    || chapter.Content.Contains(targetText, StringComparison.OrdinalIgnoreCase);
                if (!titleMatches || !contentMatches)
                    continue;

                if (await _memLib.DeleteChapterAsync(chapter.ChapterId, ct))
                    deletedChapterIds.Add(chapter.ChapterId);
            }
        }

        return SerializeDeleteResult(type, workspaceId, deletedBookIds, deletedChapterIds, deletedPointerIds);
    }

    private async Task<bool> BookBelongsToWorkspaceAsync(string bookId, string workspaceId, CancellationToken ct)
    {
        var books = await _memLib.ListBooksScopedAsync(workspaceId, 500, ct);
        return books.Any(b => string.Equals(b.BookId, bookId, StringComparison.Ordinal));
    }

    private async Task<bool> ChapterBelongsToWorkspaceAsync(string chapterId, string workspaceId, CancellationToken ct)
    {
        var chapter = await _memLib.GetChapterAsync(chapterId, ct);
        return chapter is not null && await BookBelongsToWorkspaceAsync(chapter.BookId, workspaceId, ct);
    }

    private async Task<bool> PointerBelongsToWorkspaceAsync(string pointerId, string workspaceId, CancellationToken ct)
    {
        var books = await _memLib.ListBooksScopedAsync(workspaceId, 500, ct);
        foreach (var book in books)
        {
            var chapters = await _memLib.ListChaptersAsync(book.BookId, ct);
            foreach (var chapter in chapters)
            {
                var pointers = await _memLib.GetPointersAsync(chapter.ChapterId, ct);
                if (pointers.Any(p => string.Equals(p.PointerId, pointerId, StringComparison.Ordinal)))
                    return true;
            }
        }

        return false;
    }

    private static string SerializeDeleteResult(
        string type,
        string workspaceId,
        IReadOnlyList<string> deletedBookIds,
        IReadOnlyList<string> deletedChapterIds,
        IReadOnlyList<string> deletedPointerIds)
    {
        return JsonSerializer.Serialize(new
        {
            status = "ok",
            action = "delete",
            type,
            workspaceId,
            deletedCount = deletedBookIds.Count + deletedChapterIds.Count + deletedPointerIds.Count,
            deletedBookIds,
            deletedChapterIds,
            deletedPointerIds
        });
    }

    private static readonly string[] SupportedActions = ["upsert", "delete", "set_important", "get_important"];

    /// <summary>
    /// 归一化 type：首尾空白与大小写不应改变语义。
    /// 修复前 'Preference' 能通过 OrdinalIgnoreCase 校验，却落到 type switch 的通用分支写入空内容。
    /// 未知 type 仍然放行：既有合同由 MemoryQualityFilter 产出 unknown_type 警告，这里不重复拦截。
    /// </summary>
    private static string NormalizeType(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? "fact" : raw.Trim().ToLowerInvariant();

    private static string NormalizeAction(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? "upsert" : raw.Trim().ToLowerInvariant();

    /// <summary>
    /// 业务级错误（序列化结果 status=error）必须映射为 Success=false，
    /// 否则调用方会把失败计入成功，并可能在错误前提下继续收敛。
    /// </summary>
    private static ToolExecutionResult ToResult(string outputJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(outputJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && string.Equals(status.GetString(), "error", StringComparison.OrdinalIgnoreCase))
            {
                var message = doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString()
                    : null;
                return ToolExecutionResult.Fail(message ?? "Memory operation failed.");
            }
        }
        catch (JsonException)
        {
            // 非 JSON 输出按原样返回。
        }

        return ToolExecutionResult.Ok(outputJson);
    }

    private static JsonElement BuildRoot(SaveMemoryArgs args, ToolExecutionContext context)
    {
        return JsonSerializer.SerializeToElement(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["action"] = args.Action,
            ["type"] = args.Type,
            ["book"] = args.Book,
            ["content"] = args.Content,
            ["key"] = args.Key,
            ["value"] = args.Value,
            ["title"] = args.Title,
            ["book_id"] = args.BookId,
            ["chapter_id"] = args.ChapterId,
            ["pointer_id"] = args.PointerId,
            ["source_reference"] = args.SourceReference ?? args.SourceRef,
            ["source_label"] = args.SourceLabel,
            ["reference_type"] = args.ReferenceType,
        });
    }
}
