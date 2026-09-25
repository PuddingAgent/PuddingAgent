using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Supply;

namespace PuddingFullTextIndexTests;

/// <summary>
/// A7 / A8（断言）与运行期可观测性：状态机非法转换不得静默、取消必须真正传到 builder、
/// 失败必须释放租约且立即可重试、终态历史有界、运行期间后台续期。
/// </summary>
[TestClass]
public sealed class SupplyCoordinatorLifecycleTests
{
    [TestMethod]
    public void A7_Illegal_Transitions_Are_Rejected_With_Message_And_Leave_State_Unchanged()
    {
        var store = new SupplyJobStore(maxRetainedJobs: 8, maxRetainedTransitionRejections: 8);
        var now = DateTimeOffset.UtcNow;
        var entry = store.Create(new SupplyScope(@"c:\scope", @"C:\scope"), "job-1", now, null);

        Assert.IsTrue(store.TryTransition(entry, SupplyJobState.Running, "started", now));
        Assert.IsTrue(store.TryTransition(entry, SupplyJobState.Succeeded, "done", now));

        Assert.IsFalse(store.TryTransition(entry, SupplyJobState.Running, null, now), "终态 → Running 必须被拒绝");
        Assert.AreEqual(1, store.TransitionRejections.Count, "被拒的转换必须留痕（不得静默）");
        StringAssert.Contains(store.TransitionRejections[0], "Succeeded");
        StringAssert.Contains(store.TransitionRejections[0], "Running");

        Assert.IsFalse(store.TryTransition(entry, SupplyJobState.Queued, null, now), "终态 → Queued 必须被拒绝");
        Assert.AreEqual(2, store.TransitionRejections.Count);
        Assert.AreEqual(SupplyJobState.Succeeded, store.Snapshot("job-1")!.State, "被拒的转换不得改变状态");

        var fresh = store.Create(new SupplyScope(@"c:\scope", @"C:\scope"), "job-2", now, null);
        Assert.IsFalse(store.TryTransition(fresh, SupplyJobState.Succeeded, null, now), "Queued 不能直接跳到 Succeeded");
        Assert.AreEqual(3, store.TransitionRejections.Count);
    }

    [TestMethod]
    public async Task A7_Cancel_While_Running_Ends_Cancelled_And_Signals_The_Builder()
    {
        using var fixture = new TempSupplyFixture();
        var builderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var builder = new StubSupplyBuilder(async (_, ct) =>
        {
            builderStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.TrySetResult();
                throw;
            }

            return SupplyTestHelpers.Success();
        });

        var coordinator = new FullTextIndexSupplyCoordinator(new StubSupplyInventory(), builder, new FileSupplyLease(fixture.Options));

        var outcome = await coordinator.BuildAsync(new SupplyScopeRequest(fixture.Corpus));
        var scopeOutcome = SupplyTestHelpers.SingleScopeOutcome(outcome);
        Assert.AreEqual(SupplyOutcome.Started, scopeOutcome.Outcome);
        await builderStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(await coordinator.CancelAsync(scopeOutcome.JobId!), "Running 中的 job 必须可取消");

