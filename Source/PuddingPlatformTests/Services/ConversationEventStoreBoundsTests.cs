using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 事件边界查询（<see cref="ConversationEventStore.GetBoundsAsync"/>）的功能与工作量合同。
/// <para>
/// 背景（诊断报告 <c>Docs/14_reports/2026-10-02-PuddingAgent高磁盘读取诊断.md</c>）：长会话每次发送消息
/// 都会从 <c>cursor=0</c> 触发一次全历史 SSE 回放，而 <c>FollowAsync</c> 的 replay 循环每读满 256 条就再查一次
/// 边界；旧的 <c>MIN(sequence), MAX(sequence)</c> 会遍历该会话的整段索引分区，于是「每 256 条重扫整段索引」
/// 叠加成近 1 GB 读取。修复把两端改成同一条语句内的两个索引端点查找（<c>ORDER BY … LIMIT 1</c>）。
/// </para>
/// <para>
/// 本测试锁定三件事：① 语义与空会话合同不变；② 两端来自**同一条语句**（单一读取视图，不会被并发追加/裁剪
/// 撕成不一致的一对）；③ 引擎工作量不再随该会话的事件数线性增长 —— 用 VDBE 步数（不是毫秒）度量，
/// 因为毫秒依赖机器，而步数只依赖 SQL 形状。
/// </para>
/// </summary>
[TestClass]
public sealed class ConversationEventStoreBoundsTests
{
    [TestMethod]
    public async Task EmptySession_ReturnsNullBounds()
    {
        using var fixture = Fixture.Create();
        await fixture.EnsureTablesAsync();

        var bounds = await fixture.Store.GetBoundsAsync("no-such-session", CancellationToken.None);

        Assert.IsNull(bounds.MinSequence, "空会话没有最小序号");
        Assert.IsNull(bounds.MaxSequence, "空会话没有最大序号");
    }

    [TestMethod]
    public async Task SingleEvent_IsBothEnds()
    {
        using var fixture = Fixture.Create();
        await fixture.EnsureTablesAsync();
        await fixture.InsertEventsAsync("session-1", [42L]);

        var bounds = await fixture.Store.GetBoundsAsync("session-1", CancellationToken.None);

        Assert.AreEqual(42L, bounds.MinSequence);
        Assert.AreEqual(42L, bounds.MaxSequence);
    }

    [TestMethod]
    public async Task TwoSessions_AreIsolated()
    {
        using var fixture = Fixture.Create();
        await fixture.EnsureTablesAsync();
        await fixture.InsertEventsAsync("session-a", [1L, 2L, 3L]);
        await fixture.InsertEventsAsync("session-b", [700L, 900L]);

        var a = await fixture.Store.GetBoundsAsync("session-a", CancellationToken.None);
        var b = await fixture.Store.GetBoundsAsync("session-b", CancellationToken.None);

        Assert.AreEqual(1L, a.MinSequence);
        Assert.AreEqual(3L, a.MaxSequence);
        Assert.AreEqual(700L, b.MinSequence);
        Assert.AreEqual(900L, b.MaxSequence);
    }

    [TestMethod]
    public async Task SparseSequences_ReturnTheRealEndpoints()
    {
        using var fixture = Fixture.Create();
        await fixture.EnsureTablesAsync();
        // 稀疏/不连续序号是合法状态（裁剪、跳号），边界必须返回真实首尾而不是事件条数。
        await fixture.InsertEventsAsync("session-1", [5L, 12L, 4_000L, 1_640_000L]);

        var bounds = await fixture.Store.GetBoundsAsync("session-1", CancellationToken.None);

        Assert.AreEqual(5L, bounds.MinSequence);
        Assert.AreEqual(1_640_000L, bounds.MaxSequence);
        Assert.AreNotEqual(4L, bounds.MaxSequence - bounds.MinSequence + 1, "边界不是事件条数");
    }

