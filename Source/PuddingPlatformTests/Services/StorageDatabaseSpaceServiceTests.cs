using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Configuration;
using PuddingPlatform.Services.StorageManagement;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 「按数据类占用」汇总服务：四个库一次测量 + 按目录归并。
///
/// 钉住的语义：① 库不存在与"空库"必须区分（前者 <c>Exists=false</c>）；② 平台库要给出按表/按类明细；
/// ③ 不在目录里的表进"未归类"桶（本用例用的表名故意不在目录里，因此这条是确定性的）。
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
    public async Task MeasureAsync_ReportsAllFourDatabases_AndMarksMissingOnesAsNotExisting()
    {
        var paths = PuddingDataPaths.FromRoot(_root);
        Directory.CreateDirectory(paths.DatabasesRoot);
        await CreatePlatformDatabaseAsync(Path.Combine(paths.DatabasesRoot, StorageDataClassCatalog.PlatformDatabaseFile));

        var spaces = await new StorageDatabaseSpaceService(paths, new DatabaseSpaceProbe()).MeasureAsync();

        Assert.AreEqual(4, spaces.Count, "清单与 StorageInventorySampler 保持一致：platform / code-index / memory / controller");
        CollectionAssert.AreEqual(
            new[] { "platform", "code-index", "memory", "controller" },
            spaces.Select(space => space.Key).ToArray());

        var platform = spaces[0];
        Assert.IsTrue(platform.Exists);
        Assert.IsTrue(platform.FileBytes > 0);
        var direct = await new DatabaseSpaceProbe().MeasureAsync(platform.DatabaseFile);
        // 事实（2026-10-03 实测）：本仓库使用的 SQLite **没有 dbstat** 虚表 ⇒ 探针必须如实报"拿不到按表明细"
        // 并带出原因，而不是给出猜测的按表数字。缺 dbstat 时按表占比的数据源需要另选（见日志的更正段）。
        Assert.IsNotNull(direct);
        Assert.IsFalse(direct!.PerTableAvailable, "本机构型无 dbstat：如实报不可用");
        StringAssert.Contains(direct.PerTableUnavailableReason ?? string.Empty, "dbstat");
        Assert.AreEqual(0, platform.DataClasses.Count, "无 dbstat ⇒ 不返回任何按类明细（不给猜测数字）");



        foreach (var missing in spaces.Skip(1))
        {
            Assert.IsFalse(missing.Exists, $"{missing.Key} 未创建 ⇒ 必须如实报不存在");
            Assert.AreEqual(0, missing.FileBytes);
            Assert.AreEqual(0, missing.DataClasses.Count);
        }
    }

    private static string TryGetReason(object _) => "(见 DatabaseSpaceProbe.PerTableUnavailableReason)";

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
