using Microsoft.Data.Sqlite;
using PuddingCodeIndex.Storage;

namespace PuddingCodeIndexTests.Storage;

/// <summary>
/// 索引图删除的**查询形状**门禁（2026-10-02 高磁盘读取诊断的 C 项）。
/// <para>
/// 背景：<c>RemoveSymbolGraphForFileAsync</c> 曾对每个符号执行
/// <c>DELETE … WHERE Workspace/Project = … AND (SourceSymbolId=$s OR TargetSymbolId=$s)</c>。
/// 若该 OR 谓词只能落到主键的作用域前缀上，则每个符号都会重新遍历整个 workspace/project 分区，
/// 于是一次全量重建里的文件 × 符号数被放大成「符号数 × 分区大小」。
/// 修复把每个 OR 拆成 Source/Target 两次精确删除，使约束进入完整
/// <c>(WorkspaceId, ProjectId, SourceSymbolId)</c> / <c>(…, TargetSymbolId)</c>。
/// </para>
/// <para>
/// 本文件锁定两件与机器无关的事：① 精确删除的查询计划把符号列约束进索引（不是只约束作用域前缀）；
/// ② 引擎工作量（VDBE 步数，不是毫秒）由目标符号的关联规模决定，不随整个 project 图规模线性增长。
/// 度量对象是与被测方法相同的谓词形状；真实数据上的删除对照只能在 SQLite 在线备份副本上做。
/// </para>
/// </summary>
[TestClass]
public sealed class SqliteCodeIndexStoreRemoveGraphWorkTests
{
    private const string WorkspaceId = "ws-work";
    private const string TargetSymbolId = "sym-target";

    [TestMethod]
    public async Task ExactDeletes_PushTheSymbolColumnIntoTheIndexScope()
    {
        using var fixture = Fixture.Create();
        await fixture.Store.InitializeAsync();
        await fixture.SeedScopeAsync("proj-plan", unrelatedRows: 10);
        await fixture.SeedTargetSymbolAsync("proj-plan");

        // `EXPLAIN QUERY PLAN` 只对第一条语句出计划，所以四条删除分别取计划再合并。
        var deletes = new[]
        {
            ("CodeReferences", "SourceSymbolId"),
            ("CodeReferences", "TargetSymbolId"),
            ("CodeRelations", "SourceSymbolId"),
            ("CodeRelations", "TargetSymbolId"),
        };

        var plans = new List<string>();
        foreach (var (table, symbolColumn) in deletes)
        {
            plans.Add(await fixture.ExplainQueryPlanAsync($"""
                DELETE FROM {table}
                WHERE WorkspaceId = '{WorkspaceId}' AND ProjectId = 'proj-plan'
                  AND {symbolColumn} = '{TargetSymbolId}';
                """));
        }

        var combined = string.Join(Environment.NewLine, plans);

        // 每一条删除都必须以 (WorkspaceId, ProjectId, <SymbolCol>) 做索引查找。
        // 修复前的 OR 形式在同一基线上只约束到 (WorkspaceId=?, ProjectId=?)（诊断报告已记录该计划，
        // 本文件的 ORDeletes_… 用例与 VDBE 对照共同固定这一差异）。
        foreach (var plan in plans)
        {
            StringAssert.Contains(plan, "SEARCH ", plan);
            Assert.IsTrue(
                plan.Contains("SourceSymbolId=?", StringComparison.Ordinal)
                || plan.Contains("TargetSymbolId=?", StringComparison.Ordinal),
                $"计划必须把符号列约束进索引：{plan}");
        }

        StringAssert.Contains(combined, "SEARCH CodeReferences", combined);
        StringAssert.Contains(combined, "SEARCH CodeRelations", combined);
        Assert.IsFalse(combined.Contains("SCAN ", StringComparison.Ordinal), combined);
    }

    [TestMethod]
    public async Task ORDeletes_DoNotConstrainTheSymbolColumnTheSameWay()
    {
        using var fixture = Fixture.Create();
        await fixture.Store.InitializeAsync();
        await fixture.SeedScopeAsync("proj-or", unrelatedRows: 10);

        var orPlan = await fixture.ExplainQueryPlanAsync($"""
            DELETE FROM CodeReferences
            WHERE WorkspaceId = '{WorkspaceId}' AND ProjectId = 'proj-or'
              AND (SourceSymbolId = '{TargetSymbolId}' OR TargetSymbolId = '{TargetSymbolId}');
            """);

        // 这条用例不是「旧形式必须坏」，而是固定被诊断出来的对照形状：OR 形式下符号列是否进入索引约束
        // 由 SQLite 决定（诊断实库上只落到主键作用域前缀）。因此这里只断言计划可读且不含全表扫描，
        // 真正的判据是下面的 VDBE 步数对照。
        Assert.IsFalse(orPlan.Contains("SCAN CodeReferences", StringComparison.Ordinal), orPlan);
    }

