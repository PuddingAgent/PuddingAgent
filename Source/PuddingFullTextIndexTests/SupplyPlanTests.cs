using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Supply;

namespace PuddingFullTextIndexTests;

/// <summary>
/// A2 与 R4（断言）：<c>PlanAsync</c> 必须**零写入**（不建索引目录、不写租约、不写任何文件、不触发构建），
/// 预测口径必须来自具名常量并在结果里带上所用系数，预算判定只做报表（硬限属 A2）。
/// </summary>
[TestClass]
public sealed class SupplyPlanTests
{
    [TestMethod]
    public async Task Plan_Writes_Nothing_Under_The_Index_Root()
    {
        using var fixture = new TempSupplyFixture();
        fixture.Write("a.cs", "class Alpha { }");
        fixture.Write("b.md", "# Doc");
        fixture.Write(Path.Combine("bin", "ignored.cs"), "class Ignored { }");   // 噪声目录：应被剪枝
        fixture.Write("image.png", "not indexable");
        fixture.Write("empty.cs", string.Empty);                                  // 空文件：引擎也不索引

        var builder = new StubSupplyBuilder((_, _) => throw new InvalidOperationException("Plan 不得触发构建。"));
        var coordinator = new FullTextIndexSupplyCoordinator(
            new FileSystemSupplyInventory(fixture.Options),
            builder,
            new FileSupplyLease(fixture.Options));

        Assert.IsFalse(Directory.Exists(fixture.IndexRoot), "前置：临时索引根必须一开始就不存在");
        Assert.AreEqual(0, SupplyTestHelpers.CaptureEntries(fixture.IndexRoot).Count);

        var plan = await coordinator.PlanAsync(new SupplyScopeRequest(fixture.Corpus));

        // ① 目录层面：索引根根本不能被创建
        Assert.IsFalse(Directory.Exists(fixture.IndexRoot), "Plan 不得创建索引根目录");
        // ② 条目层面：文件 + 目录的递归快照差异必须为 0（不是「没报错」）
        Assert.AreEqual(0, SupplyTestHelpers.CaptureEntries(fixture.IndexRoot).Count, "临时索引根下新增条目必须为 0");
        // ③ 租约目录同样不得出现
        Assert.IsFalse(Directory.Exists(fixture.LeaseDirectory), "Plan 不得写租约");
        // ④ 不得触发构建
        Assert.AreEqual(0, builder.BuildCallCount, "Plan 不得调用 builder");

        // Plan 仍然必须给出有意义的估算
        Assert.IsTrue(plan.Accepted, string.Join("；", plan.RejectedScopes.Select(r => r.Message)));
        Assert.AreEqual(1, plan.AcceptedScopes.Count);
        Assert.AreEqual(2, plan.AcceptedScopes[0].FileCount, "a.cs + b.md；bin 被剪枝、png 非白名单、空文件被跳过");
    }

    [TestMethod]
    public async Task Plan_Prediction_Uses_The_Documented_Index_Size_Factor()
    {
        using var fixture = new TempSupplyFixture();
        var coordinator = new FullTextIndexSupplyCoordinator(
            new StubSupplyInventory(fileCount: 10, totalBytes: 4096),
            new StubSupplyBuilder(),
            new FileSupplyLease(fixture.Options));

        var plan = await coordinator.PlanAsync(new SupplyScopeRequest(fixture.Corpus));

        // 4096 × 1.13 = 4628.48 ⇒ 向上取整 4629（系数口径见 SupplyIndexSizeEstimator 的实测注释）
        Assert.AreEqual(1.13, plan.IndexSizeFactor, 0.0, "结果必须带上所用系数");
        var scope = plan.AcceptedScopes[0];
        Assert.AreEqual(4629L, scope.PredictedIndexBytes);
        Assert.AreEqual(4096L, scope.CorpusBytes);
        Assert.AreEqual(10, scope.FileCount);
        Assert.IsTrue(scope.PredictedIndexBytes > scope.CorpusBytes, "索引体积现实地大于语料体积");
    }

    [TestMethod]
    public async Task Plan_Budget_Defaults_To_One_Gibibyte_And_Honours_Request_Override()
    {
        using var fixture = new TempSupplyFixture();
        var coordinator = new FullTextIndexSupplyCoordinator(
            new StubSupplyInventory(fileCount: 10, totalBytes: 4096),
            new StubSupplyBuilder(),
            new FileSupplyLease(fixture.Options));

        var byDefault = await coordinator.PlanAsync(new SupplyScopeRequest(fixture.Corpus));
        Assert.AreEqual(SupplyCoordinatorOptions.DefaultMaxIndexBytes, byDefault.BudgetBytes);
        Assert.AreEqual(1_073_741_824L, byDefault.BudgetBytes);
        Assert.IsTrue(byDefault.AcceptedScopes[0].WithinBudget);

        var cramped = await coordinator.PlanAsync(new SupplyScopeRequest(fixture.Corpus, budgetBytes: 1000));
        Assert.AreEqual(1000L, cramped.BudgetBytes);
        Assert.IsFalse(cramped.AcceptedScopes[0].WithinBudget, "预测 4629 > 预算 1000");
        Assert.AreEqual(4629L, cramped.AcceptedScopes[0].PredictedIndexBytes, "预算不改变预测值");
    }

    [TestMethod]
    public async Task Plan_Keeps_Accepted_Part_Visible_When_One_Scope_Is_Rejected()
    {
        using var fixture = new TempSupplyFixture();
        var missing = Path.Combine(fixture.Root, "missing-dir");
        var coordinator = new FullTextIndexSupplyCoordinator(
            new StubSupplyInventory(),
            new StubSupplyBuilder(),
            new FileSupplyLease(fixture.Options));

        var plan = await coordinator.PlanAsync(new SupplyScopeRequest(new[] { fixture.Corpus, missing }));

        Assert.IsFalse(plan.Accepted, "有拒绝项时整体不能算接受");
        Assert.AreEqual(1, plan.RejectedScopes.Count);
        Assert.AreEqual(SupplyRejectionReason.NotFound, plan.RejectedScopes[0].Reason);
        Assert.AreEqual(1, plan.AcceptedScopes.Count, "可用部分必须如实可见，不隐藏");
    }

    [TestMethod]
    public async Task Plan_Rejects_Non_Positive_Budget_At_The_Boundary()
    {
        using var fixture = new TempSupplyFixture();
        var coordinator = new FullTextIndexSupplyCoordinator(
            new StubSupplyInventory(),
            new StubSupplyBuilder(),
            new FileSupplyLease(fixture.Options));

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => coordinator.PlanAsync(new SupplyScopeRequest(fixture.Corpus, budgetBytes: 0)));
    }

    [TestMethod]
    public void Supply_Fixture_Never_Points_At_The_Real_Index_Root()
    {
        using var fixture = new TempSupplyFixture();

        Assert.IsTrue(
            fixture.IndexRoot.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
            "供给测试必须落在系统临时目录内");
        Assert.IsFalse(fixture.IndexRoot.Contains(@"D:\data", StringComparison.OrdinalIgnoreCase));
    }
}
