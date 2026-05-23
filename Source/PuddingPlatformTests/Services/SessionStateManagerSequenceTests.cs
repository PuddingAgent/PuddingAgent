using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Services;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// ADR-028：SessionStateManager 并发序号原子化测试。
/// 验证 per-session SemaphoreSlim 消除 unique constraint 竞争。
/// </summary>
[TestClass]
public sealed class SessionStateManagerSequenceTests
{
    private IServiceScopeFactory CreateScopeFactory(string dbPath)
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        var services = new ServiceCollection();
        services.AddScoped(_ => new PlatformDbContext(options));
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    private SessionStateManager CreateSsm(string dbPath)
    {
        var scopeFactory = CreateScopeFactory(dbPath);

        // 确保表已创建
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Database.EnsureCreated();

        var tmpDir = Path.Combine(Path.GetTempPath(), $"jsonl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        return new SessionStateManager(
            scopeFactory,
            NullLogger<SessionStateManager>.Instance,
            NullRuntimeActivitySink.Instance,
            new NoOpTraceAccessor(),
            new JsonlSessionWriter(tmpDir));
    }

    /// <summary>
    /// 同一 session 并发 50 个 append → 序列号连续递增、无重复、无不连续。
    /// </summary>
    [TestMethod]
    public async Task AppendAsync_ConcurrentSameSession_AssignsUniqueIncreasingSequences()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        try
        {
            var ssm = CreateSsm(dbPath);
            const string sessionId = "s1";
            const string workspaceId = "w1";

            var tasks = Enumerable.Range(0, 50)
                .Select(i => ssm.AppendAsync(
                    sessionId,
                    workspaceId,
                    new ServerSentEventFrame("delta", $$"""{"delta":"{{i}}"}""")))
                .ToArray();

            var sequences = await Task.WhenAll(tasks);

            // 50 个全部成功，无重复
            Assert.AreEqual(50, sequences.Distinct().Count());

            // 排序后应为 1..50 连续
            var sorted = sequences.OrderBy(x => x).ToArray();
            CollectionAssert.AreEqual(
                Enumerable.Range(1, 50).Select(i => (long)i).ToArray(),
                sorted);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// 不同 session 并发 append → 各自从 1 开始，不互相阻塞。
    /// </summary>
    [TestMethod]
    public async Task AppendAsync_ConcurrentDifferentSessions_EachSessionStartsAtOne()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        try
        {
            var ssm = CreateSsm(dbPath);

            var results = await Task.WhenAll(
                ssm.AppendAsync("s1", "w1", new ServerSentEventFrame("delta", "{}")),
                ssm.AppendAsync("s2", "w1", new ServerSentEventFrame("delta", "{}")));

            Assert.AreEqual(1L, results[0]);
            Assert.AreEqual(1L, results[1]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// SQLite 中不存在重复 (session_id, sequence_num)。
    /// </summary>
    [TestMethod]
    public async Task AppendAsync_NoDuplicateSequenceNum_InSqlite()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        try
        {
            var ssm = CreateSsm(dbPath);
            const string sessionId = "s1";
            const string workspaceId = "w1";

            var tasks = Enumerable.Range(0, 30)
                .Select(i => ssm.AppendAsync(
                    sessionId,
                    workspaceId,
                    new ServerSentEventFrame("delta", $$"""{"delta":"{{i}}"}""")))
                .ToArray();

            await Task.WhenAll(tasks);

            var scopeFactory = CreateScopeFactory(dbPath);
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            var duplicates = await db.SessionEventLogs
                .GroupBy(e => new { e.SessionId, e.SequenceNum })
                .Where(g => g.Count() > 1)
                .CountAsync();

            Assert.AreEqual(0, duplicates);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}

/// <summary>
/// ADR-028 测试用 Null 桩：不记录任何运行时活动。
/// </summary>
file sealed class NullRuntimeActivitySink : IRuntimeActivitySink
{
    public static readonly NullRuntimeActivitySink Instance = new();

    public Task RecordAsync(RuntimeActivity activity, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<RuntimeActivity>> QueryAsync(RuntimeActivityQuery query, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RuntimeActivity>>(Array.Empty<RuntimeActivity>());
}

/// <summary>
/// ADR-028 测试用 NoOp 桩：返回空 TraceContext。
/// </summary>
file sealed class NoOpTraceAccessor : IRuntimeTraceAccessor
{
    public RuntimeTraceContext? Current { get; set; }
}
