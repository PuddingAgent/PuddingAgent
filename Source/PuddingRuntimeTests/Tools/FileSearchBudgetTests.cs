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
            var result = await provider.SearchWithCoverageAsync(root, "missing.cs", true, 30, false, CancellationToken.None);
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
            var result = await provider.SearchWithCoverageAsync(root, "*.cs", true, 30, false, CancellationToken.None);
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
            "directory-does-not-exist", "*", true, 30, false, cancellation.Token));
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
                root, "*", true, 30, false, cancellation.Token));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>
    /// 同一棵目录树、同一份条目预算，只改 excludeNoiseDirectories：
    /// 对照组被 bin 吃满预算 → 无法证实完整；处理组跳过 bin → 枚举自然完成。
    /// 这是“噪声目录不得占用枚举预算”的直接判据（2026-11-15：本仓 Source 958,920 条中 97.2% 是噪声，
    /// 基线枚举永远撞 50,000 上限 → 每次搜索都报 partial）。
    /// </summary>
    [TestMethod]
    public async Task NoiseDirectoriesArePrunedFromTheEnumerationBudget()
    {
        var root = Path.Combine(Path.GetTempPath(), $"file-search-prune-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "a.cs"), "");
            await File.WriteAllTextAsync(Path.Combine(root, "b.cs"), "");
            var bin = Directory.CreateDirectory(Path.Combine(root, "bin"));
            for (var i = 0; i < 10; i++)
                await File.WriteAllTextAsync(Path.Combine(bin.FullName, $"n{i}.cs"), "");

            var provider = new BuiltInRecursiveFileSearchProvider(TimeProvider.System, TimeSpan.FromSeconds(10), 5);

            var unpruned = await provider.SearchWithCoverageAsync(root, "*.cs", true, 30, false, CancellationToken.None);
            Assert.AreEqual(RetrievalCoverageStatus.Truncated, unpruned.Coverage.Status);

            var pruned = await provider.SearchWithCoverageAsync(root, "*.cs", true, 30, true, CancellationToken.None);
            Assert.AreEqual(RetrievalCoverageStatus.Complete, pruned.Coverage.Status);
            CollectionAssert.AreEquivalent(
                new[] { Path.Combine(root, "a.cs"), Path.Combine(root, "b.cs") },
                pruned.Paths.ToArray());
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>ls -a 通道：不跳过时噪声目录内的文件必须仍可被找到（剪枝是 opt-in）。</summary>
    [TestMethod]
    public async Task NoiseDirectoriesRemainSearchableWhenNotPruned()
    {
        var root = Path.Combine(Path.GetTempPath(), $"file-search-lsa-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "a.cs"), "");
            var bin = Directory.CreateDirectory(Path.Combine(root, "bin"));
            for (var i = 0; i < 10; i++)
                await File.WriteAllTextAsync(Path.Combine(bin.FullName, $"n{i}.cs"), "");

            var provider = new BuiltInRecursiveFileSearchProvider(TimeProvider.System, TimeSpan.FromSeconds(10), 500);
            var result = await provider.SearchWithCoverageAsync(root, "*.cs", true, 30, false, CancellationToken.None);

            Assert.AreEqual(RetrievalCoverageStatus.Complete, result.Coverage.Status);
            Assert.AreEqual(11, result.Paths.Count);
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
