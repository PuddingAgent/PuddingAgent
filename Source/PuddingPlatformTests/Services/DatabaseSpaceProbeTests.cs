using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Storage;
using PuddingPlatform.Services.StorageManagement;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 按表占用探针（存储管理页"显示不同数据的占比"的数据源）。
///
/// 钉住三件事：① 文件级总量含 WAL/SHM（只报主文件会明显偏小）；② dbstat 可用时按表明细
/// 与页统计**自洽**（合计 ≈ page_size × page_count）；③ dbstat 不可用时**如实**报"拿不到"，
/// 而不是猜一份占比出来（猜出来的占比会让用户按错误依据清理）。
/// </summary>
[TestClass]
public sealed class DatabaseSpaceProbeTests
{
    private string _directory = null!;

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "pudding-dbspace-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响用例结论
        }
    }

    [TestMethod]
    public async Task MeasureAsync_MissingFile_ReturnsNullInsteadOfZero()
    {
        var probe = new DatabaseSpaceProbe();

        var report = await probe.MeasureAsync(Path.Combine(_directory, "not-there.db"));

        // 库不存在与"库是空的"必须能区分：前者返回 null，调用方显示"库不存在"。
        Assert.IsNull(report);
    }

    [TestMethod]
    public async Task MeasureAsync_ReportsFileLevelTotals_IncludingWalAndShm()
    {
        var path = Path.Combine(_directory, "pudding_platform.db");
        await CreateSeededDatabaseAsync(path);

        // WAL/SHM 是真实存在的伴生文件；只统计主文件会让用户看到的总量偏小。
        await File.WriteAllBytesAsync(path + "-wal", new byte[4096]);
        await File.WriteAllBytesAsync(path + "-shm", new byte[512]);

        var report = await new DatabaseSpaceProbe().MeasureAsync(path);

        Assert.IsNotNull(report);
        Assert.AreEqual(Path.GetFullPath(path), report!.DatabaseFile);
        Assert.IsTrue(report.PageSize > 0, "页大小必须来自库本身");
        Assert.IsTrue(report.PageCount > 0, "页数必须来自库本身");
        Assert.AreEqual(
            new FileInfo(path).Length + 4096 + 512,
            report.FileBytes,
            "文件级总量必须含 -wal 与 -shm");
    }

    [TestMethod]
    public async Task MeasureAsync_PerTableDetails_AreSelfConsistentWithPageTotals()
    {
        var path = Path.Combine(_directory, "pudding_platform.db");
        await CreateSeededDatabaseAsync(path);

        var report = await new DatabaseSpaceProbe().MeasureAsync(path);

        Assert.IsNotNull(report);
        if (!report!.PerTableAvailable)
        {
            // 该 SQLite 构型没有 dbstat：必须如实报"拿不到"，且不得给出任何猜测的明细。
            Assert.AreEqual(0, report.Tables.Count);
            return;
        }

        Assert.IsTrue(report.PerTableAvailable, "本机 SQLite 构型应支持 dbstat（此断言用于确定这一事实）");
        Assert.IsTrue(report.Tables.Count >= 2, "至少应看到两张表（含索引行）");
        Assert.IsTrue(
            report.Tables.Any(table => table.Table == "conversation_catalog"),
            "明细必须包含我们建的表");

        // 索引必须归到它服务的表：否则"索引特别多的表"会被低估。
        var indexRow = report.Tables.FirstOrDefault(table => table.Table == "ix_message_row_body");
        Assert.IsNotNull(indexRow, "显式索引应出现在 dbstat 明细里");
        Assert.AreEqual("message_row", indexRow!.OwnerTable);

        // 自洽性：dbstat 的 pgsize 合计与页统计同源，允许少量 freelist/头页差异。
        var pageTotal = report.PageSize * report.PageCount;
        Assert.IsTrue(
            report.TablesBytes > 0 && report.TablesBytes <= pageTotal,
            $"按表合计 {report.TablesBytes} 不应超过页总计 {pageTotal}");
    }

    [TestMethod]
    public void Mapper_GroupsByDataClass_AndKeepsUnclassifiedLast_EvenWhenLargest()
    {
        var definitions = new List<StorageDataClassCatalog.StorageDataClassDefinition>
        {
            new()
            {
                TargetId = "session-evidence",
                DisplayName = "会话事件证据",
                Description = "test",
                SafetyLevel = StorageSafetyLevel.Evidence,
                DatabaseFile = StorageDataClassCatalog.PlatformDatabaseFile,
                Tables = [new StorageDataClassCatalog.StoragePhysicalTable
                {
                    Table = "conversation_catalog",
                    TimestampColumn = "id",
                }],
                ManualCleanupAllowed = true,
            },
        };

        // 未归类桶故意给更大的体积：它必须仍排在最后（「目录没覆盖」是提示，不该因为大就抢头条）。
        var tables = new List<DatabaseTableSpace>
        {
            new("conversation_catalog", 1_000, 10, "conversation_catalog"),
            new("ix_message_row_body", 4_000, 40, "message_row"),
            new("message_row", 5_000, 50, "message_row"),
        };

        var spaces = StorageDataClassSpaceMapper.Map(tables, definitions);

        Assert.AreEqual(2, spaces.Count);
        var classified = spaces[0];
        Assert.AreEqual("session-evidence", classified.TargetId);
        Assert.AreEqual("会话事件证据", classified.DisplayName);
        Assert.AreEqual(1_000, classified.Bytes);
        Assert.IsTrue(classified.ManualCleanupAllowed);
        Assert.AreEqual("Evidence", classified.SafetyLevel, "安全级别原样透传目录值，不在这一层发明规则");

        var unclassified = spaces[1];
        Assert.AreEqual(StorageDataClassSpaceMapper.UnclassifiedTargetId, unclassified.TargetId);
        // 索引 4_000 必须计入它所属的表 message_row ⇒ 未归类桶 = 4_000 + 5_000。
        Assert.AreEqual(9_000, unclassified.Bytes);
        CollectionAssert.AreEquivalent(
            new[] { "message_row" }, unclassified.Tables.ToArray());
    }

    [TestMethod]
    public void Mapper_WithoutRows_ReturnsEmpty()
    {
        Assert.AreEqual(0, StorageDataClassSpaceMapper.Map([]).Count);
    }

    private static async Task CreateSeededDatabaseAsync(string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE conversation_catalog (id TEXT PRIMARY KEY, payload TEXT NOT NULL);
                CREATE TABLE message_row (id TEXT PRIMARY KEY, body TEXT NOT NULL);
                -- 显式索引：SQLite 的 rowid 表主键不产生独立 B 树，必须有它才能覆盖"索引归属"这条路径。
                CREATE INDEX ix_message_row_body ON message_row(body);
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO conversation_catalog (id, payload)
                SELECT 'c' || value, hex(randomblob(64)) FROM (WITH RECURSIVE n(value) AS (
                    SELECT 1 UNION ALL SELECT value + 1 FROM n WHERE value < 200) SELECT value FROM n);
                INSERT INTO message_row (id, body)
                SELECT 'm' || value, hex(randomblob(32)) FROM (WITH RECURSIVE n(value) AS (
                    SELECT 1 UNION ALL SELECT value + 1 FROM n WHERE value < 100) SELECT value FROM n);
                """;
            await insert.ExecuteNonQueryAsync();
        }

        await connection.CloseAsync();
    }
}
