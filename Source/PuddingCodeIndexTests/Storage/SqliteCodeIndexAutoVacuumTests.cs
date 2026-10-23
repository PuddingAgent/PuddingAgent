using Microsoft.Data.Sqlite;
using PuddingCodeIndex.Storage;

namespace PuddingCodeIndexTests.Storage;

/// <summary>
/// 锁定一条存储治理不变量：新建索引库必须启用 <c>auto_vacuum=INCREMENTAL</c>。
/// 背景（实测）：生产索引库曾积累 194,231 页 = 758.7 MiB（占文件 26.8%）的空闲页，
/// 且因 <c>auto_vacuum=NONE</c> 永不回收；一次全量 VACUUM 才回收 1,030.5 MiB。
/// 该 PRAGMA 只在库文件还没有任何表时生效，因此必须在建表之前执行 —— 本测试同时守住这个顺序。
/// </summary>
[TestClass]
public sealed class SqliteCodeIndexAutoVacuumTests
{
    [TestMethod]
    public async Task New_Database_Enables_Incremental_AutoVacuum()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-autovac", Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(root, "db", "code-index.db");
        try
        {
            var store = new SqliteCodeIndexStore(dbPath);
            await store.InitializeAsync();

            Assert.AreEqual(
                2L,
                await ScalarAsync(dbPath, "PRAGMA auto_vacuum;"),
                "新建库必须在建表前启用 auto_vacuum=INCREMENTAL（2）；否则空闲页永不回收");

            // 重开一次：确认模式持久化在库文件中，而不是仅当前会话有效。
            var reopened = new SqliteCodeIndexStore(dbPath);
            await reopened.InitializeAsync();

            Assert.AreEqual(
                2L,
                await ScalarAsync(dbPath, "PRAGMA auto_vacuum;"),
                "auto_vacuum 必须持久化；重复 InitializeAsync 不得把它改回去");
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不影响结论。
            }
        }
    }

    private static async Task<long> ScalarAsync(string dbPath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
