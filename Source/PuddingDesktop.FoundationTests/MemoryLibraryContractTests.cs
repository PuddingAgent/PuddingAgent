using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>DS-08 library slice: tree rendering, archiving semantics and the scoped-form rules.</summary>
public sealed class MemoryLibraryContractTests
{
    private static MemoryTreeNode Node(string id, string type, string title, string parent = "", string bookId = "",
        params MemoryTreeNode[] children) =>
        new(id, parent, type, title, "", "Active", bookId, children);

    private static IReadOnlyList<MemoryTreeNode> Tree() =>
    [
        Node("n1", "Page", "Root", children:
        [
            Node("n2", "Book", "Book A", "n1", "b1", Node("n3", "Chapter", "Chapter 1", "n2", "b1"))
        ]),
        Node("n4", "Note", "Loose note")
    ];

    [Fact]
    public void TreeRendersWithIndentationAndBookReferences()
    {
        var lines = MemoryLibraryText.RenderTree(Tree());
        Assert.Equal(4, lines.Count);
        Assert.Contains("Page（页面）", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("  [Book（记忆书）] Book A", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("    [Chapter（章节）] Chapter 1", lines[2], StringComparison.Ordinal);
        Assert.Contains("Book b1", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("Book ", lines[0], StringComparison.Ordinal);
        Assert.Equal(4, MemoryLibraryText.CountNodes(Tree()));
        Assert.Empty(MemoryLibraryText.RenderTree([]));
    }

    [Fact]
    public void FlattenKeepsCoreOrderAndExposesBookNodes()
    {
        var flat = MemoryLibraryText.Flatten(Tree());
        Assert.Equal(["n1", "n2", "n3", "n4"], flat.Select(node => node.Id));
        Assert.Equal(2, flat.Count(node => node.HasBook));
        Assert.Equal("Chapter 1", flat.Single(node => node.Id == "n3").Title);
    }

    [Fact]
    public void NodeVocabularyAndImportanceAreDescribedNotInvented()
    {
        Assert.Equal(["Page", "Book", "Chapter", "Note", "Index"], MemoryLibraryText.NodeTypes);
        Assert.Contains("记忆书", MemoryLibraryText.DescribeNodeType("book"), StringComparison.Ordinal);
        Assert.Equal("SomethingNew", MemoryLibraryText.DescribeNodeType("SomethingNew"));
        Assert.Equal("未设置", MemoryLibraryText.DescribeNodeType(null));
        Assert.True(MemoryLibraryText.IsKnownNodeType("PAGE"));
        Assert.False(MemoryLibraryText.IsKnownNodeType("Folder"));
        Assert.Equal("未标注", MemoryLibraryText.DescribeImportance(0));
        Assert.Equal("低", MemoryLibraryText.DescribeImportance(0.2));
        Assert.Equal("中", MemoryLibraryText.DescribeImportance(0.5));
        Assert.Equal("高", MemoryLibraryText.DescribeImportance(0.9));
    }

    [Fact]
    public void FormsRequireWhatCoreRequires()
    {
        var node = new MemoryTreeNodeCreate("ws", "agent", "lib", "", "Node", "", "Page");
        Assert.Empty(MemoryLibraryText.Validate(node));
        Assert.Contains("工作区与 Agent", MemoryLibraryText.Validate(node with { AgentId = "" }).Single(), StringComparison.Ordinal);
        Assert.Contains("资料库", MemoryLibraryText.Validate(node with { LibraryId = "" }).Single(), StringComparison.Ordinal);
        Assert.Contains("名称", MemoryLibraryText.Validate(node with { Name = " " }).Single(), StringComparison.Ordinal);
        Assert.Contains("节点类型", MemoryLibraryText.Validate(node with { NodeType = "Folder" }).Single(), StringComparison.Ordinal);

        Assert.Empty(MemoryLibraryText.Validate(new MemoryBookCreate("ws", "agent", "lib", "", "Title", "")));
        Assert.Contains("标题", MemoryLibraryText.Validate(new MemoryBookCreate("ws", "agent", "lib", "", "", "")).Single(), StringComparison.Ordinal);
        Assert.Contains("Book", MemoryLibraryText.Validate(new MemoryBookEdit("ws", "agent", "", "Title", "")).Single(), StringComparison.Ordinal);

        var chapter = new MemoryChapterCreate("ws", "agent", "book", "Title", "Content", 0.5);
        Assert.Empty(MemoryLibraryText.Validate(chapter));
        Assert.Contains("内容", MemoryLibraryText.Validate(chapter with { Content = " " }).Single(), StringComparison.Ordinal);
        Assert.Contains("重要度", MemoryLibraryText.Validate(chapter with { Importance = 1.5 }).Single(), StringComparison.Ordinal);
        Assert.Contains("重要度", MemoryLibraryText.Validate(chapter with { Importance = -0.1 }).Single(), StringComparison.Ordinal);
        Assert.Empty(MemoryLibraryText.Validate(new MemoryChapterEdit("ws", "agent", "ch", "T", "C", 1)));
    }

    [Fact]
    public void ImportanceParsingIsInvariantAndReportsGarbage()
    {
        Assert.Equal(0d, MemoryLibraryText.ParseImportance(""));
        Assert.Equal(0d, MemoryLibraryText.ParseImportance("   "));
        Assert.Equal(0.75d, MemoryLibraryText.ParseImportance("0.75"));
        Assert.Equal(1d, MemoryLibraryText.ParseImportance(" 1 "));
        Assert.Null(MemoryLibraryText.ParseImportance("high"));
    }

    [Fact]
    public void SearchFormsAndHitDescriptionsStayHonest()
    {
        Assert.Equal([10, 20, 50, 100], MemoryLibraryText.SearchTopKSizes);
        Assert.Empty(MemoryLibraryText.ValidateSearch("ws", "agent", "query"));
        Assert.Contains("工作区与 Agent", MemoryLibraryText.ValidateSearch("", "agent", "q").Single(), StringComparison.Ordinal);
        Assert.Contains("搜索词", MemoryLibraryText.ValidateSearch("ws", "agent", "  ").Single(), StringComparison.Ordinal);
        Assert.Contains("不重新排序", MemoryLibraryText.SearchNotice, StringComparison.Ordinal);

        var hit = new MemorySearchHit("book", "chapter", "Book Title", "snippet", 0.123456);
        Assert.Equal("0.123", hit.ScoreText);
        Assert.Equal("book", hit.BookId);
    }

    [Fact]
    public void InspectorKeysAreValidatedSeparatelyForSourcesAndPointers()
    {
        Assert.Empty(MemoryLibraryText.ValidateOwner("chapter", "ch-1", "来源 owner"));
        Assert.Contains("来源 owner类型", MemoryLibraryText.ValidateOwner("", "ch-1", "来源 owner").Single(), StringComparison.Ordinal);
        Assert.Contains("指针 source ID", MemoryLibraryText.ValidateOwner("chapter", "", "指针 source").Single(), StringComparison.Ordinal);
        Assert.Contains("不会把它们混成一套", MemoryLibraryText.InspectorNotice, StringComparison.Ordinal);

        // 方向是 Core 给的，未知方向原样显示。
        Assert.Equal("出边", MemoryLibraryText.DescribeDirection("outgoing"));
        Assert.Equal("入边（反链）", MemoryLibraryText.DescribeDirection("backlink"));
        Assert.Equal("方向未知", MemoryLibraryText.DescribeDirection(null));
        Assert.Equal("sideways", MemoryLibraryText.DescribeDirection("sideways"));
    }

    [Fact]
    public void SourceAndPointerDisplaysUseCoreFieldsOnly()
    {
        var source = new MemorySource("s1", "chapter", "ch-1", "file", "src/app.cs", "10-20", "app.cs", "desc",
            DateTimeOffset.UtcNow);
        Assert.Equal("file:src/app.cs · app.cs", source.Display);
        Assert.Equal("file:src/app.cs", (source with { Label = "" }).Display);

        var pointer = new MemoryPointer("p1", "ch-1", "chapter", "ch-2", "Other", "text", 7, DateTimeOffset.UtcNow, "outgoing");
        Assert.Contains("[outgoing]", pointer.Display, StringComparison.Ordinal);
        Assert.Contains("相关度 7", pointer.Display, StringComparison.Ordinal);
        Assert.Equal("[outgoing] chapter:ch-2 · 相关度 7", (pointer with { TargetLabel = "", Description = "" }).Display);
    }
    [Fact]
    public void NoticesSayArchiveIsNotDeleteAndThatScopeIsPerAgent()
    {
        Assert.Contains("不是删除", MemoryLibraryText.ArchiveNotice, StringComparison.Ordinal);
        Assert.Contains("工作区 + Agent", MemoryLibraryText.ScopeNotice, StringComparison.Ordinal);

        var library = new MemoryLibrary("lib", "ws", "Default", "", "agent", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.Equal("ws", library.WorkspaceId);
        Assert.Equal("agent", library.AgentId);

        var chapter = new MemoryChapter("ch", "b", "T", new string('x', 200), "text", 0.5,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.Equal(81, chapter.Preview.Length);
        Assert.EndsWith("…", chapter.Preview, StringComparison.Ordinal);
        var shortChapter = chapter with { Content = "short" };
        Assert.Equal("short", shortChapter.Preview);
    }
}