namespace PuddingDesktop.Foundation;

/// <summary>
/// A memory library as Core reports it: library id, owning workspace, and the agent it belongs to.
/// There is no separate "scope" field in Core, so none is invented here.
/// </summary>
public sealed record MemoryLibrary(
    string LibraryId, string WorkspaceId, string Name, string Description, string AgentId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>A node of the memory page tree. Children are nested exactly as Core returns them.</summary>
public sealed record MemoryTreeNode(
    string Id, string ParentId, string Type, string Title, string Summary, string Status, string BookId,
    IReadOnlyList<MemoryTreeNode> Children)
{
    public bool HasBook => BookId.Length > 0;
    public string TypeText => MemoryLibraryText.DescribeNodeType(Type);
}

public sealed record MemoryChapter(
    string ChapterId, string BookId, string Title, string Content, string ContentType,
    double Importance, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public string ImportanceText => MemoryLibraryText.DescribeImportance(Importance);
    public string Preview => Content.Length <= 80 ? Content : Content[..80] + "…";
}

public sealed record MemoryBook(
    string WorkspaceId, string LibraryId, string BookId, string Title, string Summary, string Status,
    IReadOnlyList<MemoryChapter> Chapters);

public sealed record MemoryTreeNodeCreate(
    string WorkspaceId, string AgentId, string LibraryId, string ParentNodeId, string Name, string Summary, string NodeType);

public sealed record MemoryBookCreate(
    string WorkspaceId, string AgentId, string LibraryId, string NodeId, string Title, string Summary);

public sealed record MemoryBookEdit(string WorkspaceId, string AgentId, string BookId, string Title, string Summary);

public sealed record MemoryChapterCreate(
    string WorkspaceId, string AgentId, string BookId, string Title, string Content, double Importance);

public sealed record MemoryChapterEdit(
    string WorkspaceId, string AgentId, string ChapterId, string Title, string Content, double Importance);

/// <summary>A full-text hit. Core returns the book, chapter, snippet and score it actually computed.</summary>
public sealed record MemorySearchHit(string BookId, string ChapterId, string BookTitle, string Snippet, double Score)
{
    public string ScoreText => Score.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A source reference that points at the memory owner.</summary>
public sealed record MemorySource(
    string SourceReferenceId, string OwnerType, string OwnerId, string TargetType, string TargetId,
    string TargetRange, string Label, string Description, DateTimeOffset CreatedAt)
{
    public string Display => $"{TargetType}:{TargetId}" + (Label.Length == 0 ? "" : $" · {Label}");
}

/// <summary>A knowledge-graph pointer, with the direction Core reported it in.</summary>
public sealed record MemoryPointer(
    string PointerId, string ChapterId, string TargetType, string TargetId, string TargetLabel,
    string Description, int Relevance, DateTimeOffset CreatedAt, string Direction)
{
    public string Display => $"[{Direction}] {TargetType}:{TargetId}" +
                             (TargetLabel.Length == 0 ? "" : $" · {TargetLabel}") +
                             $" · 相关度 {Relevance}";
}

/// <summary>
/// Task-shaped operations for the memory library cards, implemented in Composition against the shared
/// MemoryLibraryAdminService.
/// </summary>
public interface IMemoryLibrarySettings
{
    Task<IReadOnlyList<MemorySearchHit>> SearchAsync(
        string workspaceId, string agentId, string query, int topK, CancellationToken cancellationToken = default);
    /// <summary>Source references are addressed by owner type/id; pointers by source type/id (Core's own split).</summary>
    Task<IReadOnlyList<MemorySource>> ListSourcesAsync(
        string workspaceId, string agentId, string ownerType, string ownerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemoryPointer>> ListPointersAsync(
        string workspaceId, string agentId, string sourceType, string sourceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemoryLibrary>> ListLibrariesAsync(string workspaceId, string agentId, CancellationToken cancellationToken = default);
    Task<MemoryLibrary> EnsureDefaultLibraryAsync(string workspaceId, string agentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemoryTreeNode>> ReadTreeAsync(string workspaceId, string agentId, string libraryId, CancellationToken cancellationToken = default);
    Task CreateTreeNodeAsync(MemoryTreeNodeCreate create, CancellationToken cancellationToken = default);
    Task<MemoryBook?> ReadBookAsync(string workspaceId, string agentId, string bookId, CancellationToken cancellationToken = default);
    Task CreateBookAsync(MemoryBookCreate create, CancellationToken cancellationToken = default);
    Task UpdateBookAsync(MemoryBookEdit edit, CancellationToken cancellationToken = default);
    Task CreateChapterAsync(MemoryChapterCreate create, CancellationToken cancellationToken = default);
    Task UpdateChapterAsync(MemoryChapterEdit edit, CancellationToken cancellationToken = default);
    Task ArchiveBookAsync(string workspaceId, string agentId, string bookId, CancellationToken cancellationToken = default);
    Task ArchiveChapterAsync(string workspaceId, string agentId, string chapterId, CancellationToken cancellationToken = default);
}

public static class MemoryLibraryText
{
    /// <summary>Core's node vocabulary; unknown values are shown verbatim rather than renamed.</summary>
    public static IReadOnlyList<string> NodeTypes { get; } = ["Page", "Book", "Chapter", "Note", "Index"];

    public const string ArchiveNotice =
        "归档只是把 Book/章节标记为已归档（Core 会保留内容），不是删除；归档后仍可在这里看到它们的状态。";

    /// <summary>Core returns FTS hits with its own score; the page never re-ranks or invents relevance.</summary>
    public const string SearchNotice =
        "搜索是 Core 的全文检索：结果与分数由 Core 返回，界面只做展示与定位，不重新排序。";

    public const string InspectorNotice =
        "来源（sources）按 ownerType/ownerId 查询，指针（pointers）按 sourceType/sourceId 查询——" +
        "两者是 Core 自己的两套键，界面不会把它们混成一套。";

    public static IReadOnlyList<int> SearchTopKSizes { get; } = [10, 20, 50, 100];

    public static string DescribeDirection(string? direction) => direction switch
    {
        null or "" => "方向未知",
        var value when string.Equals(value, "outgoing", StringComparison.OrdinalIgnoreCase) => "出边",
        var value when string.Equals(value, "backlink", StringComparison.OrdinalIgnoreCase) => "入边（反链）",
        var value => value
    };

    public static IReadOnlyList<string> ValidateSearch(string workspaceId, string agentId, string query)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(agentId)) errors.Add("必须选择工作区与 Agent。");
        if (string.IsNullOrWhiteSpace(query)) errors.Add("搜索词不能为空。");
        return errors;
    }

    public static IReadOnlyList<string> ValidateOwner(string ownerType, string ownerId, string label)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ownerType)) errors.Add($"{label}类型不能为空。");
        if (string.IsNullOrWhiteSpace(ownerId)) errors.Add($"{label} ID 不能为空。");
        return errors;
    }

    public const string ScopeNotice =
        "记忆资料库严格按 工作区 + Agent 作用域读写：切换 Agent 会得到另一份资料库，不会互相串数据。";

    public static string DescribeNodeType(string? nodeType) => nodeType switch
    {
        null or "" => "未设置",
        var value when string.Equals(value, "Page", StringComparison.OrdinalIgnoreCase) => "Page（页面）",
        var value when string.Equals(value, "Book", StringComparison.OrdinalIgnoreCase) => "Book（记忆书）",
        var value when string.Equals(value, "Chapter", StringComparison.OrdinalIgnoreCase) => "Chapter（章节）",
        var value when string.Equals(value, "Note", StringComparison.OrdinalIgnoreCase) => "Note（笔记）",
        var value when string.Equals(value, "Index", StringComparison.OrdinalIgnoreCase) => "Index（索引）",
        var value => value
    };

    public static string DescribeImportance(double importance) => importance switch
    {
        <= 0 => "未标注",
        < 0.34 => "低",
        < 0.67 => "中",
        _ => "高"
    };

    public static bool IsKnownNodeType(string? nodeType) =>
        nodeType is not null && NodeTypes.Contains(nodeType, StringComparer.OrdinalIgnoreCase);

    /// <summary>Flattens the tree into indented lines for a read-only display; order follows Core's payload.</summary>
    public static IReadOnlyList<string> RenderTree(IReadOnlyList<MemoryTreeNode> nodes)
    {
        var lines = new List<string>();
        void Walk(MemoryTreeNode node, int depth)
        {
            var indent = new string(' ', depth * 2);
            var book = node.HasBook ? $" · Book {node.BookId}" : "";
            var summary = node.Summary.Length == 0 ? "" : $" — {node.Summary}";
            lines.Add($"{indent}[{node.TypeText}] {node.Title}{summary} · {node.Status}{book}");
            foreach (var child in node.Children) Walk(child, depth + 1);
        }
        foreach (var node in nodes) Walk(node, 0);
        return lines;
    }

    public static int CountNodes(IReadOnlyList<MemoryTreeNode> nodes) =>
        nodes.Sum(node => 1 + CountNodes(node.Children));

    /// <summary>Flattens the tree so a picker can offer every Book node.</summary>
    public static IReadOnlyList<MemoryTreeNode> Flatten(IReadOnlyList<MemoryTreeNode> nodes) =>
        nodes.SelectMany(node => new[] { node }.Concat(Flatten(node.Children))).ToArray();

    public static IReadOnlyList<string> Validate(MemoryTreeNodeCreate create)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(create.WorkspaceId) || string.IsNullOrWhiteSpace(create.AgentId))
            errors.Add("必须选择工作区与 Agent。");
        if (string.IsNullOrWhiteSpace(create.LibraryId)) errors.Add("必须选择资料库。");
        if (string.IsNullOrWhiteSpace(create.Name)) errors.Add("节点名称不能为空。");
        if (!IsKnownNodeType(create.NodeType)) errors.Add($"节点类型必须是 {string.Join(" / ", NodeTypes)} 之一。");
        return errors;
    }

    public static IReadOnlyList<string> Validate(MemoryBookCreate create)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(create.WorkspaceId) || string.IsNullOrWhiteSpace(create.AgentId))
            errors.Add("必须选择工作区与 Agent。");
        if (string.IsNullOrWhiteSpace(create.LibraryId)) errors.Add("必须选择资料库。");
        if (string.IsNullOrWhiteSpace(create.Title)) errors.Add("Book 标题不能为空。");
        return errors;
    }

    public static IReadOnlyList<string> Validate(MemoryBookEdit edit)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(edit.BookId)) errors.Add("缺少 Book。");
        if (string.IsNullOrWhiteSpace(edit.Title)) errors.Add("Book 标题不能为空。");
        return errors;
    }

    public static IReadOnlyList<string> Validate(MemoryChapterCreate create)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(create.WorkspaceId) || string.IsNullOrWhiteSpace(create.AgentId))
            errors.Add("必须选择工作区与 Agent。");
        if (string.IsNullOrWhiteSpace(create.BookId)) errors.Add("缺少 Book。");
        AddChapterErrors(errors, create.Title, create.Content, create.Importance);
        return errors;
    }

    public static IReadOnlyList<string> Validate(MemoryChapterEdit edit)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(edit.ChapterId)) errors.Add("缺少章节。");
        AddChapterErrors(errors, edit.Title, edit.Content, edit.Importance);
        return errors;
    }

    private static void AddChapterErrors(List<string> errors, string title, string content, double importance)
    {
        if (string.IsNullOrWhiteSpace(title)) errors.Add("章节标题不能为空。");
        if (string.IsNullOrWhiteSpace(content)) errors.Add("章节内容不能为空。");
        if (importance is < 0 or > 1) errors.Add("重要度必须在 0 与 1 之间。");
    }

    /// <summary>Parses a 0..1 importance typed by hand; blank means "unmarked" (0).</summary>
    public static double? ParseImportance(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
