using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// RSI S3 增量 B3（任务书 §5.3）：新数据访问方法 GetRecentTurnIdsAsync 的<b>执行级证据</b>。
/// <para>
/// 两条用例分工：
/// <list type="bullet">
/// <item>R1：真 SQLite 执行 —— 三个 turn（其中两个 created_at 相同）⇒ 证明「最近 N + turn_id tiebreaker + 内存反转升序」三件事同时成立。</item>
/// <item>R2：对<b>实际生成的那条 SQL</b> 跑 <c>EXPLAIN QUERY PLAN</c> ⇒ 证明命中 conversation 维度索引、不是全表扫描（任务书 §5.4 变异 B 的红靶）。</item>
/// </list>
/// 照抄 RsiTrajectoryDataAccessTests 的既有模式：内存 SQLite + DbCommandInterceptor 捕获真实 SQL。
/// </para>
/// </summary>
[TestClass]
public sealed class RsiRecentTurnIdsQueryTests
{
    private SqliteConnection _connection = null!;
    private PlatformDbContext _db = null!;
    private QueryRecorder _queries = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _queries = new QueryRecorder();
        _db = new PlatformDbContext(Options(_connection, _queries));
        await _db.Database.EnsureCreatedAsync();
        _queries.Commands.Clear();
    }

    [TestCleanup]
    public async Task CleanupAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private static DbContextOptions<PlatformDbContext> Options(
        SqliteConnection connection, DbCommandInterceptor interceptor) =>
        new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;

    /// <summary>
    /// R1：三个 turn，created_at 递增且<b>其中两个相同</b>（100 / 200 / 200，tiebreaker 靠 turn_id）。
    /// limit=2 必须返回 [turn-mid, turn-new]：
    /// ① 不含最旧的 turn-old（确实取的最近 N）；② created_at 并列时取 turn_id 更大的两个（turn_id DESC tiebreaker）；
    /// ③ 返回顺序是时间升序（内存反转生效，忘反转会得到 [turn-new, turn-mid]）。
    /// </summary>
    [TestMethod]
    public async Task R1_LimitTwo_TiebreakByTurnIdDesc_ReturnsRecentTwoInAscendingOrder()
    {
        AddTurn("conv-1", "turn-old", createdAt: 100);
        AddTurn("conv-1", "turn-mid", createdAt: 200);
        AddTurn("conv-1", "turn-new", createdAt: 200);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        _queries.Commands.Clear();

        var access = new RsiTrajectoryDataAccess(new SharedConnectionFactory(_connection, _queries));
        var turnIds = await access.GetRecentTurnIdsAsync("conv-1", 2, CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "turn-mid", "turn-new" },
            turnIds.ToArray(),
            "limit=2 必须返回最近两个 turn（created_at 并列时按 turn_id DESC tiebreaker），且反转为时间升序。");
    }

    /// <summary>
    /// R2：索引命中（执行级证据）。对该实现<b>实际生成</b>的 SQL 跑 EXPLAIN QUERY PLAN。
    /// <para>
    /// 断言三件事：① 计划涉及 conversation_turns；② 出现 USING INDEX 且命中 conversation 维度
    /// （索引名或 seek 条件含 conversation_id）；③ <b>不存在</b>裸全表扫描行（SCAN conversation_turns，无 USING）。
    /// 变异：把 Where 去掉 ⇒ 计划退化为 SCAN 且 ③ 必红；改成按 turn_id 过滤 ⇒ 命中 TurnId 索引且 ② 必红。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task R2_QueryPlan_SeeksViaConversationIdCreatedAtIndex_NoFullScan()
    {
        AddTurn("conv-plan", "turn-1", createdAt: 100);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        _queries.Commands.Clear();

        var access = new RsiTrajectoryDataAccess(new SharedConnectionFactory(_connection, _queries));
        _ = await access.GetRecentTurnIdsAsync("conv-plan", 3, CancellationToken.None);

        var sql = _queries.Commands.Single();
        var plan = await ExplainQueryPlanAsync(sql);

        foreach (var line in plan)
        {
            Console.WriteLine("PLAN>" + line);
        }

        var planText = string.Join(" | ", plan);
        StringAssert.Contains(planText, "conversation_turns", "计划必须涉及 conversation_turns。");

        var indexLines = plan
            .Where(l => l.Contains("USING INDEX", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.IsTrue(
            indexLines.Count > 0,
            $"计划中未出现索引使用 ⇒ 退化为全表扫描。实际计划：{planText}");
        Assert.IsTrue(
            indexLines.Any(l =>
            {
                var lower = l.ToLowerInvariant();
                return lower.Contains("conversation_id")          // 蛇形索引名或 seek 条件（列名映射）；
                       || lower.Replace("_", string.Empty).Contains("conversationid");   // EF 帕斯卡索引名。
            }),
            $"命中的索引不是 conversation 维度 ⇒ 查询形状已偏离规格 §2.9 的 (conversation_id, created_at)。实际计划：{planText}");
        Assert.IsFalse(
            plan.Any(l => l.StartsWith("SCAN", StringComparison.OrdinalIgnoreCase)
                          && l.Contains("conversation_turns", StringComparison.OrdinalIgnoreCase)
                          && !l.Contains("USING", StringComparison.OrdinalIgnoreCase)),
            $"出现对 conversation_turns 的裸全表扫描。实际计划：{planText}");
    }

    private async Task<List<string>> ExplainQueryPlanAsync(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        using var reader = await command.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync())
        {
            lines.Add(reader.GetString(3));
        }

        return lines;
    }

    private void AddTurn(string conversationId, string turnId, long createdAt) =>
        _db.ConversationTurns.Add(new ConversationTurnEntity
        {
            ConversationId = conversationId,
            TurnId = turnId,
            WorkspaceId = "default",
            CreatedAt = createdAt,
        });

    /// <summary>每个实例新建 DbContext（镜像生产：实现内部会 dispose 它拿到的上下文），共享同一连接。</summary>
    private sealed class SharedConnectionFactory : IDbContextFactory<PlatformDbContext>
    {
        private readonly SqliteConnection _connection;
        private readonly DbCommandInterceptor _interceptor;

        public SharedConnectionFactory(SqliteConnection connection, DbCommandInterceptor interceptor)
        {
            _connection = connection;
            _interceptor = interceptor;
        }

        public PlatformDbContext CreateDbContext() => new(Options(_connection, _interceptor));
    }

    private sealed class QueryRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
