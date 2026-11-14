using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Serialization;
using PuddingCode.SubAgents;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 2026-11-14：子代理「运行中」可观测性。
/// 三件事在这里被钉住：
/// ① run.json 只在终态带轮次/工具调用数 ⇒ 运行中必须从增量日志现算；
/// ② 终态仍以 manifest 为准（日志不得覆盖权威值）；
/// ③ 延迟分解的事件字段名是 eventType、duration_ms 在 payload 里——读错就永远返回全 0。
/// </summary>
[TestClass]
public sealed class SubAgentRunProgressTests
{
    private const string WorkspaceId = "default";
    private const string AgentInstanceId = "default.agent-001";

    // ===== 读取器 =====

    [TestMethod]
    public void Reader_Counts_Round_Completed_And_Tool_Lines()
    {
        using var temp = TemporaryDirectory.Create();
        var runDir = Path.Combine(temp.Path, "run_1");
        Directory.CreateDirectory(runDir);

        File.WriteAllLines(Path.Combine(runDir, "events.jsonl"),
        [
            """{"eventType":"subagent.run.started","timestamp":"2026-01-01T00:00:00+00:00"}""",
            """{"eventType":"subagent.llm.completed","timestamp":"2026-01-01T00:00:01+00:00"}""",
            """{"eventType":"subagent.round.completed","timestamp":"2026-01-01T00:00:01+00:00"}""",
            """{"eventType":"subagent.round.completed","timestamp":"2026-01-01T00:00:02+00:00"}""",
            """{"eventType":"subagent.tool.completed","timestamp":"2026-01-01T00:00:03+00:00"}""",
        ]);
        File.WriteAllLines(Path.Combine(runDir, "tools.jsonl"),
        [
            """{"toolCallId":"a","toolName":"file_read"}""",
            """{"toolCallId":"b","toolName":"shell"}""",
            """{"toolCallId":"c","toolName":"shell"}""",
        ]);

        var progress = SubAgentRunProgressReader.TryRead(runDir);

        Assert.IsTrue(progress.HasValue);
        // 正向对照：llm.completed / tool.completed 都不是轮次事件，只有 round.completed 算轮。
        Assert.AreEqual(2, progress.Value.Rounds, "轮次只数 round.completed");
        Assert.AreEqual(3, progress.Value.ToolCalls, "工具调用数 = tools.jsonl 的非空行数");
    }

    [TestMethod]
    public void Reader_Ignores_Blank_Lines_In_Tools_Audit()
    {
        using var temp = TemporaryDirectory.Create();
        var runDir = Path.Combine(temp.Path, "run_blank");
        Directory.CreateDirectory(runDir);
        File.WriteAllLines(Path.Combine(runDir, "tools.jsonl"),
        [
            """{"toolCallId":"a"}""",
            "",
            "   ",
            """{"toolCallId":"b"}""",
        ]);

        var progress = SubAgentRunProgressReader.TryRead(runDir);

        Assert.IsTrue(progress.HasValue);
        Assert.AreEqual(2, progress.Value.ToolCalls);
        Assert.AreEqual(0, progress.Value.Rounds, "没有 events.jsonl 时轮次为 0，而不是抛异常");
    }

    [TestMethod]
    public void Reader_Returns_Null_When_No_Logs_Or_No_Directory()
    {
        using var temp = TemporaryDirectory.Create();
        var emptyRunDir = Path.Combine(temp.Path, "run_empty");
        Directory.CreateDirectory(emptyRunDir);

        Assert.IsFalse(SubAgentRunProgressReader.TryRead(emptyRunDir).HasValue, "目录存在但没有日志 ⇒ null");
        Assert.IsFalse(SubAgentRunProgressReader.TryRead(Path.Combine(temp.Path, "missing")).HasValue);
        Assert.IsFalse(SubAgentRunProgressReader.TryRead(null).HasValue);
        Assert.IsFalse(SubAgentRunProgressReader.TryRead("   ").HasValue);
    }

