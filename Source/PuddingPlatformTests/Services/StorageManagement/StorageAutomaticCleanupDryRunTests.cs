using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;
using PuddingCode.Storage;
using PuddingPlatform.Services.StorageManagement;

namespace PuddingPlatformTests.Services.StorageManagement;

/// <summary>
/// 自动清理**试运行**的回归（2026-10-27，S1 第三部分）。
///
/// 为什么值得单独测：这段逻辑一旦接线到周期 tick，就会每天自动跑。
/// 它必须做到三件事——(1) 只记录不删、(2) 绝不建作业、(3) 跳过原因如实上报。
/// 其中 (2) 由结构保证（<see cref="StorageAutomaticCleanupDryRun"/> 不持有作业存储、
/// 也没有任何作业类型可用），测试在这里额外断言它**不写盘**，把「不删数据」也钉住。
/// </summary>
[TestClass]
public sealed class StorageAutomaticCleanupDryRunTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task ExecuteAsync_NoCandidates_ReportsZeroAndCountsEverySkipReason()
    {
        var logger = new CapturingLogger();
        var drill = new StorageAutomaticCleanupDryRun(CreatePaths(), logger);

        var result = await drill.ExecuteAsync(
            Now,
            CancellationToken.None,
            [
                Definition("protected-one", StorageSafetyLevel.Evidence, retentionDays: 7),
                Definition("disabled-one", StorageSafetyLevel.Disposable, retentionDays: 7, automaticAllowed: false),
                Definition("rollup-one", StorageSafetyLevel.Disposable, retentionDays: 7, requiresRollup: true),
            ]);

        Assert.AreEqual(3, result.Evaluated, "三个数据类都应被评估");
        Assert.AreEqual(0, result.Entries.Count, "没有任何候选时不应产出条目");
        Assert.AreEqual(1, result.SkippedByReason["Protected"]);
        Assert.AreEqual(1, result.SkippedByReason["AutomaticDisabled"]);
        Assert.AreEqual(1, result.SkippedByReason["RequiresRollup"]);
        Assert.IsTrue(
            logger.Contains("candidates=0"),
            "零候选也必须留下日志，否则运维无法判断它跑过");
    }

    [TestMethod]
    public async Task ExecuteAsync_CandidateWithoutDatabaseFile_CountsZeroRowsAndNeverReportsAJob()
    {
        var root = CreateTempRoot();
        try
        {
            var logger = new CapturingLogger();
            var drill = new StorageAutomaticCleanupDryRun(PuddingDataPaths.FromRoot(root), logger);

            var result = await drill.ExecuteAsync(
                Now,
                CancellationToken.None,
                [Definition("runtime", StorageSafetyLevel.Disposable, retentionDays: 14, withTable: true)]);

            Assert.AreEqual(1, result.Evaluated);
            Assert.AreEqual(1, result.Entries.Count);
            var entry = result.Entries[0];
            Assert.AreEqual("runtime", entry.TargetId);
            Assert.AreEqual(14, entry.RetentionDays);
            Assert.AreEqual(Now.AddDays(-14), entry.CutoffUtc, "cutoff 必须由显式 nowUtc 推出，不读时钟");
            Assert.AreEqual(0L, entry.Candidates, "数据库文件不存在时行数只能是 0");
            Assert.IsFalse(entry.Truncated);
            Assert.IsTrue(logger.Contains("jobCreated=False"), "日志必须明说没有创建作业");

            // 「不删数据」的可观测形式：试运行不往数据根写任何东西。
            var written = Directory.Exists(root)
                ? Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                : [];
            Assert.AreEqual(0, written.Length, "试运行不应写盘");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_RealCatalog_EntriesPlusSkippedEqualsEvaluated()
    {
        var root = CreateTempRoot();
        try
        {
            var logger = new CapturingLogger();
            var drill = new StorageAutomaticCleanupDryRun(PuddingDataPaths.FromRoot(root), logger);

            // definitions 传 null → 使用生产目录。断言的是**守恒关系**而不是具体数量，
            // 这样目录新增数据类时测试不会假失败，但「漏报/重报」仍然会被抓到。
            var result = await drill.ExecuteAsync(Now, CancellationToken.None);

            var skippedTotal = result.SkippedByReason.Values.Sum();
            Assert.IsTrue(result.Evaluated > 0, "生产目录不应为空");
            Assert.AreEqual(
                result.Evaluated,
                result.Entries.Count + skippedTotal,
                "每个被评估的数据类必须恰好落在「候选」或某个跳过原因里");
            Assert.IsTrue(
                result.SkippedByReason.ContainsKey(nameof(StorageAutomaticCleanupPolicy.SkipReason.RequiresRollup)),
                "遥测/上下文指标依赖聚合，必须如实报为 RequiresRollup 而不是当成缺陷");
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- helpers -----------------------------------------------------------

    private static PuddingDataPaths CreatePaths() =>
        PuddingDataPaths.FromRoot(CreateTempRoot());

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-dryrun-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不应让测试变红。
        }
    }

    private static StorageDataClassCatalog.StorageDataClassDefinition Definition(
        string targetId,
        StorageSafetyLevel safetyLevel,
        int? retentionDays,
        bool automaticAllowed = true,
        bool requiresRollup = false,
        bool withTable = false) => new()
        {
            TargetId = targetId,
            DisplayName = targetId,
            Description = targetId,
            SafetyLevel = safetyLevel,
            DatabaseFile = withTable ? StorageDataClassCatalog.PlatformDatabaseFile : null,
            Tables = withTable
                ?
                [
                    new StorageDataClassCatalog.StoragePhysicalTable
                    {
                        Table = "runtime_activity",
                        TimestampColumn = "started_at_utc",
                    },
                ]
                : [],
            DefaultRetentionDays = retentionDays,
            MinRetentionDays = 1,
            MaxRetentionDays = 365,
            AutomaticCleanupAllowed = automaticAllowed,
            RequiresRollupBeforeAutomatic = requiresRollup,
        };

    private sealed class CapturingLogger : ILogger<StorageAutomaticCleanupDryRun>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages => _messages;

        public bool Contains(string fragment) =>
            _messages.Any(message => message.Contains(fragment, StringComparison.Ordinal));

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
