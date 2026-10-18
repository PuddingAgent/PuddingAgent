using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingConversationReadModel;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.AgentChat;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 阶段 C3b：百万事件查询门禁（Plan §10.1/§11 阶段 C）。
/// <para>
/// 默认不跑（1.82M 事件 fixture 构建与测量耗时较大）；显式设置 <c>PUDDING_QUERY_GATE=1</c> 后运行：
/// <code>$env:PUDDING_QUERY_GATE='1'; dotnet test Source/PuddingPlatformTests --filter Name=QueryGate</code>
/// 断言：打开/历史/过程查询的 SQL 计划无会话级全表扫描，且 p95 满足方案通过线；结果写入 temp/test-out。
/// </para>
/// </summary>
[TestClass]
public sealed class ConversationOpenQueryPerformanceGateTests
{
    private const int EventCount = 1_820_000;
    private const string ConversationId = "conv-gate";
    private const string GateEnvironmentVariable = "PUDDING_QUERY_GATE";

    [TestMethod]
    public async Task QueryGate_MillionEvents_NoConversationScanAndP95WithinBudget()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(GateEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                $"Set {GateEnvironmentVariable}=1 to run the million-event query gate.");
            return;
        }

        using var temp = new GateFixture();
        await using (var db = new PlatformDbContext(temp.Options))
        {
            await db.Database.EnsureCreatedAsync();
            await BuildEventsAsync(db);
            await BuildReadModelAsync(db);
        }

        await AssertQueryPlansAsync(temp);

        var service = new ConversationOpenQueryService(
            temp.DbFactory,
            NullLogger<ConversationOpenQueryService>.Instance);

        var open = await MeasureAsync(
            "open",
            () => service.OpenAsync(ConversationId, messageLimit: 20),
            warmup: 2,
            iterations: 20);
        var history = await MeasureAsync(
            "history",
            () => service.GetHistoryPageAsync(new ConversationHistoryQuery(ConversationId, Limit: 20)),
            warmup: 2,
            iterations: 20);
        var process = await MeasureAsync(
            "process",
            () => service.GetProcessPageAsync(new ConversationProcessQuery(ConversationId, Limit: 100)),
            warmup: 2,
            iterations: 20);

        var snapshot = await service.OpenAsync(ConversationId, messageLimit: 20);
        Assert.AreEqual((long)EventCount, snapshot.EventHead);
        Assert.AreEqual((long)EventCount, snapshot.SnapshotCursor);

        Assert.IsTrue(
            open.P95Ms <= 200,
            $"open p95 {open.P95Ms:F1}ms exceeds 200ms target");
        Assert.IsTrue(
            history.P95Ms <= 300,
            $"history p95 {history.P95Ms:F1}ms exceeds 300ms target");
        Assert.IsTrue(
            process.P95Ms <= 250,
            $"process p95 {process.P95Ms:F1}ms exceeds 250ms target");

        var reportPath = Path.Combine(
            RepoRoot(),
            "temp",
            "test-out",
            $"query-gate-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, BuildReport(open, history, process), Encoding.UTF8);
        Console.WriteLine($"query gate report: {reportPath}");
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 12 && directory is not null; depth++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PuddingAgentNetwork.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Cannot locate repository root above " + AppContext.BaseDirectory);
    }

    private static async Task BuildEventsAsync(PlatformDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();

        await ExecuteAsync(connection, "PRAGMA journal_mode=MEMORY;");
        await ExecuteAsync(connection, "PRAGMA synchronous=OFF;");
        await ExecuteAsync(connection, "PRAGMA temp_store=MEMORY;");

        var stopwatch = Stopwatch.StartNew();
        await ExecuteAsync(connection,
            $$"""
             WITH RECURSIVE seq(n) AS (
                 SELECT 1
                 UNION ALL
                 SELECT n + 1 FROM seq WHERE n < {{EventCount}}
             )
             INSERT INTO conversation_events
                 (conversation_id, sequence, event_id, workspace_id, turn_id, message_id,
                  type, schema_version, payload, occurred_at, committed_at)
             SELECT
                 '{{ConversationId}}',
                 n,
                 'evt-gate-' || n,
                 'ws',
                 'turn-' || ((n - 1) / 1000 + 1),
                 'msg-' || (n % 50 + 1),
                 CASE
                     WHEN n % 1000 = 0 THEN 'turn.completed'
                     WHEN n % 10 = 0 THEN 'tool.call.completed'
                     ELSE 'message.content.appended'
                 END,
                 1,
                 '{"delta":"x"}',
                 '2026-10-18T00:00:00.0000000+00:00',
                 '2026-10-18T00:00:00.0000000+00:00'
             FROM seq;
             """);
        stopwatch.Stop();
        Console.WriteLine($"[query-gate] inserted {EventCount:N0} events in {stopwatch.Elapsed.TotalSeconds:F1}s");
    }

    private static async Task BuildReadModelAsync(PlatformDbContext db)
    {
        db.ConversationHeads.Add(new ConversationHeadEntity
        {
            ConversationId = ConversationId,
            HeadSequence = EventCount,
        });
        db.ConversationProjectionCheckpoints.Add(new ConversationProjectionCheckpointEntity
        {
            ConversationId = ConversationId,
            ProjectedThrough = EventCount,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
        });
        db.ConversationReadStates.Add(new ConversationReadStateEntity
        {
            ConversationId = ConversationId,
            InitializationState = (int)ReadModelInitializationState.Ready,
            CurrentTurnId = "turn-1820",
            ReadModelVersion = ConversationReadModelLimits.CurrentReadModelVersion,
            Revision = 9,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
        });

        for (var index = 1; index <= 50; index++)
        {
            db.ChatMessages.Add(new ChatMessageEntity
            {
                MessageId = $"msg-{index}",
                SessionId = ConversationId,
                WorkspaceId = "ws",
                AgentInstanceId = "agent-1",
                AgentTemplateId = "tpl",
                Role = index % 2 == 0 ? "user" : "agent",
                Content = new string('x', 600),
                TurnId = $"turn-{1791 + index}",
                CreatedAt = 1_000 + index,
            });
        }

        for (var turn = 1791; turn <= 1820; turn++)
        {
            db.ConversationTurnSummaries.Add(new ConversationTurnSummaryEntity
            {
                ConversationId = ConversationId,
                TurnId = $"turn-{turn}",
                RootRunId = $"run-{turn}",
                TerminalState = (int)TurnTerminalState.Completed,
                HasAssistantBody = true,
                ActivePreview = "preview",
                ProcessCompleteness = (int)SummaryCompleteness.Complete,
                FirstSequence = (long)(turn - 1) * 1000 + 1,
                LastSequence = (long)turn * 1000,
                SummaryRevision = 1,
                ReadModelVersion = ConversationReadModelLimits.CurrentReadModelVersion,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task AssertQueryPlansAsync(GateFixture temp)
    {
        await using var db = new PlatformDbContext(temp.Options);
        var plans = new StringBuilder();

        async Task AssertNoScanAsync(string label, string sql)
        {
            var plan = string.Join(" | ", await ExplainAsync(db, sql));
            plans.AppendLine($"[{label}] {plan}");
            Assert.IsFalse(
                plan.Contains("SCAN conversation_events", StringComparison.OrdinalIgnoreCase)
                && !plan.Contains("USING COVERING INDEX", StringComparison.OrdinalIgnoreCase),
                $"{label} must not scan conversation_events: {plan}");
            Assert.IsTrue(
                plan.Contains("USING", StringComparison.OrdinalIgnoreCase),
                $"{label} must use an index: {plan}");
        }

        await AssertNoScanAsync(
            "process-candidates",
            $"""
             EXPLAIN QUERY PLAN
             SELECT sequence, event_id, type
             FROM conversation_events
             WHERE conversation_id = '{ConversationId}'
               AND sequence <= {EventCount}
               AND sequence < {EventCount + 1}
             ORDER BY sequence DESC
             LIMIT 400;
             """);
        await AssertNoScanAsync(
            "event-head-max",
            $"EXPLAIN QUERY PLAN SELECT MAX(sequence) FROM conversation_events WHERE conversation_id = '{ConversationId}';");

        var messagePlan = string.Join(
            " | ",
            await ExplainAsync(
                db,
                $"EXPLAIN QUERY PLAN SELECT Id FROM ChatMessages WHERE SessionId = '{ConversationId}' ORDER BY CreatedAt DESC, Id DESC LIMIT 21;"));
        plans.AppendLine($"[messages] {messagePlan}");
        Assert.IsFalse(messagePlan.Contains("SCAN ChatMessages", StringComparison.OrdinalIgnoreCase), messagePlan);
        Assert.IsTrue(messagePlan.Contains("USING", StringComparison.OrdinalIgnoreCase), messagePlan);

        await File.WriteAllTextAsync(
            Path.Combine(RepoRoot(), "temp", "test-out", "query-gate-plans.txt"),
            plans.ToString(),
            Encoding.UTF8);
    }

    private static async Task<GateMeasurement> MeasureAsync<T>(
        string label,
        Func<Task<T>> action,
        int warmup,
        int iterations)
    {
        for (var index = 0; index < warmup; index++)
            await action();

        var samples = new List<double>(iterations);
        for (var index = 0; index < iterations; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            await action();
            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        var p95Index = Math.Max(0, (int)Math.Ceiling(samples.Count * 0.95) - 1);
        return new GateMeasurement(
            label,
            samples[0],
            samples[samples.Count / 2],
            samples[p95Index],
            samples[^1],
            samples.Count);
    }

    private static string BuildReport(params GateMeasurement[] measurements)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"fixture events={EventCount:N0} conversation={ConversationId}");
        builder.AppendLine($"utc={DateTimeOffset.UtcNow:O}");
        foreach (var measurement in measurements)
        {
            builder.AppendLine(
                $"{measurement.Label}: n={measurement.Samples} min={measurement.MinMs:F1}ms " +
                $"p50={measurement.P50Ms:F1}ms p95={measurement.P95Ms:F1}ms max={measurement.MaxMs:F1}ms");
        }

        return builder.ToString();
    }

    private static async Task<IReadOnlyList<string>> ExplainAsync(PlatformDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var details = new List<string>();
        while (await reader.ReadAsync())
            details.Add(reader.IsDBNull(3) ? string.Empty : reader.GetString(3));
        return details;
    }

    private static async Task ExecuteAsync(System.Data.Common.DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync();
    }

    private sealed record GateMeasurement(
        string Label,
        double MinMs,
        double P50Ms,
        double P95Ms,
        double MaxMs,
        int Samples);

    private sealed class GateFixture : IDisposable
    {
        public GateFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "pudding-query-gate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite($"Data Source={Path.Combine(Root, "platform.db")}")
                .Options;
            DbFactory = new PlatformDbContextFactory(Options);
        }

        public string Root { get; }
        public DbContextOptions<PlatformDbContext> Options { get; }
        public PlatformDbContextFactory DbFactory { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup of temp data outside the repo
            }
        }
    }
}
