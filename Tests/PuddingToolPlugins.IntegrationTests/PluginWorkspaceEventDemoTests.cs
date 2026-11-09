using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingEventing;
using PuddingEventing.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Eventing;
using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Runtime;
using Pudding.ToolPlugins.Windows;
using PuddingCode.Configuration;
using PuddingRuntime.Services.Plugins;

namespace PuddingToolPlugins.IntegrationTests;

/// <summary>
/// End-to-end demo (real Worker + real PluginManager + real SQLite): a zero-tool observer plugin is loaded, a versioned
/// artifact event is committed and dispatched, one archive record is queryable; a redelivery still yields one; unload +
/// subscription cancel stops new artifacts from being archived. Requires the plugin acceptance environment
/// (PUDDING_PLUGIN_WORKER / PUDDING_PLUGIN_FIXTURE) and is inconclusive otherwise.
/// </summary>
[TestClass]
public sealed class PluginWorkspaceEventDemoTests
{
    [TestMethod]
    public async Task ZeroToolObserver_ArchivesOnce_AndUnloadStopsGrowth()
    {
        var workerPath = Environment.GetEnvironmentVariable("PUDDING_PLUGIN_WORKER");
        var fixturePath = Environment.GetEnvironmentVariable("PUDDING_PLUGIN_FIXTURE");
        if (!File.Exists(workerPath) || !File.Exists(fixturePath))
            Assert.Inconclusive("Set PUDDING_PLUGIN_WORKER and PUDDING_PLUGIN_FIXTURE (plugin acceptance environment).");

        var root = Path.Combine(Environment.GetEnvironmentVariable("PUDDING_PLUGIN_TEST_ROOT") ?? Path.GetTempPath(),
            "event-demo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var plugins = new ExecutablePluginService(PuddingDataPaths.FromRoot(root),
            new WindowsPluginWorkerFactory(workerPath!));
        try
        {
            var package = await ImportObserverAsync(plugins, fixturePath!);
            var scope = new PluginScope("ws-1", "", "", "host-subscriber");
            var loaded = await plugins.Manager.LoadAsync("demo", package, scope,
                new PluginAuthorization(false, new HashSet<string>(), false, SubscriptionsApproved: true));
            Assert.AreEqual("Active", loaded.State, loaded.Failure?.Message);

            var dbPath = Path.Combine(root, "demo.db");
            await using var host = await EventHost.CreateAsync(plugins, dbPath);
            await host.SyncAsync(loaded);
            var active = (await host.Subscriptions.ListAsync("ws-1")).Where(s => !s.Cancelled).ToList();
            Assert.AreEqual(1, active.Count, "one active subscription");
            var eventId = await host.PublishAsync("ref://artifact/v1");
            var first = await host.Worker.RunOnceAsync();
            Assert.AreEqual(1, first.Delivered,
                $"subscriptions={first.Subscriptions},failed={first.Failed},snapshot={first.SnapshotRequired},pending={(await host.Deliveries.ListPendingAsync(active[0].SubscriptionId)).Length}");
            Assert.AreEqual(1, (await host.Archive.ListAsync("ws-1", eventId)).Count, "Exactly one archive record.");
            Assert.AreEqual(1, await CountArchivedFromFileAsync(dbPath, eventId),
                "The archive is persisted in the file database, not only in memory.");

            // Simulated lost receipt: redelivery reaches the plugin again, the archive replays one logical record.
            var page = await host.Log.ReadAsync(new("ws-1", 0, 10));
            await host.Deliveries.EnqueueAsync(active[0].SubscriptionId, page.Events);
            Assert.AreEqual(1, (await host.Worker.RunOnceAsync()).Delivered);
            Assert.AreEqual(1, (await host.Archive.ListAsync("ws-1", eventId)).Count);

            await plugins.Manager.UnloadAsync(loaded.ActivationId, scope);
            await host.SyncAsync(null);
            await host.PublishAsync("ref://artifact/v2");
            Assert.AreEqual(0, (await host.Worker.RunOnceAsync()).Delivered);
            Assert.AreEqual(1, (await host.Archive.ListAsync("ws-1")).Count,
                "An unloaded observer archives no new artifact.");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<PluginPackage> ImportObserverAsync(ExecutablePluginService plugins, string fixturePath,
        string entryType = "Sample.PluginFixture.WorkspaceObserverPlugin")
    {
        var manifest = new PluginManifest
        {
            Id = "sample.observer", Name = "Workspace observer", Version = "1.0.0",
            Entry = new("bin/Sample.PluginFixture.dll", entryType),
            Limits = new(30, 1),
            Subscriptions = [new("archive", PluginEvents.WorkspaceArtifactCreated)],
        };
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("plugin.json").Open()))
                writer.Write(JsonSerializer.Serialize(manifest, PluginJson.Options));
            using var target = zip.CreateEntry(manifest.Entry.Assembly).Open();
            using var source = File.OpenRead(fixturePath);
            source.CopyTo(target);
        }
        stream.Position = 0;
        return await plugins.Packages.ImportAsync(stream, CancellationToken.None);
    }

    [TestMethod]
    public async Task PassiveObserver_ArchivesNothing()
    {
        var workerPath = Environment.GetEnvironmentVariable("PUDDING_PLUGIN_WORKER");
        var fixturePath = Environment.GetEnvironmentVariable("PUDDING_PLUGIN_FIXTURE");
        if (!File.Exists(workerPath) || !File.Exists(fixturePath))
            Assert.Inconclusive("Set PUDDING_PLUGIN_WORKER and PUDDING_PLUGIN_FIXTURE (plugin acceptance environment).");
        var root = Path.Combine(Environment.GetEnvironmentVariable("PUDDING_PLUGIN_TEST_ROOT") ?? Path.GetTempPath(),
            "passive-demo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var plugins = new ExecutablePluginService(PuddingDataPaths.FromRoot(root),
            new WindowsPluginWorkerFactory(workerPath!));
        try
        {
            var package = await ImportObserverAsync(plugins, fixturePath!,
                "Sample.PluginFixture.WorkspaceObserverPassivePlugin");
            var scope = new PluginScope("ws-1", "", "", "host-subscriber");
            var loaded = await plugins.Manager.LoadAsync("passive", package, scope,
                new PluginAuthorization(false, new HashSet<string>(), false, SubscriptionsApproved: true));
            Assert.AreEqual("Active", loaded.State, loaded.Failure?.Message);

            await using var host = await EventHost.CreateAsync(plugins, Path.Combine(root, "passive.db"));
            await host.SyncAsync(loaded);
            await host.PublishAsync("ref://artifact/v1");
            var run = await host.Worker.RunOnceAsync();
            Assert.AreEqual(1, run.Delivered, "The passive observer consumes the event successfully.");
            Assert.AreEqual(0, (await host.Archive.ListAsync("ws-1")).Count,
                "A successful callback that omits the archive request writes nothing.");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<int> CountArchivedFromFileAsync(string path, string eventId)
    {
        await using var connection = new SqliteConnection("DataSource=" + path);
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
        var store = new PluginArchiveStore(new Factory(options));
        return (await store.ListAsync("ws-1", eventId)).Count;
    }

    private sealed class ManagerTarget(ExecutablePluginService plugins, PluginArchiveStore archive)
        : IWorkspacePluginEventTarget
    {
        public async ValueTask<IReadOnlyList<PluginEventDelivery>> DeliverAsync(PluginWorkspaceEvent workspaceEvent,
            string pluginId, long generation, CancellationToken ct = default)
        {
            var payloadRef = workspaceEvent.Payload.TryGetProperty("payloadRef", out var refNode)
                ? refNode.GetString() ?? ""
                : "";
            var payloadHash = workspaceEvent.Payload.TryGetProperty("payloadHash", out var hashNode)
                ? hashNode.GetString() ?? ""
                : "";
            async Task<System.Text.Json.JsonElement> Callback(string method,
                System.Text.Json.JsonElement arguments, CancellationToken token)
            {
                if (method != "plugin.archive.record")
                    throw new PluginException("plugin.core_callback_denied", "core_callback",
                        "No other host capability is granted to an event callback.");
                var requestedEventId = arguments.TryGetProperty("eventId", out var idNode)
                    ? idNode.GetString()
                    : null;
                if (!string.Equals(requestedEventId, workspaceEvent.EventId, StringComparison.Ordinal))
                    throw new PluginException("plugin.core_callback_denied", "core_callback",
                        "An observer may only archive the event it was delivered.");
                var receipt = await archive.RecordAsync(new PluginArchiveRecord(workspaceEvent.EventId,
                    workspaceEvent.EventId, workspaceEvent.Scope.WorkspaceId, pluginId, payloadRef, payloadHash),
                    token).ConfigureAwait(false);
                return System.Text.Json.JsonSerializer.SerializeToElement(new
                {
                    accepted = true,
                    archiveId = receipt.ArchiveId,
                    replayed = receipt.Replayed,
                });
            }

            return await plugins.Manager.PublishWorkspaceEventAsync(workspaceEvent, pluginId, generation,
                Callback, ct).ConfigureAwait(false);
        }
    }

    private sealed class EventHost(SqliteConnection connection, SubscriptionStore subscriptions,
        EventDeliveryStore deliveries, OutboxCommittedEventLog log, WorkspaceArtifactEventPublisher publisher,
        PluginArchiveStore archive, PluginSubscriptionSynchronizer synchronizer, PluginEventDispatchWorker worker,
        string subscriptionId) : IAsyncDisposable
    {
        public SubscriptionStore Subscriptions { get; } = subscriptions;
        public EventDeliveryStore Deliveries { get; } = deliveries;
        public OutboxCommittedEventLog Log { get; } = log;
        public PluginArchiveStore Archive { get; } = archive;
        public PluginSubscriptionSynchronizer Synchronizer { get; } = synchronizer;
        public PluginEventDispatchWorker Worker { get; } = worker;
        public string SubscriptionId { get; } = subscriptionId;

        public ValueTask<string> PublishAsync(string artifactRef) =>
            publisher.PublishCreatedAsync("ws-1", artifactRef, "sha-" + artifactRef, "artifact-service",
                businessRef: artifactRef);

        public async Task SyncAsync(PluginActivation? activation)
        {
            var candidates = activation is null
                ? Array.Empty<PluginSubscriptionCandidate>()
                : [new PluginSubscriptionCandidate("ws-1", "sample.observer", Math.Max(1, activation.Revision),
                    [PluginEvents.WorkspaceArtifactCreated])];
            await Synchronizer.SyncAsync(candidates, ["ws-1"]);
        }

        public static async Task<EventHost> CreateAsync(ExecutablePluginService plugins, string dbPath)
        {
            var connection = new SqliteConnection("DataSource=" + dbPath);
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
            var factory = new Factory(options);
            using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await EventSubscriptionSchemaBootstrapper.EnsureCreatedAsync(db, null);
                await PluginArchiveSchemaBootstrapper.EnsureCreatedAsync(db, null);
            }
            var subscriptions = new SubscriptionStore(factory);
            var deliveries = new EventDeliveryStore(factory);
            var log = new OutboxCommittedEventLog(factory);
            var dispatcher = new SubscriptionDispatcher(log, subscriptions, deliveries, subscriptions);
            var archive = new PluginArchiveStore(factory);
            var sink = new PluginManagerEventDeliverySink(new ManagerTarget(plugins, archive), subscriptions);
            var worker = new PluginEventDispatchWorker(new Workspaces(), subscriptions, dispatcher, deliveries, sink);
            var publisher = new WorkspaceArtifactEventPublisher(new EventOutboxUnitOfWork(factory));
            var synchronizer = new PluginSubscriptionSynchronizer(subscriptions);
            // The host owns the grant id; create it through the synchronizer so this test uses the production path.
            var candidates = new[] { new PluginSubscriptionCandidate("ws-1", "sample.observer", 1,
                ImmutableArray.Create(PluginEvents.WorkspaceArtifactCreated)) };
            await synchronizer.SyncAsync(candidates, ["ws-1"]);
            var subscriptionId = (await subscriptions.ListAsync("ws-1")).Single(s => !s.Cancelled).SubscriptionId;
            return new(connection, subscriptions, deliveries, log, publisher, archive, synchronizer, worker, subscriptionId);
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }

    private sealed class Workspaces : IEventDispatchWorkspaceSource
    {
        public ValueTask<ImmutableArray<string>> ListAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(ImmutableArray.Create("ws-1"));
    }

    private sealed class Factory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }
}
