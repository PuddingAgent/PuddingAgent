using Microsoft.Data.Sqlite;
using PuddingApproval.Sqlite;

namespace PuddingApproval.SqliteTests;

public class StoreTests : IDisposable
{
    [Fact]
    public async Task CancellationAndItsOutboxSurviveReopenWithoutRenewingPermission()
    {
        var store = await SqliteApprovalStore.OpenAsync(_path); var request = Request();
        var cancel = new ApprovalCancellation("stop", "owner", "Stop task");
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(request with { Cancellation = cancel }));
        await store.CreateAsync(request);
        await new ApprovalService(store).DecideAsync(request.Id, 0, request.Binding, Allow);
        await new ApprovalService(store).CancelAsync(request.Id, 1, request.Binding, cancel);
        var reopened = await SqliteApprovalStore.OpenAsync(_path);
        var result = await new ApprovalService(reopened).CancelAsync(request.Id, 1, request.Binding, cancel);
        Assert.Equal(ApprovalOutcome.Replayed, result.Outcome); Assert.Equal(ApprovalState.Cancelled, result.Record!.State);
        Assert.Equal(cancel, result.Record.Cancellation); Assert.Equal(Allow, result.Record.Decision);
        Assert.Equal(ApprovalState.Cancelled, (await reopened.ReadOutboxAsync()).Last().Record.State);
        Assert.Empty((await reopened.ReadPendingAsync(new("w", "a", "s"))).Items);
        Assert.Equal(ApprovalOutcome.Unavailable, (await new ApprovalService(reopened).ConsumeAsync(request.Id, request.Binding, true)).Outcome);
    }
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"pudding-approval-test-{Guid.NewGuid():N}.db");
    private static readonly ApprovalOperation Operation = new("tool", "{\"path\":\"file.txt\"}", "{\"version\":1}", Path.GetTempPath());
    private static ApprovalRecord Request() => new("request", new("w", "a", "s", "r", "t", "i", Operation.Fingerprint(), "policy"), DateTimeOffset.UtcNow.AddMinutes(5), Operation);
    private static DecisionCommand Allow => new("decision", HumanDecision.AllowOnce, "human", null);
    [Fact]
    public async Task OperationSnapshotIsDurableImmutableAndCannotBeForged()
    {
        var store = await SqliteApprovalStore.OpenAsync(_path); var request = Request();
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(request with { Operation = Operation with { ArgumentsJson = "{}" } }));
        Assert.Empty(await store.ReadOutboxAsync());
        await store.CreateAsync(request);
        var reopened = await SqliteApprovalStore.OpenAsync(_path);
        Assert.Equal(Operation, (await reopened.ReadAsync(request.Id))!.Operation);
        Assert.Equal(Operation, Assert.Single(await reopened.ReadOutboxAsync()).Record.Operation);
        // Equivalent JSON has the same fingerprint, but the exact display/execution snapshot stays immutable.
        var equivalent = Operation with { ArgumentsJson = "{ \"path\" : \"file.txt\" }" };
        Assert.Equal(Operation.Fingerprint(), equivalent.Fingerprint());
        await Assert.ThrowsAsync<ArgumentException>(() => reopened.CompareExchangeAsync(request.Id, 0,
            request with { Version = 1, Operation = equivalent }, default));
        Assert.Equal(request, await reopened.ReadAsync(request.Id)); Assert.Single(await reopened.ReadOutboxAsync());
    }
    public void Dispose() { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix); }

    [Fact]
    public async Task ReopenPreservesStateAndUnacknowledgedEvents()
    {
        var store = await SqliteApprovalStore.OpenAsync(_path); var request = Request();
        Assert.True(await store.CreateAsync(request));
        Assert.Equal(ApprovalOutcome.Applied, (await new ApprovalService(store).DecideAsync(request.Id, 0, request.Binding, Allow)).Outcome);
        var reopened = await SqliteApprovalStore.OpenAsync(_path);
        Assert.Equal(ApprovalState.Approved, (await reopened.ReadAsync(request.Id))!.State);
        var events = await reopened.ReadOutboxAsync(); Assert.Equal(2, events.Count);
        Assert.Equal(new long[] { 0, 1 }, events.Select(e => e.Record.Version));
        Assert.Equal(events, await reopened.ReadOutboxAsync());
        await reopened.AcknowledgeAsync(events[0].Sequence); await reopened.AcknowledgeAsync(events[0].Sequence);
        Assert.Equal(events[1], Assert.Single(await reopened.ReadOutboxAsync()));
    }
    [Fact]
    public async Task TwoStoreInstancesPermitOnlyOneDecisionAndConsumption()
    {
        var one = await SqliteApprovalStore.OpenAsync(_path); var two = await SqliteApprovalStore.OpenAsync(_path); var request = Request();
        await one.CreateAsync(request);
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() => new ApprovalService(i % 2 == 0 ? one : two)
            .DecideAsync(request.Id, 0, request.Binding, Allow with { DecisionId = $"d{i}" }))));
        Assert.Single(results, r => r.Outcome == ApprovalOutcome.Applied);
        var consumed = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() => new ApprovalService(i % 2 == 0 ? one : two)
            .ConsumeAsync(request.Id, request.Binding, true))));
        Assert.Single(consumed, r => r.Outcome == ApprovalOutcome.Applied);
        Assert.Equal(3, (await one.ReadOutboxAsync()).Count);
    }
    [Fact]
    public async Task OutboxFailureRollsBackDecision()
    {
        var store = await SqliteApprovalStore.OpenAsync(_path); var request = Request(); await store.CreateAsync(request);
        await using var db = new SqliteConnection($"Data Source={_path};Pooling=False"); await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_event BEFORE INSERT ON approval_outbox BEGIN SELECT RAISE(ABORT, 'injected outbox failure'); END";
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<SqliteException>(() => new ApprovalService(store).DecideAsync(request.Id, 0, request.Binding, Allow));
        Assert.Equal(request, await store.ReadAsync(request.Id)); Assert.Single(await store.ReadOutboxAsync());
        var other = request with { Id = "other", Binding = request.Binding with { InvocationId = "other" } };
        await Assert.ThrowsAsync<SqliteException>(() => store.CreateAsync(other));
        Assert.Null(await store.ReadAsync(other.Id));
        command.CommandText = "DROP TRIGGER reject_event"; await command.ExecuteNonQueryAsync();
        Assert.Equal(ApprovalOutcome.Applied, (await new ApprovalService(store).DecideAsync(request.Id, 0, request.Binding, Allow)).Outcome);
    }
    [Fact]
    public async Task InvocationCannotCreateTwoIndependentPermitsAndBindingCannotChange()
    {
        var store = await SqliteApprovalStore.OpenAsync(_path); var request = Request(); await store.CreateAsync(request);
        Assert.False(await store.CreateAsync(request));
        Assert.False(await store.CreateAsync(request with { Id = "duplicate" }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CompareExchangeAsync(request.Id, 0,
            request with { Version = 1, Binding = request.Binding with { AgentId = "other" } }, default));
        Assert.Single(await store.ReadOutboxAsync());
    }
    [Fact]
    public async Task ConsumedPermissionCannotBeConsumedAgainAfterReopen()
    {
        var store = await SqliteApprovalStore.OpenAsync(_path); var request = Request(); await store.CreateAsync(request);
        var service = new ApprovalService(store);
        await service.DecideAsync(request.Id, 0, request.Binding, Allow);
        await service.ConsumeAsync(request.Id, request.Binding, true);
        var reopened = new ApprovalService(await SqliteApprovalStore.OpenAsync(_path));
        Assert.Equal(ApprovalOutcome.Unavailable, (await reopened.ConsumeAsync(request.Id, request.Binding, true)).Outcome);
        Assert.Equal(ApprovalState.Consumed, (await reopened.DecideAsync(request.Id, 0, request.Binding, Allow)).Record!.State);
    }
    [Fact]
    public void StorageReferencesOnlyFrameworkSqliteAndApprovalContracts() => Assert.All(typeof(SqliteApprovalStore).Assembly.GetReferencedAssemblies(),
        reference => Assert.True(reference.Name!.StartsWith("System.", StringComparison.Ordinal)
            || reference.Name is "PuddingApproval" or "Microsoft.Data.Sqlite", reference.Name));
}
