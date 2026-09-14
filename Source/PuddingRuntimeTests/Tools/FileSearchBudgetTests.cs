using PuddingCode.Tools.Retrieval;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class FileSearchBudgetTests
{
    [TestMethod]
    public async Task EmptyDirectoriesConsumeBudget_WithoutClaimingNoMatch()
    {
        var root = Path.Combine(Path.GetTempPath(), $"file-search-budget-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            for (var i = 0; i < 10; i++)
                Directory.CreateDirectory(Path.Combine(root, $"empty-{i}"));
            var provider = new BuiltInRecursiveFileSearchProvider(TimeProvider.System, TimeSpan.FromSeconds(10), 3);
            var result = await provider.SearchWithCoverageAsync(root, "missing.cs", true, 30, CancellationToken.None);
            Assert.AreEqual(0, result.Paths.Count);
            Assert.AreEqual(RetrievalCoverageStatus.Truncated, result.Coverage.Status);
            Assert.IsFalse(result.Coverage.IsComplete);
            StringAssert.Contains(string.Join(' ', result.Coverage.Reasons), "3 entries");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task TimeBudgetReturnsTimeout_AndPreservesMatches()
    {
        var root = Path.Combine(Path.GetTempPath(), $"file-search-deadline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "match.cs"), "");
            var provider = new BuiltInRecursiveFileSearchProvider(new AdvancingClock(), TimeSpan.FromSeconds(2), 100);
            var result = await provider.SearchWithCoverageAsync(root, "*.cs", true, 30, CancellationToken.None);
            Assert.AreEqual(1, result.Paths.Count);
            Assert.AreEqual(RetrievalCoverageStatus.Timeout, result.Coverage.Status);
            Assert.IsFalse(result.Coverage.IsComplete);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CancelledSearchDoesNotEnumerateOrReturnSuccessfulEmptyResult()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = new BuiltInRecursiveFileSearchProvider();
        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.SearchWithCoverageAsync(
            "directory-does-not-exist", "*", true, 30, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.SearchAsync(
            "directory-does-not-exist", "*", true, 30, cancellation.Token));
    }

    [TestMethod]
    public async Task CancellationDuringTraversalIsObservedBeforeNextEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), $"file-search-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "match.cs"), "");
            using var cancellation = new CancellationTokenSource();
            var clock = new AdvancingClock { OnTick = tick => { if (tick == 2) cancellation.Cancel(); } };
            var provider = new BuiltInRecursiveFileSearchProvider(clock, TimeSpan.FromSeconds(10), 100);
            await Assert.ThrowsAsync<OperationCanceledException>(() => provider.SearchWithCoverageAsync(
                root, "*", true, 30, cancellation.Token));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class AdvancingClock : TimeProvider
    {
        private long _ticks;
        public Action<long>? OnTick { get; init; }
        public override long TimestampFrequency => 1;
        public override long GetTimestamp()
        {
            var tick = Interlocked.Increment(ref _ticks);
            OnTick?.Invoke(tick);
            return tick;
        }
    }
}
