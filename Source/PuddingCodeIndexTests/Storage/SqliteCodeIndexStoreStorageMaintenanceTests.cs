using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Storage;

namespace PuddingCodeIndexTests.Storage;

/// <summary>
/// Integration tests for the storage-maintenance surface of the SQLite store.
/// <para>
/// The decisive one is <see cref="Reclaim_Actually_Removes_Freelist_Pages"/>: the contract says the
/// result reports the freelist delta, never "the pragma executed", because
/// <c>PRAGMA incremental_vacuum</c> returns no row count and succeeds while reclaiming nothing.
/// A test that only asserted "no exception" would pass on a database where reclamation is a no-op.
/// </para>
/// </summary>
[TestClass]
public sealed class SqliteCodeIndexStoreStorageMaintenanceTests
{
    private const long PageSize = 4096;

    [TestMethod]
    public async Task New_Store_Reports_Incremental_Mode_And_Reads_Counters()
    {
        var root = NewRoot();
        var dbPath = Path.Combine(root, "db", "code-index.db");
        try
        {
            var store = new SqliteCodeIndexStore(dbPath);
            await store.InitializeAsync();

            var stats = await store.GetStorageStatsAsync();

            Assert.AreEqual(CodeIndexStorageStats.IncrementalAutoVacuumMode, stats.AutoVacuumMode,
                "新建库必须处于 INCREMENTAL 模式（见 InitializeAsync 中的 PRAGMA 顺序约定）");
            Assert.IsTrue(stats.SupportsIncrementalReclaim);
            Assert.AreEqual(PageSize, stats.PageSize);
            Assert.IsTrue(stats.PageCount > 0, $"初始化后应有页，实际 {stats.PageCount}");
            Assert.IsTrue(stats.DatabaseFileBytes > 0, $"库文件应存在，实际 {stats.DatabaseFileBytes} 字节");
            Assert.AreEqual(0L, stats.FreelistCount, "全新库不应有空闲页");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task Reclaim_Actually_Removes_Freelist_Pages()
    {
        var root = NewRoot();
        var dbPath = Path.Combine(root, "db", "code-index.db");
        try
        {
            var store = new SqliteCodeIndexStore(dbPath);
            await store.InitializeAsync();

            // Fill a file with symbols, then drop them all: the freed pages land on the freelist.
            const int symbolCount = 20_000;
            var symbols = Enumerable.Range(0, symbolCount)
                .Select(i => new CodeSymbolRecord(
                    WorkspaceId: "ws",
                    ProjectId: "proj",
                    FilePath: "src/bulk.cs",
                    SymbolId: $"sym-{i:D6}",
                    Name: $"GeneratedSymbol{i}",
                    Kind: CodeSymbolKind.Class,
                    StartLine: i,
                    EndLine: i + 1))
                .ToList();

            await store.UpsertSymbolsAsync("ws", "proj", symbols);
            await store.ClearSymbolsForFileAsync("ws", "proj", "src/bulk.cs");

            var before = await store.GetStorageStatsAsync();
            Assert.IsTrue(before.FreelistCount > 0,
                $"清空 {symbolCount} 个符号后应留下空闲页，实际 {before.FreelistCount}");
            Assert.IsTrue(before.SupportsIncrementalReclaim);

            var result = await store.ReclaimFreePagesAsync(CodeIndexStoragePolicy.DefaultBatchPages);

            Assert.IsTrue(result.Executed, $"应执行回收，SkipReason={result.SkipReason}");
            Assert.IsTrue(result.ReclaimedPages > 0,
                $"必须实测到回收，实际 ReclaimedPages={result.ReclaimedPages}（调用成功不等于回收成功）");
            Assert.AreEqual(
                before.FreelistCount - result.ReclaimedPages,
                result.After.FreelistCount,
                "结果中的回收数必须与前后空闲页差值严格一致");
            Assert.IsTrue(result.After.FreelistCount < before.FreelistCount,
                "回收后空闲页必须下降");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task Reclaim_Is_Batched_And_Leaves_Pages_For_Later()
    {
        var root = NewRoot();
        var dbPath = Path.Combine(root, "db", "code-index.db");
        try
        {
            var store = new SqliteCodeIndexStore(dbPath);
            await store.InitializeAsync();

            const int symbolCount = 20_000;
            var symbols = Enumerable.Range(0, symbolCount)
                .Select(i => new CodeSymbolRecord(
                    WorkspaceId: "ws",
                    ProjectId: "proj",
                    FilePath: "src/bulk.cs",
                    SymbolId: $"sym-{i:D6}",
                    Name: $"GeneratedSymbol{i}",
                    Kind: CodeSymbolKind.Class,
                    StartLine: i,
                    EndLine: i + 1))
                .ToList();

            await store.UpsertSymbolsAsync("ws", "proj", symbols);
            await store.ClearSymbolsForFileAsync("ws", "proj", "src/bulk.cs");

            var before = await store.GetStorageStatsAsync();
            const int batch = 8;   // 32 KiB: far below the pending freelist, so pages must remain.

            var result = await store.ReclaimFreePagesAsync(batch);

            Assert.IsTrue(result.Executed);
            Assert.IsTrue(result.ReclaimedPages <= batch,
                $"单批不得回收超过 {batch} 页，实际 {result.ReclaimedPages}");
            Assert.IsTrue(result.After.FreelistCount > 0,
                "限批回收后应仍有余量留给后续批次");
            Assert.IsTrue(result.After.FreelistCount < before.FreelistCount);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task Reclaim_Rejects_Non_Positive_Batch()
    {
        var root = NewRoot();
        var dbPath = Path.Combine(root, "db", "code-index.db");
        try
        {
            var store = new SqliteCodeIndexStore(dbPath);
            await store.InitializeAsync();

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => store.ReclaimFreePagesAsync(0));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => store.ReclaimFreePagesAsync(-1));
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// Guards the defect this surface exists to prevent: reporting the requested batch size as if
    /// it were the amount reclaimed. The request is deliberately far larger than the pending
    /// freelist, so "measured" and "requested" cannot coincide and the two can be told apart.
    /// </summary>
    [TestMethod]
    public async Task Reclaim_Never_Reports_More_Pages_Than_Were_Free()
    {
        var root = NewRoot();
        var dbPath = Path.Combine(root, "db", "code-index.db");
        try
        {
            var store = new SqliteCodeIndexStore(dbPath);
            await store.InitializeAsync();

            var symbols = Enumerable.Range(0, 20_000)
                .Select(i => new CodeSymbolRecord(
                    WorkspaceId: "ws",
                    ProjectId: "proj",
                    FilePath: "src/bulk.cs",
                    SymbolId: $"sym-{i:D6}",
                    Name: $"GeneratedSymbol{i}",
                    Kind: CodeSymbolKind.Class,
                    StartLine: i,
                    EndLine: i + 1))
                .ToList();

            await store.UpsertSymbolsAsync("ws", "proj", symbols);
            await store.ClearSymbolsForFileAsync("ws", "proj", "src/bulk.cs");

            var before = await store.GetStorageStatsAsync();
            const int absurdBatch = 100_000;
            Assert.IsTrue(
                before.FreelistCount < absurdBatch,
                $"前提不成立：空闲页 {before.FreelistCount} 应远少于请求的 {absurdBatch}");

            var result = await store.ReclaimFreePagesAsync(absurdBatch);

            Assert.IsTrue(result.Executed, $"应执行回收，SkipReason={result.SkipReason}");
            Assert.IsTrue(
                result.ReclaimedPages <= before.FreelistCount,
                $"回收量不得超过回收前的空闲页数：Reclaimed={result.ReclaimedPages}, FreeBefore={before.FreelistCount}");
            Assert.IsTrue(
                result.ReclaimedPages < absurdBatch,
                $"不可能回收请求的 {absurdBatch} 页 —— 实际只有 {before.FreelistCount} 页空闲；"
                    + "报出请求量说明结果取自请求参数而非实测");
        }
        finally
        {
            TryDelete(root);
        }
    }
    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "pudding-storage-maint", Guid.NewGuid().ToString("N"));

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Temporary cleanup must not mask the assertion result.
        }
    }
}
