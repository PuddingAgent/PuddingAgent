using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Configuration;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Diagnostics;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class BenchmarkEvaluationServiceTests
{
    [TestMethod]
    public async Task EvaluateAsync_ScoresArtifactsBudgetsAndParentSubagentUsage()
    {
        var root = CreateTempRoot();
        var paths = PuddingDataPaths.FromRoot(root);
        var now = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var time = new FixedTimeProvider(now);
        var runService = new BenchmarkRunService(paths, time);
        var benchmarkCase = CreateCase(new BenchmarkEvaluationContract
        {
            Artifacts =
            [
                new BenchmarkArtifactExpectation
                {
                    Path = "report.md",
                    RequiredContents = ["confirmed", "next action"],
                },
            ],
            MaxDurationSeconds = 60,
            MaxRounds = 6,
            MaxTotalTokens = 10_000,
            MaxCostCny = 1m,
            MaxFailedToolResults = 0,
        });
        var run = await runService.CreateAsync(
            benchmarkCase,
            "bench-workspace",
            "session-1",
            new BenchmarkSeedResultDto(),
            CancellationToken.None);

        var workspace = paths.WorkspaceRoot("bench-workspace");
        Directory.CreateDirectory(workspace);
        var reportPath = Path.Combine(workspace, "report.md");
        await File.WriteAllTextAsync(reportPath, "confirmed facts\nnext action");
        File.SetLastWriteTimeUtc(reportPath, now.AddSeconds(5).UtcDateTime);

        await using var fixture = await DatabaseFixture.CreateAsync();
        fixture.Db.TokenUsageEvents.AddRange(
            Usage("main-1", "session-1", null, null, "deepseek-v4-pro", 1_000, 100, 0.04m, 0),
            Usage("sub-1", "child-1", "session-1", "worker-1", "deepseek-v4-flash", 2_000, 200, 0.02m, 2));
        fixture.Db.ChatExecutionCommands.Add(new ChatExecutionCommandEntity
        {
            CommandId = "command-1",
            BatchId = "batch-1",
            WorkspaceId = "bench-workspace",
            SessionId = "session-1",
            MessageId = "assistant-1",
            UserMessageId = "user-1",
            TurnId = "turn-1",
            AgentInstanceId = "agent-1",
            Status = "succeeded",
            CreatedAt = now.ToUnixTimeMilliseconds(),
            StartedAt = now.ToUnixTimeMilliseconds(),
            CompletedAt = now.AddSeconds(30).ToUnixTimeMilliseconds(),
        });
        fixture.Db.SubAgentRuns.Add(new SubAgentRunEntity
        {
            RunId = "subrun-1",
            ParentSessionId = "session-1",
            SubSessionId = "child-1",
            WorkspaceId = "bench-workspace",
            AgentInstanceId = "worker-1",
            TemplateId = "developer",
            Status = "completed",
            StartedAt = now.ToString("O"),
            CompletedAt = now.AddSeconds(10).ToString("O"),
            ArchivePath = "runs/subrun-1",
            TaskPlanningMetadataJson = "{\"role_in_plan\":\"developer\",\"profile_id\":\"flash-role\"}",
        });
        await fixture.Db.SaveChangesAsync();

        var diagnostics = new SessionBenchmarkDiagnosticsService(paths, fixture.Db);
        var evaluator = new BenchmarkEvaluationService(paths, fixture.Db, runService, diagnostics, time);
        var result = await evaluator.EvaluateAsync(run.RunId, ct: CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual("passed", result.Status);
        Assert.AreEqual(100, result.InstructionScore);
        Assert.AreEqual(3_300L, result.Metrics.TotalTokens);
        Assert.AreEqual(30_000L, result.Metrics.DurationMs);
        Assert.AreEqual(3, result.Metrics.Rounds);
        Assert.AreEqual("succeeded", result.Metrics.TerminalStatus);
        Assert.AreEqual(0, result.Metrics.BlockingToolFailures);
        Assert.AreEqual(2, result.Metrics.ModelUsage.Count);
        Assert.IsTrue(result.Metrics.ModelUsage.Any(item =>
            item.Scope == "subagent"
            && item.ModelId == "deepseek-v4-flash"
            && item.RoleId == "developer"
            && item.ProfileId == "flash-role"));
        Assert.IsTrue(File.Exists(Path.Combine(
            paths.RuntimeRoot,
            "benchmark-runs",
            $"{run.RunId}.evaluation.json")));
    }

    [TestMethod]
    public async Task EvaluateAsync_UsesLlmCallCountWhenRoundTelemetryIsMissing_AndFailsTerminalCommand()
    {
        var root = CreateTempRoot();
        var paths = PuddingDataPaths.FromRoot(root);
        Directory.CreateDirectory(paths.WorkspaceRoot("default"));
        var runService = new BenchmarkRunService(paths);
        var run = await runService.CreateAsync(
            CreateCase(new BenchmarkEvaluationContract
            {
                Artifacts = [new BenchmarkArtifactExpectation { Path = "required.md" }],
                MaxRounds = 1,
            }),
            "default",
            "session-4",
            new BenchmarkSeedResultDto(),
            CancellationToken.None);
        await using var fixture = await DatabaseFixture.CreateAsync();
        var usage = Usage("main-missing-round", "session-4", null, null, "deepseek-v4-pro", 100, 10, 0.01m, 0);
        usage.TurnRound = null;
        fixture.Db.TokenUsageEvents.Add(usage);
        fixture.Db.ChatExecutionCommands.Add(new ChatExecutionCommandEntity
        {
            CommandId = "command-failed",
            BatchId = "batch-failed",
            WorkspaceId = "default",
            SessionId = "session-4",
            MessageId = "assistant-failed",
            UserMessageId = "user-failed",
            TurnId = "turn-failed",
            AgentInstanceId = "agent-1",
            Status = "failed",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        await fixture.Db.SaveChangesAsync();
        var diagnostics = new SessionBenchmarkDiagnosticsService(paths, fixture.Db);
        var evaluator = new BenchmarkEvaluationService(paths, fixture.Db, runService, diagnostics);

        var result = await evaluator.EvaluateAsync(run.RunId, ct: CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.Metrics.Rounds);
        Assert.AreEqual("failed", result.Metrics.TerminalStatus);
        Assert.IsFalse(result.Checks.Single(check => check.Id == "terminal-status").Passed);
    }

    [TestMethod]
    public async Task EvaluateAsync_WithoutArtifactOracle_IsUnscoredInsteadOfFalsePass()
    {
        var root = CreateTempRoot();
        var paths = PuddingDataPaths.FromRoot(root);
        var runService = new BenchmarkRunService(paths);
        var run = await runService.CreateAsync(
            CreateCase(null),
            "default",
            "session-2",
            new BenchmarkSeedResultDto(),
            CancellationToken.None);
        await using var fixture = await DatabaseFixture.CreateAsync();
        var diagnostics = new SessionBenchmarkDiagnosticsService(paths, fixture.Db);
        var evaluator = new BenchmarkEvaluationService(paths, fixture.Db, runService, diagnostics);

        var result = await evaluator.EvaluateAsync(run.RunId, ct: CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual("unscored", result.Status);
        Assert.IsNull(result.InstructionScore);
        Assert.IsNull(result.OverallScore);
    }

    [TestMethod]
    public async Task EvaluateAsync_MissingRequiredArtifact_FailsInstructionCheck()
    {
        var root = CreateTempRoot();
        var paths = PuddingDataPaths.FromRoot(root);
        Directory.CreateDirectory(paths.WorkspaceRoot("default"));
        var runService = new BenchmarkRunService(paths);
        var run = await runService.CreateAsync(
            CreateCase(new BenchmarkEvaluationContract
            {
                Artifacts = [new BenchmarkArtifactExpectation { Path = "required.md" }],
            }),
            "default",
            "session-3",
            new BenchmarkSeedResultDto(),
            CancellationToken.None);
        await using var fixture = await DatabaseFixture.CreateAsync();
        var diagnostics = new SessionBenchmarkDiagnosticsService(paths, fixture.Db);
        var evaluator = new BenchmarkEvaluationService(paths, fixture.Db, runService, diagnostics);

        var result = await evaluator.EvaluateAsync(run.RunId, ct: CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual("failed", result.Status);
        Assert.AreEqual(0, result.InstructionScore);
        Assert.IsFalse(result.Checks.Single(check => check.Category == "instruction").Passed);
    }

    private static BenchmarkCaseConfig CreateCase(BenchmarkEvaluationContract? evaluation) => new()
    {
        Id = "case-1",
        Version = "2",
        Title = "Case",
        Category = "test",
        Prompt = "请整理当前目录并生成报告。",
        Evaluation = evaluation,
    };

    private static TokenUsageEventEntity Usage(
        string sourceId,
        string sessionId,
        string? parentSessionId,
        string? subAgentId,
        string modelId,
        long promptTokens,
        long completionTokens,
        decimal cost,
        int round) => new()
    {
        SourceType = "test",
        SourceId = sourceId,
        WorkspaceId = "bench-workspace",
        SessionId = sessionId,
        ParentSessionId = parentSessionId,
        ProviderId = "deepseek",
        ModelId = modelId,
        OccurredAtUtc = DateTimeOffset.UtcNow,
        YearMonth = "2026-08",
        PromptTokens = promptTokens,
        CompletionTokens = completionTokens,
        TotalTokens = promptTokens + completionTokens,
        CacheHitTokens = promptTokens / 2,
        CacheMissTokens = promptTokens / 2,
        TotalCost = cost,
        TurnRound = round,
        SubAgentId = subAgentId,
    };

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-benchmark-evaluation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class DatabaseFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public PlatformDbContext Db { get; }

        private DatabaseFixture(SqliteConnection connection, PlatformDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public static async Task<DatabaseFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new PlatformDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new DatabaseFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
