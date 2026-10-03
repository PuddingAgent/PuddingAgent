using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Configuration;
using PuddingPlatform.Services.StorageManagement;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 「按数据类占用」汇总服务：四个库一次测量 + 按目录归并。
///
/// 钉住的语义：① 库不存在与"空库"必须区分（前者 <c>Exists=false</c>）；
/// ② **本机构型无 dbstat**（实测 `no such table: dbstat`）⇒ 服务必须退回估算数据源且**仍然给出**按类明细；
/// ③ 数据来源要如实带出（<c>spaceSource</c>），界面与排障据此判断可信度。
/// </summary>
[TestClass]
public sealed class StorageDatabaseSpaceServiceTests
{
    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-dbclass-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响用例结论
        }
    }

    [TestMethod]
    public async Task MeasureAsync_ReportsAllFourDatabases_AndFallsBackToRowCountEstimate()
    {
        var paths = PuddingDataPaths.FromRoot(_root);
        Directory.CreateDirectory(paths.DatabasesRoot);
        var platformPath = Path.Combine(paths.DatabasesRoot, StorageDataClassCatalog.PlatformDatabaseFile);
        await CreatePlatformDatabaseAsync(platformPath);

        var spaces = await new StorageDatabaseSpaceService(
            paths, new DatabaseSpaceProbe(), new DatabaseTableEstimator()).MeasureAsync();

        Assert.AreEqual(4, spaces.Count, "清单与 StorageInventorySampler 保持一致：platform / code-index / memory / controller");
        CollectionAssert.AreEqual(
            new[] { "platform", "code-index", "memory", "controller" },
            spaces.Select(space => space.Key).ToArray());

        var platform = spaces[0];
        Assert.IsTrue(platform.Exists);
        Assert.IsTrue(platform.FileBytes > 0, "文件级总量精确可用（page_size × page_count + WAL/SHM）");

        // 事实（2026-10-03 实测）：本仓库的 SQLite 没有 dbstat ⇒ 探针如实报不可用并带原因。
        var direct = await new DatabaseSpaceProbe().MeasureAsync(platform.DatabaseFile);
        Assert.IsNotNull(direct);
        Assert.IsFalse(direct!.PerTableAvailable, "本机构型无 dbstat：如实报不可用");
        StringAssert.Contains(direct.PerTableUnavailableReason ?? string.Empty, "dbstat");

        // 退回估算后**仍要有按类明细**（否则界面无数据可展示），且来源要如实标出。
        Assert.AreEqual(StorageDatabaseSpaceService.SpaceSourceRowCountSample, platform.SpaceSource);
        Assert.IsTrue(platform.DataClasses.Count > 0, "退回估算后仍要有按类明细");

        var unclassified = platform.DataClasses.Single(space =>
            space.TargetId == StorageDataClassSpaceMapper.UnclassifiedTargetId);
        CollectionAssert.Contains(unclassified.Tables.ToArray(), "message_row");
        Assert.IsTrue(unclassified.Bytes > 0, "估算占用必须为正（否则等于没数据）");

        foreach (var missing in spaces.Skip(1))
        {
            Assert.IsFalse(missing.Exists, $"{missing.Key} 未创建 ⇒ 必须如实报不存在");
            Assert.AreEqual(0, missing.FileBytes);
            Assert.AreEqual(0, missing.DataClasses.Count);
            Assert.AreEqual(StorageDatabaseSpaceService.SpaceSourceUnavailable, missing.SpaceSource);
        }
    }

    [TestMethod]
    public async Task Estimator_RowCountsAreExact_AndBytesComeFromBoundedSample()
    {
        var paths = PuddingDataPaths.FromRoot(_root);
        Directory.CreateDirectory(paths.DatabasesRoot);
        var platformPath = Path.Combine(paths.DatabasesRoot, StorageDataClassCatalog.PlatformDatabaseFile);
        await CreatePlatformDatabaseAsync(platformPath);

        var estimates = await new DatabaseTableEstimator().EstimateAsync(platformPath, sampleRows: 50);

        var messageRow = estimates.Single(estimate => estimate.Table == "message_row");
        Assert.AreEqual(300, messageRow.RowCount, "行数必须精确（来自 COUNT(*)）");
        Assert.AreEqual(50, messageRow.SampledRows, "抽样行数必须被采样上限约束（大表不能读穿）");
        Assert.IsTrue(messageRow.Bytes > 0, "估算占用必须为正");

        // 估算必须落在合理量级：不小于"64 字节 blob × 300 行"的六成，也不大得离谱。
        Assert.IsTrue(messageRow.Bytes > 300 * 64 * 0.6, $"估算 {messageRow.Bytes} 偏小，抽样或换算可能错了");
        Assert.IsTrue(messageRow.Bytes < 300 * 4096, $"估算 {messageRow.Bytes} 偏大，抽样或换算可能错了");
    }

    private static async Task CreatePlatformDatabaseAsync(string path)
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
                CREATE TABLE message_row (id TEXT PRIMARY KEY, body TEXT NOT NULL);
                CREATE INDEX ix_message_row_body ON message_row(body);
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO message_row (id, body)
                SELECT 'm' || value, hex(randomblob(64)) FROM (WITH RECURSIVE n(value) AS (
                    SELECT 1 UNION ALL SELECT value + 1 FROM n WHERE value < 300) SELECT value FROM n);
                """;
            await insert.ExecuteNonQueryAsync();
        }

        await connection.CloseAsync();
    }
}
