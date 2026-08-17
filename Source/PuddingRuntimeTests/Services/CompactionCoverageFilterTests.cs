using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class CompactionCoverageFilterTests
{
    [TestMethod]
    public async Task LoadAsync_ReturnsEmpty_WhenMemoryDbFactoryIsNull()
    {
        var filter = new CompactionCoverageFilter(null);

        var coverage = await filter.LoadAsync("session-any");

        Assert.IsNotNull(coverage);
        Assert.AreSame(CompactionCoverage.Empty, coverage, "null factory must no-op to the shared empty coverage.");
        Assert.AreEqual(0, coverage.CoveredMessageIds.Count);
        Assert.AreEqual(0, coverage.CoveredHashes.Count);
        Assert.IsNull(coverage.LatestTargetGeneration);
    }

    [TestMethod]
    public async Task LoadAsync_ReturnsEmpty_WhenSessionHasNoManifest()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await db.SaveChangesAsync();

        var filter = new CompactionCoverageFilter(new TestMemoryDbContextFactory(options));

        var coverage = await filter.LoadAsync("session-without-manifest");

        Assert.AreEqual(0, coverage.CoveredMessageIds.Count);
        Assert.AreEqual(0, coverage.CoveredHashes.Count);
        Assert.IsNull(coverage.LatestTargetGeneration);
    }

    [TestMethod]
    public async Task LoadAsync_ParsesSourceIdsAndHashes_FromLatestManifestOnly()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.CompactionCoverageManifests.AddRange(
            new CompactionCoverageManifestEntity
            {
                CompactionId = "compaction-1",
                SessionId = "session-1",
                SourceGeneration = 0,
                TargetGeneration = 1,
                SourceMessageIds = """["msg-1","msg-2"]""",
                SourceHashes = """["hash-1","hash-2"]""",
            },
            new CompactionCoverageManifestEntity
            {
                CompactionId = "compaction-2",
                SessionId = "session-1",
                SourceGeneration = 1,
                TargetGeneration = 2,
                SourceMessageIds = """["msg-3"]""",
                SourceHashes = """["hash-3"]""",
            });
        await db.SaveChangesAsync();

        var filter = new CompactionCoverageFilter(new TestMemoryDbContextFactory(options));

        var coverage = await filter.LoadAsync("session-1");

        Assert.AreEqual(2, coverage.LatestTargetGeneration, "Latest manifest is the one with max TargetGeneration.");
        CollectionAssert.AreEquivalent(new[] { "msg-3" }, coverage.CoveredMessageIds.ToArray());
        CollectionAssert.AreEquivalent(new[] { "hash-3" }, coverage.CoveredHashes.ToArray());
    }

    [TestMethod]
    public async Task LoadAsync_ReturnsEmptySets_WhenSourceArraysAreMalformedOrNull()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.CompactionCoverageManifests.Add(new CompactionCoverageManifestEntity
        {
            CompactionId = "compaction-malformed",
            SessionId = "session-malformed",
            SourceGeneration = 0,
            TargetGeneration = 1,
            SourceMessageIds = "not-a-json-array",
            SourceHashes = null,
        });
        await db.SaveChangesAsync();

        var filter = new CompactionCoverageFilter(new TestMemoryDbContextFactory(options));

        var coverage = await filter.LoadAsync("session-malformed");

        Assert.IsNotNull(coverage);
        Assert.AreEqual(0, coverage.CoveredMessageIds.Count, "Malformed JSON must degrade to an empty set, not throw.");
        Assert.AreEqual(0, coverage.CoveredHashes.Count, "Null hash array must degrade to an empty set.");
        Assert.AreEqual(1, coverage.LatestTargetGeneration);
    }

    private static DbContextOptions<MemoryDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection)
            .Options;

    private sealed class TestMemoryDbContextFactory(DbContextOptions<MemoryDbContext> options) : IDbContextFactory<MemoryDbContext>
    {
        public MemoryDbContext CreateDbContext() => new(options);

        public Task<MemoryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
