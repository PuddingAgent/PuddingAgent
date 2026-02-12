using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

// ═══════════════════════════════════════════════════════════════
// MemoryToolArgs — 记忆工具参数定义（集中管理）
// ═══════════════════════════════════════════════════════════════

public sealed record SaveMemoryArgs
{
    [ToolParam("Operation: upsert or delete.")]
    public string? Action { get; init; }

    [ToolParam("Content type: fact / preference / summary / chapter.")]
    public string? Type { get; init; }

    [ToolParam("Optional target book name.")]
    public string? Book { get; init; }

    [ToolParam("Main content for fact, summary, or chapter.")]
    public string? Content { get; init; }

    [ToolParam("Preference key. Required for preference entries.")]
    public string? Key { get; init; }

    [ToolParam("Preference value. Required for preference entries.")]
    public string? Value { get; init; }

    [ToolParam("Chapter title.")]
    public string? Title { get; init; }

    [ToolParam("Exact book id for delete.")]
    public string? BookId { get; init; }

    [ToolParam("Exact chapter id for delete.")]
    public string? ChapterId { get; init; }

    [ToolParam("Exact pointer id for delete.")]
    public string? PointerId { get; init; }

    [ToolParam("Optional source reference such as session id or URL.")]
    public string? SourceRef { get; init; }

    [ToolParam("Optional source label.")]
    public string? SourceLabel { get; init; }

    [ToolParam("Internal session path or external URL for source verification.")]
    public string? SourceReference { get; init; }

    [ToolParam("Reference type: internal / external / none.")]
    public string? ReferenceType { get; init; }
}

public sealed record ManageMemoryArgs
{
    [ToolParam("Operation: list_books / create_book / list_chapters / add_chapter / update_chapter / delete_book / add_pointer / list_pointers / add_relation / list_relations / get_related / dedup_report / merge_chapters。章节支持设置 scene（场景）/ constraints（约束）/ tags（标签）和 source_reference（引用来源），用于溯源核实和知识图谱关联。")]
    public string? Action { get; init; }

    [ToolParam("Target book id.")]
    public string? BookId { get; init; }

    [ToolParam("Set true only for explicit history/audit views. Defaults to false.")]
    public bool? IncludeHistory { get; init; }

    [ToolParam("Optional library id.")]
    public string? LibraryId { get; init; }

    [ToolParam("Book or chapter title.")]
    public string? Title { get; init; }

    [ToolParam("Chapter content.")]
    public string? Content { get; init; }

    [ToolParam("Book summary.")]
    public string? Summary { get; init; }

    [ToolParam("Target chapter id.")]
    public string? ChapterId { get; init; }

    [ToolParam("Pointer source type for list_pointers.")]
    public string? SourceType { get; init; }

    [ToolParam("Pointer source id for list_pointers.")]
    public string? SourceId { get; init; }

    [ToolParam("Comma-separated tags.")]
    public string? Tags { get; init; }

    [ToolParam("Chapter order.")]
    public double? ChapterOrder { get; init; }

    [ToolParam("Internal session path or external URL for source verification.")]
    public string? SourceReference { get; init; }

    [ToolParam("Reference type: internal / external / none.")]
    public string? ReferenceType { get; init; }

    // ── Phase 1: 元数据字段 ──

    [ToolParam("Scene context: when/where this memory is applicable. Phase 1.")]
    public string? Scene { get; init; }

    [ToolParam("Constraints: limitations and prerequisites for using this memory. Phase 1.")]
    public string? Constraints { get; init; }

    [ToolParam("Source chapter id for relation operations.")]
    public string? SourceChapterId { get; init; }

    [ToolParam("Target chapter id for relation operations.")]
    public string? TargetChapterId { get; init; }

    [ToolParam("Relation type for add_relation or list_relations.")]
    public string? RelationType { get; init; }

    [ToolParam("Optional relation description.")]
    public string? Description { get; init; }

    [ToolParam("Optional relation weight.")]
    public double? Weight { get; init; }

    [ToolParam("Traversal depth for get_related.")]
    public int? Depth { get; init; }

    [ToolParam("Minimum relation weight for get_related.")]
    public double? MinWeight { get; init; }

    [ToolParam("Source book id for merge_chapters.")]
    public string? SourceBookId { get; init; }

    [ToolParam("Target book id for merge_chapters.")]
    public string? TargetBookId { get; init; }
}

public sealed record GrepMemoryArgs
{
    [ToolParam("Operation: search / in_book / list_books / toc.")]
    public string? Action { get; init; }

    [ToolParam("Search query for search or in_book.")]
    public string? Query { get; init; }

    [ToolParam("Search mode: fts5 or regex.")]
    public string? Mode { get; init; }

    [ToolParam("Book name for in_book.")]
    public string? Book { get; init; }

    [ToolParam("Maximum result count.")]
    public double? TopK { get; init; }

    [ToolParam("Target book id for toc.")]
    public string? BookId { get; init; }

    [ToolParam("Set true only for explicit history/audit search. Defaults to false.")]
    public bool? IncludeHistory { get; init; }
}
