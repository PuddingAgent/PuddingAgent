using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Tools;
using PuddingMemoryEngine.Data;
using PuddingRuntime.Services.Tools;
using PuddingRuntime.Services.Tools.Handlers;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class MemoryToolsTests
{
    [TestMethod]
    public void MemoryLibraryTool_Uses_Strongly_Typed_Tool_Base()
    {
        Assert.IsTrue(
            typeof(PuddingToolBase<SearchMemoryArgs>).IsAssignableFrom(typeof(MemoryLibraryTool)),
            "MemoryLibraryTool should derive from PuddingToolBase<SearchMemoryArgs>.");
    }

    [TestMethod]
    public void ManageMemoryTool_Uses_Strongly_Typed_Tool_Base()
    {
        Assert.IsTrue(
            typeof(PuddingToolBase<ManageMemoryArgs>).IsAssignableFrom(typeof(ManageMemoryTool)),
            "ManageMemoryTool should derive from PuddingToolBase<ManageMemoryArgs>.");
    }

    [TestMethod]
    public void GrepMemoryTool_Uses_Strongly_Typed_Tool_Base()
    {
        Assert.IsTrue(
            typeof(PuddingToolBase<GrepMemoryArgs>).IsAssignableFrom(typeof(GrepMemoryTool)),
            "GrepMemoryTool should derive from PuddingToolBase<GrepMemoryArgs>.");
    }

    [TestMethod]
    public void SaveMemoryTool_Uses_Strongly_Typed_Tool_Base()
    {
        Assert.IsTrue(
            typeof(PuddingToolBase<SaveMemoryArgs>).IsAssignableFrom(typeof(SaveMemoryTool)),
            "SaveMemoryTool should derive from PuddingToolBase<SaveMemoryArgs>.");
    }

    [TestMethod]
    public async Task SaveMemory_Delete_Removes_Chapter_And_Fts_Result()
    {
        await using var scope = await CreateScopeAsync();
        var save = new SaveMemoryTool(scope.Convenience, scope.Library, NullLogger<SaveMemoryTool>.Instance);
        var grep = new GrepMemoryTool(scope.Convenience, scope.Library, NullLogger<GrepMemoryTool>.Instance);

        var write = await ExecuteJsonAsync(save, """
        {
          "action": "upsert",
          "type": "fact",
          "book": "Delete Contract",
          "content": "deletable-memory-token",
          "workspace_id": "ws-memory-tools"
        }
        """);
        var chapterId = JsonDocument.Parse(write).RootElement.GetProperty("chapterId").GetString();

        var beforeDelete = await ExecuteJsonAsync(grep, """
        {
          "action": "search",
          "query": "deletable-memory-token",
          "workspace_id": "ws-memory-tools"
        }
        """);
        Assert.AreEqual(1, JsonDocument.Parse(beforeDelete).RootElement.GetProperty("count").GetInt32());

        var delete = await ExecuteJsonAsync(save, $$"""
        {
          "action": "delete",
          "type": "chapter",
          "chapter_id": "{{chapterId}}",
          "workspace_id": "ws-memory-tools"
        }
        """);

        var deleteRoot = JsonDocument.Parse(delete).RootElement;
        Assert.AreEqual("ok", deleteRoot.GetProperty("status").GetString());
        Assert.AreEqual(1, deleteRoot.GetProperty("deletedCount").GetInt32());
        Assert.IsNull(await scope.Library.GetChapterAsync(chapterId!));

        var afterDelete = await ExecuteJsonAsync(grep, """
        {
          "action": "search",
          "query": "deletable-memory-token",
          "workspace_id": "ws-memory-tools"
        }
        """);
        Assert.AreEqual(0, JsonDocument.Parse(afterDelete).RootElement.GetProperty("count").GetInt32());
    }

    [TestMethod]
    public async Task SaveMemory_Delete_ByChapterId_Does_Not_Delete_Other_Workspace_Memory()
    {
        await using var scope = await CreateScopeAsync();
        var save = new SaveMemoryTool(scope.Convenience, scope.Library, NullLogger<SaveMemoryTool>.Instance);

        var otherLibrary = await scope.Library.CreateLibraryAsync("ws-other", "Other", null);
        var otherBook = await scope.Library.CreateBookAsync(otherLibrary.LibraryId, "Other Workspace", "");
        var otherChapter = await scope.Library.AddChapterAsync(otherBook.BookId, "Private", "do not delete");

        var delete = await ExecuteJsonAsync(save, $$"""
        {
          "action": "delete",
          "type": "chapter",
          "chapter_id": "{{otherChapter.ChapterId}}",
          "workspace_id": "ws-memory-tools"
        }
        """);

        var deleteRoot = JsonDocument.Parse(delete).RootElement;
        Assert.AreEqual("ok", deleteRoot.GetProperty("status").GetString());
        Assert.AreEqual(0, deleteRoot.GetProperty("deletedCount").GetInt32());
        Assert.IsNotNull(await scope.Library.GetChapterAsync(otherChapter.ChapterId));
    }

    [TestMethod]
    public async Task ManageMemory_ListPointers_Without_ChapterId_Returns_Workspace_Pointers()
    {
        await using var scope = await CreateScopeAsync();
        var manage = CreateManageTool(scope.Library);
        var library = await scope.Library.CreateLibraryAsync("ws-memory-tools", "Default", null);
        var book = await scope.Library.CreateBookAsync(library.LibraryId, "Pointer Contract", "");
        var chapter = await scope.Library.AddChapterAsync(book.BookId, "Evidence", "pointer body");
        var pointer = await scope.Library.CreatePointerAsync(chapter.ChapterId, "session", "session-123", "origin");

        var result = await ExecuteJsonAsync(manage, """
        {
          "action": "list_pointers",
          "workspace_id": "ws-memory-tools"
        }
        """);

        var root = JsonDocument.Parse(result).RootElement;
        Assert.AreEqual("ok", root.GetProperty("status").GetString());
        Assert.AreEqual(1, root.GetProperty("count").GetInt32());
        Assert.AreEqual(pointer.PointerId, root.GetProperty("pointers")[0].GetProperty("PointerId").GetString());
    }

    [TestMethod]
    public async Task ManageMemory_DeleteBook_Removes_Book_From_ListBooks()
    {
        await using var scope = await CreateScopeAsync();
        var manage = CreateManageTool(scope.Library);
        var library = await scope.Library.CreateLibraryAsync("ws-memory-tools", "Default", null);
        var book = await scope.Library.CreateBookAsync(library.LibraryId, "Delete Book Contract", "");
        await scope.Library.AddChapterAsync(book.BookId, "Evidence", "delete-book-contract-token");

        var delete = await ExecuteJsonAsync(manage, $$"""
        {
          "action": "delete_book",
          "book_id": "{{book.BookId}}",
          "workspace_id": "ws-memory-tools"
        }
        """);

        var deleteRoot = JsonDocument.Parse(delete).RootElement;
        Assert.AreEqual("ok", deleteRoot.GetProperty("status").GetString());
        Assert.AreEqual("delete_book", deleteRoot.GetProperty("action").GetString());

        var list = await ExecuteJsonAsync(manage, """
        {
          "action": "list_books",
          "workspace_id": "ws-memory-tools"
        }
        """);

        var books = JsonDocument.Parse(list).RootElement.GetProperty("books");
        Assert.AreEqual(0, books.GetArrayLength());
        Assert.IsNull(await scope.Library.GetBookReadOnlyAsync(book.BookId));
    }

    [TestMethod]
    public async Task GrepMemory_Fts5_Finds_Newly_Saved_Memory_Immediately()
    {
        await using var scope = await CreateScopeAsync();
        var save = new SaveMemoryTool(scope.Convenience, scope.Library, NullLogger<SaveMemoryTool>.Instance);
        var grep = new GrepMemoryTool(scope.Convenience, scope.Library, NullLogger<GrepMemoryTool>.Instance);

        await ExecuteJsonAsync(save, """
        {
          "action": "upsert",
          "type": "fact",
          "book": "Realtime FTS",
          "content": "fresh-memory-token",
          "workspace_id": "ws-memory-tools"
        }
        """);

        var search = await ExecuteJsonAsync(grep, """
        {
          "action": "search",
          "query": "fresh-memory-token",
          "mode": "fts5",
          "workspace_id": "ws-memory-tools"
        }
        """);

        var root = JsonDocument.Parse(search).RootElement;
        Assert.AreEqual("ok", root.GetProperty("status").GetString());
        Assert.AreEqual(1, root.GetProperty("count").GetInt32());
        StringAssert.Contains(root.GetProperty("results")[0].GetProperty("Snippet").GetString(), "fresh-memory-token");
    }

    [TestMethod]
    public async Task SaveMemory_Upsert_Does_Not_Route_To_Same_Title_Book_In_Other_Workspace()
    {
        await using var scope = await CreateScopeAsync();
        var save = new SaveMemoryTool(scope.Convenience, scope.Library, NullLogger<SaveMemoryTool>.Instance);

        var otherLibrary = await scope.Library.CreateLibraryAsync("ws-other", "Other", null);
        var otherBook = await scope.Library.CreateBookAsync(
            otherLibrary.LibraryId,
            "Scoped Write Contract",
            "existing book in another workspace");

        var write = await ExecuteJsonAsync(save, """
        {
          "action": "upsert",
          "type": "fact",
          "book": "Scoped Write Contract",
          "content": "current-workspace-memory-token",
          "workspace_id": "ws-memory-tools"
        }
        """);

        var root = JsonDocument.Parse(write).RootElement;
        Assert.AreEqual("ok", root.GetProperty("status").GetString());
        Assert.AreNotEqual(otherBook.BookId, root.GetProperty("bookId").GetString());

        var currentWorkspaceBooks = await scope.Library.ListBooksScopedAsync("ws-memory-tools", 10);
        Assert.AreEqual(1, currentWorkspaceBooks.Count);
        Assert.AreEqual("Scoped Write Contract", currentWorkspaceBooks[0].Title);

        var otherWorkspaceChapters = await scope.Library.ListChaptersAsync(otherBook.BookId);
        Assert.AreEqual(0, otherWorkspaceChapters.Count);
    }

    [TestMethod]
    public async Task SaveMemory_Upsert_Reuses_Existing_Chapter_For_Same_Title_And_Content()
    {
        await using var scope = await CreateScopeAsync();
        var save = new SaveMemoryTool(scope.Convenience, scope.Library, NullLogger<SaveMemoryTool>.Instance);

        var first = await ExecuteJsonAsync(save, """
        {
          "action": "upsert",
          "type": "fact",
          "book": "Chapter Dedup Contract",
          "content": "chapter-dedup-token",
          "workspace_id": "ws-memory-tools"
        }
        """);

        var second = await ExecuteJsonAsync(save, """
        {
          "action": "upsert",
          "type": "fact",
          "book": "Chapter Dedup Contract",
          "content": "chapter-dedup-token",
          "workspace_id": "ws-memory-tools"
        }
        """);

        var firstRoot = JsonDocument.Parse(first).RootElement;
        var secondRoot = JsonDocument.Parse(second).RootElement;
        Assert.AreEqual("ok", secondRoot.GetProperty("status").GetString());
        Assert.AreEqual(firstRoot.GetProperty("bookId").GetString(), secondRoot.GetProperty("bookId").GetString());
        Assert.AreEqual(firstRoot.GetProperty("chapterId").GetString(), secondRoot.GetProperty("chapterId").GetString());

        var chapters = await scope.Library.ListChaptersAsync(firstRoot.GetProperty("bookId").GetString()!);
        Assert.AreEqual(1, chapters.Count);
    }

    [TestMethod]
    public async Task SaveMemory_Upsert_Uses_Llm_To_Reuse_Semantically_Equivalent_Chapter()
    {
        var llm = new ReuseFirstCandidateMemoryLlmClient();
        await using var scope = await CreateScopeAsync(llm);
        var save = new SaveMemoryTool(scope.Convenience, scope.Library, NullLogger<SaveMemoryTool>.Instance);

        var first = await ExecuteJsonAsync(save, """
        {
          "action": "upsert",
          "type": "fact",
          "book": "Semantic Chapter Dedup Contract",
          "content": "用户喜欢蓝莓芝士蛋糕",
          "workspace_id": "ws-memory-tools"
        }
        """);

        var second = await ExecuteJsonAsync(save, """
        {
          "action": "upsert",
          "type": "fact",
          "book": "Semantic Chapter Dedup Contract",
          "content": "蓝莓芝士蛋糕是用户偏好的甜点",
          "workspace_id": "ws-memory-tools"
        }
        """);

        var firstRoot = JsonDocument.Parse(first).RootElement;
        var secondRoot = JsonDocument.Parse(second).RootElement;
        Assert.AreEqual(firstRoot.GetProperty("chapterId").GetString(), secondRoot.GetProperty("chapterId").GetString());
        Assert.AreEqual(1, llm.ChatCalls);

        var chapters = await scope.Library.ListChaptersAsync(firstRoot.GetProperty("bookId").GetString()!);
        Assert.AreEqual(1, chapters.Count);
    }

    [TestMethod]
    public async Task SaveMemory_Upsert_Uses_Llm_To_Supersede_Existing_Chapter()
    {
        var llm = new SupersedeFirstCandidateMemoryLlmClient();
        await using var scope = await CreateScopeAsync(llm);
        var save = new SaveMemoryTool(scope.Convenience, scope.Library, NullLogger<SaveMemoryTool>.Instance);

        var first = await ExecuteJsonAsync(save, """
        {
          "action": "upsert",
          "type": "preference",
          "book": "Preference Supersede Contract",
          "key": "dessert",
          "value": "用户喜欢蓝莓芝士蛋糕",
          "workspace_id": "ws-memory-tools"
        }
        """);

        var second = await ExecuteJsonAsync(save, """
        {
          "action": "upsert",
          "type": "preference",
          "book": "Preference Supersede Contract",
          "key": "dessert",
          "value": "用户现在不喜欢蓝莓芝士蛋糕，改喜欢抹茶蛋糕",
          "workspace_id": "ws-memory-tools"
        }
        """);

        var firstRoot = JsonDocument.Parse(first).RootElement;
        var secondRoot = JsonDocument.Parse(second).RootElement;
        var bookId = firstRoot.GetProperty("bookId").GetString()!;
        var firstChapterId = firstRoot.GetProperty("chapterId").GetString()!;
        var secondChapterId = secondRoot.GetProperty("chapterId").GetString()!;
        Assert.AreNotEqual(firstChapterId, secondChapterId);
        Assert.AreEqual(1, llm.ChatCalls);

        var manage = CreateManageTool(scope.Library);
        var grep = new GrepMemoryTool(scope.Convenience, scope.Library, NullLogger<GrepMemoryTool>.Instance);

        var chapters = await scope.Library.ListChaptersAsync(bookId);
        Assert.AreEqual(1, chapters.Count);
        Assert.AreEqual(secondChapterId, chapters[0].ChapterId);
        Assert.AreEqual("active", chapters[0].Status);
        StringAssert.Contains(chapters[0].Content, "抹茶蛋糕");

        var oldChapter = await scope.Library.GetChapterAsync(firstChapterId);
        Assert.IsNotNull(oldChapter);
        Assert.AreEqual("superseded", oldChapter.Status);
        Assert.AreEqual(secondChapterId, oldChapter.SupersededByChapterId);
        Assert.IsNotNull(oldChapter.SupersededAt);
        StringAssert.Contains(oldChapter.Content, "蓝莓芝士蛋糕");

        var defaultList = await ExecuteJsonAsync(manage, $$"""
        {
          "action": "list_chapters",
          "book_id": "{{bookId}}",
          "workspace_id": "ws-memory-tools"
        }
        """);
        var defaultChapters = JsonDocument.Parse(defaultList).RootElement.GetProperty("chapters");
        Assert.AreEqual(1, defaultChapters.GetArrayLength());
        Assert.AreEqual("active", defaultChapters[0].GetProperty("Status").GetString());

        var historyList = await ExecuteJsonAsync(manage, $$"""
        {
          "action": "list_chapters",
          "book_id": "{{bookId}}",
          "include_history": true,
          "workspace_id": "ws-memory-tools"
        }
        """);
        var historyChapters = JsonDocument.Parse(historyList).RootElement.GetProperty("chapters");
        Assert.AreEqual(2, historyChapters.GetArrayLength());
        var historical = historyChapters.EnumerateArray()
            .Single(ch => ch.GetProperty("ChapterId").GetString() == firstChapterId);
        Assert.AreEqual("superseded", historical.GetProperty("Status").GetString());
        Assert.AreEqual(secondChapterId, historical.GetProperty("SupersededByChapterId").GetString());

        var defaultSearch = await ExecuteJsonAsync(grep, """
        {
          "action": "search",
          "query": "用户喜欢蓝莓芝士蛋糕",
          "mode": "regex",
          "book": "Preference Supersede Contract",
          "workspace_id": "ws-memory-tools"
        }
        """);
        Assert.AreEqual(0, JsonDocument.Parse(defaultSearch).RootElement.GetProperty("count").GetInt32());

        var historySearch = await ExecuteJsonAsync(grep, """
        {
          "action": "search",
          "query": "用户喜欢蓝莓芝士蛋糕",
          "mode": "regex",
          "book": "Preference Supersede Contract",
          "include_history": true,
          "workspace_id": "ws-memory-tools"
        }
        """);
        var historySearchRoot = JsonDocument.Parse(historySearch).RootElement;
        Assert.AreEqual(1, historySearchRoot.GetProperty("count").GetInt32());
        Assert.AreEqual("superseded", historySearchRoot.GetProperty("results")[0].GetProperty("Status").GetString());
        Assert.AreEqual(secondChapterId, historySearchRoot.GetProperty("results")[0].GetProperty("SupersededByChapterId").GetString());
    }

    [TestMethod]
    public async Task GrepMemory_Fts5_Falls_Back_To_Scoped_Substring_For_New_Chinese_Text()
    {
        await using var scope = await CreateScopeAsync();
        var save = new SaveMemoryTool(scope.Convenience, scope.Library, NullLogger<SaveMemoryTool>.Instance);
        var grep = new GrepMemoryTool(scope.Convenience, scope.Library, NullLogger<GrepMemoryTool>.Instance);

        await ExecuteJsonAsync(save, """
        {
          "action": "upsert",
          "type": "fact",
          "book": "中文实时检索",
          "content": "用户喜欢蓝莓芝士蛋糕",
          "workspace_id": "ws-memory-tools"
        }
        """);

        var search = await ExecuteJsonAsync(grep, """
        {
          "action": "search",
          "query": "莓芝",
          "mode": "fts5",
          "workspace_id": "ws-memory-tools"
        }
        """);

        var root = JsonDocument.Parse(search).RootElement;
        Assert.AreEqual("ok", root.GetProperty("status").GetString());
        Assert.AreEqual(1, root.GetProperty("count").GetInt32());
        Assert.AreEqual("like", root.GetProperty("results")[0].GetProperty("MatchSource").GetString());
        StringAssert.Contains(root.GetProperty("results")[0].GetProperty("Snippet").GetString(), "莓芝");
    }

    [TestMethod]
    public async Task SaveMemory_QualityFilter_EmptyContent_Warns()
    {
        await using var scope = await CreateScopeAsync();
        var qualityFilter = CreateQualityFilter();
        var save = new SaveMemoryTool(scope.Convenience, scope.Library, NullLogger<SaveMemoryTool>.Instance, qualityFilter: qualityFilter);

        var result = await ExecuteJsonAsync(save, """
        {
          "action": "upsert",
          "type": "fact",
          "book": "Quality Tests",
          "content": ""
        }
        """);

        var root = JsonDocument.Parse(result).RootElement;
        Assert.AreEqual("ok", root.GetProperty("status").GetString());
        Assert.IsTrue(root.TryGetProperty("quality", out var quality));
        Assert.AreEqual(JsonValueKind.Object, quality.ValueKind);
        var warnings = quality.GetProperty("warnings");
        Assert.IsTrue(warnings.GetArrayLength() > 0);
        Assert.AreEqual("empty_content", warnings[0].GetProperty("Rule").GetString());
        Assert.AreEqual("warn", warnings[0].GetProperty("Severity").GetString());
    }

    [TestMethod]
    public async Task SaveMemory_QualityFilter_TooShort_Warns()
    {
        await using var scope = await CreateScopeAsync();
        var qualityFilter = CreateQualityFilter();
        var save = new SaveMemoryTool(scope.Convenience, scope.Library, NullLogger<SaveMemoryTool>.Instance, qualityFilter: qualityFilter);

        var result = await ExecuteJsonAsync(save, """
        {
          "action": "upsert",
          "type": "fact",
          "book": "Quality Tests",
          "content": "ab"
        }
        """);

        var root = JsonDocument.Parse(result).RootElement;
        Assert.AreEqual("ok", root.GetProperty("status").GetString());
        Assert.IsTrue(root.TryGetProperty("quality", out var quality));
        Assert.AreEqual(JsonValueKind.Object, quality.ValueKind);
        var warnings = quality.GetProperty("warnings");
        Assert.IsTrue(warnings.GetArrayLength() > 0);
        Assert.AreEqual("too_short", warnings[0].GetProperty("Rule").GetString());
        Assert.AreEqual("info", warnings[0].GetProperty("Severity").GetString());
    }

    [TestMethod]
    public async Task SaveMemory_QualityFilter_DirtyWord_Filters()
    {
        await using var scope = await CreateScopeAsync();
        var qualityFilter = CreateQualityFilter();
        var save = new SaveMemoryTool(scope.Convenience, scope.Library, NullLogger<SaveMemoryTool>.Instance, qualityFilter: qualityFilter);

        var result = await ExecuteJsonAsync(save, """
        {
          "action": "upsert",
          "type": "fact",
          "book": "Quality Tests",
          "content": "this is a shit test"
        }
        """);

        var root = JsonDocument.Parse(result).RootElement;
        Assert.AreEqual("ok", root.GetProperty("status").GetString());
        Assert.IsTrue(root.TryGetProperty("quality", out var quality));
        Assert.AreEqual(JsonValueKind.Object, quality.ValueKind);
        var warnings = quality.GetProperty("warnings");
        Assert.IsTrue(warnings.GetArrayLength() > 0);
        Assert.AreEqual("dirty_word", warnings[0].GetProperty("Rule").GetString());
        Assert.AreEqual("warn", warnings[0].GetProperty("Severity").GetString());
        StringAssert.Contains(warnings[0].GetProperty("Message").GetString(), "shit");
    }

    [TestMethod]
    public async Task SaveMemory_QualityFilter_NormalContent_NoWarnings()
    {
        await using var scope = await CreateScopeAsync();
        var qualityFilter = CreateQualityFilter();
        var save = new SaveMemoryTool(scope.Convenience, scope.Library, NullLogger<SaveMemoryTool>.Instance, qualityFilter: qualityFilter);

        var result = await ExecuteJsonAsync(save, """
        {
          "action": "upsert",
          "type": "fact",
          "book": "Quality Tests",
          "content": "正常的中文记忆内容，长度足够"
        }
        """);

        var root = JsonDocument.Parse(result).RootElement;
        Assert.AreEqual("ok", root.GetProperty("status").GetString());
        Assert.IsFalse(root.TryGetProperty("quality", out var q) && q.ValueKind == JsonValueKind.Object);
    }

    [TestMethod]
    public async Task SaveMemory_QualityFilter_UnknownType_Warns()
    {
        await using var scope = await CreateScopeAsync();
        var qualityFilter = CreateQualityFilter();
        var save = new SaveMemoryTool(scope.Convenience, scope.Library, NullLogger<SaveMemoryTool>.Instance, qualityFilter: qualityFilter);

        var result = await ExecuteJsonAsync(save, """
        {
          "action": "upsert",
          "type": "invalid_type_xyz",
          "book": "Quality Tests",
          "content": "normal content here"
        }
        """);

        var root = JsonDocument.Parse(result).RootElement;
        Assert.AreEqual("ok", root.GetProperty("status").GetString());
        Assert.IsTrue(root.TryGetProperty("quality", out var quality));
        Assert.AreEqual(JsonValueKind.Object, quality.ValueKind);
        var warnings = quality.GetProperty("warnings");
        Assert.IsTrue(warnings.GetArrayLength() > 0);
        Assert.AreEqual("unknown_type", warnings[0].GetProperty("Rule").GetString());
        Assert.AreEqual("info", warnings[0].GetProperty("Severity").GetString());
    }

    private static async Task<string> ExecuteJsonAsync(IPuddingTool tool, string argumentsJson)
    {
        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = argumentsJson,
            Context = new ToolExecutionContext
            {
                WorkspaceId = "ws-memory-tools",
                SessionId = "session-memory-tools",
                AgentInstanceId = "agent-memory-tools",
            },
        });

        Assert.IsTrue(result.Success, result.Error);
        return result.Output;
    }

    private static ManageMemoryTool CreateManageTool(IMemoryLibrary library)
        => new(
            library,
            NullLogger<ManageMemoryTool>.Instance,
            new BookHandler(library, NullLogger<BookHandler>.Instance),
            new ChapterHandler(library, NullLogger<ChapterHandler>.Instance),
            new ReferenceHandler(library, NullLogger<ReferenceHandler>.Instance),
            new GraphHandler(library, NullLogger<GraphHandler>.Instance),
            new DedupHandler(library, NullLogger<DedupHandler>.Instance));

    private static MemoryQualityFilter CreateQualityFilter()
        => new(NullLogger<MemoryQualityFilter>.Instance);

    private static async Task<MemoryToolTestScope> CreateScopeAsync(IMemoryLlmClient? llmClient = null)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=ON;";
            await pragma.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<MemoryLibraryDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
        }
        await SetupFts5Async(connection);

        var library = new MemoryLibrary(factory);
        var convenience = new MemoryLibraryConvenience(library, llmClient);
        return new MemoryToolTestScope(connection, library, convenience);
    }

    private static async Task SetupFts5Async(SqliteConnection connection)
    {
        var statements = new[]
        {
            "CREATE VIRTUAL TABLE IF NOT EXISTS Books_fts USING fts5(Title, Summary, BookId UNINDEXED, content=Books, content_rowid=rowid)",
            "CREATE TRIGGER IF NOT EXISTS trg_Books_ai AFTER INSERT ON Books BEGIN INSERT INTO Books_fts(rowid, Title, Summary, BookId) VALUES (new.rowid, new.Title, new.Summary, new.BookId); END",
            "CREATE TRIGGER IF NOT EXISTS trg_Books_ad AFTER DELETE ON Books BEGIN INSERT INTO Books_fts(Books_fts, rowid, Title, Summary, BookId) VALUES ('delete', old.rowid, old.Title, old.Summary, old.BookId); END",
            "CREATE TRIGGER IF NOT EXISTS trg_Books_au AFTER UPDATE ON Books BEGIN INSERT INTO Books_fts(Books_fts, rowid, Title, Summary, BookId) VALUES ('delete', old.rowid, old.Title, old.Summary, old.BookId); INSERT INTO Books_fts(rowid, Title, Summary, BookId) VALUES (new.rowid, new.Title, new.Summary, new.BookId); END",
            "CREATE VIRTUAL TABLE IF NOT EXISTS Chapters_fts USING fts5(TitleTokens, ContentTokens, ChapterId UNINDEXED, content=Chapters, content_rowid=rowid)",
            "CREATE TRIGGER IF NOT EXISTS trg_Chapters_ai AFTER INSERT ON Chapters BEGIN INSERT INTO Chapters_fts(rowid, TitleTokens, ContentTokens, ChapterId) VALUES (new.rowid, new.TitleTokens, new.ContentTokens, new.ChapterId); END",
            "CREATE TRIGGER IF NOT EXISTS trg_Chapters_ad AFTER DELETE ON Chapters BEGIN INSERT INTO Chapters_fts(Chapters_fts, rowid, TitleTokens, ContentTokens, ChapterId) VALUES ('delete', old.rowid, old.TitleTokens, old.ContentTokens, old.ChapterId); END",
            "CREATE TRIGGER IF NOT EXISTS trg_Chapters_au AFTER UPDATE ON Chapters BEGIN INSERT INTO Chapters_fts(Chapters_fts, rowid, TitleTokens, ContentTokens, ChapterId) VALUES ('delete', old.rowid, old.TitleTokens, old.ContentTokens, old.ChapterId); INSERT INTO Chapters_fts(rowid, TitleTokens, ContentTokens, ChapterId) VALUES (new.rowid, new.TitleTokens, new.ContentTokens, new.ChapterId); END",
        };

        foreach (var sql in statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<MemoryLibraryDbContext>
    {
        private readonly DbContextOptions<MemoryLibraryDbContext> _options;

        public TestDbContextFactory(DbContextOptions<MemoryLibraryDbContext> options)
        {
            _options = options;
        }

        public MemoryLibraryDbContext CreateDbContext() => new(_options);

        public Task<MemoryLibraryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryLibraryDbContext(_options));
    }

    private sealed class ReuseFirstCandidateMemoryLlmClient : IMemoryLlmClient
    {
        public int ChatCalls { get; private set; }

        public Task<MemoryClassification> ClassifyAsync(string messageText, CancellationToken ct = default)
            => Task.FromResult(new MemoryClassification(false, 0.1, 0.1, null, null));

        public Task<string?> SummarizeAsync(IReadOnlyList<string> memoryContents, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<MemoryQueryIntent?> ParseIntentAsync(string userMessage, CancellationToken ct = default)
            => Task.FromResult<MemoryQueryIntent?>(null);

        public Task<string> ChatAsync(
            string systemPrompt,
            string userMessage,
            IReadOnlyList<object>? tools = null,
            CancellationToken ct = default)
        {
            ChatCalls++;
            var match = Regex.Match(userMessage, "\"chapterId\"\\s*:\\s*\"([^\"]+)\"");
            var chapterId = match.Success ? match.Groups[1].Value : "";
            return Task.FromResult($$"""
            {"action":"reuse_existing","chapterId":"{{chapterId}}","confidence":0.96,"reason":"same preference expressed differently"}
            """);
        }
    }

    private sealed class SupersedeFirstCandidateMemoryLlmClient : IMemoryLlmClient
    {
        public int ChatCalls { get; private set; }

        public Task<MemoryClassification> ClassifyAsync(string messageText, CancellationToken ct = default)
            => Task.FromResult(new MemoryClassification(false, 0.1, 0.1, null, null));

        public Task<string?> SummarizeAsync(IReadOnlyList<string> memoryContents, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<MemoryQueryIntent?> ParseIntentAsync(string userMessage, CancellationToken ct = default)
            => Task.FromResult<MemoryQueryIntent?>(null);

        public Task<string> ChatAsync(
            string systemPrompt,
            string userMessage,
            IReadOnlyList<object>? tools = null,
            CancellationToken ct = default)
        {
            ChatCalls++;
            var match = Regex.Match(userMessage, "\"chapterId\"\\s*:\\s*\"([^\"]+)\"");
            var chapterId = match.Success ? match.Groups[1].Value : "";
            return Task.FromResult($$"""
            {"action":"supersede_existing","chapterId":"{{chapterId}}","confidence":0.98,"reason":"new preference explicitly replaces old preference"}
            """);
        }
    }

    private sealed class MemoryToolTestScope : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public MemoryToolTestScope(
            SqliteConnection connection,
            IMemoryLibrary library,
            IMemoryLibraryConvenience convenience)
        {
            _connection = connection;
            Library = library;
            Convenience = convenience;
        }

        public IMemoryLibrary Library { get; }
        public IMemoryLibraryConvenience Convenience { get; }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
        }
    }
}
