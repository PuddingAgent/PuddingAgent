using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Diagnostics;

namespace PuddingPlatformTests.Diagnostics;

/// <summary>
/// 拦截器与 EF Core 的真实集成契约（内存 SQLite，不碰生产库）。
/// </summary>
/// <remarks>
/// 为什么不用 mock：这里要证明的恰恰是"EF 的回调真的会被触发、触发次数对得上、
/// 拿到的 command.CommandText 是真的 SQL"。用假对象测只能证明我自己写的假对象能工作。
/// </remarks>
[TestClass]
public sealed class SqlStatementMetricsInterceptorTests
{
    private SqliteConnection _connection = null!;
    private PlatformDbContext _db = null!;
    private SqlStatementMetrics _metrics = null!;
    private SqlStatementMetricsInterceptor _interceptor = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        _metrics = new SqlStatementMetrics();
        _interceptor = new SqlStatementMetricsInterceptor(_metrics);

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_interceptor)
            .Options;

        _db = new PlatformDbContext(options);
    }

    [TestCleanup]
    public async Task CleanupAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [TestMethod]
    public async Task NonQuery_IsRecorded_WithAffectedRowCount()
    {
        await _db.Database.ExecuteSqlRawAsync("CREATE TABLE probe (id INTEGER NOT NULL)");
        await _db.Database.ExecuteSqlRawAsync("INSERT INTO probe (id) VALUES (1)");

        var snap = _metrics.Snapshot();
        Assert.IsTrue(snap.Count >= 2, "建表与插入应为两条不同的语句形状");

        SqlStatementSnapshot? insert = null;
        foreach (var s in snap)
        {
            if (s.Key.Contains("INSERT INTO probe", StringComparison.Ordinal))
            {
                insert = s;
                break;
            }
        }

        Assert.IsNotNull(insert, "插入语句必须被记录");
        Assert.AreEqual(1, insert!.Calls);
        Assert.AreEqual(1, insert.Rows, "NonQuery 必须记录确切的影响行数");
    }

    [TestMethod]
    public async Task SameShapeDifferentLiterals_MergeIntoSingleEntry()
    {
        await _db.Database.ExecuteSqlRawAsync("CREATE TABLE probe (id INTEGER NOT NULL)");

        // 这两条只有字面量不同；归一化后必须合并成一条统计，calls=2。
        await _db.Database.ExecuteSqlRawAsync("INSERT INTO probe (id) VALUES (1)");
        await _db.Database.ExecuteSqlRawAsync("INSERT INTO probe (id) VALUES (2)");

        SqlStatementSnapshot? insert = null;
        foreach (var s in _metrics.Snapshot())
        {
            if (s.Key.Contains("INSERT INTO probe", StringComparison.Ordinal))
            {
                insert = s;
                break;
            }
        }

        Assert.IsNotNull(insert);
        Assert.AreEqual(2, insert!.Calls,
            "只有字面量不同必须合并——否则每插入一行就长出一个统计项，统计表自己会爆");
        Assert.AreEqual(2, insert.Rows, "两次各影响 1 行，累计应为 2");
    }

    [TestMethod]
    public async Task Reader_IsRecorded_AndRowCountStaysNull()
    {
        await _db.Database.ExecuteSqlRawAsync("CREATE TABLE probe (id INTEGER NOT NULL)");
        await _db.Database.ExecuteSqlRawAsync("INSERT INTO probe (id) VALUES (7)");

        var rows = await _db.Database
            .SqlQueryRaw<int>("SELECT id FROM probe")
            .ToListAsync();

        Assert.AreEqual(1, rows.Count);

        SqlStatementSnapshot? select = null;
        foreach (var s in _metrics.Snapshot())
        {
            if (s.Key.Contains("SELECT id FROM probe", StringComparison.Ordinal))
            {
                select = s;
                break;
            }
        }

        Assert.IsNotNull(select, "查询语句必须被记录（Reader 路径）");
        Assert.IsNull(select!.Rows,
            "Reader 回调时 reader 还没被消费，行数未知；此时必须记 null 而不是猜一个值");
    }

    [TestMethod]
    public async Task Interceptor_DoesNotAlterResults()
    {
        await _db.Database.ExecuteSqlRawAsync("CREATE TABLE probe (id INTEGER NOT NULL)");
        await _db.Database.ExecuteSqlRawAsync("INSERT INTO probe (id) VALUES (1), (2), (3)");

        var rows = await _db.Database
            .SqlQueryRaw<int>("SELECT id FROM probe ORDER BY id")
            .ToListAsync();

        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, rows.ToArray(),
            "统计层绝不能改变被观测者的行为：结果必须原样返回");

        var affected = await _db.Database.ExecuteSqlRawAsync("DELETE FROM probe");
        Assert.AreEqual(3, affected, "统计层不得修改影响行数的返回值");
    }

}
