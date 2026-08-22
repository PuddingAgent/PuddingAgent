using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Services.ExternalApi;

using PuddingPlatformTests.Security;

namespace PuddingPlatformTests.Services;

/// <summary>ADR-075 §8.5 简化幂等：claim → execute → complete/replay；同 key 不同 body 409。</summary>
[TestClass]
public sealed class ExternalApiIdempotencyStoreTests
{
    [TestMethod]
    public async Task Claim_Complete_Then_Replay_SameResource()
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        var store = new ExternalApiIdempotencyStore(harness.Factory);

        var first = await store.TryClaimAsync("pat_1", "POST", "/route", "key-1", "body-A", TimeSpan.FromDays(7));
        Assert.AreEqual(ExternalIdempotencyOutcome.Proceed, first.Outcome);

        await store.CompleteAsync("pat_1", "POST", "/route", "key-1", 201, "task_new", CancellationToken.None);

        var replay = await store.TryClaimAsync("pat_1", "POST", "/route", "key-1", "body-A", TimeSpan.FromDays(7));
        Assert.AreEqual(ExternalIdempotencyOutcome.Replay, replay.Outcome);
        Assert.AreEqual(201, replay.ResponseStatus);
        Assert.AreEqual("task_new", replay.ResourceId);
    }

    [TestMethod]
    public async Task SameKey_DifferentBody_Conflict()
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        var store = new ExternalApiIdempotencyStore(harness.Factory);

        await store.TryClaimAsync("pat_1", "POST", "/route", "key-1", "body-A", TimeSpan.FromDays(7));
        await store.CompleteAsync("pat_1", "POST", "/route", "key-1", 201, "task_new", CancellationToken.None);

        var conflict = await store.TryClaimAsync("pat_1", "POST", "/route", "key-1", "body-B", TimeSpan.FromDays(7));
        Assert.AreEqual(ExternalIdempotencyOutcome.Conflict, conflict.Outcome);
    }

    [TestMethod]
    public async Task InFlightKey_ReturnsInProgress()
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        var store = new ExternalApiIdempotencyStore(harness.Factory);

        await store.TryClaimAsync("pat_1", "POST", "/route", "key-1", "body-A", TimeSpan.FromDays(7));

        // 未 Complete 的第二次认领 → InProgress（并发防重复）。
        var second = await store.TryClaimAsync("pat_1", "POST", "/route", "key-1", "body-A", TimeSpan.FromDays(7));
        Assert.AreEqual(ExternalIdempotencyOutcome.InProgress, second.Outcome);
    }

    [TestMethod]
    public async Task Release_AllowsRetryAfterFailure()
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        var store = new ExternalApiIdempotencyStore(harness.Factory);

        await store.TryClaimAsync("pat_1", "POST", "/route", "key-1", "body-A", TimeSpan.FromDays(7));
        await store.ReleaseAsync("pat_1", "POST", "/route", "key-1", CancellationToken.None);

        var retry = await store.TryClaimAsync("pat_1", "POST", "/route", "key-1", "body-A", TimeSpan.FromDays(7));
        Assert.AreEqual(ExternalIdempotencyOutcome.Proceed, retry.Outcome);
    }

    [TestMethod]
    public async Task KeyScopedByTokenAndRoute()
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        var store = new ExternalApiIdempotencyStore(harness.Factory);

        await store.TryClaimAsync("pat_1", "POST", "/route-a", "key-1", "body", TimeSpan.FromDays(7));

        // 不同 token 或不同 route 不冲突。
        var otherToken = await store.TryClaimAsync("pat_2", "POST", "/route-a", "key-1", "body", TimeSpan.FromDays(7));
        Assert.AreEqual(ExternalIdempotencyOutcome.Proceed, otherToken.Outcome);
        var otherRoute = await store.TryClaimAsync("pat_1", "POST", "/route-b", "key-1", "body", TimeSpan.FromDays(7));
        Assert.AreEqual(ExternalIdempotencyOutcome.Proceed, otherRoute.Outcome);
    }

    [TestMethod]
    public async Task ExpiredKeys_CleanedOnAccess()
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        var store = new ExternalApiIdempotencyStore(harness.Factory);

        await store.TryClaimAsync("pat_1", "POST", "/route", "old-key", "body", TimeSpan.FromDays(7));

        // 回填到 8 天前（超过 7 天保留期）。
        await using (var db = await harness.Factory.CreateDbContextAsync())
        {
            var row = await db.ExternalApiIdempotency.SingleAsync(x => x.TokenId == "pat_1");
            row.CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-8);
            await db.SaveChangesAsync();
        }

        var reclaimed = await store.TryClaimAsync("pat_1", "POST", "/route", "old-key", "body", TimeSpan.FromDays(7));
        Assert.AreEqual(ExternalIdempotencyOutcome.Proceed, reclaimed.Outcome, "过期 key 被清理后可重新认领");
    }
}
