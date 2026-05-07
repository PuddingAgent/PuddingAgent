using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;

namespace PuddingMemoryEngineTests;

[TestClass]
public sealed class MemoryLibraryTests
{
    // ── Library CRUD ───────────────────────────────────────────────────

    [TestMethod]
    public async Task CreateLibrary_ShouldSucceed()
    {
        await using var scope = await CreateLibraryScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "测试图书馆", "用于单元测试", CancellationToken.None);
        Assert.IsNotNull(lib);
        Assert.AreEqual("测试图书馆", lib.Name);
        Assert.AreEqual("ws-1", lib.WorkspaceId);
        Assert.IsTrue(lib.LibraryId.Length == 32);
    }

    [TestMethod]
    public async Task ListLibraries_ShouldReturnByWorkspace()
    {
        await using var scope = await CreateLibraryScopeAsync();
        await scope.Library.CreateLibraryAsync("ws-a", "LibA", null);
        await scope.Library.CreateLibraryAsync("ws-a", "LibB", null);
        await scope.Library.CreateLibraryAsync("ws-b", "LibC", null);

        var listA = await scope.Library.ListLibrariesAsync("ws-a");
        Assert.HasCount(2, listA);
        Assert.IsTrue(listA.All(l => l.WorkspaceId == "ws-a"));

        var listB = await scope.Library.ListLibrariesAsync("ws-b");
        Assert.HasCount(1, listB);
    }

    // ── Book CRUD ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task CreateBook_ShouldSucceed()
    {
        await using var scope = await CreateLibraryScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "Lib", null);
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "MySQL调优指南", "总结MySQL 8.0调优经验");
        Assert.IsNotNull(book);
        Assert.AreEqual("MySQL调优指南", book.Title);
        Assert.AreEqual("active", book.Status);
        Assert.AreEqual(1, book.Version);
    }

    [TestMethod]
    public async Task CreateBook_WithTagPaths_ShouldCreateIndexes()
    {
        await using var scope = await CreateLibraryScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "Lib", null);
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "Go并发模式", "总结Go并发编程", ["技术/Go", "技术/并发"]);

        await using var db = await scope.Factory.CreateDbContextAsync();
        var indexes = await db.BookIndexes.Where(i => i.BookId == book.BookId).ToListAsync();
        Assert.HasCount(2, indexes);
        Assert.IsTrue(indexes.Any(i => i.TagPath == "技术/Go"));
        Assert.IsTrue(indexes.Any(i => i.TagPath == "技术/并发"));
    }

    [TestMethod]
    public async Task GetBook_ShouldIncrementAccessCount()
    {
        await using var scope = await CreateLibraryScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "Lib", null);
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "Title", "Summary");

        // GetBookAsync 内部更新 AccessCount，但返回的 record 来自 AsNoTracking 查询
        // 验证数据库中的值确实递增
        await scope.Library.GetBookAsync(book.BookId);

        await using var db = await scope.Factory.CreateDbContextAsync();
        var entity = await db.Books.FirstAsync(b => b.BookId == book.BookId);
        Assert.AreEqual(1, entity.AccessCount);
        Assert.IsNotNull(entity.LastAccessedAt);

        await scope.Library.GetBookAsync(book.BookId);
        await db.Entry(entity).ReloadAsync();
        Assert.AreEqual(2, entity.AccessCount);
    }

    [TestMethod]
    public async Task GetBookReadOnly_ShouldNotIncrementAccessCount()
    {
        await using var scope = await CreateLibraryScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "Lib", null);
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "Title", "Summary");

        var read1 = await scope.Library.GetBookReadOnlyAsync(book.BookId);
        Assert.AreEqual(0, read1!.AccessCount);
        Assert.IsNull(read1.LastAccessedAt);

        var read2 = await scope.Library.GetBookReadOnlyAsync(book.BookId);
        Assert.AreEqual(0, read2!.AccessCount);
    }

    [TestMethod]
    public async Task ListBooks_ShouldReturnByLibrary()
    {
        await using var scope = await CreateLibraryScopeAsync();
        var libA = await scope.Library.CreateLibraryAsync("ws-1", "LibA", null);
        var libB = await scope.Library.CreateLibraryAsync("ws-1", "LibB", null);
        await scope.Library.CreateBookAsync(libA.LibraryId, "Book1", "S1");
        await scope.Library.CreateBookAsync(libA.LibraryId, "Book2", "S2");
        await scope.Library.CreateBookAsync(libB.LibraryId, "Book3", "S3");

        var listA = await scope.Library.ListBooksAsync(libA.LibraryId);
        Assert.HasCount(2, listA);

        var listB = await scope.Library.ListBooksAsync(libB.LibraryId);
        Assert.HasCount(1, listB);
    }

    [TestMethod]
    public async Task ArchiveBook_ShouldChangeStatus()
    {
        await using var scope = await CreateLibraryScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "Lib", null);
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "Title", "Summary");

        var result = await scope.Library.ArchiveBookAsync(book.BookId);
        Assert.IsTrue(result);

        var archived = await scope.Library.GetBookReadOnlyAsync(book.BookId);
        Assert.AreEqual("archived", archived!.Status);
    }

    [TestMethod]
    public async Task UpdateBook_ShouldApplyChanges()
    {
        await using var scope = await CreateLibraryScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "Lib", null);
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "Old Title", "Old Summary");

        var updated = await scope.Library.UpdateBookAsync(book.BookId, r => r with
        {
            Title = "New Title",
            Summary = "New Summary"
        });

        Assert.AreEqual("New Title", updated.Title);
        Assert.AreEqual("New Summary", updated.Summary);
        Assert.AreEqual(2, updated.Version);
    }

    // ── Chapter CRUD ───────────────────────────────────────────────────

    [TestMethod]
    public async Task AddChapter_ShouldSucceed()
    {
        await using var scope = await CreateLibraryScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "Lib", null);
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "Book", "Summary");

        var chapter = await scope.Library.AddChapterAsync(book.BookId, "第一章", "# Hello\nWorld", 0, "session-1");
        Assert.IsNotNull(chapter);
        Assert.AreEqual("第一章", chapter.Title);
        Assert.AreEqual("# Hello\nWorld", chapter.Content);
        Assert.AreEqual("# Hello\nWorld".Length, chapter.WordCount);
        Assert.AreEqual("session-1", chapter.SourceSessionId);
    }

    [TestMethod]
    public async Task ListChapters_ShouldReturnByBook()
    {
        await using var scope = await CreateLibraryScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "Lib", null);
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "Book", "Summary");
        await scope.Library.AddChapterAsync(book.BookId, "Ch1", "c1", 0);
        await scope.Library.AddChapterAsync(book.BookId, "Ch2", "c2", 1);
        await scope.Library.AddChapterAsync(book.BookId, "Ch3", "c3", 2);

        var chapters = await scope.Library.ListChaptersAsync(book.BookId);
        Assert.HasCount(3, chapters);
        Assert.AreEqual(0, chapters[0].ChapterOrder);
        Assert.AreEqual(2, chapters[2].ChapterOrder);
    }

    [TestMethod]
    public async Task UpdateChapterContent_ShouldUpdate()
    {
        await using var scope = await CreateLibraryScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "Lib", null);
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "Book", "Summary");
        var chapter = await scope.Library.AddChapterAsync(book.BookId, "Ch", "old", 0);

        var updated = await scope.Library.UpdateChapterContentAsync(chapter.ChapterId, "new content here");
        Assert.AreEqual("new content here", updated.Content);
        Assert.AreEqual("new content here".Length, updated.WordCount);
    }

    // ── Pointer ────────────────────────────────────────────────────────

    [TestMethod]
    public async Task CreatePointer_ShouldSucceed()
    {
        await using var scope = await CreateLibraryScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "Lib", null);
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "Book", "Summary");
        var chapter = await scope.Library.AddChapterAsync(book.BookId, "Ch", "content", 0);

        var pointer = await scope.Library.CreatePointerAsync(chapter.ChapterId, "Book", book.BookId, "参考", "引用书籍");
        Assert.IsNotNull(pointer);
        Assert.AreEqual("Book", pointer.TargetType);
        Assert.AreEqual(book.BookId, pointer.TargetId);
    }

    [TestMethod]
    public async Task GetPointers_ShouldReturnByChapter()
    {
        await using var scope = await CreateLibraryScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "Lib", null);
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "Book", "Summary");
        var ch1 = await scope.Library.AddChapterAsync(book.BookId, "Ch1", "c1", 0);
        var ch2 = await scope.Library.AddChapterAsync(book.BookId, "Ch2", "c2", 1);

        await scope.Library.CreatePointerAsync(ch1.ChapterId, "Chapter", ch2.ChapterId, "see-also");
        await scope.Library.CreatePointerAsync(ch1.ChapterId, "URL", "https://example.com", "ref");

        var pointers = await scope.Library.GetPointersAsync(ch1.ChapterId);
        Assert.HasCount(2, pointers);
    }

    [TestMethod]
    public async Task ResolveBacklinks_ShouldFindReferences()
    {
        await using var scope = await CreateLibraryScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "Lib", null);
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "Book", "Summary");
        var ch1 = await scope.Library.AddChapterAsync(book.BookId, "Ch1", "c1", 0);
        var ch2 = await scope.Library.AddChapterAsync(book.BookId, "Ch2", "c2", 1);

        await scope.Library.CreatePointerAsync(ch1.ChapterId, "Chapter", ch2.ChapterId, "ref-to-ch2");
        await scope.Library.CreatePointerAsync(ch2.ChapterId, "Chapter", ch1.ChapterId, "ref-to-ch1");

        var backlinks = await scope.Library.ResolveBacklinksAsync("Chapter", ch1.ChapterId);
        Assert.HasCount(1, backlinks);
        Assert.AreEqual(ch2.ChapterId, backlinks[0].ChapterId);
    }

    // ── Search ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SearchBooksFts_ShouldFindByTitle()
    {
        await using var scope = await CreateLibraryScopeAsync(enableFts5: true);
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "Lib", null);
        await scope.Library.CreateBookAsync(lib.LibraryId, "MySQL Performance Tuning Guide", "MySQL tuning summary");
        await scope.Library.CreateBookAsync(lib.LibraryId, "PostgreSQL Getting Started", "PG basics");
        await scope.Library.CreateBookAsync(lib.LibraryId, "Redis Cache Design", "Redis in action");

        var results = await scope.Library.SearchBooksFtsAsync("MySQL", topK: 10);
        Assert.IsTrue(results.Count >= 1);
        Assert.IsTrue(results.Any(r => r.Title.Contains("MySQL")));
    }

    [TestMethod]
    public async Task SearchChaptersFts_ShouldFindByContent()
    {
        await using var scope = await CreateLibraryScopeAsync(enableFts5: true);
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "Lib", null);
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "DevOps Guide", "Summary");
        await scope.Library.AddChapterAsync(book.BookId, "Ch1", "Best practices for Kubernetes deployment", 0);
        await scope.Library.AddChapterAsync(book.BookId, "Ch2", "Docker basics introduction guide", 1);

        var results = await scope.Library.SearchChaptersFtsAsync("Kubernetes", topK: 10);
        Assert.IsTrue(results.Count >= 1);
        Assert.IsTrue(results.Any(r => r.Content.Contains("Kubernetes")));
    }

    [TestMethod]
    public async Task SearchBooksByTag_ShouldFindByPrefix()
    {
        await using var scope = await CreateLibraryScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "Lib", null);
        await scope.Library.CreateBookAsync(lib.LibraryId, "Go Book", "summary", ["技术/Go/并发"]);
        await scope.Library.CreateBookAsync(lib.LibraryId, "Rust Book", "summary", ["技术/Rust"]);
        await scope.Library.CreateBookAsync(lib.LibraryId, "Go Web", "summary", ["技术/Go/Web"]);

        var results = await scope.Library.SearchBooksByTagAsync("技术/Go", topK: 10);
        Assert.IsTrue(results.Count >= 2);
        Assert.IsTrue(results.All(r => r.Status == "active"));
    }

    // ── Branch ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task BranchBook_ShouldCreateBranch()
    {
        await using var scope = await CreateLibraryScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "Lib", null);
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "Book", "Summary", ["技术/AI"]);

        var branch = await scope.Library.BranchBookAsync(book.BookId, "experiment-v2", "实验性改动");
        Assert.IsNotNull(branch);
        Assert.AreEqual("experiment-v2", branch.BranchName);
        Assert.AreEqual(book.BookId, branch.BookId);
        Assert.IsFalse(branch.IsDefault);
    }

    [TestMethod]
    public async Task ListBranches_ShouldReturnByBook()
    {
        await using var scope = await CreateLibraryScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "Lib", null);
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "Book", "Summary");

        await scope.Library.BranchBookAsync(book.BookId, "b1", null);
        await scope.Library.BranchBookAsync(book.BookId, "b2", null);

        var branches = await scope.Library.ListBranchesAsync(book.BookId);
        Assert.HasCount(2, branches);
    }

    [TestMethod]
    public async Task MergeBranch_ShouldMarkMergedInto()
    {
        await using var scope = await CreateLibraryScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "Lib", null);
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "Book", "Summary");

        var source = await scope.Library.BranchBookAsync(book.BookId, "source", null);
        var target = await scope.Library.BranchBookAsync(book.BookId, "target", null);

        var merged = await scope.Library.MergeBranchAsync(source.BranchId, target.BranchId);
        Assert.IsTrue(merged);

        var branches = await scope.Library.ListBranchesAsync(book.BookId);
        var mergedSource = branches.First(b => b.BranchId == source.BranchId);
        Assert.AreEqual(target.BranchId, mergedSource.MergedInto);
    }

    // ── Convenience ────────────────────────────────────────────────────

    [TestMethod]
    public async Task UpsertExperience_ShouldCreateBookAndChapter()
    {
        await using var scope = await CreateLibraryScopeAsync(enableFts5: true);
        var experience = new ExperiencePackage
        {
            Title = "Redis Cluster Setup Guide",
            Content = "## Steps\n1. Install Redis\n2. Configure cluster\n3. Test connectivity",
            SuggestedTags = ["tech/Redis"],
            SourceSessionId = "session-redis-01"
        };

        var result = await scope.Convenience.UpsertExperienceAsync("ws-1", experience);
        Assert.IsNotNull(result);
        Assert.IsNotNull(result.Book);
        Assert.IsNotNull(result.Chapter);
        Assert.IsTrue(result.Book.Title.Contains("Redis"));
    }

    [TestMethod]
    public async Task SmartSearch_ShouldFindByKeyword()
    {
        await using var scope = await CreateLibraryScopeAsync(enableFts5: true);
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "Lib", null);
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "Distributed Transactions Comparison", "Distributed transaction patterns", ["tech/distributed"]);
        await scope.Library.AddChapterAsync(book.BookId, "Two Phase Commit", "Two Phase Commit (2PC) is a classic distributed transaction protocol...", 0);

        var results = await scope.Convenience.SmartSearchAsync("distributed transaction", topK: 10);
        Assert.IsTrue(results.Count >= 1);
    }

    [TestMethod]
    public async Task GetOrCreateBook_ShouldDeduplicate()
    {
        await using var scope = await CreateLibraryScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "Lib", null);

        var book1 = await scope.Convenience.GetOrCreateBookAsync(lib.LibraryId, "UniqueBook", "summary", null, CancellationToken.None);
        var book2 = await scope.Convenience.GetOrCreateBookAsync(lib.LibraryId, "UniqueBook", "other summary", null, CancellationToken.None);

        Assert.AreEqual(book1.BookId, book2.BookId);
    }

    [TestMethod]
    public async Task AppendChapter_ShouldAutoOrder()
    {
        await using var scope = await CreateLibraryScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "Lib", null);
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "Book", "Summary");

        var ch1 = await scope.Convenience.AppendChapterAsync(book.BookId, "First", "content1");
        Assert.AreEqual(0, ch1.ChapterOrder);

        var ch2 = await scope.Convenience.AppendChapterAsync(book.BookId, "Second", "content2");
        Assert.AreEqual(1, ch2.ChapterOrder);

        var ch3 = await scope.Convenience.AppendChapterAsync(book.BookId, "Third", "content3");
        Assert.AreEqual(2, ch3.ChapterOrder);
    }

    // ── Phase 4: 向量检索 ─────────────────────────────────────────────

    [TestMethod]
    public async Task SearchByVector_ShouldFindSemanticMatch()
    {
        await using var scope = await CreateLibraryScopeAsync(enableFts5: true);
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "Lib", null);
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "AI Book", "AI topics");

        // 创建两个有 Embedding 的 Chapter
        var ch1 = await scope.Library.AddChapterAsync(book.BookId, "Python ML", "Machine learning with Python", 0);
        var ch2 = await scope.Library.AddChapterAsync(book.BookId, "Cooking", "How to cook pasta", 1);

        // 设置模拟嵌入向量（2维简化，实际为1536维）
        var mlEmbedding = VectorSimilarity.FloatsToBytes(new float[] { 1.0f, 0.0f });
        var cookingEmbedding = VectorSimilarity.FloatsToBytes(new float[] { 0.0f, 1.0f });
        await scope.Library.UpdateChapterEmbeddingAsync(ch1.ChapterId, mlEmbedding);
        await scope.Library.UpdateChapterEmbeddingAsync(ch2.ChapterId, cookingEmbedding);

        // 搜索与 ML 相关的向量
        var results = await scope.Library.SearchChaptersByVectorAsync(
            new float[] { 0.9f, 0.1f }, topK: 10);
        Assert.IsTrue(results.Count >= 1);
        Assert.AreEqual("Python ML", results[0].ChapterTitle);
        Assert.AreEqual("vector", results[0].MatchSource);
    }

    [TestMethod]
    public async Task HybridSearch_ShouldFuseMultipleSources()
    {
        await using var scope = await CreateLibraryScopeAsync(enableFts5: true);
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "Lib", null);

        // Book with Tag (TagTree 路)
        var book = await scope.Library.CreateBookAsync(
            lib.LibraryId, "Distributed Systems", "Distributed computing patterns",
            ["tech/distributed"]);
        await scope.Library.AddChapterAsync(
            book.BookId, "Consensus Protocols", "Raft and Paxos consensus algorithms explained", 0);

        // 另一个 Book (FTS5 路)
        var book2 = await scope.Library.CreateBookAsync(
            lib.LibraryId, "Database Internals", "Database internals and distributed transactions");
        var ch2 = await scope.Library.AddChapterAsync(
            book2.BookId, "Distributed Transactions", "Two phase commit and distributed consensus", 0);

        // 设置向量（模拟与 "distributed consensus" 语义相近）
        var vecEmbedding = VectorSimilarity.FloatsToBytes(new float[] { 1.0f, 0.5f });
        await scope.Library.UpdateChapterEmbeddingAsync(ch2.ChapterId, vecEmbedding);

        var results = await scope.Library.HybridSearchAsync(
            "distributed consensus",
            new float[] { 1.0f, 0.5f }, topK: 10);
        Assert.IsTrue(results.Count >= 1);
        // 多路融合的 MatchSource 应包含 +
        Assert.IsTrue(results.Any(r => r.MatchSource.Contains("+") || r.MatchSource.Contains("fts5")));
    }

    [TestMethod]
    public async Task HybridSearch_EmptyQuery_ShouldReturnEmpty()
    {
        await using var scope = await CreateLibraryScopeAsync(enableFts5: true);
        var results = await scope.Library.HybridSearchAsync("", null, topK: 10);
        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task SearchByVector_NullEmbedding_ShouldSkip()
    {
        await using var scope = await CreateLibraryScopeAsync(enableFts5: true);
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "Lib", null);
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "Book", "Summary");
        // Chapter 没有 Embedding
        await scope.Library.AddChapterAsync(book.BookId, "No Embedding", "Content without vector", 0);

        var results = await scope.Library.SearchChaptersByVectorAsync(
            new float[] { 1.0f, 0.0f }, topK: 10);
        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task ChapterEmbedding_ShouldPersistAndRetrieve()
    {
        await using var scope = await CreateLibraryScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-1", "Lib", null);
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "Book", "Summary");
        var ch = await scope.Library.AddChapterAsync(book.BookId, "Ch", "Content", 0);

        var embedding = new float[] { 0.1f, 0.2f, 0.3f };
        var bytes = VectorSimilarity.FloatsToBytes(embedding);

        var updated = await scope.Library.UpdateChapterEmbeddingAsync(ch.ChapterId, bytes);
        Assert.IsTrue(updated);

        // 通过向量搜索验证持久化
        var results = await scope.Library.SearchChaptersByVectorAsync(
            new float[] { 0.1f, 0.2f, 0.3f }, topK: 10);
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(ch.ChapterId, results[0].ChapterId);
    }

    [TestMethod]
    public async Task VectorSimilarity_ShouldReturnOneForSame()
    {
        var vec = new float[] { 1.0f, 2.0f, 3.0f };
        var sim = VectorSimilarity.CosineSimilarity(vec, vec);
        Assert.AreEqual(1.0f, sim, 0.0001f);
    }

    [TestMethod]
    public async Task VectorSimilarity_ShouldReturnZeroForOrthogonal()
    {
        var a = new float[] { 1.0f, 0.0f };
        var b = new float[] { 0.0f, 1.0f };
        var sim = VectorSimilarity.CosineSimilarity(a, b);
        Assert.AreEqual(0.0f, sim, 0.0001f);
    }

    [TestMethod]
    public async Task BytesToFloats_RoundTrip_ShouldPreserve()
    {
        var original = new float[] { 0.1f, -0.5f, 3.14f, 0.0f };
        var bytes = VectorSimilarity.FloatsToBytes(original);
        var restored = VectorSimilarity.BytesToFloats(bytes);
        Assert.AreEqual(original.Length, restored.Length);
        for (int i = 0; i < original.Length; i++)
            Assert.AreEqual(original[i], restored[i], 0.0001f);
    }

    // ── Test Infrastructure ────────────────────────────────────────────

    /// <summary>
    /// 创建测试 Scope——使用 SQLite InMemory 数据库。
    /// enableFts5=true 时额外创建 FTS5 虚拟表和触发器以支持全文搜索测试。
    /// </summary>
    private static async Task<LibraryTestScope> CreateLibraryScopeAsync(bool enableFts5 = false)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        // 启用外键约束
        using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA foreign_keys=ON;";
            await pragmaCmd.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<MemoryLibraryDbContext>()
            .UseSqlite(connection)
            .EnableSensitiveDataLogging()
            .Options;

        var factory = new LibraryTestDbContextFactory(options);

        // EnsureCreated 通过 EF 模型创建基础表
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
        }

        // 按需创建 FTS5 虚拟表与触发器（支持 SearchBooksFts / SearchChaptersFts）
        if (enableFts5)
        {
            await SetupFts5Async(connection);
        }

        var library = new MemoryLibrary(factory);
        var convenience = new MemoryLibraryConvenience(library);

        return new LibraryTestScope(connection, factory, library, convenience);
    }

    /// <summary>创建 FTS5 虚拟表及对应 INSERT/UPDATE/DELETE 触发器（external content 模式，与 init_library.sql 一致）。</summary>
    private static async Task SetupFts5Async(SqliteConnection connection)
    {
        var statements = new[]
        {
            "CREATE VIRTUAL TABLE IF NOT EXISTS Books_fts USING fts5(Title, Summary, BookId UNINDEXED, content=Books, content_rowid=rowid)",
            "CREATE TRIGGER IF NOT EXISTS trg_Books_ai AFTER INSERT ON Books BEGIN INSERT INTO Books_fts(rowid, Title, Summary, BookId) VALUES (new.rowid, new.Title, new.Summary, new.BookId); END",
            "CREATE TRIGGER IF NOT EXISTS trg_Books_ad AFTER DELETE ON Books BEGIN INSERT INTO Books_fts(Books_fts, rowid, Title, Summary, BookId) VALUES ('delete', old.rowid, old.Title, old.Summary, old.BookId); END",
            "CREATE TRIGGER IF NOT EXISTS trg_Books_au AFTER UPDATE ON Books BEGIN INSERT INTO Books_fts(Books_fts, rowid, Title, Summary, BookId) VALUES ('delete', old.rowid, old.Title, old.Summary, old.BookId); INSERT INTO Books_fts(rowid, Title, Summary, BookId) VALUES (new.rowid, new.Title, new.Summary, new.BookId); END",
            "CREATE VIRTUAL TABLE IF NOT EXISTS Chapters_fts USING fts5(Title, Content, ChapterId UNINDEXED, content=Chapters, content_rowid=rowid)",
            "CREATE TRIGGER IF NOT EXISTS trg_Chapters_ai AFTER INSERT ON Chapters BEGIN INSERT INTO Chapters_fts(rowid, Title, Content, ChapterId) VALUES (new.rowid, new.Title, new.Content, new.ChapterId); END",
            "CREATE TRIGGER IF NOT EXISTS trg_Chapters_ad AFTER DELETE ON Chapters BEGIN INSERT INTO Chapters_fts(Chapters_fts, rowid, Title, Content, ChapterId) VALUES ('delete', old.rowid, old.Title, old.Content, old.ChapterId); END",
            "CREATE TRIGGER IF NOT EXISTS trg_Chapters_au AFTER UPDATE ON Chapters BEGIN INSERT INTO Chapters_fts(Chapters_fts, rowid, Title, Content, ChapterId) VALUES ('delete', old.rowid, old.Title, old.Content, old.ChapterId); INSERT INTO Chapters_fts(rowid, Title, Content, ChapterId) VALUES (new.rowid, new.Title, new.Content, new.ChapterId); END",
        };

        foreach (var sql in statements)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private sealed class LibraryTestScope : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public IDbContextFactory<MemoryLibraryDbContext> Factory { get; }
        public IMemoryLibrary Library { get; }
        public IMemoryLibraryConvenience Convenience { get; }

        public LibraryTestScope(
            SqliteConnection connection,
            IDbContextFactory<MemoryLibraryDbContext> factory,
            IMemoryLibrary library,
            IMemoryLibraryConvenience convenience)
        {
            _connection = connection;
            Factory = factory;
            Library = library;
            Convenience = convenience;
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
        }
    }

    private sealed class LibraryTestDbContextFactory : IDbContextFactory<MemoryLibraryDbContext>
    {
        private readonly DbContextOptions<MemoryLibraryDbContext> _options;

        public LibraryTestDbContextFactory(DbContextOptions<MemoryLibraryDbContext> options)
        {
            _options = options;
        }

        public MemoryLibraryDbContext CreateDbContext() => new(_options);

        public Task<MemoryLibraryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryLibraryDbContext(_options));
    }
}
