using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingPlatform.Services.StorageManagement;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class StorageInventorySamplerQueryTests
{
    [TestMethod]
    public void DirectorySample_BoundsDirectoriesAndNonMatchingFiles()
    {
        var root = Directory.CreateTempSubdirectory("pudding-inventory-");
        try
        {
            for (var i = 0; i < 30; i++)
            {
                var child = root.CreateSubdirectory(i.ToString());
                File.WriteAllBytes(Path.Combine(child.FullName, "ignored.bin"), [1, 2, 3]);
            }
            var sample = StorageInventorySampler.SampleDirectoryEntries([root.FullName], true, 7, default);
            Assert.AreEqual(7, sample.VisitedEntries);
            Assert.AreEqual(0L, sample.Files);
            Assert.AreEqual(0L, sample.Bytes);
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    public void DirectorySample_CollectsMetadata_AndSharesBudgetAcrossLazyRoots()
    {
        var root = Directory.CreateTempSubdirectory("pudding-inventory-");
        var rootsDisposed = false;
        IEnumerable<string> Roots()
        {
            try
            {
                yield return root.FullName;
                Assert.Fail("Root enumeration must stop at the shared entry budget.");
            }
            finally { rootsDisposed = true; }
        }
        try
        {
            File.WriteAllBytes(Path.Combine(root.FullName, "sample.log"), [1, 2, 3]);
            var sample = StorageInventorySampler.SampleDirectoryEntries(Roots(), true, 2, default);
            Assert.AreEqual(2, sample.VisitedEntries);
            Assert.AreEqual(1L, sample.Files);
            Assert.AreEqual(3L, sample.Bytes);
            Assert.IsNotNull(sample.OldestUtc);
            Assert.IsTrue(rootsDisposed);
            var all = StorageInventorySampler.SampleDirectoryEntries([root.FullName], false, 10, default);
            Assert.AreEqual(sample, all);
        }
        finally { root.Delete(recursive: true); }
    }

    [TestMethod]
    public void DirectorySample_ZeroBudgetDoesNotEnumerate_AndCancellationIsObserved()
    {
        IEnumerable<string> MustNotEnumerate()
        {
            Assert.Fail("No filesystem work is allowed with a zero budget or a cancelled request.");
            yield break;
        }
        var empty = StorageInventorySampler.SampleDirectoryEntries(MustNotEnumerate(), true, 0, default);
        Assert.AreEqual(0, empty.VisitedEntries);
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            StorageInventorySampler.SampleDirectoryEntries(MustNotEnumerate(), true, 2, new CancellationToken(true)));
    }

    [TestMethod]
    public async Task TimeRange_UsesTwoCoveringIndexSeeks_NotCombinedAggregateScan()
    {
        await using var connection = await OpenAsync();
        await ExecuteAsync(connection, "CREATE INDEX ix_ts ON events(ts); INSERT INTO events(ts) VALUES(NULL),('2026-09-03'),('2026-09-01'),('2026-09-05');");
        var index = await StorageInventorySampler.FindTimeIndexAsync(connection, "events", "ts", default);
        Assert.AreEqual("ix_ts", index);
        var range = await StorageInventorySampler.ReadTimeRangeAsync(connection, "events", "ts", index!, default);
        Assert.AreEqual("2026-09-01", range.Min);
        Assert.AreEqual("2026-09-05", range.Max);

        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + StorageInventorySampler.BuildTimeRangeQuery("events", "ts", index!);
        await using var reader = await command.ExecuteReaderAsync();
        var details = new List<string>();
        while (await reader.ReadAsync()) details.Add(reader.GetString(3));
        Assert.AreEqual(2, details.Count(d => d.Contains("SEARCH events USING COVERING INDEX ix_ts", StringComparison.Ordinal)));
        Assert.IsFalse(details.Any(d => d.Contains("SCAN events", StringComparison.Ordinal) || d.Contains("TEMP B-TREE", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task TimeRange_EmptyAndAllNull_ReturnNoEndpoints()
    {
        await using var connection = await OpenAsync();
        await ExecuteAsync(connection, "CREATE INDEX ix_ts ON events(ts DESC);");
        foreach (var insert in new[] { "SELECT 1;", "INSERT INTO events(ts) VALUES(NULL),(NULL);" })
        {
            await ExecuteAsync(connection, insert);
            var range = await StorageInventorySampler.ReadTimeRangeAsync(connection, "events", "ts", "ix_ts", default);
            Assert.IsNull(range.Min);
            Assert.IsNull(range.Max);
        }
    }

    [TestMethod]
    public async Task TimeIndex_RejectsPartialExpressionNonLeadingAndCollatedIndexes()
    {
        await using var connection = await OpenAsync();
        await ExecuteAsync(connection, """
            CREATE INDEX partial_ts ON events(ts) WHERE id>10;
            CREATE INDEX expression_ts ON events(lower(ts));
            CREATE INDEX nonleading_ts ON events(id,ts);
            CREATE INDEX collated_ts ON events(ts COLLATE NOCASE);
            """);
        Assert.IsNull(await StorageInventorySampler.FindTimeIndexAsync(connection, "events", "ts", default));
        await ExecuteAsync(connection, "CREATE INDEX full_ts ON events(ts DESC,id);");
        Assert.AreEqual("full_ts", await StorageInventorySampler.FindTimeIndexAsync(connection, "events", "ts", default));
    }

    [TestMethod]
    public async Task TimeRange_QuotesIdentifiers_AndSupportsDescendingIndex()
    {
        await using var connection = await OpenAsync();
        await ExecuteAsync(connection, """
            CREATE TABLE "event rows" ("time""stamp" TEXT);
            CREATE INDEX "time index" ON "event rows"("time""stamp" DESC);
            INSERT INTO "event rows" VALUES ('z'),(NULL),('a');
            """);
        var index = await StorageInventorySampler.FindTimeIndexAsync(connection, "event rows", "time\"stamp", default);
        Assert.AreEqual("time index", index);
        var range = await StorageInventorySampler.ReadTimeRangeAsync(connection, "event rows", "time\"stamp", index!, default);
        Assert.AreEqual("a", range.Min);
        Assert.AreEqual("z", range.Max);
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await ExecuteAsync(connection, "CREATE TABLE events(id INTEGER PRIMARY KEY,ts TEXT);");
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
