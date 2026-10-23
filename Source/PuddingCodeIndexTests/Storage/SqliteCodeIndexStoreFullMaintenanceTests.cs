using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Storage;

namespace PuddingCodeIndexTests.Storage;

/// <summary>
/// Tests for the explicit full-maintenance pass.
/// <para>
/// The value here is the acceptance contract: a report is only successful when the backup verified,
/// the mode switched, the freelist emptied, every table kept its row count, and the file length
/// equals <c>page_count * page_size</c>. "VACUUM returned without throwing" is not evidence.
/// </para>
/// </summary>
[TestClass]
public sealed class SqliteCodeIndexStoreFullMaintenanceTests
{
    [TestMethod]
    public async Task Full_Maintenance_Reclaims_Space_And_Verifies()
    {
        var root = NewRoot();
        var dbPath = Path.Combine(root, "db", "code-index.db");
        var backupPath = Path.Combine(root, "backup", "before.db");
        try
        {
            var store = new SqliteCodeIndexStore(dbPath);
            await store.InitializeAsync();
            await SeedThenDeleteAsync(store);

            var before = await store.GetStorageStatsAsync();
            Assert.IsTrue(before.FreelistCount > 0, $"前提：应已有空闲页，实际 {before.FreelistCount}");

            var report = await store.RunFullMaintenanceAsync(backupPath);

            Assert.IsTrue(report.Succeeded,
                "维护必须全部验收通过，失败项：" + string.Join(" | ", report.VerificationFailures));
            Assert.IsTrue(report.BackupVerified, "备份必须通过自身校验");
            Assert.IsTrue(report.BackupBytes > 0, "备份文件不得为空");
            Assert.IsTrue(File.Exists(backupPath), "备份文件必须存在");

            Assert.AreEqual(CodeIndexStorageStats.IncrementalAutoVacuumMode, report.After.AutoVacuumMode);
            Assert.AreEqual(0L, report.After.FreelistCount, "全量 VACUUM 后空闲页必须归零");
            Assert.AreEqual(
                report.After.PageCount * report.After.PageSize,
                report.After.DatabaseFileBytes,
                "文件长度必须等于 page_count * page_size");
            Assert.IsTrue(report.ReclaimedBytes > 0, $"应实际回收字节，实际 {report.ReclaimedBytes}");

            CollectionAssert.AreEquivalent(
                report.RowCountsBefore.Keys.ToList(),
                report.RowCountsAfter.Keys.ToList(),
                "表集合不得变化");
            foreach (var (table, count) in report.RowCountsBefore)
            {
                Assert.AreEqual(count, report.RowCountsAfter[table], $"表 {table} 行数不得变化");
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task Full_Maintenance_Keeps_Data_Queryable()
    {
        var root = NewRoot();
        var dbPath = Path.Combine(root, "db", "code-index.db");
        var backupPath = Path.Combine(root, "backup", "before.db");
        try
        {
            var store = new SqliteCodeIndexStore(dbPath);
            await store.InitializeAsync();

            var symbols = Enumerable.Range(0, 500)
                .Select(i => new CodeSymbolRecord(
                    WorkspaceId: "ws",
                    ProjectId: "proj",
                    FilePath: "src/keep.cs",
                    SymbolId: $"keep-{i:D4}",
                    Name: $"Kept{i}",
                    Kind: CodeSymbolKind.Class,
                    StartLine: i,
                    EndLine: i + 1))
                .ToList();
            await store.UpsertSymbolsAsync("ws", "proj", symbols);

            var report = await store.RunFullMaintenanceAsync(backupPath);

            Assert.IsTrue(report.Succeeded,
                "失败项：" + string.Join(" | ", report.VerificationFailures));

            // The point of maintenance is that the data is still there afterwards.
            var kept = await store.GetSymbolsByFileAsync("ws", "proj", "src/keep.cs");
            Assert.AreEqual(symbols.Count, kept.Count, "维护后符号必须仍可读回且数量不变");
            var one = await store.GetSymbolAsync("ws", "proj", "keep-0420");
            Assert.IsNotNull(one);
            Assert.AreEqual("Kept420", one!.Name);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task Full_Maintenance_Replaces_A_Stale_Backup()
    {
        var root = NewRoot();
        var dbPath = Path.Combine(root, "db", "code-index.db");
        var backupPath = Path.Combine(root, "backup", "before.db");
        try
        {
            var store = new SqliteCodeIndexStore(dbPath);
            await store.InitializeAsync();

            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            await File.WriteAllTextAsync(backupPath, "stale garbage that must not survive");

            var report = await store.RunFullMaintenanceAsync(backupPath);

            Assert.IsTrue(report.Succeeded,
                "失败项：" + string.Join(" | ", report.VerificationFailures));
            Assert.IsTrue(report.BackupBytes > 100, "陈旧备份必须被真正的新备份替换");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task Full_Maintenance_Rejects_Empty_Backup_Path()
    {
        var root = NewRoot();
        var dbPath = Path.Combine(root, "db", "code-index.db");
        try
        {
            var store = new SqliteCodeIndexStore(dbPath);
            await store.InitializeAsync();

            await Assert.ThrowsAsync<ArgumentException>(() => store.RunFullMaintenanceAsync("   "));
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// The scenario that matters in production: the existing file was created with
    /// <c>auto_vacuum=NONE</c>, so <c>PRAGMA auto_vacuum=INCREMENTAL</c> in InitializeAsync cannot
    /// take effect (the file is no longer empty) and only the maintenance VACUUM can switch it.
    /// </summary>
    [TestMethod]
    public async Task Full_Maintenance_Switches_An_Existing_None_Mode_Database()
    {
        var root = NewRoot();
        var dbPath = Path.Combine(root, "db", "code-index.db");
        var backupPath = Path.Combine(root, "backup", "before.db");
        try
        {
            // Build the file the old way: a table exists before any auto_vacuum pragma runs,
            // which pins the database to NONE.
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            await using (var legacy = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={dbPath};Pooling=false"))
            {
                await legacy.OpenAsync();
                await using var command = legacy.CreateCommand();
                command.CommandText =
                    "PRAGMA journal_mode = WAL;"
                    + "CREATE TABLE LegacySeed (Id INTEGER NOT NULL PRIMARY KEY, Payload TEXT NOT NULL);"
                    + "INSERT INTO LegacySeed (Payload) VALUES ('x');";
                await command.ExecuteNonQueryAsync();
            }

            var store = new SqliteCodeIndexStore(dbPath);
            await store.InitializeAsync();

            var before = await store.GetStorageStatsAsync();
            Assert.AreEqual(0, before.AutoVacuumMode,
                "前提：存量库应仍处于 NONE 模式（InitializeAsync 的 PRAGMA 对非空库无效）");

            var report = await store.RunFullMaintenanceAsync(backupPath);

            Assert.IsTrue(report.Succeeded,
                "失败项：" + string.Join(" | ", report.VerificationFailures));
            Assert.AreEqual(
                CodeIndexStorageStats.IncrementalAutoVacuumMode,
                report.After.AutoVacuumMode,
                "全量维护必须把存量库切换到 INCREMENTAL");
        }
        finally
        {
            TryDelete(root);
        }
    }
    private static async Task SeedThenDeleteAsync(SqliteCodeIndexStore store)
    {
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
    }

    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "pudding-full-maint", Guid.NewGuid().ToString("N"));

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
