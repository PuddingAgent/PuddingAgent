using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingEventing.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Eventing;

namespace PuddingPlatformTests.Services;

/// <summary>
/// Plugin archive/audit sink: the frozen observer rule (OperationId=EventId) makes a redelivered event replay the same
/// logical archive record instead of inserting a second one; queries return exactly one record.
/// </summary>
[TestClass]
public sealed class PluginArchiveStoreTests
{
    [TestMethod]
    public async Task SameOperation_ReplaysWithoutASecondRecord()
    {
        await using var host = await TestHost.CreateAsync();
        var first = await host.Store.RecordAsync(Record());
        var second = await host.Store.RecordAsync(Record());

        Assert.IsFalse(first.Replayed);
        Assert.IsTrue(second.Replayed);
        Assert.AreEqual(first.ArchiveId, second.ArchiveId);
        var entries = await host.Store.ListAsync("ws-1", "event-1");
        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("artifact-1", entries[0].ArtifactRef);
    }

    [TestMethod]
    public async Task DifferentEvents_AreDistinctArchiveRecords()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.RecordAsync(Record());
        await host.Store.RecordAsync(Record() with { OperationId = "event-2", EventId = "event-2", ArtifactRef = "artifact-2" });

        var entries = await host.Store.ListAsync("ws-1");
        Assert.AreEqual(2, entries.Count);
    }

    [TestMethod]
    public async Task ReusingAnOperationForAnotherEvent_IsRejected()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.RecordAsync(Record());

        var error = await Assert.ThrowsExactlyAsync<EventingException>(async () =>
            await host.Store.RecordAsync(Record() with { EventId = "other-event" }));
        Assert.AreEqual(EventingErrorCodes.RequestInvalid, error.Code);
        Assert.AreEqual(1, (await host.Store.ListAsync("ws-1")).Count);
    }

    private static PluginArchiveRecord Record() => new("event-1", "event-1", "ws-1", "sample.observer",
        "artifact-1", "sha256-artifact-1");

    private sealed class TestHost(SqliteConnection connection, PluginArchiveStore store) : IAsyncDisposable
    {
        public PluginArchiveStore Store { get; } = store;

        public static async Task<TestHost> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
            var factory = new Factory(options);
            using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await PluginArchiveSchemaBootstrapper.EnsureCreatedAsync(db);
            }
            return new(connection, new PluginArchiveStore(factory));
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }

    private sealed class Factory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }
}
