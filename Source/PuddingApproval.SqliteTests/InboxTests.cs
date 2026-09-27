using PuddingApproval.Sqlite;

namespace PuddingApproval.SqliteTests;

public class InboxTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"pudding-inbox-{Guid.NewGuid():N}.db");
    private static readonly ApprovalInboxScope Scope = new("workspace", "author", "session");
    private static ApprovalRecord Request(string id, ApprovalInboxScope? scope = null)
    {
        scope ??= Scope;
        var operation = new ApprovalOperation("tool", "{}", "{}", Path.GetTempPath());
        return new(id, new(scope.WorkspaceId, scope.AgentId, scope.SessionId, "run", "turn", id, operation.Fingerprint(), "policy"),
            DateTimeOffset.UtcNow.AddHours(1), operation);
    }
    [Fact]
    public async Task InboxIsScopedPagedAndReflectsCommittedDecisionsAfterReopen()
    {
        var store = await SqliteApprovalStore.OpenAsync(_path);
        foreach (var id in new[] { "c", "a", "b" }) Assert.True(await store.CreateAsync(Request(id)));
        foreach (var other in new[] { Scope with { WorkspaceId = "other" }, Scope with { AgentId = "reviewer" }, Scope with { SessionId = "other" } })
            Assert.True(await store.CreateAsync(Request(Guid.NewGuid().ToString(), other)));
        var page = await store.ReadPendingAsync(Scope, limit: 2);
        Assert.Equal(new[] { "a", "b" }, page.Items.Select(r => r.Id)); Assert.Equal("b", page.NextAfterId);
        var tail = await store.ReadPendingAsync(Scope, page.NextAfterId, 2);
        Assert.Equal("c", Assert.Single(tail.Items).Id); Assert.Null(tail.NextAfterId);
        var record = page.Items[0];
        await new ApprovalService(store).DecideAsync(record.Id, record.Version, record.Binding, new("decision", HumanDecision.Deny, "human", null));
        var reopened = await SqliteApprovalStore.OpenAsync(_path);
        Assert.Equal(new[] { "b", "c" }, (await reopened.ReadPendingAsync(Scope)).Items.Select(r => r.Id));
        Assert.Empty((await reopened.ReadPendingAsync(Scope with { AgentId = "author' OR 1=1 --" })).Items);
    }
    [Fact]
    public async Task ReadDoesNotExpireRequestsOrProduceEventsAndValidatesBounds()
    {
        var store = await SqliteApprovalStore.OpenAsync(_path);
        var expired = Request("expired") with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        await store.CreateAsync(expired);
        Assert.Equal(expired, Assert.Single((await store.ReadPendingAsync(Scope)).Items));
        Assert.Single(await store.ReadOutboxAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadPendingAsync(Scope with { SessionId = "" }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadPendingAsync(Scope, " "));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadPendingAsync(Scope, limit: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadPendingAsync(Scope, limit: 101));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadPendingAsync(Scope, ct: cancelled.Token));
    }
    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
    }
}
