using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Supply;

namespace PuddingFullTextIndexTests;

/// <summary>
/// A2a **协调器 × staged 供给** 的接线断言：预检拒绝如何反映到 job 终态（A2）、
/// Plan 报表与构建硬限是否真的同口径（R2）、job 终态消息是否带齐 R6 字段。
/// </summary>
[TestClass]
public sealed class StagedSupplyCoordinatorTests
{
    [TestMethod]
    public async Task A2_Coordinator_Rejects_Over_Budget_Without_Building_And_Live_Is_Untouched()
    {
        using var rig = new StagedRig(budgetBytes: 10_000, stagingWriteBytes: 4096);
        var coordinator = CreateCoordinator(rig, corpusBytes: 100_000);   // 预测 113000 > 预算 10000

        var outcome = await coordinator.BuildAsync(new SupplyScopeRequest(rig.Fixture.Corpus));
        Assert.AreEqual(SupplyOutcome.Started, outcome.Outcome, outcome.Reason);

        var job = await SupplyTestHelpers.AwaitJobAsync(coordinator, outcome.JobId!);

        Assert.AreEqual(SupplyJobState.Failed, job.State);
        Assert.IsNotNull(job.Message);
        StringAssert.Contains(job.Message!, "OverBudget");
        StringAssert.Contains(job.Message!, "outcome=RejectedOverBudget");
        StringAssert.Contains(job.Message!, "budgetBytes=10000");

        Assert.IsEmpty(rig.StagingEngines, "预检拒绝 ⇒ 不得创建 staging 引擎（更不得构建）");
        Assert.AreEqual(0, rig.LiveEngine.BuildCallCount);
        Assert.IsFalse(Directory.Exists(rig.LiveDirectory), "live 索引目录不得被创建");
    }

    [TestMethod]
    public async Task R2_Plan_And_The_Build_Gate_Agree_On_The_Same_Budget()
    {
        using var rig = new StagedRig(budgetBytes: 1_000_000, stagingWriteBytes: 4096);
        var coordinator = CreateCoordinator(rig, corpusBytes: 1_000);      // 预测 ceil(1000 × 1.13) = 1130

        // 别的 scope 已占 99_000 字节 live —— 集合口径的另一半
        var otherScopeLive = Path.Combine(rig.Fixture.IndexRoot, new string('b', 64));
        Directory.CreateDirectory(otherScopeLive);
        File.WriteAllBytes(Path.Combine(otherScopeLive, "segments_1"), new byte[99_000]);

        // ① 预算充足：Plan 说合规 ⇒ 构建必须真的成功
        var planRoomy = await coordinator.PlanAsync(new SupplyScopeRequest(rig.Fixture.Corpus, budgetBytes: 200_000));
        Assert.IsTrue(planRoomy.Accepted, string.Join("；", planRoomy.RejectedScopes.Select(r => r.Message)));
        Assert.IsTrue(planRoomy.AcceptedScopes[0].WithinBudget);
        Assert.AreEqual(
            99_000L,
            planRoomy.AcceptedScopes[0].LiveIndexBytes,
            "Plan 的 live 侧必须来自真正持有索引根的 builder（否则报表口径与构建口径会漂移）");

        var roomyOutcome = await coordinator.BuildAsync(new SupplyScopeRequest(rig.Fixture.Corpus, budgetBytes: 200_000));
        var roomyJob = await SupplyTestHelpers.AwaitJobAsync(coordinator, roomyOutcome.JobId!);
        Assert.AreEqual(SupplyJobState.Succeeded, roomyJob.State, roomyJob.Message);
        StringAssert.Contains(roomyJob.Message!, "outcome=Swapped");

        // ② 预算差一点：Plan 说不合规 ⇒ 构建必须被拒（同一个是非判断，不允许两套标准）
        var planCramped = await coordinator.PlanAsync(new SupplyScopeRequest(rig.Fixture.Corpus, budgetBytes: 100_000));
        Assert.IsFalse(planCramped.AcceptedScopes[0].WithinBudget, "live 103096 + 预测 1130 > 100000");
        Assert.AreEqual(103_096L, planCramped.AcceptedScopes[0].LiveIndexBytes);

        var crampedOutcome = await coordinator.BuildAsync(new SupplyScopeRequest(rig.Fixture.Corpus, budgetBytes: 100_000));
        var crampedJob = await SupplyTestHelpers.AwaitJobAsync(coordinator, crampedOutcome.JobId!);
        Assert.AreEqual(SupplyJobState.Failed, crampedJob.State);
        StringAssert.Contains(crampedJob.Message!, "OverBudget");
        StringAssert.Contains(crampedJob.Message!, "budgetBytes=100000");
    }

    [TestMethod]
    public async Task R6_Terminal_Job_Message_Carries_The_Swap_Fields()
    {
        using var rig = new StagedRig(budgetBytes: 1_000_000, stagingWriteBytes: 2048);
        var coordinator = CreateCoordinator(rig, corpusBytes: 512);

        var outcome = await coordinator.BuildAsync(new SupplyScopeRequest(rig.Fixture.Corpus));
        var job = await SupplyTestHelpers.AwaitJobAsync(coordinator, outcome.JobId!);

        Assert.AreEqual(SupplyJobState.Succeeded, job.State, job.Message);

        foreach (var field in new[]
                 {
                     "outcome=Swapped",
                     "stagingBytes=2048",
                     "liveBytesBefore=0",
                     "liveBytesAfter=2048",
                     "budgetBytes=1000000",
                 })
        {
            StringAssert.Contains(job.Message!, field, $"job 终态消息必须带 R6 字段：{field}");
        }

        var status = await coordinator.GetStatusAsync(outcome.JobId!);
        Assert.AreEqual(job.Message, status!.Message, "状态快照与终态消息必须是同一份可观察性信息");
    }

    private static FullTextIndexSupplyCoordinator CreateCoordinator(StagedRig rig, long corpusBytes) =>
        new(
            new StubSupplyInventory(fileCount: 1, totalBytes: corpusBytes),
            rig.Builder,
            new FileSupplyLease(rig.Fixture.Options),
            rig.Options with { MinRebuildInterval = TimeSpan.Zero });
}