    /// <summary>
    /// C 的核心判据：删除代价由目标符号的关联规模决定，不随整个 project 图规模重复线性增长。
    /// <para>
    /// 度量用 VDBE 步数（SQLite <c>sqlite3_progress_handler</c>，每步回调一次），与机器性能无关。
    /// 为保持可重复，度量用与删除相同的 WHERE 谓词做只读 SELECT（与诊断报告同一口径），不修改数据。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task ExactGraphReads_DoNotScaleWithTheScopeGraphSize()
    {
        using var fixture = Fixture.Create();
        await fixture.Store.InitializeAsync();

        const int smallRows = 2_000;
        const int largeRows = 8_000;

        await fixture.SeedScopeAsync("proj-small", smallRows);
        await fixture.SeedScopeAsync("proj-large", largeRows);
        await fixture.SeedTargetSymbolAsync("proj-small");
        await fixture.SeedTargetSymbolAsync("proj-large");

        var small = MeasureBothForms(fixture, "proj-small");
        var large = MeasureBothForms(fixture, "proj-large");

        // ① 精确形式（修复后的形状）：4 倍图规模下工作量基本不变。
        Assert.IsTrue(
            large.ExactTotal <= small.ExactTotal * 2,
            $"精确删除的步数必须与作用域图规模无关：2k={small} 8k={large}");

        // ② OR 形式（修复前的形状）：图规模 4 倍时表明确实放大 —— 这是被诊断出来的放大本身。
        //    若某天 SQLite 对 OR 形式也做出等价的索引约束，这条断言会失败，那正是「可以安全回到 OR」的证据，
        //    此时应连同本文件一起重新评估，而不是放宽断言。
        Assert.IsTrue(
            large.OrTotal >= small.OrTotal * 3,
            $"OR 形式的步数必须随作用域图规模放大：2k={small} 8k={large}");

        // ③ 4 倍规模下的差距是一个数量级以上。
        Assert.IsTrue(
            large.ExactTotal * 10 < large.OrTotal,
            $"8k 图规模上精确形式必须比 OR 形式少一个数量级：2k={small} 8k={large}");
    }

    /// <summary>六条被测谓词各自的 VDBE 步数（四条精确 + 两条 OR）。</summary>
    private sealed record Measurements(
        long ReferencesSource,
        long ReferencesTarget,
        long RelationsSource,
        long RelationsTarget,
        long OrReferences,
        long OrRelations)
    {
        public long ExactTotal => ReferencesSource + ReferencesTarget + RelationsSource + RelationsTarget;

        public long OrTotal => OrReferences + OrRelations;

        public override string ToString() =>
            $"exact[refsSrc={ReferencesSource} refsTgt={ReferencesTarget} relsSrc={RelationsSource} relsTgt={RelationsTarget}] " +
            $"or[refs={OrReferences} rels={OrRelations}] exactTotal={ExactTotal} orTotal={OrTotal}";
    }

    private static Measurements MeasureBothForms(Fixture fixture, string projectId) =>
        new(
            fixture.MeasureDeleteVdbeSteps(DeleteExact("CodeReferences", "SourceSymbolId", projectId)),
            fixture.MeasureDeleteVdbeSteps(DeleteExact("CodeReferences", "TargetSymbolId", projectId)),
            fixture.MeasureDeleteVdbeSteps(DeleteExact("CodeRelations", "SourceSymbolId", projectId)),
            fixture.MeasureDeleteVdbeSteps(DeleteExact("CodeRelations", "TargetSymbolId", projectId)),
            fixture.MeasureDeleteVdbeSteps(DeleteOr("CodeReferences", projectId)),
            fixture.MeasureDeleteVdbeSteps(DeleteOr("CodeRelations", projectId)));

    private static string DeleteExact(string table, string symbolColumn, string projectId) => $"""
        DELETE FROM {table}
        WHERE WorkspaceId = '{WorkspaceId}' AND ProjectId = '{projectId}' AND {symbolColumn} = '{TargetSymbolId}';
        """;

    private static string DeleteOr(string table, string projectId) => $"""
        DELETE FROM {table}
        WHERE WorkspaceId = '{WorkspaceId}' AND ProjectId = '{projectId}'
          AND (SourceSymbolId = '{TargetSymbolId}' OR TargetSymbolId = '{TargetSymbolId}');
        """;

    /// <summary>文件型 SQLite 库 + 产品 provider 上的 <see cref="SqliteCodeIndexStore"/>。</summary>
    private sealed class Fixture : IDisposable
    {
        private Fixture(string root, string databasePath)
        {
            Root = root;
            DatabasePath = databasePath;
            Store = new SqliteCodeIndexStore(databasePath);
        }

        public string Root { get; }

        public string DatabasePath { get; }

        public SqliteCodeIndexStore Store { get; }

        public static Fixture Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(), "pudding-remove-work-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new Fixture(root, Path.Combine(root, "db", "code-index.db"));
        }

