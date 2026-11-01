using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Services;

namespace PuddingMemoryEngineTests;

/// <summary>
/// 静默失效检测：把「表里 0 行」变成一个会被注意到的状态。
/// <para>
/// 两个方向都测 —— 只测"能报故障"会让正常状态被误报成红；
/// 只测"不误报"则退化成永远 Healthy，等于没做这件事。
/// </para>
/// </summary>
[TestClass]
public sealed class FactMemoryWriteMonitorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static FactMemoryWriteSnapshot Snapshot(
        bool enabled = true,
        long count = 10,
        DateTimeOffset? last = null,
        int staleDays = 7) =>
        new(enabled, count, last ?? Now.AddDays(-1), Now, staleDays);

    [TestMethod]
    public void Disabled_is_not_an_alarm()
    {
        var health = FactMemoryWriteMonitor.Evaluate(Snapshot(enabled: false, count: 0));

        Assert.AreEqual(FactMemoryWriteHealth.Disabled, health);
        Assert.IsFalse(FactMemoryWriteMonitor.NeedsAttention(health),
            "没开启是预期状态，报成故障会让告警失去意义");
    }

    /// <summary>这条就是今天的真实情形：开关开了、表还是 0 行。</summary>
    [TestMethod]
    public void Enabled_but_empty_is_never_written()
    {
        var health = FactMemoryWriteMonitor.Evaluate(Snapshot(count: 0));

        Assert.AreEqual(FactMemoryWriteHealth.NeverWritten, health);
        Assert.IsTrue(FactMemoryWriteMonitor.NeedsAttention(health));
    }

    [TestMethod]
    public void Recent_write_is_healthy()
    {
        var health = FactMemoryWriteMonitor.Evaluate(Snapshot(count: 3, last: Now.AddHours(-2)));

        Assert.AreEqual(FactMemoryWriteHealth.Healthy, health);
        Assert.IsFalse(FactMemoryWriteMonitor.NeedsAttention(health));
    }

    [TestMethod]
    public void Old_write_is_stalled()
    {
        var health = FactMemoryWriteMonitor.Evaluate(Snapshot(count: 3, last: Now.AddDays(-30)));

        Assert.AreEqual(FactMemoryWriteHealth.Stalled, health);
    }

    [TestMethod]
    public void Boundary_exactly_at_threshold_is_still_healthy()
    {
        var health = FactMemoryWriteMonitor.Evaluate(Snapshot(count: 3, last: Now.AddDays(-7)));

        Assert.AreEqual(FactMemoryWriteHealth.Healthy, health, "刚好等于阈值不算超时");
    }

    [TestMethod]
    public void Rows_without_timestamp_is_stalled_because_it_cannot_be_trusted()
    {
        var snapshot = new FactMemoryWriteSnapshot(true, 5, LastWrittenAt: null, Now);

        Assert.AreEqual(FactMemoryWriteHealth.Stalled, FactMemoryWriteMonitor.Evaluate(snapshot),
            "有行但说不出最后写入时间，说明观测链路本身有问题");
    }

    [TestMethod]
    public void Stale_days_below_one_is_clamped()
    {
        var snapshot = new FactMemoryWriteSnapshot(true, 5, Now.AddHours(-30), Now, StaleDays: 0);

        Assert.AreEqual(FactMemoryWriteHealth.Stalled, FactMemoryWriteMonitor.Evaluate(snapshot));
    }

    [TestMethod]
    public void Every_state_has_a_description()
    {
        foreach (var health in Enum.GetValues<FactMemoryWriteHealth>())
        {
            var text = FactMemoryWriteMonitor.Describe(health);
            Assert.IsFalse(string.IsNullOrWhiteSpace(text), $"状态 {health} 缺少说明");
        }
    }

    // ── 端到端：判定接上真实数据源（in-memory sqlite，真实 EF 查询） ──────────

    private static async Task<(TestScope Scope, FactMemoryService Service, FactMemoryWriteProbe Probe)> CreateProbeAsync()
    {
        var scope = await CreateScopeAsync();
        var service = new FactMemoryService(scope.Factory);
        return (scope, service, new FactMemoryWriteProbe(scope.Factory));
    }

    private static async Task<TestScope> CreateScopeAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MemoryLibraryDbContext>()
            .UseSqlite(connection)
            .Options;

        var factory = new TestDbContextFactory(options);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
        }

        return new TestScope(connection, factory);
    }

    [TestMethod]
    public async Task Probe_reports_never_written_on_an_empty_library()
    {
        var (scope, _, probe) = await CreateProbeAsync();
        await using var _ = scope;

        var health = await probe.CaptureAsync(writeEnabled: true, Now);

        Assert.AreEqual(FactMemoryWriteHealth.NeverWritten, health);
    }

    [TestMethod]
    public async Task Probe_reports_healthy_right_after_a_real_write()
    {
        var (scope, service, probe) = await CreateProbeAsync();
        await using var _ = scope;

        var sink = new FactMemorySink(service);
        await sink.SinkAsync(new FactMemoryDraft("ws", "agent", "一条事实", "fact", "s1"));

        // 落库时间由服务写，这里给一个足够宽的窗口以免时钟细节影响判定
        var health = await probe.CaptureAsync(writeEnabled: true, DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.AreEqual(FactMemoryWriteHealth.Healthy, health);
    }

    [TestMethod]
    public async Task Probe_reports_disabled_without_touching_the_database()
    {
        var (scope, _, probe) = await CreateProbeAsync();
        await using var _ = scope;

        var health = await probe.CaptureAsync(writeEnabled: false, Now);

        Assert.AreEqual(FactMemoryWriteHealth.Disabled, health);
    }

    private sealed class TestScope(SqliteConnection connection, IDbContextFactory<MemoryLibraryDbContext> factory)
        : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = connection;

        public IDbContextFactory<MemoryLibraryDbContext> Factory { get; } = factory;

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class TestDbContextFactory(DbContextOptions<MemoryLibraryDbContext> options)
        : IDbContextFactory<MemoryLibraryDbContext>
    {
        public MemoryLibraryDbContext CreateDbContext() => new(options);
    }
}
