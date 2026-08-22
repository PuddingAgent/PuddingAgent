using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Services.Security;

namespace PuddingPlatformTests.Security;

/// <summary>
/// ADR-075 §5.5 last-used 合并写：5 分钟窗口内不落库；窗口过后落最新值；停机 force flush。
/// </summary>
[TestClass]
public sealed class ExternalAccessTokenUsageCoalescerTests
{
    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 8, 22, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }

    [TestMethod]
    public async Task FirstWriteImmediate_SubsequentWritesAtMostOncePerInterval()
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        await harness.SeedOwnerAsync("admin");
        var service = harness.CreateService();
        var created = await service.CreateAsync(new ExternalAccessTokenCreateCommand
        {
            Name = "coalesce",
            Scopes = ["tasks.read"],
            WorkspaceIds = ["default"],
            OwnerUserId = "admin",
        });
        var tokenId = created.Value!.Item.TokenId;

        var time = new MutableTimeProvider();
        using var coalescer = new ExternalAccessTokenUsageCoalescer(harness.Store, timeProvider: time);

        var t1 = time.GetUtcNow();
        coalescer.RecordSuccess(tokenId, t1);

        // 首次落库立即执行（整个 5 分钟窗口内仅此一次写）。
        await coalescer.FlushAsync(force: false, CancellationToken.None);
        Assert.AreEqual(t1, await GetLastUsedAsync(harness, tokenId));

        // 窗口内新使用不触发第二次写。
        var t2 = t1.AddMinutes(2);
        coalescer.RecordSuccess(tokenId, t2);
        await coalescer.FlushAsync(force: false, CancellationToken.None);
        Assert.AreEqual(t1, await GetLastUsedAsync(harness, tokenId));

        // 窗口过后落最新值（t2，不是 t1）。
        time.Advance(ExternalAccessTokenUsageCoalescer.MinPersistInterval + TimeSpan.FromMinutes(1));
        await coalescer.FlushAsync(force: false, CancellationToken.None);
        Assert.AreEqual(t2, await GetLastUsedAsync(harness, tokenId));
    }

    [TestMethod]
    public async Task ForceFlush_PersistsImmediately()
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        await harness.SeedOwnerAsync("admin");
        var service = harness.CreateService();
        var created = await service.CreateAsync(new ExternalAccessTokenCreateCommand
        {
            Name = "force",
            Scopes = ["tasks.read"],
            WorkspaceIds = ["default"],
            OwnerUserId = "admin",
        });
        var tokenId = created.Value!.Item.TokenId;

        using var coalescer = new ExternalAccessTokenUsageCoalescer(
            harness.Store, timeProvider: TimeProvider.System);

        var usedAt = DateTimeOffset.UtcNow;
        coalescer.RecordSuccess(tokenId, usedAt);
        await coalescer.FlushAsync(force: true, CancellationToken.None);

        Assert.AreEqual(usedAt, await GetLastUsedAsync(harness, tokenId));
    }

    private static async Task<DateTimeOffset?> GetLastUsedAsync(
        ExternalAccessTokenTestHarness harness,
        string tokenId)
    {
        await using var db = await harness.Factory.CreateDbContextAsync();
        var entity = await db.ExternalAccessTokens.AsNoTracking()
            .FirstAsync(t => t.TokenId == tokenId);
        return entity.LastUsedAtUtc;
    }
}
