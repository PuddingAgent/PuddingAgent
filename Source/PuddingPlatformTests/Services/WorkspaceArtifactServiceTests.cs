using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingEventing;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Eventing;
using PuddingPlatform.Services.Files;
using Pudding.ToolPlugins.Contracts;

namespace PuddingPlatformTests.Services;

/// <summary>
/// Artifact/event atomicity: a versioned artifact and its <c>workspace.artifact.created</c> outbox event commit in one
/// transaction; a failure before commit leaves neither, and the committed event carries the artifact reference.
/// </summary>
[TestClass]
public sealed class WorkspaceArtifactServiceTests
{
    [TestMethod]
    public async Task Commit_WritesArtifactAndEvent_ReadableByIdVersion()
    {
        await using var host = await TestHost.CreateAsync();
        var record = await host.Service.CommitAsync("ws-1", "artifact-1", "1.0.0", "ref://artifact/v1", "sha-v1");

        var stored = await host.Service.GetAsync("ws-1", "artifact-1", "1.0.0");
        Assert.IsNotNull(stored);
        Assert.AreEqual("ref://artifact/v1", stored!.ContentRef);
        Assert.AreEqual("sha-v1", stored.PayloadHash);
        Assert.AreEqual(record.CreatedAt, stored.CreatedAt);

        var page = await host.Log.ReadAsync(new("ws-1", 0, 10));
        Assert.AreEqual(1, page.Events.Length);
        Assert.AreEqual(PluginEvents.WorkspaceArtifactCreated, page.Events[0].Kind);
        Assert.AreEqual("artifact-1@1.0.0", page.Events[0].BusinessRef);
        Assert.AreEqual("ref://artifact/v1", page.Events[0].PayloadRef);
    }

    [TestMethod]
    public async Task FailureBeforeCommit_LeavesNeitherArtifactNorEvent()
    {
        await using var host = await TestHost.CreateAsync();
        host.Service.BeforeCommitForTests = () => throw new IOException("injected commit failure");

        await Assert.ThrowsAsync<IOException>(async () =>
            await host.Service.CommitAsync("ws-1", "artifact-1", "1.0.0", "ref://artifact/v1", "sha-v1"));

        Assert.IsNull(await host.Service.GetAsync("ws-1", "artifact-1", "1.0.0"));
        Assert.AreEqual(0, (await host.Log.ReadAsync(new("ws-1", 0, 10))).Events.Length);
    }

    private sealed class TestHost(SqliteConnection connection, WorkspaceArtifactService service,
        OutboxCommittedEventLog log) : IAsyncDisposable
    {
        public WorkspaceArtifactService Service { get; } = service;
        public OutboxCommittedEventLog Log { get; } = log;

        public static async Task<TestHost> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
            var factory = new Factory(options);
            using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await WorkspaceArtifactSchemaBootstrapper.EnsureCreatedAsync(db);
            }
            var uow = new EventOutboxUnitOfWork(factory);
            return new(connection, new WorkspaceArtifactService(factory, uow), new OutboxCommittedEventLog(factory));
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }

    private sealed class Factory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }
}
