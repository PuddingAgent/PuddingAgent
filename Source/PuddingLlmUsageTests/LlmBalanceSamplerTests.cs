using PuddingLlmUsage;
using PuddingLlmUsage.Contracts;

namespace PuddingLlmUsageTests;

/// <summary>
/// 采样编排：一轮从「落 Started」到「落结果」的全过程。
/// 每条都守一个"不能静默失败"的点：存储炸了不许白查询、端口抛了不许停摆、取消必须穿透。
/// </summary>
[TestClass]
public sealed class LlmBalanceSamplerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 10, 10, 0, TimeSpan.Zero);

    private sealed class FakeProbe(Func<string, DateTimeOffset, Task<IReadOnlyList<LlmBalanceObservation>>> impl)
        : ILlmBalanceProbe
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<LlmBalanceObservation>> QueryAsync(
            string providerId,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return impl(providerId, observedAtUtc);
        }
    }

    private sealed class FakeStore : ILlmUsageStore
    {
        public List<string> Trace { get; } = [];

        public List<IReadOnlyList<LlmBalanceObservation>> Completed { get; } = [];

        public Func<Exception>? BeginThrows { get; init; }

        public Func<Exception>? CompleteThrows { get; init; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<long> BeginAttemptAsync(
            string providerId,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken = default)
        {
            if (BeginThrows is not null)
                throw BeginThrows();

            Trace.Add($"begin:{providerId}");
            return Task.FromResult(1L);
        }

        public Task CompleteAttemptAsync(
            long attemptId,
            DateTimeOffset completedAtUtc,
            IReadOnlyList<LlmBalanceObservation> observations,
            CancellationToken cancellationToken = default)
        {
            if (CompleteThrows is not null)
                throw CompleteThrows();

            Trace.Add($"complete:{attemptId}");
            Completed.Add(observations);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LlmBalanceObservation>> ReadObservationsAsync(
            string providerId,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LlmBalanceObservation>>([]);

        public Task<int> MarkRunningAttemptsInterruptedAsync(
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    [TestMethod]
    public async Task Success_WritesStartedThenCompleted_AndReportsSuccess()
    {
        var probe = new FakeProbe((id, at) => Task.FromResult<IReadOnlyList<LlmBalanceObservation>>(
            [LlmBalanceObservation.Success(id, "CNY", 248.55m, at)]));
        var store = new FakeStore();

        var outcome = await new LlmBalanceSampler(probe, store).RunOnceAsync("deepseek", Now);

        Assert.IsTrue(outcome.Succeeded);
        Assert.AreEqual(1, outcome.ObservationCount);
        Assert.IsNull(outcome.Error);
        Assert.IsFalse(outcome.StoreFailed);
        CollectionAssert.AreEqual(new[] { "begin:deepseek", "complete:1" }, store.Trace,
            "必须先落 Started 再落结果，顺序不能反。");
    }

    [TestMethod]
    public async Task ProbeThrows_IsRecordedAsFailure_AndDoesNotEscape()
    {
        var probe = new FakeProbe((_, _) => throw new InvalidOperationException("boom"));
        var store = new FakeStore();

        var outcome = await new LlmBalanceSampler(probe, store).RunOnceAsync("deepseek", Now);

        Assert.IsFalse(outcome.Succeeded, "端口抛异常不能被当成成功。");
        StringAssert.Contains(outcome.Error!, "boom");
        Assert.AreEqual(1, store.Completed.Count);
        Assert.IsFalse(store.Completed[0][0].Succeeded, "异常必须落成一条 Failure 观测。");
    }

    [TestMethod]
    public async Task StoreBeginFails_SkipsExternalQueryEntirely()
    {
        // 查到的结果无处安放，就不该去查 —— 白白消耗额度还制造噪声。
        var probe = new FakeProbe((id, at) => Task.FromResult<IReadOnlyList<LlmBalanceObservation>>(
            [LlmBalanceObservation.Success(id, "CNY", 1m, at)]));
        var store = new FakeStore { BeginThrows = () => new InvalidOperationException("db locked") };

        var outcome = await new LlmBalanceSampler(probe, store).RunOnceAsync("deepseek", Now);

        Assert.IsTrue(outcome.StoreFailed);
        Assert.AreEqual(0, probe.Calls, "存储不可写时不得发起外部查询。");
        StringAssert.Contains(outcome.Error!, "db locked");
    }

    [TestMethod]
    public async Task StoreCompleteFails_ReportsStoreFailure_ButKeepsQueryResult()
    {
        var probe = new FakeProbe((id, at) => Task.FromResult<IReadOnlyList<LlmBalanceObservation>>(
            [LlmBalanceObservation.Success(id, "CNY", 248.55m, at)]));
        var store = new FakeStore { CompleteThrows = () => new InvalidOperationException("disk full") };

        var outcome = await new LlmBalanceSampler(probe, store).RunOnceAsync("deepseek", Now);

        Assert.IsTrue(outcome.StoreFailed);
        Assert.IsTrue(outcome.Succeeded, "查询本身成功了，这个事实仍要如实上报。");
        StringAssert.Contains(outcome.Error!, "disk full");
    }

    [TestMethod]
    public async Task Cancellation_Propagates()
    {
        var probe = new FakeProbe((_, _) => throw new OperationCanceledException());
        var store = new FakeStore();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new LlmBalanceSampler(probe, store).RunOnceAsync("deepseek", Now, cts.Token));
    }

    [TestMethod]
    public async Task Round_RunsProvidersSerially_InConfiguredOrder()
    {
        var probe = new FakeProbe((id, at) => Task.FromResult<IReadOnlyList<LlmBalanceObservation>>(
            [LlmBalanceObservation.Success(id, "CNY", 1m, at)]));
        var store = new FakeStore();
        var options = new LlmUsageSamplingOptions(
            true, ["deepseek", "bigmodel"], TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(20)).Validate();

        var outcomes = await new LlmBalanceSampler(probe, store).RunRoundAsync(options, Now);

        Assert.AreEqual(2, outcomes.Count);
        Assert.AreEqual("deepseek", outcomes[0].ProviderId);
        Assert.AreEqual("bigmodel", outcomes[1].ProviderId);
        CollectionAssert.AreEqual(
            new[] { "begin:deepseek", "complete:1", "begin:bigmodel", "complete:1" },
            store.Trace,
            "同一轮内必须串行：并发打同一上游端点没有收益，只让失败更难归因。");
    }

    [TestMethod]
    public async Task Round_Disabled_DoesNothing()
    {
        var probe = new FakeProbe((_, _) => Task.FromResult<IReadOnlyList<LlmBalanceObservation>>([]));
        var store = new FakeStore();
        var options = new LlmUsageSamplingOptions(
            false, ["deepseek"], TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(20)).Validate();

        var outcomes = await new LlmBalanceSampler(probe, store).RunRoundAsync(options, Now);

        Assert.AreEqual(0, outcomes.Count);
        Assert.AreEqual(0, probe.Calls);
        Assert.AreEqual(0, store.Trace.Count);
    }

    // ── 节奏 ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void NextSlot_AlignsToUtcBoundary()
    {
        var ten = TimeSpan.FromMinutes(10);

        Assert.AreEqual(
            new DateTimeOffset(2026, 10, 30, 10, 20, 0, TimeSpan.Zero),
            LlmSamplingSchedule.NextSlot(new DateTimeOffset(2026, 10, 30, 10, 13, 7, TimeSpan.Zero), ten));
    }

    [TestMethod]
    public void NextSlot_WhenExactlyOnSlot_ReturnsFollowingSlot()
    {
        // 恰好对齐时若返回它自己，会造成「刚采完」与「下一次」指向同一时刻的零间隔连采。
        var ten = TimeSpan.FromMinutes(10);

        Assert.AreEqual(
            new DateTimeOffset(2026, 10, 30, 10, 20, 0, TimeSpan.Zero),
            LlmSamplingSchedule.NextSlot(new DateTimeOffset(2026, 10, 30, 10, 10, 0, TimeSpan.Zero), ten));
    }

    [TestMethod]
    public void DelayUntilNextSlot_IsAlwaysPositive()
    {
        var ten = TimeSpan.FromMinutes(10);

        var delay = LlmSamplingSchedule.DelayUntilNextSlot(
            new DateTimeOffset(2026, 10, 30, 10, 10, 0, TimeSpan.Zero), ten);

        Assert.IsTrue(delay > TimeSpan.Zero, "延迟必须为正，否则会退化成忙等。");
        Assert.AreEqual(TimeSpan.FromMinutes(10), delay);
    }

    [TestMethod]
    public void IsOnSlot_DetectsAlignedInstants()
    {
        var ten = TimeSpan.FromMinutes(10);

        Assert.IsTrue(LlmSamplingSchedule.IsOnSlot(new DateTimeOffset(2026, 10, 30, 10, 20, 0, TimeSpan.Zero), ten));
        Assert.IsFalse(LlmSamplingSchedule.IsOnSlot(new DateTimeOffset(2026, 10, 30, 10, 21, 0, TimeSpan.Zero), ten));
    }

    [TestMethod]
    public void NextSlot_RejectsNonPositiveInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LlmSamplingSchedule.NextSlot(Now, TimeSpan.Zero));
    }

    // ── 配置 ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Options_NormalizeProviderIds()
    {
        var options = new LlmUsageSamplingOptions(
            true, ["  DeepSeek ", "deepseek", "", "bigmodel"], TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(20))
            .Validate();

        CollectionAssert.AreEqual(new[] { "deepseek", "bigmodel" }, options.ProviderIds.ToArray());
    }

    [TestMethod]
    public void Options_RejectTimeoutNotSmallerThanInterval()
    {
        Assert.Throws<ArgumentException>(() => new LlmUsageSamplingOptions(
            true, ["deepseek"], TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)).Validate());
    }

    [TestMethod]
    public void Options_RejectEnabledWithNoProviders()
    {
        Assert.Throws<ArgumentException>(() => new LlmUsageSamplingOptions(
            true, ["  "], TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(20)).Validate());
    }

    [TestMethod]
    public void Options_AllowDisabledWithNoProviders()
    {
        var options = new LlmUsageSamplingOptions(
            false, [], TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(20)).Validate();

        Assert.IsFalse(options.Enabled);
        Assert.AreEqual(0, options.ProviderIds.Count);
    }
}