        /// <summary>
        /// 在给定 project 中铺 <paramref name="unrelatedRows"/> 条与目标符号无关的图行：
        /// 用途是让「作用域分区的规模」与「目标符号的关联规模」解耦。
        /// </summary>
        public async Task SeedScopeAsync(string projectId, int unrelatedRows)
        {
            await ExecuteAsync($"""
                INSERT INTO CodeRelations
                    (WorkspaceId, ProjectId, SourceSymbolId, TargetSymbolId, Kind, SourceLine, SourceFilePath, CreatedAtUtc)
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {unrelatedRows})
                SELECT '{WorkspaceId}', '{projectId}', 'unrelated-src-' || i, 'unrelated-dst-' || i,
                       'Calls', i, 'unrelated-' || i || '.cs', '2026-10-02T00:00:00Z'
                FROM n;

                INSERT INTO CodeReferences
                    (WorkspaceId, ProjectId, SourceSymbolId, TargetSymbolId, SourceFilePath, SourceLine, SourceText, ObservedAtUtc)
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {unrelatedRows})
                SELECT '{WorkspaceId}', '{projectId}', 'unrelated-src-' || i, 'unrelated-dst-' || i,
                       'unrelated-' || i || '.cs', i, 'x', '2026-10-02T00:00:00Z'
                FROM n;
                """);
        }

        /// <summary>目标符号的两条边：一条出边、一条入边（精确删除必须分别命中的两种形状）。</summary>
        public async Task SeedTargetSymbolAsync(string projectId)
        {
            await ExecuteAsync($"""
                INSERT OR REPLACE INTO CodeRelations
                    (WorkspaceId, ProjectId, SourceSymbolId, TargetSymbolId, Kind, SourceLine, SourceFilePath, CreatedAtUtc)
                VALUES ('{WorkspaceId}', '{projectId}', '{TargetSymbolId}', 'other-dst', 'Calls', 1, 'target.cs', '2026-10-02T00:00:00Z'),
                       ('{WorkspaceId}', '{projectId}', 'other-src', '{TargetSymbolId}', 'Calls', 2, 'other.cs', '2026-10-02T00:00:00Z');

                INSERT OR REPLACE INTO CodeReferences
                    (WorkspaceId, ProjectId, SourceSymbolId, TargetSymbolId, SourceFilePath, SourceLine, SourceText, ObservedAtUtc)
                VALUES ('{WorkspaceId}', '{projectId}', '{TargetSymbolId}', 'other-dst', 'target.cs', 1, 'x', '2026-10-02T00:00:00Z'),
                       ('{WorkspaceId}', '{projectId}', 'other-src', '{TargetSymbolId}', 'other.cs', 2, 'y', '2026-10-02T00:00:00Z');
                """);
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

        private SqliteConnection OpenWriter()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }

        /// <summary>执行 <paramref name="sql"/> 并统计 SQLite 虚拟机的指令步数（与机器性能无关）。</summary>
        public long MeasureVdbeSteps(string sql) => MeasureVdbeStepsCore(sql, rollback: false);

        /// <summary>
        /// 度量 <c>DELETE</c> 形式本身：在显式事务里执行后 <c>ROLLBACK</c>，所以同一条语句可以反复度量，
        /// 夹具的数据也保持不变。
        /// <para>
        /// 不用「相同 WHERE 的 SELECT」代替 DELETE：SELECT 的取列会改变索引的覆盖性，从而改变 SQLite 的
        /// 计划选择（实测 <c>TargetSymbolId</c> 的 SELECT 会退化为作用域前缀扫描，而同一谓词的 DELETE
        /// 使用 <c>IX_…_Target</c> 索引查找）。删除路径的代价只能由删除语句自己度量。
        /// </para>
        /// </summary>
        public long MeasureDeleteVdbeSteps(string sql) => MeasureVdbeStepsCore(sql, rollback: true);

        private long MeasureVdbeStepsCore(string sql, bool rollback)
        {
            SQLitePCL.Batteries_V2.Init();

            var openResult = SQLitePCL.raw.sqlite3_open(DatabasePath, out var database);
            Assert.AreEqual(0, openResult, "无法打开测试数据库");

            try
            {
                if (rollback)
                {
                    var beginResult = SQLitePCL.raw.sqlite3_exec(database, "BEGIN", out var beginError);
                    Assert.AreEqual(0, beginResult, $"无法开启度量事务：{beginError}");
                }

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

                if (rollback)
                {
                    var rollbackResult = SQLitePCL.raw.sqlite3_exec(database, "ROLLBACK", out var rollbackError);
                    Assert.AreEqual(0, rollbackResult, $"无法回滚度量事务：{rollbackError}");
                }

                return steps;
            }
            finally
            {
                // 连接关闭会丢弃未结束的事务，夹具数据因此不会被度量改动。
                SQLitePCL.raw.sqlite3_close(database);
            }
        }

        public void Dispose()
        {
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
