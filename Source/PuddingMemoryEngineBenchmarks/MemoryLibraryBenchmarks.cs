using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Data;

namespace PuddingMemoryEngineBenchmarks;

public class Program
{
    public static void Main(string[] args)
    {
        var config = ManualConfig.Create(DefaultConfig.Instance)
            .WithOptions(ConfigOptions.DisableOptimizationsValidator)
            .AddJob(Job.ShortRun
                .WithToolchain(InProcessNoEmitToolchain.Instance)
                .WithIterationCount(5)
                .WithWarmupCount(3));

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
            .Run(args, config);
    }
}

[MemoryDiagnoser]
public class MemoryLibraryBenchmarks
{
    private IDbContextFactory<MemoryLibraryDbContext> _dbFactory = null!;
    private MemoryLibrary _library = null!;
    private MemoryLibraryConvenience _convenience = null!;
    private string _libraryId = null!;
    private string _bookId = null!;
    private string _chapterId = null!;

    [GlobalSetup]
    public void Setup()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bench_memlib_{Guid.NewGuid().ToString("N")}.db");
        var options = new DbContextOptionsBuilder<MemoryLibraryDbContext>()
            .UseSqlite($"DataSource={dbPath}")
            .Options;
        _dbFactory = new SingletonDbContextFactory(options);

        // 初始化 Schema——用 MemoryLibraryDbInitializer 一样的模式
        using var db = _dbFactory.CreateDbContext();
        db.Database.EnsureCreated();

        // 原生 FTS5（逐条执行，避免多语句失败）
        var conn = db.Database.GetDbConnection();
        conn.Open();
        var fts5Statements = new[]
        {
            "CREATE VIRTUAL TABLE IF NOT EXISTS Books_fts USING fts5(Title, Summary, BookId UNINDEXED, content=Books, content_rowid=rowid)",
            "CREATE VIRTUAL TABLE IF NOT EXISTS Chapters_fts USING fts5(TitleTokens, ContentTokens, ChapterId UNINDEXED, content=Chapters, content_rowid=rowid)",
            "CREATE TRIGGER IF NOT EXISTS trg_Books_ai AFTER INSERT ON Books BEGIN INSERT INTO Books_fts(rowid, Title, Summary, BookId) VALUES (new.rowid, new.Title, new.Summary, new.BookId); END",
            "CREATE TRIGGER IF NOT EXISTS trg_Chapters_ai AFTER INSERT ON Chapters BEGIN INSERT INTO Chapters_fts(rowid, TitleTokens, ContentTokens, ChapterId) VALUES (new.rowid, new.TitleTokens, new.ContentTokens, new.ChapterId); END",
        };
        foreach (var sql in fts5Statements)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        _library = new MemoryLibrary(_dbFactory);
        _convenience = new MemoryLibraryConvenience(_library);

        // Global Setup: 创建 Library + Book + Chapter 用于后续测试
        var lib = _library.CreateLibraryAsync("bench-ws", "基准图书馆", null).GetAwaiter().GetResult();
        _libraryId = lib.LibraryId;

        var book = _library.CreateBookAsync(
            _libraryId, "MySQL 性能优化指南",
            "一本关于 MySQL 索引优化、查询调优和主从复制的综合指南",
            ["技术/数据库/MySQL", "运维/性能"], CancellationToken.None).GetAwaiter().GetResult();
        _bookId = book.BookId;

        var chapter = _library.AddChapterAsync(
            _bookId, "索引优化实践",
            "## 索引优化\n\nB+Tree 索引是最常用的索引类型。以下要点：\n1. 避免在 WHERE 子句中对列使用函数\n2. 复合索引遵循最左前缀原则\n3. 使用 EXPLAIN 分析查询计划\n\n### 示例\n```sql\nCREATE INDEX idx_name_age ON users(name, age);\n```",
            0, "session-001").GetAwaiter().GetResult();
        _chapterId = chapter.ChapterId;

