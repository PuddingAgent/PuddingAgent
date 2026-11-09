using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingEventing.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Plugins;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-07A S5 durable plugin job protocol on real SQLite: submit is persist-and-ticket only and idempotent by
/// (workspace, operation); cancellation is non-blocking; generation fences cancel/transition; terminal states cannot
/// regress; an expired deadline cannot be marked succeeded; listings are workspace-ordered.
/// </summary>
[TestClass]
public sealed class PluginJobStoreTests
{
    [TestMethod]
    public async Task Submit_IsIdempotentByWorkspaceOperation_AndConflictsOnDifferentContent()
    {
        await using var host = await TestHost.CreateAsync();
        var first = await host.Store.SubmitAsync(Record("job-1", "op-1"));
        Assert.AreEqual("job-1", first.JobId);
        var replay = await host.Store.SubmitAsync(Record("job-1", "op-1"));
        Assert.AreEqual("job-1", replay.JobId, "A replay returns the original ticket, not a second job.");

        var conflict = await Assert.ThrowsExactlyAsync<JobException>(async () =>
            await host.Store.SubmitAsync(Record("job-1", "op-1") with
            {
                Request = Record("job-1", "op-1").Request with { InputRef = "different-input" },
            }));
        Assert.AreEqual(JobErrorCodes.Conflict, conflict.Code);
    }

    [TestMethod]
    public async Task Get_MissingIsTyped_AndListIsWorkspaceOrdered()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.SubmitAsync(Record("job-b", "op-b") with { AcceptedSequence = 2 });
        await host.Store.SubmitAsync(Record("job-a", "op-a") with { AcceptedSequence = 1 });
        await host.Store.SubmitAsync(Record("job-other", "op-other", workspace: "ws-2"));

        var jobs = await host.Store.ListAsync("ws-1");
        CollectionAssert.AreEqual(new[] { "job-a", "job-b" }, jobs.Select(j => j.JobId).ToArray());
        var missing = await Assert.ThrowsExactlyAsync<JobException>(async () => await host.Store.GetAsync("missing"));
        Assert.AreEqual(JobErrorCodes.JobMissing, missing.Code);
    }

    [TestMethod]
    public async Task Transitions_FenceGenerationAndStateMachine()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.SubmitAsync(Record("job-1", "op-1"));

        var stale = await Assert.ThrowsExactlyAsync<JobException>(async () =>
            await host.Store.TransitionAsync("job-1", 99, PluginJobState.Running, null, null, null));
        Assert.AreEqual(JobErrorCodes.GenerationStale, stale.Code);

        var skip = await Assert.ThrowsExactlyAsync<JobException>(async () =>
            await host.Store.TransitionAsync("job-1", 3, PluginJobState.Succeeded, null, "ref", "hash"));
        Assert.AreEqual(JobErrorCodes.StateInvalid, skip.Code);

        var running = await host.Store.TransitionAsync("job-1", 3, PluginJobState.Running, null, null, null);
        Assert.AreEqual(PluginJobState.Running, running.State);
        Assert.AreEqual(1, running.Attempt);
        var succeeded = await host.Store.TransitionAsync("job-1", 3, PluginJobState.Succeeded, null, "ref-1", "hash-1");
        Assert.AreEqual(PluginJobState.Succeeded, succeeded.State);
        Assert.AreEqual("ref-1", succeeded.ResultRef);

        var terminal = await Assert.ThrowsExactlyAsync<JobException>(async () =>
            await host.Store.TransitionAsync("job-1", 3, PluginJobState.Failed, "late", null, null));
        Assert.AreEqual(JobErrorCodes.StateInvalid, terminal.Code);
    }

    [TestMethod]
    public async Task Cancel_IsGenerationFenced_AndOnlyNonTerminal()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.SubmitAsync(Record("job-1", "op-1"));
        var stale = await Assert.ThrowsExactlyAsync<JobException>(async () =>
            await host.Store.CancelAsync("job-1", 2, "stale"));
        Assert.AreEqual(JobErrorCodes.GenerationStale, stale.Code);

        var cancelled = await host.Store.CancelAsync("job-1", 3, "user.cancelled");
        Assert.AreEqual(PluginJobState.Cancelled, cancelled.State);
        Assert.AreEqual("user.cancelled", cancelled.ReasonCode);
        var again = await Assert.ThrowsExactlyAsync<JobException>(async () =>
            await host.Store.CancelAsync("job-1", 3, "again"));
        Assert.AreEqual(JobErrorCodes.StateInvalid, again.Code);
    }

    [TestMethod]
    public async Task ExpiredDeadline_CannotBeMarkedSucceeded()
    {
        await using var host = await TestHost.CreateAsync();
        await host.Store.SubmitAsync(Record("job-1", "op-1") with
        {
            Binding = Record("job-1", "op-1").Binding with { Deadline = DateTimeOffset.UtcNow.AddMilliseconds(150) },
        });
        await host.Store.TransitionAsync("job-1", 3, PluginJobState.Running, null, null, null);
        await Task.Delay(250);
        var expired = await Assert.ThrowsExactlyAsync<JobException>(async () =>
            await host.Store.TransitionAsync("job-1", 3, PluginJobState.Succeeded, null, null, null));
        Assert.AreEqual(JobErrorCodes.DeadlineExpired, expired.Code);
    }

    private static readonly DateTimeOffset AcceptedAt = DateTimeOffset.UtcNow;
    private static readonly DateTimeOffset Deadline = DateTimeOffset.UtcNow.AddMinutes(5);

    private static PluginJobRecord Record(string jobId, string operationId, string workspace = "ws-1") => new(jobId,
        new("plugin.audit", operationId, "input://review-1", "sha-input", 30, ["events.observe"]),
        new("plugin-a", 3, workspace, "work-1", "budget-1", Deadline),
        AcceptedAt, AcceptedSequence: 0);

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class TestHost(SqliteConnection connection, PluginJobStore store) : IAsyncDisposable
    {
        public PluginJobStore Store { get; } = store;

        public static async Task<TestHost> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
            var factory = new TestDbContextFactory(options);
            using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await PluginJobSchemaBootstrapper.EnsureCreatedAsync(db, null);
            }
            return new(connection, new PluginJobStore(factory));
        }

        public async ValueTask DisposeAsync() => await connection.DisposeAsync();
    }
}
