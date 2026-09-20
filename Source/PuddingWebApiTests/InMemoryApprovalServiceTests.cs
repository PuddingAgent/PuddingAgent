using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Platform;
using PuddingController.Services;

namespace PuddingWebApiTests;

/// <summary>
/// <see cref="InMemoryApprovalService"/> 的直接单元测试（不起 HTTP）。
/// </summary>
/// <remarks>
/// 审批服务改为进程内实现（迭代 #15）后，这些行为首次可以直接验证，不必再
/// mock Redis。回收测试靠构造**短有效期实例**完成，因此不需要让真实时钟推进 24h。
/// </remarks>
[TestClass]
public sealed class InMemoryApprovalServiceTests
{
    [TestMethod]
    public async Task Active_Record_Is_Listed_And_Retrievable()
    {
        var service = new InMemoryApprovalService(TimeSpan.FromHours(1));
        var record = await service.RequestApprovalAsync("s", "w", "action");

        var pending = await service.QueryPendingAsync();
        Assert.IsTrue(
            pending.Any(p => p.ApprovalId == record.ApprovalId),
            "未过期且仍 Pending 的审批单必须在待审列表中。");

        var fetched = await service.GetAsync(record.ApprovalId);
        Assert.IsNotNull(fetched);
        Assert.AreEqual(ApprovalStatus.Pending, fetched!.Status);
        Assert.AreEqual(0, fetched.FailedAttempts, "初始失败计数必须为 0。");
    }

    [TestMethod]
    public async Task Expired_Record_Is_Pruned_On_Query_And_No_Longer_Retrievable()
    {
        // 用 50ms 有效期代替 24h，避免测试等待真实时钟。
        var service = new InMemoryApprovalService(TimeSpan.FromMilliseconds(50));
        var record = await service.RequestApprovalAsync("s", "w", "action");

        Assert.IsNotNull(await service.GetAsync(record.ApprovalId), "未过期时应当可读。");

        await Task.Delay(150);

        var pending = await service.QueryPendingAsync();
        Assert.AreEqual(0, pending.Count, "过期审批单不得出现在待审列表中。");
        Assert.IsNull(
            await service.GetAsync(record.ApprovalId),
            "过期记录必须在查询时被回收——否则字典会随运行时长单调增长。");

        // 回收是幂等的：再次查询不应抛错，也不应让记录复活。
        Assert.AreEqual(0, (await service.QueryPendingAsync()).Count);
        Assert.IsNull(await service.GetAsync(record.ApprovalId));
    }

    [TestMethod]
    public async Task Expired_Record_Cannot_Be_Confirmed_Even_With_Correct_Code()
    {
        var service = new InMemoryApprovalService(TimeSpan.FromMilliseconds(50));
        var record = await service.RequestApprovalAsync("s", "w", "action");

        await Task.Delay(150);

        var ok = await service.ConfirmAsync(record.ApprovalId, record.ConfirmationCode!, "tester");

        Assert.IsFalse(ok, "过期审批单即使确认码正确也必须拒绝。");
        Assert.AreEqual(
            ApprovalStatus.Expired,
            (await service.GetAsync(record.ApprovalId))!.Status,
            "被拒绝的过期单应落到 Expired 终态。");
    }

    [TestMethod]
    public async Task Write_Path_Also_Prunes_Expired_Records()
    {
        var service = new InMemoryApprovalService(TimeSpan.FromMilliseconds(50));
        var stale = await service.RequestApprovalAsync("s", "w", "old");

        await Task.Delay(150);

        // 刻意不经过查询，直接写入新单——写入路径同样应顺带回收。
        var fresh = await service.RequestApprovalAsync("s", "w", "new");

        Assert.IsNull(await service.GetAsync(stale.ApprovalId), "写入路径也应回收过期记录。");
        Assert.IsNotNull(await service.GetAsync(fresh.ApprovalId), "新单不得被误回收。");
    }
}