        var final = await SupplyTestHelpers.AwaitJobAsync(coordinator, scopeOutcome.JobId!);
        Assert.AreEqual(SupplyJobState.Cancelled, final.State);
        Assert.IsNotNull(final.FinishedAt, "终态必须有结束时刻");

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, builder.BuildCallCount);
        Assert.IsNull(
            await new FileSupplyLease(fixture.Options).DescribeHolderAsync(fixture.ScopeKey),
            "取消后租约必须已释放");
    }

    [TestMethod]
    public async Task A7_Cancel_On_Terminal_Or_Unknown_Job_Is_Rejected_And_Recorded()
    {
        using var fixture = new TempSupplyFixture();
        var coordinator = new FullTextIndexSupplyCoordinator(new StubSupplyInventory(), new StubSupplyBuilder(), new FileSupplyLease(fixture.Options));

        var outcome = await coordinator.BuildAsync(new SupplyScopeRequest(fixture.Corpus));
        var jobId = SupplyTestHelpers.SingleScopeOutcome(outcome).JobId!;
        var final = await SupplyTestHelpers.AwaitJobAsync(coordinator, jobId);
        Assert.AreEqual(SupplyJobState.Succeeded, final.State);

        Assert.IsFalse(await coordinator.CancelAsync(jobId), "终态 job 不可取消");
        Assert.IsFalse(await coordinator.CancelAsync("no-such-job"), "未知 job 不可取消");

        var rejections = coordinator.TransitionRejections;
        Assert.IsTrue(
            rejections.Any(r => r.Contains(jobId, StringComparison.Ordinal) && r.Contains("终态", StringComparison.Ordinal)),
            "对终态 job 的取消必须留痕并可读：" + string.Join(" | ", rejections));
        Assert.IsTrue(
            rejections.Any(r => r.Contains("no-such-job", StringComparison.Ordinal)),
            "对未知 job 的取消必须留痕：" + string.Join(" | ", rejections));
        Assert.AreEqual(SupplyJobState.Succeeded, (await coordinator.GetStatusAsync(jobId))!.State, "被拒的取消不得改变状态");
    }

    [TestMethod]
    public async Task A8_Builder_Exception_Ends_Failed_With_Reason_And_Releases_The_Lease()
    {
        using var fixture = new TempSupplyFixture();
        var builder = new StubSupplyBuilder((_, _) => throw new InvalidOperationException("stub-boom"));
        var coordinator = new FullTextIndexSupplyCoordinator(new StubSupplyInventory(), builder, new FileSupplyLease(fixture.Options));

        var firstOutcome = await coordinator.BuildAsync(new SupplyScopeRequest(fixture.Corpus));
        var first = SupplyTestHelpers.SingleScopeOutcome(firstOutcome);
        Assert.AreEqual(SupplyOutcome.Started, first.Outcome);

        var failed = await SupplyTestHelpers.AwaitJobAsync(coordinator, first.JobId!);
        Assert.AreEqual(SupplyJobState.Failed, failed.State);
        StringAssert.Contains(failed.Message!, "InvalidOperationException");
        StringAssert.Contains(failed.Message!, "stub-boom");
        Assert.IsNotNull(failed.FinishedAt);
        Assert.IsNull(
            await new FileSupplyLease(fixture.Options).DescribeHolderAsync(fixture.ScopeKey),
            "失败后租约必须释放（不得泄漏成永久 Busy）");

        // 立即可再次 Started —— 这是「租约确实已释放」的行为级证据
        var second = SupplyTestHelpers.SingleScopeOutcome(await coordinator.BuildAsync(new SupplyScopeRequest(fixture.Corpus)));
        Assert.AreEqual(SupplyOutcome.Started, second.Outcome);
        Assert.AreNotEqual(first.JobId, second.JobId);
        await SupplyTestHelpers.AwaitJobAsync(coordinator, second.JobId!);
        Assert.AreEqual(2, builder.BuildCallCount);
    }

    [TestMethod]
    public async Task A8_Failed_Build_Result_Ends_Failed_With_The_Builder_Error_Text()
    {
        using var fixture = new TempSupplyFixture();
        var builder = new StubSupplyBuilder((_, _) => Task.FromResult(SupplyTestHelpers.Failure("stub-error-text")));
        var coordinator = new FullTextIndexSupplyCoordinator(new StubSupplyInventory(), builder, new FileSupplyLease(fixture.Options));

        var outcome = await coordinator.BuildAsync(new SupplyScopeRequest(fixture.Corpus));
        var jobId = SupplyTestHelpers.SingleScopeOutcome(outcome).JobId!;

        var failed = await SupplyTestHelpers.AwaitJobAsync(coordinator, jobId);
        Assert.AreEqual(SupplyJobState.Failed, failed.State);
        StringAssert.Contains(failed.Message!, "stub-error-text");
        Assert.IsNull(await new FileSupplyLease(fixture.Options).DescribeHolderAsync(fixture.ScopeKey));
    }

    [TestMethod]
    public async Task Terminal_Job_History_Is_Bounded_And_Evicts_The_Oldest()
    {
        using var fixture = new TempSupplyFixture();
        var builder = new StubSupplyBuilder();
        var coordinator = new FullTextIndexSupplyCoordinator(
            new StubSupplyInventory(),
            builder,
            new FileSupplyLease(fixture.Options),
            new SupplyCoordinatorOptions { MaxRetainedJobs = 3, MinRebuildInterval = TimeSpan.Zero });

        var jobIds = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var outcome = await coordinator.BuildAsync(new SupplyScopeRequest(fixture.Corpus));
            var jobId = SupplyTestHelpers.SingleScopeOutcome(outcome).JobId!;
            await SupplyTestHelpers.AwaitJobAsync(coordinator, jobId);
            jobIds.Add(jobId);
        }

        Assert.AreEqual(3, (await coordinator.ListStatusAsync()).Count, "终态历史必须被有界裁剪（默认 32，此处注入 3）");
        Assert.IsNull(await coordinator.GetStatusAsync(jobIds[0]), "最旧的终态 job 必须被淘汰");
        Assert.IsNotNull(await coordinator.GetStatusAsync(jobIds[4]), "最新的 job 必须保留");
        Assert.AreEqual(5, builder.BuildCallCount);
    }

    [TestMethod]
    public async Task Status_Reports_Scope_Phase_Discovered_Inventory_And_Lease_Holder()
    {
        using var fixture = new TempSupplyFixture();
        fixture.Write("a.cs", "class Alpha { }");
        var builder = new StubSupplyBuilder();
        var coordinator = new FullTextIndexSupplyCoordinator(
            new StubSupplyInventory(fileCount: 12, totalBytes: 8192),
            builder,
            new FileSupplyLease(fixture.Options));

        var outcome = await coordinator.BuildAsync(new SupplyScopeRequest(fixture.Corpus));
        var jobId = SupplyTestHelpers.SingleScopeOutcome(outcome).JobId!;

        var final = await SupplyTestHelpers.AwaitJobAsync(coordinator, jobId);

        Assert.AreEqual(jobId, final.JobId);
        Assert.AreEqual(fixture.ScopeKey, final.ScopeKey);
        Assert.AreEqual(fixture.Corpus, final.RootPath);
        Assert.AreEqual(SupplyJobState.Succeeded, final.State);
        Assert.AreEqual(SupplyJobPhases.Completed, final.Phase);
        Assert.AreEqual(12, final.DiscoveredFileCount);
        Assert.AreEqual(8192L, final.DiscoveredBytes);
        Assert.IsTrue(final.FinishedAt >= final.StartedAt);
        Assert.IsNotNull(final.LeaseHolder, "job 状态必须能看出当时是谁持有租约");
        Assert.AreEqual(Environment.ProcessId, final.LeaseHolder!.ProcessId);

        var listed = await coordinator.ListStatusAsync();
        Assert.AreEqual(1, listed.Count);
        Assert.AreEqual(jobId, listed[0].JobId);
    }

    [TestMethod]
    public async Task Lease_Is_Renewed_While_The_Job_Runs_And_Stops_After_The_Terminal_State()
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

        var renewingLease = new RenewCountingLease(new FileSupplyLease(fixture.Options));
        var coordinator = new FullTextIndexSupplyCoordinator(
            new StubSupplyInventory(),
            builder,
            renewingLease,
            new SupplyCoordinatorOptions { LeaseRenewInterval = TimeSpan.FromMilliseconds(50) });

        var outcome = await coordinator.BuildAsync(new SupplyScopeRequest(fixture.Corpus));
        var jobId = SupplyTestHelpers.SingleScopeOutcome(outcome).JobId!;
        await builderStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (renewingLease.RenewCallCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.IsTrue(renewingLease.RenewCallCount >= 1, "长构建期间必须后台续期租约（否则会被别的进程合法接管）");

        releaseBuilder.TrySetResult();
        var final = await SupplyTestHelpers.AwaitJobAsync(coordinator, jobId);
        Assert.AreEqual(SupplyJobState.Succeeded, final.State);

        await Task.Delay(250);
        var settled = renewingLease.RenewCallCount;
        await Task.Delay(250);
        Assert.AreEqual(settled, renewingLease.RenewCallCount, "终态后必须停止续期循环");
    }
}