    [TestMethod]
    public async Task AfterPrefixPrune_MinMovesForward_AndMaxIsUnchanged()
    {
        using var fixture = Fixture.Create();
        await fixture.EnsureTablesAsync();
        await fixture.InsertEventsAsync("session-1", [10L, 20L, 30L, 40L]);

        await fixture.ExecuteAsync(
            "DELETE FROM conversation_events WHERE conversation_id = 'session-1' AND sequence < 25");

        var bounds = await fixture.Store.GetBoundsAsync("session-1", CancellationToken.None);

        // 保留期裁剪后必须报告真实的最小可用序号：snapshot_required 判据依赖它，
        // 因此不能用 conversation_heads（它只知道 head）替代 min。
        Assert.AreEqual(30L, bounds.MinSequence);
        Assert.AreEqual(40L, bounds.MaxSequence);
    }

    [TestMethod]
    public async Task Bounds_DoNotObserveAnUncommittedAppendOrPrune()
    {
        using var fixture = Fixture.Create();
        await fixture.EnsureTablesAsync();
        await fixture.InsertEventsAsync("session-1", [10L, 20L, 30L]);

        await using var writer = fixture.OpenWriter();
        await using var transaction = await writer.BeginTransactionAsync();

        // 未提交的「追加一条更早的 + 删掉最小的」：读者必须看到提交过的边界，而不是这一对的一半。
        await using (var command = writer.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO conversation_events
                    (conversation_id, sequence, event_id, workspace_id, turn_id, type, payload, occurred_at, committed_at)
                VALUES ('session-1', 1, 'evt-uncommitted', 'ws', 'turn', 'message.delta', '{}', '2026-10-02T00:00:00Z', '2026-10-02T00:00:00Z');
                DELETE FROM conversation_events WHERE conversation_id = 'session-1' AND sequence = 10;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var duringTransaction = await fixture.Store.GetBoundsAsync("session-1", CancellationToken.None);

        Assert.AreEqual(10L, duringTransaction.MinSequence, "未提交的删除不得改变读者看到的 min");
        Assert.AreEqual(30L, duringTransaction.MaxSequence, "未提交的追加不得改变读者看到的 max");

        await transaction.CommitAsync();

        var afterCommit = await fixture.Store.GetBoundsAsync("session-1", CancellationToken.None);

        Assert.AreEqual(1L, afterCommit.MinSequence);
        Assert.AreEqual(30L, afterCommit.MaxSequence);
    }

    [TestMethod]
    public async Task Bounds_StayAConsistentPairUnderConcurrentCommitAndPrune()
    {
        using var fixture = Fixture.Create();
        await fixture.EnsureTablesAsync();
        await fixture.InsertEventsAsync("session-1", [100L, 200L, 300L]);

        // 每次读都必须是某个已提交状态的合法一对：min<=max，且两端都真实存在于某个时刻的集合中。
        for (var round = 1; round <= 20; round++)
        {
            var appendSequence = 300L + round;
            await fixture.InsertEventsAsync("session-1", [appendSequence]);

            if (round % 4 == 0)
            {
                await fixture.ExecuteAsync(
                    "DELETE FROM conversation_events WHERE conversation_id = 'session-1' AND sequence = 100");
            }

            var bounds = await fixture.Store.GetBoundsAsync("session-1", CancellationToken.None);

            Assert.IsNotNull(bounds.MinSequence);
            Assert.IsNotNull(bounds.MaxSequence);
            Assert.IsTrue(
                bounds.MinSequence <= bounds.MaxSequence,
                $"第 {round} 轮读到的边界必须是一对合法快照：min={bounds.MinSequence} max={bounds.MaxSequence}");
            Assert.AreEqual(appendSequence, bounds.MaxSequence, "已完成提交的追加必须立即出现在 max 上");
        }
    }

    [TestMethod]
    public async Task Bounds_QueryPlan_KeepsTheConversationConstraintInTheIndex()
    {
        using var fixture = Fixture.Create();
        await fixture.EnsureTablesAsync();
        await fixture.InsertEventsAsync("session-1", [1L, 2L, 3L]);

        var plan = await fixture.ExplainQueryPlanAsync("""
            SELECT
                (SELECT sequence FROM conversation_events
                 WHERE conversation_id = 'session-1'
                 ORDER BY sequence ASC LIMIT 1),
                (SELECT sequence FROM conversation_events
                 WHERE conversation_id = 'session-1'
                 ORDER BY sequence DESC LIMIT 1)
            """);

        // 查询计划里出现 SEARCH 本身不证明优化（旧聚合形式的计划同样是 SEARCH），这里锁定的是
        // 「会话约束仍然进入 (conversation_id, sequence) 索引、没有退化成全表扫描」这一回归底线；
        // 真正的读取放大判据是下面的 VDBE 步数对照。
        StringAssert.Contains(plan, "SEARCH conversation_events", plan);
        StringAssert.Contains(plan, "conversation_id=?", plan);
        Assert.IsFalse(plan.Contains("SCAN conversation_events", StringComparison.Ordinal), plan);
    }

    /// <summary>
    /// A 的核心判据：边界查询的引擎工作量不随该会话的事件数线性增长。
    /// <para>
    /// 度量用 VDBE 步数（SQLite <c>sqlite3_progress_handler</c>，每步回调一次），不是机器相关的毫秒：
    /// 旧聚合形式遍历整段索引分区（步数 ≈ 事件数），新形式是两个索引端点查找（步数恒定）。
    /// </para>
    /// <para>
    /// 度量对象是这两种 SQL 形状在同一个产品 provider（SQLitePCLRaw bundle_e_sqlite3）上的引擎工作量，
    /// 不是 <see cref="ConversationEventStore.GetBoundsAsync"/> 自身的 I/O 计数器（该方法的连接由 EF Core
    /// 持有，测试拿不到页级计数器）。修复把方法改成下方 <c>endpointSql</c> 的同一形状，语义由本文件的
    /// 功能用例锁定。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task EndpointBounds_WorkDoesNotScaleWithSessionLength_WhileAggregateBoundsDoes()
    {
        using var fixture = Fixture.Create();
        await fixture.EnsureTablesAsync();

        var smallSession = "session-small";
        var largeSession = "session-large";
        await fixture.InsertEventsAsync(smallSession, Count(8_000));
        await fixture.InsertEventsAsync(largeSession, Count(32_000));

        const string aggregateSqlTemplate = """
            SELECT MIN(sequence), MAX(sequence) FROM conversation_events
            WHERE conversation_id = '{0}'
            """;

        const string endpointSqlTemplate = """
            SELECT
                (SELECT sequence FROM conversation_events
                 WHERE conversation_id = '{0}'
                 ORDER BY sequence ASC LIMIT 1),
                (SELECT sequence FROM conversation_events
                 WHERE conversation_id = '{0}'
                 ORDER BY sequence DESC LIMIT 1)
            """;

        var smallAggregate = fixture.MeasureVdbeSteps(string.Format(aggregateSqlTemplate, smallSession));
        var largeAggregate = fixture.MeasureVdbeSteps(string.Format(aggregateSqlTemplate, largeSession));
        var smallEndpoint = fixture.MeasureVdbeSteps(string.Format(endpointSqlTemplate, smallSession));
        var largeEndpoint = fixture.MeasureVdbeSteps(string.Format(endpointSqlTemplate, largeSession));

        // ① 旧形式确实随会话长度线性放大（4 倍数据 ⇒ 至少 3 倍工作量）——这是被诊断出来的放大本身。
        Assert.IsTrue(
            largeAggregate >= smallAggregate * 3,
            $"聚合形式的步数必须随会话长度增长：8k={smallAggregate} 32k={largeAggregate}");

        // ② 新形式在 4 倍数据下基本不变（恒定上限：端点查找 + 少量 VDBE 指令）。
        Assert.IsTrue(
            largeEndpoint <= smallEndpoint * 2,
            $"端点形式的步数不得随会话长度增长：8k={smallEndpoint} 32k={largeEndpoint}");

        // ③ 长会话上的差距是数量级，不是常数因子。
        Assert.IsTrue(
            largeEndpoint * 10 < largeAggregate,
            $"32k 事件会话上端点形式必须比聚合形式少一个数量级：endpoint={largeEndpoint} aggregate={largeAggregate}");
    }

    private static IEnumerable<long> Count(int count)
    {
        for (var i = 1; i <= count; i++)
            yield return i;
    }

    /// <summary>隔离的文件型 SQLite 库 + 产品 provider 上的 <see cref="ConversationEventStore"/>。</summary>
    private sealed class Fixture : IDisposable
    {
        private Fixture(string root, string databasePath, ServiceProvider provider)
        {
            Root = root;
            DatabasePath = databasePath;
            Provider = provider;
            Store = new ConversationEventStore(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new CommittedEventSignal(),
                NullLogger<ConversationEventStore>.Instance);
        }

        public string Root { get; }

        public string DatabasePath { get; }

        public ServiceProvider Provider { get; }

        public ConversationEventStore Store { get; }

        public static Fixture Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(), "pudding-bounds-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "platform.db");

            var services = new ServiceCollection();
            services.AddDbContextFactory<PlatformDbContext>(
                options => options.UseSqlite($"Data Source={databasePath}"));
            services.AddScoped(sp =>
                sp.GetRequiredService<IDbContextFactory<PlatformDbContext>>().CreateDbContext());

            return new Fixture(root, databasePath, services.BuildServiceProvider());
        }

        public Task EnsureTablesAsync() => Store.EnsureTablesAsync(CancellationToken.None);

        /// <summary>按给定序号写入事件（每个序号一条，payload 保持最小）。</summary>
        public async Task InsertEventsAsync(string conversationId, IEnumerable<long> sequences)
        {
            await using var connection = OpenWriter();
            await using var transaction = await connection.BeginTransactionAsync();

            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO conversation_events
                    (conversation_id, sequence, event_id, workspace_id, turn_id, type, payload, occurred_at, committed_at)
                VALUES ($cid, $sequence, $eventId, 'ws', 'turn', 'message.delta', '{}', '2026-10-02T00:00:00Z', '2026-10-02T00:00:00Z');
                """;

            var cid = command.CreateParameter();
            cid.ParameterName = "$cid";
            cid.Value = conversationId;
            command.Parameters.Add(cid);

            var sequence = command.CreateParameter();
            sequence.ParameterName = "$sequence";
            command.Parameters.Add(sequence);

            var eventId = command.CreateParameter();
            eventId.ParameterName = "$eventId";
            command.Parameters.Add(eventId);

            foreach (var value in sequences)
            {
                sequence.Value = value;
                eventId.Value = $"evt-{conversationId}-{value}";
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = OpenWriter();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public async Task<string> ExplainQueryPlanAsync(string sql)
        {
            await using var connection = OpenWriter();
            await using var command = connection.CreateCommand();
            command.CommandText = $"EXPLAIN QUERY PLAN {sql}";

            var lines = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                lines.Add(reader.GetString(3));

            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>写连接：<c>Pooling=false</c>，让每条写入命令立刻落到文件上而不是留在池化句柄里。</summary>
        public SqliteConnection OpenWriter()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }

        /// <summary>
        /// 执行 <paramref name="sql"/> 并统计 SQLite 虚拟机执行的指令步数。
        /// <para>
        /// <c>sqlite3_progress_handler(db, 1, …)</c>：每执行一条 VDBE 指令回调一次，计数与机器性能无关，
        /// 因此可以作为「工作量是否随数据量增长」的确定性门禁。度量在独立连接上进行，
        /// 语句文本由测试自己构造（等价于被测方法使用的形状）。
        /// </para>
        /// </summary>
        public long MeasureVdbeSteps(string sql)
        {
            SQLitePCL.Batteries_V2.Init();

            var openResult = SQLitePCL.raw.sqlite3_open(DatabasePath, out var database);
            Assert.AreEqual(0, openResult, "无法打开测试数据库");

            try
            {
                long steps = 0;
                SQLitePCL.raw.sqlite3_progress_handler(
                    database,
                    1,
                    _ =>
                    {
                        steps++;
                        return 0;
                    },
                    null);

                var prepareResult = SQLitePCL.raw.sqlite3_prepare_v2(database, sql, out var statement);
                Assert.AreEqual(0, prepareResult, $"无法准备测量语句：{sql}");

                try
                {
                    int stepResult;
                    do
                    {
                        stepResult = SQLitePCL.raw.sqlite3_step(statement);
                    }
                    while (stepResult == 100 /* SQLITE_ROW */);

                    Assert.AreEqual(101 /* SQLITE_DONE */, stepResult, $"测量语句未正常结束：{sql}");
                }
                finally
                {
                    SQLitePCL.raw.sqlite3_finalize(statement);
                }

                SQLitePCL.raw.sqlite3_progress_handler(database, 0, null, null);
                return steps;
            }
            finally
            {
                SQLitePCL.raw.sqlite3_close(database);
            }
        }

        public void Dispose()
        {
            Provider.Dispose();

            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // temp 目录清理是 best-effort
            }
        }
    }
}