        // Phase 4: 设置模拟 Embedding（1536 维随机向量）
        var rng = new Random(42);
        var embedding = new float[1536];
        for (int i = 0; i < 1536; i++)
            embedding[i] = (float)rng.NextDouble();
        _library.UpdateChapterEmbeddingAsync(
            _chapterId, VectorSimilarity.FloatsToBytes(embedding)).GetAwaiter().GetResult();
    }

    // ── Book CRUD ──────────────────────────────────────────────────────────

    [Benchmark]
    public BookRecord CreateBook()
    {
        return _library.CreateBookAsync(
            _libraryId, $"Bench Book {Guid.NewGuid().ToString("N")[..8]}", "Benchmark summary",
            null, CancellationToken.None).GetAwaiter().GetResult();
    }

    [Benchmark]
    public BookRecord? GetBook()
    {
        return _library.GetBookAsync(_bookId, CancellationToken.None).GetAwaiter().GetResult();
    }

    [Benchmark]
    public IReadOnlyList<BookRecord> ListBooks()
    {
        return _library.ListBooksAsync(_libraryId, 50, CancellationToken.None).GetAwaiter().GetResult();
    }

    // ── Chapter CRUD ───────────────────────────────────────────────────────

    [Benchmark]
    public ChapterRecord AddChapter()
    {
        return _library.AddChapterAsync(
            _bookId, $"Bench Chapter {Guid.NewGuid().ToString("N")[..8]}",
            "## Test Content\n\nThis is a benchmark chapter content.",
            999, null, null, CancellationToken.None).GetAwaiter().GetResult();
    }

    [Benchmark]
    public IReadOnlyList<ChapterRecord> ListChapters()
    {
        return _library.ListChaptersAsync(_bookId, CancellationToken.None).GetAwaiter().GetResult();
    }

    // ── Pointer ────────────────────────────────────────────────────────────

    [Benchmark]
    public PointerRecord CreatePointer()
    {
        return _library.CreatePointerAsync(
            _chapterId, "url", "https://dev.mysql.com/doc/",
            "MySQL 官方文档", "官方参考", CancellationToken.None).GetAwaiter().GetResult();
    }

    [Benchmark]
    public IReadOnlyList<PointerRecord> GetPointers()
    {
        return _library.GetPointersAsync(_chapterId, CancellationToken.None).GetAwaiter().GetResult();
    }

    // ── Search ─────────────────────────────────────────────────────────────

    [Benchmark]
    public IReadOnlyList<BookRecord> SearchBooksFts()
    {
        return _library.SearchBooksFtsAsync("MySQL 索引优化", 20, CancellationToken.None).GetAwaiter().GetResult();
    }

    [Benchmark]
    public IReadOnlyList<ChapterRecord> SearchChaptersFts()
    {
        return _library.SearchChaptersFtsAsync("B+Tree 索引优化 EXPLAIN", 20, CancellationToken.None).GetAwaiter().GetResult();
    }

    // ── Convenience ────────────────────────────────────────────────────────

    [Benchmark]
    public ExperienceWriteResult UpsertExperience()
    {
        return _convenience.UpsertExperienceAsync(
            "bench-ws", new ExperiencePackage
            {
                Title = $"MySQL 慢查询优化经验 {Guid.NewGuid().ToString("N")[..8]}",
                Content = "## 问题\n查询耗时超过 5 秒\n## 解决\n添加复合索引 (user_id, created_at)\n## 结论\nEXPLAIN 是排查慢查询的第一工具",
                SuggestedTags = ["技术/数据库/MySQL/调优"],
                Importance = 0.8,
            }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [Benchmark]
    public IReadOnlyList<RankedResult> SmartSearch()
    {
        return _convenience.SmartSearchAsync("MySQL 索引优化实践", 20, CancellationToken.None).GetAwaiter().GetResult();
    }

    [Benchmark]
    public IReadOnlyList<PointerRecord> AutoDiscoverPointers()
    {
        return _convenience.AutoDiscoverPointersAsync(_chapterId, CancellationToken.None).GetAwaiter().GetResult();
    }

    // ── Phase 4: 向量检索 ─────────────────────────────────────────────────

    [Benchmark]
    public IReadOnlyList<RankedResult> HybridSearch()
    {
        // 模拟一个简单的查询向量
        var queryEmbedding = new float[1536];
        queryEmbedding[0] = 1.0f;
        return _library.HybridSearchAsync(
            "MySQL 索引优化", queryEmbedding, 20, CancellationToken.None).GetAwaiter().GetResult();
    }

    [Benchmark]
    public IReadOnlyList<RankedResult> SearchByVector()
    {
        var queryEmbedding = new float[1536];
        queryEmbedding[0] = 1.0f;
        return _library.SearchChaptersByVectorAsync(
            queryEmbedding, 20, CancellationToken.None).GetAwaiter().GetResult();
    }

    [Benchmark]
    public float CosineSimilarity()
    {
        var a = new float[1536];
        var b = new float[1536];
        for (int i = 0; i < 1536; i++)
        {
            a[i] = (float)i / 1536;
            b[i] = (float)(1536 - i) / 1536;
        }
        return (float)VectorSimilarity.CosineSimilarity(a, b);
    }

    [Benchmark]
    public byte[] BytesToFloatsRoundTrip()
    {
        var floats = new float[1536];
        for (int i = 0; i < 1536; i++)
            floats[i] = (float)i / 1536;
        var bytes = VectorSimilarity.FloatsToBytes(floats);
        return bytes; // 只测序列化
    }
}

/// <summary>单例 DbContextFactory——BenchmarkDotNet 的 GlobalSetup 中使用。</summary>
internal sealed class SingletonDbContextFactory : IDbContextFactory<MemoryLibraryDbContext>
{
    private readonly DbContextOptions<MemoryLibraryDbContext> _options;

    public SingletonDbContextFactory(DbContextOptions<MemoryLibraryDbContext> options)
    {
        _options = options;
    }

    public MemoryLibraryDbContext CreateDbContext()
    {
        var db = new MemoryLibraryDbContext(_options);
        // EnsureCreated 每次创建新内存 DB 但保留 Schema
        db.Database.EnsureCreated();
        return db;
    }
}
