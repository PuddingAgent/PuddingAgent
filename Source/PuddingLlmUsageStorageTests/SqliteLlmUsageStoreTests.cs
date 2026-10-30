using PuddingLlmUsage;
using PuddingLlmUsage.Contracts;
using PuddingLlmUsage.Storage;

namespace PuddingLlmUsageStorageTests;

/// <summary>
/// 存储适配器契约。每条都在守一个「不能失真」的点：
/// 金额精度、失败可见、缺口可见、空采样不算成功。
/// </summary>
[TestClass]
public sealed class SqliteLlmUsageStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);

    private string _root = string.Empty;
    private string _dbPath = string.Empty;
    private SqliteLlmUsageStore _store = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-llm-usage-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "llm_usage.db");
        _store = new SqliteLlmUsageStore(_dbPath);
        await _store.InitializeAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 测试清理失败不影响结论。
        }
    }

    private static DateTimeOffset At(int minutes) => T0.AddMinutes(minutes);

    private static LlmBalanceObservation Ok(int minutes, decimal total, decimal? granted = null, decimal? toppedUp = null) =>
        LlmBalanceObservation.Success("deepseek", "CNY", total, At(minutes), granted, toppedUp);

    [TestMethod]
    public async Task RoundTrip_KeepsDecimalPrecision()
    {
        var attemptId = await _store.BeginAttemptAsync("deepseek", At(0));
        await _store.CompleteAttemptAsync(attemptId, At(0), [Ok(0, 248.55m, 0.00m, 248.55m)]);

        var observations = await _store.ReadObservationsAsync("deepseek", At(-1), At(1));

        Assert.AreEqual(1, observations.Count);
        Assert.AreEqual(248.55m, observations[0].TotalBalance, "金额必须精确往返；用 REAL 存会引入末位误差。");
        Assert.AreEqual(0.00m, observations[0].GrantedBalance);
        Assert.AreEqual(248.55m, observations[0].ToppedUpBalance);
        Assert.IsTrue(observations[0].Succeeded);
    }

    [TestMethod]
    public async Task MissingAmounts_StayNull_AfterRoundTrip()
    {
        var attemptId = await _store.BeginAttemptAsync("deepseek", At(0));
        await _store.CompleteAttemptAsync(attemptId, At(0), [Ok(0, 10.00m)]);

        var observation = (await _store.ReadObservationsAsync("deepseek", At(-1), At(1))).Single();

        Assert.AreEqual(10.00m, observation.TotalBalance);
        Assert.IsNull(observation.GrantedBalance, "写入时没给的金额，读回后仍必须是 null。");
        Assert.IsNull(observation.ToppedUpBalance);
    }

    [TestMethod]
    public async Task FailedAttempt_SurfacesAsFailureObservation()
    {
        var attemptId = await _store.BeginAttemptAsync("deepseek", At(0));
        await _store.CompleteAttemptAsync(
            attemptId,
            At(0),
            [LlmBalanceObservation.Failure("deepseek", "unknown", "HTTP 500", At(0))]);

        var observation = (await _store.ReadObservationsAsync("deepseek", At(-1), At(1))).Single();

        Assert.IsFalse(observation.Succeeded, "失败的尝试必须能被读回来，否则曲线会掩盖缺口。");
        StringAssert.Contains(observation.Error!, "HTTP 500");
        Assert.AreEqual(UnknownCurrency.Value, observation.Currency);
    }

    [TestMethod]
    public async Task EmptyObservationList_IsRecordedAsFailure_NotSuccess()
    {
        var attemptId = await _store.BeginAttemptAsync("deepseek", At(0));
        await _store.CompleteAttemptAsync(attemptId, At(0), []);

        var observations = await _store.ReadObservationsAsync("deepseek", At(-1), At(1));

        Assert.AreEqual(1, observations.Count);
        Assert.IsFalse(observations[0].Succeeded, "「什么都没采到」不能被记成一次成功的空采样。");
    }

    [TestMethod]
    public async Task StaleRunningAttempt_BecomesInterrupted_AndIsVisible()
    {
        // 模拟进程在请求途中被杀：只有 Started，没有 Complete。
        await _store.BeginAttemptAsync("deepseek", At(0));

        var rewritten = await _store.MarkRunningAttemptsInterruptedAsync(At(5));
        Assert.AreEqual(1, rewritten, "遗留的 Running 必须被改写，否则它会永远悬着。");

        var observation = (await _store.ReadObservationsAsync("deepseek", At(-1), At(10))).Single();
        Assert.IsFalse(observation.Succeeded);
        StringAssert.Contains(observation.Error!, "异常退出");
    }

    [TestMethod]
    public async Task SecondInterruptionSweep_IsNoOp()
    {
        await _store.BeginAttemptAsync("deepseek", At(0));
        Assert.AreEqual(1, await _store.MarkRunningAttemptsInterruptedAsync(At(5)));
        Assert.AreEqual(0, await _store.MarkRunningAttemptsInterruptedAsync(At(6)), "已改写的行不该被再次处理。");
    }

    [TestMethod]
    public async Task ReadRespectsTimeWindow()
    {
        foreach (var minute in new[] { 0, 10, 20, 30 })
        {
            var id = await _store.BeginAttemptAsync("deepseek", At(minute));
            await _store.CompleteAttemptAsync(id, At(minute), [Ok(minute, 100m - minute)]);
        }

        var observations = await _store.ReadObservationsAsync("deepseek", At(10), At(20));

        Assert.AreEqual(2, observations.Count);
        Assert.AreEqual(At(10), observations[0].ObservedAtUtc);
        Assert.AreEqual(At(20), observations[1].ObservedAtUtc);
    }

    [TestMethod]
    public async Task DifferentProviders_DoNotLeakIntoEachOther()
    {
        var a = await _store.BeginAttemptAsync("deepseek", At(0));
        await _store.CompleteAttemptAsync(a, At(0), [Ok(0, 100m)]);
        var b = await _store.BeginAttemptAsync("bigmodel", At(0));
        await _store.CompleteAttemptAsync(b, At(0),
            [LlmBalanceObservation.Success("bigmodel", "CNY", 900m, At(0))]);

        var deepseek = await _store.ReadObservationsAsync("deepseek", At(-1), At(1));
        var bigmodel = await _store.ReadObservationsAsync("bigmodel", At(-1), At(1));

        Assert.AreEqual(1, deepseek.Count);
        Assert.AreEqual(100m, deepseek[0].TotalBalance);
        Assert.AreEqual(1, bigmodel.Count);
        Assert.AreEqual(900m, bigmodel[0].TotalBalance);
    }

    [TestMethod]
    public async Task InvertedWindow_IsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.ReadObservationsAsync("deepseek", At(10), At(0)));
    }

    [TestMethod]
    public async Task StoredObservations_FeedTheAnalyzerEndToEnd()
    {
        // 这条是存储层存在的理由：写进去的两点，必须能被分析器直接判成「消耗 0.5」。
        var first = await _store.BeginAttemptAsync("deepseek", At(0));
        await _store.CompleteAttemptAsync(first, At(0), [Ok(0, 248.55m)]);
        var second = await _store.BeginAttemptAsync("deepseek", At(10));
        await _store.CompleteAttemptAsync(second, At(10), [Ok(10, 248.05m)]);

        var observations = await _store.ReadObservationsAsync("deepseek", At(-1), At(11));
        var intervals = LlmUsageAnalyzer.Analyze(observations);

        Assert.AreEqual(1, intervals.Count);
        Assert.AreEqual(LlmUsageIntervalKind.Consumption, intervals[0].Kind);
        Assert.AreEqual(0.5m, intervals[0].Consumption);
    }

    [TestMethod]
    public async Task Initialize_IsIdempotent_AndReopenWorks()
    {
        await _store.InitializeAsync();
        await _store.InitializeAsync();

        var id = await _store.BeginAttemptAsync("deepseek", At(0));
        await _store.CompleteAttemptAsync(id, At(0), [Ok(0, 5m)]);

        var reopened = new SqliteLlmUsageStore(_dbPath);
        await reopened.InitializeAsync();
        var observations = await reopened.ReadObservationsAsync("deepseek", At(-1), At(1));

        Assert.AreEqual(1, observations.Count, "重开同一个库必须能看到已写入的数据。");
        Assert.AreEqual(5m, observations[0].TotalBalance);
    }

    [TestMethod]
    public void Constructor_RejectsEmptyPath()
    {
        Assert.Throws<ArgumentException>(() => new SqliteLlmUsageStore("  "));
    }
}
