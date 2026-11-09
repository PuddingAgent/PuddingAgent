using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingEventing.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Eventing;
using Pudding.ToolPlugins.Contracts;

namespace PuddingPlatformTests.Services;

/// <summary>
/// Subscription lifecycle: an active plugin's subscription is created once, a generation change replaces it, and an
/// unloaded plugin's subscription is cancelled so later workspace events are not delivered to it.
/// </summary>
[TestClass]
public sealed class PluginSubscriptionSynchronizerTests
{
    [TestMethod]
    public async Task Sync_CreatesOnce_AndIsIdempotent()
    {
        await using var host = await TestHost.CreateAsync();
        var candidates = new[] { Candidate("sample.observer", 1) };

        var first = await host.Sync.SyncAsync(candidates);
        var second = await host.Sync.SyncAsync(candidates);

        Assert.AreEqual(1, first.Created);
        Assert.AreEqual(0, first.Cancelled);
        Assert.AreEqual(0, second.Created);
        Assert.AreEqual(0, second.Cancelled);
        var subscriptions = await host.Store.ListAsync("ws-1");
        Assert.AreEqual(1, subscriptions.Count(s => !s.Cancelled));
    }

    [TestMethod]
    public async Task Sync_GenerationChange_ReplacesTheGrant()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Sync.SyncAsync([Candidate("sample.observer", 1)]);

        var result = await host.Sync.SyncAsync([Candidate("sample.observer", 2)]);

        Assert.AreEqual(1, result.Created);
        Assert.AreEqual(1, result.Cancelled);
        var active = (await host.Store.ListAsync("ws-1")).Single(s => !s.Cancelled);
        Assert.AreEqual(2, active.Grant.Generation);
    }

    [TestMethod]
    public async Task Sync_UnloadedPlugin_CancelsItsSubscription()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Sync.SyncAsync([Candidate("sample.observer", 1)]);

        var result = await host.Sync.SyncAsync([], ["ws-1"]);

        Assert.AreEqual(0, result.Created);
        Assert.AreEqual(1, result.Cancelled);
        Assert.IsTrue((await host.Store.ListAsync("ws-1")).All(s => s.Cancelled));
    }

    private static PluginSubscriptionCandidate Candidate(string pluginId, long generation) => new(
        "ws-1", pluginId, generation, ImmutableArray.Create(PluginEvents.WorkspaceArtifactCreated));

    private sealed class TestHost(SqliteConnection connection, SubscriptionStore store,
        PluginSubscriptionSynchronizer sync) : IAsyncDisposable
    {
        public SubscriptionStore Store { get; } = store;
        public PluginSubscriptionSynchronizer Sync { get; } = sync;

        public static async Task<TestHost> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
            var factory = new Factory(options);
            using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await EventSubscriptionSchemaBootstrapper.EnsureCreatedAsync(db, null);
            }
            var store = new SubscriptionStore(factory);
            return new(connection, store, new PluginSubscriptionSynchronizer(store));
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }

    private sealed class Factory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }
}
