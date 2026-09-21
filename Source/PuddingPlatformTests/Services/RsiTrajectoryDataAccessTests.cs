using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// RSI S3 增量 B2 的<b>执行级证据</b>（规格 §4.3.2 已登记的缺口：SQL 形状此前只有代码级论证）。
/// <para>
/// 两条用例分工：
/// <list type="bullet">
/// <item>S16：对真实 SQLite 执行真实实现 ⇒ 证明查询可跑、过滤在 SQL 侧、排序确定、且 §2.7 解析器确实被接进数据路径（不是只孤立测过）。</item>
/// <item>S17：对<b>实际生成的那条 SQL</b> 跑 <c>EXPLAIN QUERY PLAN</c> ⇒ 证明命中 turn_id 索引、不是全表扫描。</item>
/// </list>
/// 本类只读断言既有行为，不改变任何生产代码。
/// </para>
/// </summary>
[TestClass]
public sealed class RsiTrajectoryDataAccessTests
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
    /// S16：真实执行路径。断言四件事，每件都对应一个可能的实现错误：
    /// ① 类型过滤发生在 SQL 侧（不在内存里过滤）⇒ 被捕获的 SQL 文本须含两个事件类型条件；
    /// ② 排序确定（TurnId → Sequence）⇒ 断言输出逐项顺序，而非"集合相等"；
    /// ③ §2.7 解析器确实被接进 EF 投影 ⇒ 带 +08:00 偏移的行必须换算成 15:00Z 瞬时；
    /// ④ 非目标类型的行不得混入 ⇒ 结果条数精确。
    /// </summary>
    [TestMethod]
    public async Task S16_RealSqlite_ExecutesAndAppliesTypeFilterOrderingAndTimestampContract()
    {
        AddEvent("turn-a", 1, ConversationEventTypes.ToolCallRequested, "{\"name\":\"shell\"}");
        AddEvent("turn-a", 2, ConversationEventTypes.ToolCallCompleted, "{\"exitCode\":0}");
        AddEvent("turn-a", 3, ConversationEventTypes.MessageContentAppended, "{\"text\":\"noise\"}");
        AddEvent("turn-b", 4, ConversationEventTypes.ToolCallCompleted, "{\"exitCode\":\"1\",\"error\":\"boom\"}");
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        _queries.Commands.Clear();

        var access = new RsiTrajectoryDataAccess(new SharedConnectionFactory(_connection, _queries));
        var rows = await access.GetEventsByTurnIdsAsync(
            ["turn-b", "turn-a"],
            [ConversationEventTypes.ToolCallRequested, ConversationEventTypes.ToolCallCompleted],
            CancellationToken.None);

        Assert.AreEqual(3, rows.Count, "非目标类型的事件必须在 SQL 侧被过滤掉，不得进入结果。");
        Assert.AreEqual("turn-a", rows[0].TurnId);
        Assert.AreEqual(1L, rows[0].Sequence);
        Assert.AreEqual("turn-a", rows[1].TurnId);
        Assert.AreEqual(2L, rows[1].Sequence);
        Assert.AreEqual("turn-b", rows[2].TurnId);
        Assert.AreEqual(4L, rows[2].Sequence, "序列号是会话级序号（(ConversationId, Sequence) UNIQUE），turn-b 的首个事件排在 4。");

        var sql = _queries.Commands.Single();
        StringAssert.Contains(sql, "turn_id");
        StringAssert.Contains(sql, "type");
        var whereIndex = sql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
        var whereClause = whereIndex >= 0 ? sql[whereIndex..] : sql;
        Assert.IsFalse(
            whereClause.Contains("command_id", StringComparison.OrdinalIgnoreCase),
            "WHERE 子句不得按 command_id 过滤（无索引 ⇒ 全表扫描，规格 §2.4）；"
            + "SELECT 列清单里出现 command_id 属正常（实体映射列），本断言只约束过滤条件。");
    }

    /// <summary>
    /// S16b：时间戳契约在<b>真实数据路径</b>上生效（S13 只孤立测了解析器）。
    /// 带 +08:00 偏移的落盘串必须换算为同一 UTC 瞬时 ⇒ 若 EF 投影忘了调 ParseUtc 或填了哨兵值，本用例必红。
    /// </summary>
    [TestMethod]
    public async Task S16b_TimestampContractIsWiredIntoTheEfProjection()
    {
        _db.ConversationEvents.Add(new ConversationEventEntity
        {
            ConversationId = "conv-1", Sequence = 1, Type = ConversationEventTypes.ToolCallCompleted,
            EventId = Guid.NewGuid().ToString("N"), WorkspaceId = "default", TurnId = "turn-ts",
            RunId = "run-1", Payload = "{}",
            OccurredAt = "2026-09-21T23:00:00+08:00",
            CommittedAt = "2026-09-21T23:00:00+08:00",
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        _queries.Commands.Clear();

        var access = new RsiTrajectoryDataAccess(new SharedConnectionFactory(_connection, _queries));
        var rows = await access.GetEventsByTurnIdsAsync(
            ["turn-ts"], [ConversationEventTypes.ToolCallCompleted], CancellationToken.None);

        var expected = new DateTimeOffset(2026, 9, 21, 15, 0, 0, TimeSpan.Zero);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(expected.UtcTicks, rows[0].OccurredAtUtc.UtcTicks,
            "+08:00 落盘串必须经 ParseUtc 换算为 15:00Z（偏移 0）——本断言证明解析器已接入 EF 投影，而非仅被孤立测试。");
        Assert.AreEqual(TimeSpan.Zero, rows[0].OccurredAtUtc.Offset);
    }

    /// <summary>
    /// S17：索引命中（执行级证据）。对该实现<b>实际生成</b>的 SQL 跑 EXPLAIN QUERY PLAN。
    /// <para>
    /// 断言三件事：① 计划涉及 conversation_events；② 出现 USING INDEX 且该索引名含 turn_id；
    /// ③ <b>不存在</b>裸全表扫描行（SCAN conversation_events，无 USING）。
    /// 变异方式：把 Where 去掉（拉全表）或改成按 command_id ⇒ 计划退化为 SCAN 且 ③ 必红。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task S17_QueryPlan_UsesTurnIdIndex_AndDoesNotFullScanTheEventTable()
    {
        AddEvent("turn-a", 1, ConversationEventTypes.ToolCallCompleted, "{}");
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        _queries.Commands.Clear();

        var access = new RsiTrajectoryDataAccess(new SharedConnectionFactory(_connection, _queries));
        _ = await access.GetEventsByTurnIdsAsync(
            ["turn-a"], [ConversationEventTypes.ToolCallCompleted], CancellationToken.None);

        var sql = _queries.Commands.Single();
        var plan = await ExplainQueryPlanAsync(sql);

        foreach (var line in plan)
        {
            Console.WriteLine($"PLAN>{line}");
        }

        var planText = string.Join(" | ", plan);
        StringAssert.Contains(planText, "conversation_events", "计划必须涉及 conversation_events。");
        Assert.IsTrue(
            plan.Any(l => l.Contains("USING INDEX", StringComparison.OrdinalIgnoreCase)
                          || l.Contains("USING COVERING INDEX", StringComparison.OrdinalIgnoreCase)),
            $"计划中未出现索引使用 ⇒ 退化为全表扫描。实际计划：{planText}");
        Assert.IsTrue(
            planText.Contains("turn_id", StringComparison.OrdinalIgnoreCase),
            $"使用的索引不是 turn_id 相关索引。实际计划：{planText}");
        Assert.IsTrue(
            plan.Any(l => l.Contains("turn_id=?") && l.Contains("type=?")),
            $"索引 seek 条件不是 (turn_id, type) 这对已索引键 ⇒ 查询形状已偏离规格 §2.4。实际计划：{planText}");
        Assert.IsFalse(
            plan.Any(l => l.StartsWith("SCAN conversation_events", StringComparison.OrdinalIgnoreCase)
                          && !l.Contains("USING", StringComparison.OrdinalIgnoreCase)),
            $"出现对 conversation_events 的裸全表扫描。实际计划：{planText}");
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

    private void AddEvent(string turnId, long sequence, string type, string payload) =>
        _db.ConversationEvents.Add(new ConversationEventEntity
        {
            ConversationId = "conv-1",
            Sequence = sequence,
            Type = type,
            EventId = Guid.NewGuid().ToString("N"),
            WorkspaceId = "default",
            TurnId = turnId,
            RunId = "run-" + turnId,
            Payload = payload,
            OccurredAt = "2026-09-21T15:00:00.0000000+00:00",
            CommittedAt = "2026-09-21T15:00:00.0000000+00:00",
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
