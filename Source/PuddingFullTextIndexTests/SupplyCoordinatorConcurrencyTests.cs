using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Supply;

namespace PuddingFullTextIndexTests;

/// <summary>
/// A3 / A4 / A6（断言）：同任务幂等合并、进程内单写者、多 scope 互相独立。
/// <para>
/// 这些用例用 <see cref="GateOnFirstAcquireLease"/> 把「首次取租约」挂起，从而让 N 个提交**真正**同时在飞
/// （否则第一个提交会在调用方线程上同步跑完，测不出并发语义）。
/// </para>
/// </summary>
[TestClass]
public sealed class SupplyCoordinatorConcurrencyTests
{
    [TestMethod]
    public async Task A3_Two_Concurrent_Same_Scope_Requests_Run_Builder_Once_And_Share_The_Same_JobId()
    {
        using var fixture = new TempSupplyFixture();
        var builderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBuilder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = new StubSupplyBuilder(async (_, ct) =>
        {
            builderStarted.TrySetResult();
            await releaseBuilder.Task.WaitAsync(ct);
            return SupplyTestHelpers.Success();
        });

        var gatedLease = new GateOnFirstAcquireLease(new FileSupplyLease(fixture.Options));
        var coordinator = new FullTextIndexSupplyCoordinator(new StubSupplyInventory(), builder, gatedLease);

        var first = coordinator.BuildAsync(new SupplyScopeRequest(fixture.Corpus));
        await gatedLease.EnteredFirstAcquire.WaitAsync(TimeSpan.FromSeconds(10));

        var second = coordinator.BuildAsync(new SupplyScopeRequest(fixture.Corpus));
        gatedLease.ReleaseFirstAcquire();

        var outcomes = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15));

        var started = outcomes.Single(o => o.Outcome == SupplyOutcome.Started);
        var merged = outcomes.Single(o => o.Outcome == SupplyOutcome.Merged);
        Assert.IsNotNull(started.JobId);
        Assert.AreEqual(started.JobId, merged.JobId, "合并必须共享同一个 JobId");

        await builderStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, builder.BuildCallCount, "同 scope 并发提交只允许执行一次构建（builder 真正开跑后核对）");
        releaseBuilder.TrySetResult();
        var final = await SupplyTestHelpers.AwaitJobAsync(coordinator, started.JobId!);
        Assert.AreEqual(SupplyJobState.Succeeded, final.State);
    }

    [TestMethod]
    public async Task A4_Eight_Concurrent_Same_Scope_Requests_Run_Builder_Exactly_Once()
    {
        using var fixture = new TempSupplyFixture();
        var builderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBuilder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = new StubSupplyBuilder(async (_, ct) =>
        {
            builderStarted.TrySetResult();
            await releaseBuilder.Task.WaitAsync(ct);
            return SupplyTestHelpers.Success();
        });

        var gatedLease = new GateOnFirstAcquireLease(new FileSupplyLease(fixture.Options));
        var coordinator = new FullTextIndexSupplyCoordinator(new StubSupplyInventory(), builder, gatedLease);

        var first = coordinator.BuildAsync(new SupplyScopeRequest(fixture.Corpus));
        await gatedLease.EnteredFirstAcquire.WaitAsync(TimeSpan.FromSeconds(10));

        var others = Enumerable.Range(0, 7)
            .Select(_ => coordinator.BuildAsync(new SupplyScopeRequest(fixture.Corpus)))
            .ToArray();

        gatedLease.ReleaseFirstAcquire();

        var outcomes = await Task.WhenAll(others.Prepend(first)).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.AreEqual(1, outcomes.Count(o => o.Outcome == SupplyOutcome.Started));
        Assert.AreEqual(7, outcomes.Count(o => o.Outcome == SupplyOutcome.Merged));
        Assert.AreEqual(1, outcomes.Select(o => o.JobId).Distinct(StringComparer.Ordinal).Count(), "8 次请求共享同一个 JobId");

        await builderStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, builder.BuildCallCount, "N=8 并发同 scope：builder 执行次数必须为 1（builder 真正开跑后核对）");
        releaseBuilder.TrySetResult();
        await SupplyTestHelpers.AwaitJobAsync(coordinator, outcomes[0].JobId!);
    }

    [TestMethod]
    public async Task A6_Two_Different_Scopes_Build_Independently_Without_Blocking_Each_Other()
    {
        using var fixture = new TempSupplyFixture();
        var scopeA = fixture.CreateDirectory("scope-a");
        var scopeB = fixture.CreateDirectory("scope-b");

        var startedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var builder = new StubSupplyBuilder(async (scope, ct) =>
        {
            if (string.Equals(scope.RootPath, scopeA, StringComparison.OrdinalIgnoreCase))
                startedA.TrySetResult();
            else if (string.Equals(scope.RootPath, scopeB, StringComparison.OrdinalIgnoreCase))
                startedB.TrySetResult();

            await release.Task.WaitAsync(ct);
            return SupplyTestHelpers.Success();
        });

        var coordinator = new FullTextIndexSupplyCoordinator(new StubSupplyInventory(), builder, new FileSupplyLease(fixture.Options));

        var first = coordinator.BuildAsync(new SupplyScopeRequest(scopeA));
        var second = coordinator.BuildAsync(new SupplyScopeRequest(scopeB));

        var outcomes = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.AreEqual(SupplyOutcome.Started, outcomes[0].Outcome);
        Assert.AreEqual(SupplyOutcome.Started, outcomes[1].Outcome);
        Assert.AreNotEqual(outcomes[0].JobId, outcomes[1].JobId);

        // 互不阻塞的直接证据：两个 scope 的 builder 都必须在**任一方返回之前**已经开跑
        await Task.WhenAll(startedA.Task, startedB.Task).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(2, builder.BuildCallCount);

        release.TrySetResult();
        await Task.WhenAll(
            SupplyTestHelpers.AwaitJobAsync(coordinator, outcomes[0].JobId!),
            SupplyTestHelpers.AwaitJobAsync(coordinator, outcomes[1].JobId!));
    }

    [TestMethod]
    public async Task Same_Scope_Within_MinRebuild_Interval_Merges_Without_Rebuilding()
    {
        using var fixture = new TempSupplyFixture();
        var builder = new StubSupplyBuilder();
        var coordinator = new FullTextIndexSupplyCoordinator(new StubSupplyInventory(), builder, new FileSupplyLease(fixture.Options));

        var firstOutcome = await coordinator.BuildAsync(new SupplyScopeRequest(fixture.Corpus));
        var first = SupplyTestHelpers.SingleScopeOutcome(firstOutcome);
        Assert.AreEqual(SupplyOutcome.Started, first.Outcome);
        await SupplyTestHelpers.AwaitJobAsync(coordinator, first.JobId!);

        var secondOutcome = await coordinator.BuildAsync(new SupplyScopeRequest(fixture.Corpus));
        var second = SupplyTestHelpers.SingleScopeOutcome(secondOutcome);

        Assert.AreEqual(SupplyOutcome.Merged, second.Outcome);
        Assert.AreEqual(first.JobId, second.JobId);
        Assert.AreEqual(1, builder.BuildCallCount, "未过期的成功终态不得触发重复构建");
        StringAssert.Contains(second.Reason!, "成功构建");
    }

    [TestMethod]
    public async Task Same_Scope_After_MinRebuild_Interval_Starts_A_New_Job()
    {
        using var fixture = new TempSupplyFixture();
        var builder = new StubSupplyBuilder();
        var coordinator = new FullTextIndexSupplyCoordinator(
            new StubSupplyInventory(),
            builder,
            new FileSupplyLease(fixture.Options),
            new SupplyCoordinatorOptions { MinRebuildInterval = TimeSpan.Zero });

        var first = SupplyTestHelpers.SingleScopeOutcome(await coordinator.BuildAsync(new SupplyScopeRequest(fixture.Corpus)));
        await SupplyTestHelpers.AwaitJobAsync(coordinator, first.JobId!);

        var second = SupplyTestHelpers.SingleScopeOutcome(await coordinator.BuildAsync(new SupplyScopeRequest(fixture.Corpus)));

        Assert.AreEqual(SupplyOutcome.Started, second.Outcome);
        Assert.AreNotEqual(first.JobId, second.JobId);
        await SupplyTestHelpers.AwaitJobAsync(coordinator, second.JobId!);
        Assert.AreEqual(2, builder.BuildCallCount, "阈值已过 ⇒ 必须真的再构建一次");
    }
}