    // ===== 诊断集成 =====

    [TestMethod]
    public async Task Diagnostics_Reports_Live_Rounds_For_Running_Run()
    {
        using var temp = TemporaryDirectory.Create();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        var runId = "run_20260101_000000_live0001";
        var runDir = paths.SubAgentRunRoot(WorkspaceId, AgentInstanceId, runId);
        Directory.CreateDirectory(runDir);

        // 运行中的 manifest 没有总轮次/工具调用数——这正是缺陷的现场形状。
        await WriteManifestAsync(runDir, runId, status: "running", totalRounds: null, totalToolCalls: null);
        await File.WriteAllLinesAsync(
            Path.Combine(runDir, "events.jsonl"),
            RoundEvents(rounds: 4, toolCalls: 0));
        await File.WriteAllLinesAsync(
            Path.Combine(runDir, "tools.jsonl"),
            Enumerable.Range(1, 3).Select(i => $$"""{"toolCallId":"t{{i}}","toolName":"file_read"}"""));

        var report = await CreateService(paths).GetDiagnosticsAsync(new SubAgentDiagnosticsRequest
        {
            WorkspaceId = WorkspaceId,
            AgentInstanceId = AgentInstanceId,
        });

        var run = report.RecentRuns.Single();
        Assert.AreEqual("running", run.Status);
        Assert.AreEqual(4, run.TotalRounds, "运行中必须报真实轮次，而不是 0");
        Assert.AreEqual(3, run.TotalToolCalls);
    }

    [TestMethod]
    public async Task Diagnostics_Prefers_Manifest_For_Terminal_Run()
    {
        using var temp = TemporaryDirectory.Create();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        var runId = "run_20260101_000000_done0001";
        var runDir = paths.SubAgentRunRoot(WorkspaceId, AgentInstanceId, runId);
        Directory.CreateDirectory(runDir);

        // manifest 说 7 轮 5 次；日志里故意放 4 轮 3 次——终态必须以 manifest 为准。
        await WriteManifestAsync(runDir, runId, status: "completed", totalRounds: 7, totalToolCalls: 5);
        await File.WriteAllLinesAsync(
            Path.Combine(runDir, "events.jsonl"),
            RoundEvents(rounds: 4, toolCalls: 0));
        await File.WriteAllLinesAsync(
            Path.Combine(runDir, "tools.jsonl"),
            Enumerable.Range(1, 3).Select(i => $$"""{"toolCallId":"t{{i}}"}"""));

        var report = await CreateService(paths).GetDiagnosticsAsync(new SubAgentDiagnosticsRequest
        {
            WorkspaceId = WorkspaceId,
            AgentInstanceId = AgentInstanceId,
        });

        var run = report.RecentRuns.Single();
        Assert.AreEqual(7, run.TotalRounds, "终态以 manifest 为准，不被增量日志覆盖");
        Assert.AreEqual(5, run.TotalToolCalls);
    }

    [TestMethod]
    public async Task Diagnostics_Falls_Back_To_Zero_When_Running_Run_Has_No_Logs()
    {
        using var temp = TemporaryDirectory.Create();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        var runId = "run_20260101_000000_nolog001";
        var runDir = paths.SubAgentRunRoot(WorkspaceId, AgentInstanceId, runId);
        Directory.CreateDirectory(runDir);
        await WriteManifestAsync(runDir, runId, status: "running", totalRounds: null, totalToolCalls: null);

        var report = await CreateService(paths).GetDiagnosticsAsync(new SubAgentDiagnosticsRequest
        {
            WorkspaceId = WorkspaceId,
            AgentInstanceId = AgentInstanceId,
        });

        var run = report.RecentRuns.Single();
        Assert.AreEqual(0, run.TotalRounds, "没有日志时回落 0，不抛异常");
        Assert.AreEqual(0, run.TotalToolCalls);
    }

    // ===== 延迟分解：字段名与层级 =====

