using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingMemoryEngine.Services;

namespace PuddingMemoryEngineBenchmarks;

/// <summary>
/// MemoryRecallService 在不同事实规模下的性能基准。
/// 使用 SQLite InMemory（共享连接）隔离磁盘 IO 干扰。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class MemoryRecallBenchmarks
{
    private const string WorkspaceId = "bench-workspace";

    private SqliteConnection _connection = null!;
    private IDbContextFactory<MemoryDbContext> _dbFactory = null!;
    private IMemoryLibraryConvenience _libraryConvenience = null!;
    private MemoryRecallService _service = null!;

    [Params(10, 100, 1000, 5000)]
    public int FactCount { get; set; }

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _connection = new SqliteConnection("Data Source=:memory:;Cache=Shared");
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbFactory = new InMemoryRecallDbContextFactory(options);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            await SeedDataAsync(db, FactCount);
        }

        _libraryConvenience = new FakeMemoryLibraryConvenience();
        _service = new MemoryRecallService(
            _libraryConvenience,
            _dbFactory,
            NullLogger<MemoryRecallService>.Instance);
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        if (_connection != null)
        {
            await _connection.DisposeAsync();
        }
    }

    [Benchmark]
    public Task<MemoryRecallResult> Recall_SmallQuery()
    {
        return _service.RecallAsync(
            query: "name ramen",
            workspaceId: WorkspaceId,
            topK: 10,
            ct: CancellationToken.None);
    }

    [Benchmark]
    public Task<MemoryRecallResult> Recall_LargeQuery()
    {
        return _service.RecallAsync(
            query: "my name age city favorite ramen noodles travel preference coding language hobby profile",
            workspaceId: WorkspaceId,
            topK: 20,
            ct: CancellationToken.None);
    }

    [Benchmark]
    public Task<MemoryRecallResult> Recall_EmptyResult()
    {
        return _service.RecallAsync(
            query: "zzzz-not-found-topic-quantum-asteroid",
            workspaceId: WorkspaceId,
            topK: 10,
            ct: CancellationToken.None);
    }

    private static async Task SeedDataAsync(MemoryDbContext db, int factCount)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var facts = new List<MemoryFactEntity>(factCount);
        for (int i = 0; i < factCount; i++)
        {
            facts.Add(new MemoryFactEntity
            {
                FactId = Guid.NewGuid().ToString("N"),
                WorkspaceId = WorkspaceId,
                Statement = $"fact-{i}: my name is user-{i}, age {20 + (i % 30)}, city city-{i % 100}, I like ramen-{i % 50}",
                Confidence = 0.7 + ((i % 20) * 0.01),
                Category = "profile",
                SourceSessionId = "bench-session",
                SourceMessageId = $"msg-{i}",
                Status = "active",
                CreatedAt = now + i,
                UpdatedAt = now + i,
            });
        }

        var prefs = new List<MemoryPreferenceEntity>
        {
            new()
            {
                PreferenceId = Guid.NewGuid().ToString("N"),
                WorkspaceId = WorkspaceId,
                Category = "food",
                Key = "favorite_food",
                Value = "ramen",
                SourceSessionId = "bench-session",
                SourceMessageId = "msg-pref-1",
                CreatedAt = now,
                UpdatedAt = now,
            },
            new()
            {
                PreferenceId = Guid.NewGuid().ToString("N"),
                WorkspaceId = WorkspaceId,
                Category = "lifestyle",
                Key = "favorite_city",
                Value = "tokyo",
                SourceSessionId = "bench-session",
                SourceMessageId = "msg-pref-2",
                CreatedAt = now,
                UpdatedAt = now,
            },
        };

        await db.MemoryFacts.AddRangeAsync(facts);
        await db.MemoryPreferences.AddRangeAsync(prefs);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 基准场景使用的内存 DbContextFactory。
    /// </summary>
    private sealed class InMemoryRecallDbContextFactory : IDbContextFactory<MemoryDbContext>
    {
        private readonly DbContextOptions<MemoryDbContext> _options;

        public InMemoryRecallDbContextFactory(DbContextOptions<MemoryDbContext> options)
        {
            _options = options;
        }

        public MemoryDbContext CreateDbContext()
        {
            return new MemoryDbContext(_options);
        }

        public Task<MemoryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new MemoryDbContext(_options));
        }
    }

    /// <summary>
    /// 轻量假实现：避免引入 Library 存储构建成本，聚焦 RecallService 融合路径。
    /// </summary>
    private sealed class FakeMemoryLibraryConvenience : IMemoryLibraryConvenience
    {
        public Task<ExperienceWriteResult> UpsertExperienceAsync(string workspaceId, ExperiencePackage experience, CancellationToken ct = default)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var book = new BookRecord(
                BookId: Guid.NewGuid().ToString("N"),
                LibraryId: "bench-lib",
                Title: experience.Title,
                Summary: "bench",
                Status: "active",
                Version: 1,
                AccessCount: 0,
                LastAccessedAt: now,
                CreatedAt: now,
                UpdatedAt: now);

            var chapter = new ChapterRecord(
                ChapterId: Guid.NewGuid().ToString("N"),
                BookId: book.BookId,
                Title: "bench-chapter",
                ChapterOrder: 0,
                Content: experience.Content,
                ContentType: "markdown",
                Importance: 0.5,
                SourceSessionId: null,
                WordCount: experience.Content.Length,
                CreatedAt: now,
                UpdatedAt: now);

            return Task.FromResult(new ExperienceWriteResult(book, chapter));
        }

        public Task<IReadOnlyList<RankedResult>> SmartSearchAsync(string naturalLanguageQuery, int topK = 20, CancellationToken ct = default)
        {
            if (naturalLanguageQuery.Contains("zzzz-not-found", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<IReadOnlyList<RankedResult>>(Array.Empty<RankedResult>());
            }

            var result = new RankedResult
            {
                BookId = "lib-book-1",
                BookTitle = "Memory Recall Synthetic Book",
                ChapterId = "lib-ch-1",
                ChapterTitle = "Synthetic Chapter",
                Snippet = "synthetic library snippet about profile and ramen",
                Score = 0.95,
                MatchSource = "fts5",
                IsPendingDeepExplore = false,
            };

            return Task.FromResult<IReadOnlyList<RankedResult>>(new[] { result });
        }

        public Task<BookRecord> GetOrCreateBookAsync(string libraryId, string title, string? summary, IReadOnlyList<string>? tagPaths, CancellationToken ct)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return Task.FromResult(new BookRecord(
                BookId: Guid.NewGuid().ToString("N"),
                LibraryId: libraryId,
                Title: title,
                Summary: summary ?? string.Empty,
                Status: "active",
                Version: 1,
                AccessCount: 0,
                LastAccessedAt: now,
                CreatedAt: now,
                UpdatedAt: now));
        }

        public Task<ChapterRecord> AppendChapterAsync(string bookId, string title, string content, string? sourceSessionId = null, CancellationToken ct = default)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return Task.FromResult(new ChapterRecord(
                ChapterId: Guid.NewGuid().ToString("N"),
                BookId: bookId,
                Title: title,
                ChapterOrder: 0,
                Content: content,
                ContentType: "markdown",
                Importance: 0.5,
                SourceSessionId: sourceSessionId,
                WordCount: content.Length,
                CreatedAt: now,
                UpdatedAt: now));
        }

        public Task<IReadOnlyList<PointerRecord>> AutoDiscoverPointersAsync(string chapterId, CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<PointerRecord>>(Array.Empty<PointerRecord>());
        }

        public Task<IReadOnlyList<TagTreeNode>> GetTagRootsAsync(CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<TagTreeNode>>(Array.Empty<TagTreeNode>());
        }

        public Task StartDeepExploreAsync(string query, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public IReadOnlyList<RankedResult> GetPendingExplorations(string query)
        {
            return Array.Empty<RankedResult>();
        }
    }
}
