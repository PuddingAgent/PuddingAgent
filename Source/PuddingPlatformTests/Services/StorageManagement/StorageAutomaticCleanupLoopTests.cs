using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;
using PuddingPlatform.Services.StorageManagement;

namespace PuddingPlatformTests.Services.StorageManagement;

/// <summary>
/// 自动清理**周期驱动**的回归（2026-10-27，S1 第四部分）。
///
/// 这些断言存在的意义：tick 之前是 <c>private static readonly FromHours(6)</c>，
/// 无法配置、也无法在测试里缩短，所以「循环到底有没有按间隔真的跑起来」「取消能不能
/// 及时退出」这两件事长期只能靠等真实日志。抽成 <see cref="StorageAutomaticCleanupLoop"/>
/// 且间隔可注入之后，它们变成毫秒级的确定性断言。
/// </summary>
[TestClass]
public sealed class StorageAutomaticCleanupLoopTests
{
    [TestMethod]
    public async Task RunAsync_InvokesDryRunRepeatedlyAtTheConfiguredInterval()
    {
        var root = CreateTempRoot();
        try
        {
            var logger = new CapturingLogger<StorageAutomaticCleanupLoop>();
            var dryRunLogger = new CapturingLogger<StorageAutomaticCleanupDryRun>();
            var paths = PuddingDataPaths.FromRoot(root);
            var options = new StorageAutomaticCleanupOptions { EvaluationInterval = TimeSpan.FromMilliseconds(40) };

            var loop = new StorageAutomaticCleanupLoop(
                new StorageAutomaticCleanupDryRun(paths, dryRunLogger),
                options,
                logger);

            Assert.AreEqual(TimeSpan.FromMilliseconds(40), loop.Interval, "正间隔应原样生效");

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await loop.RunAsync(cts.Token);

            var evaluations = dryRunLogger.CountContaining("[StorageMaintenance:Auto]");
            Assert.IsTrue(
                evaluations >= 2,
                $"250ms 内、间隔 40ms 应至少跑 2 轮，实际 {evaluations} 轮（日志共 {dryRunLogger.Messages.Count} 条）");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public void Constructor_NonPositiveInterval_ClampsInsteadOfHotSpinning()
    {
        var root = CreateTempRoot();
        try
        {
            var logger = new CapturingLogger<StorageAutomaticCleanupLoop>();
            var loop = new StorageAutomaticCleanupLoop(
                new StorageAutomaticCleanupDryRun(PuddingDataPaths.FromRoot(root), new CapturingLogger<StorageAutomaticCleanupDryRun>()),
                new StorageAutomaticCleanupOptions { EvaluationInterval = TimeSpan.Zero },
                logger);

            Assert.AreEqual(TimeSpan.FromMinutes(1), loop.Interval, "0 间隔必须被夹紧，否则是热自旋");
            Assert.IsTrue(
                logger.CountContaining("clamped") >= 1,
                "夹紧必须留日志，否则配置写错时没人知道它没按预期生效");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task RunAsync_LongInterval_CancelsPromptly()
    {
        var root = CreateTempRoot();
        try
        {
            var loop = new StorageAutomaticCleanupLoop(
                new StorageAutomaticCleanupDryRun(PuddingDataPaths.FromRoot(root), new CapturingLogger<StorageAutomaticCleanupDryRun>()),
                new StorageAutomaticCleanupOptions { EvaluationInterval = TimeSpan.FromMinutes(10) },
                new CapturingLogger<StorageAutomaticCleanupLoop>());

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            var run = loop.RunAsync(cts.Token);

            // 10 分钟的间隔必须在取消时立刻放手：等不到就说明 Task.Delay 没接 token，
            // 宿主 StopAsync 会被拖住。
            var completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.AreSame(run, completed, "取消后 RunAsync 必须在 5 秒内返回");
            await run;
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---- helpers -----------------------------------------------------------

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-loop-" + Guid.NewGuid().ToString("N"));
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

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages => _messages;

        public int CountContaining(string fragment) =>
            _messages.Count(message => message.Contains(fragment, StringComparison.Ordinal));

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