    [TestMethod]
    public async Task LatencyBreakdown_Reads_EventType_And_PayloadDuration()
    {
        using var temp = TemporaryDirectory.Create();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        var runId = "run_20260101_000000_lat00001";
        var runDir = paths.SubAgentRunRoot(WorkspaceId, AgentInstanceId, runId);
        Directory.CreateDirectory(runDir);
        await WriteManifestAsync(runDir, runId, status: "running", totalRounds: null, totalToolCalls: null);

        File.WriteAllLines(Path.Combine(runDir, "events.jsonl"),
        [
            """{"eventType":"subagent.llm.started","timestamp":"2026-01-01T00:00:00+00:00"}""",
            """{"eventType":"subagent.llm.completed","timestamp":"2026-01-01T00:00:01+00:00"}""",
            """{"eventType":"subagent.round.completed","timestamp":"2026-01-01T00:00:01+00:00"}""",
            """{"eventType":"subagent.tool.completed","timestamp":"2026-01-01T00:00:01.250+00:00","payload":{"duration_ms":250}}""",
            """{"eventType":"subagent.llm.started","timestamp":"2026-01-01T00:00:02+00:00"}""",
            """{"eventType":"subagent.llm.completed","timestamp":"2026-01-01T00:00:03+00:00"}""",
            """{"eventType":"subagent.round.completed","timestamp":"2026-01-01T00:00:03+00:00"}""",
            """{"eventType":"subagent.tool.completed","timestamp":"2026-01-01T00:00:03.250+00:00","payload":{"duration_ms":250}}""",
        ]);

        var breakdown = await CreateService(paths).GetRunLatencyBreakdownAsync(runId);

        Assert.IsNotNull(breakdown, "events.jsonl 存在时必须返回分解，而不是 null");
        Assert.AreEqual(2, breakdown.RoundCount, "轮次来自 round.completed");
        Assert.AreEqual(2, breakdown.ToolCallCount, "工具调用来自 tool.completed");
        Assert.AreEqual(2000, breakdown.LlmDurationMs, "LLM 时长来自 llm.started/completed 的配对差");
        Assert.AreEqual(500, breakdown.ToolDurationMs, "工具时长必须从 payload.duration_ms 读");
    }

    // ===== 辅助 =====

    private static SubAgentDiagnosticsService CreateService(PuddingDataPaths paths) =>
        new(paths, NullLogger<SubAgentDiagnosticsService>.Instance);

    private static IEnumerable<string> RoundEvents(int rounds, int toolCalls)
    {
        // 用 JsonSerializer 拼行：直接写原始字符串会在 “}}}+引号” 处撞上 CS9007。
        for (var i = 1; i <= rounds; i++)
        {
            yield return JsonSerializer.Serialize(new
            {
                eventType = "subagent.round.completed",
                timestamp = $"2026-01-01T00:00:{i:00}+00:00",
                payload = new { round = i },
            });
        }

        for (var i = 1; i <= toolCalls; i++)
        {
            yield return JsonSerializer.Serialize(new
            {
                eventType = "subagent.tool.completed",
                timestamp = $"2026-01-01T00:00:{i:00}+00:00",
                payload = new { duration_ms = 10 },
            });
        }
    }

    private static async Task WriteManifestAsync(
        string runDir,
        string runId,
        string status,
        int? totalRounds,
        int? totalToolCalls)
    {
        var manifest = new SubAgentRunManifest
        {
            RunId = runId,
            ParentSessionId = "parent-session",
            SubSessionId = "parent-session/sub/sub-agent",
            WorkspaceId = WorkspaceId,
            AgentInstanceId = AgentInstanceId,
            TemplateId = "researcher",
            Task = "count things",
            Status = status,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = status == "running" ? null : DateTimeOffset.UtcNow,
            TotalRounds = totalRounds,
            TotalToolCalls = totalToolCalls,
        };

        await File.WriteAllTextAsync(
            Path.Combine(runDir, "run.json"),
            JsonSerializer.Serialize(manifest, PuddingJsonContracts.PrettyJson));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pudding-platform-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
