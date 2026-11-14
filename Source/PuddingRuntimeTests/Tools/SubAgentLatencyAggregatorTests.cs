using System.Text.Json;
using PuddingCode.Configuration;
using PuddingCode.SubAgents;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 2026-11-14：延迟分解聚合器（诊断服务与 pool_action=logs 共用同一实现）。
/// 钉住三件事：配对各段耗时、畸形行不污染、logs 输出真的把「时间去哪了」摆出来。
/// </summary>
[TestClass]
public sealed class SubAgentLatencyAggregatorTests
{
    private const string WorkspaceId = "default";
    private const string AgentId = "default.agent-001";
    private const string SubSessionId = "parent-session-sub-lat";

    [TestMethod]
    public void Aggregator_ComputesLlmToolRoundsAndOverhead()
    {
        var aggregator = new SubAgentLatencyAggregator();
        foreach (var line in new[]
        {
            """{"eventType":"subagent.llm.started","timestamp":"2026-01-01T00:00:00+00:00"}""",
            """{"eventType":"subagent.llm.completed","timestamp":"2026-01-01T00:00:01+00:00"}""",
            """{"eventType":"subagent.round.completed","timestamp":"2026-01-01T00:00:01+00:00"}""",
            """{"eventType":"subagent.tool.completed","timestamp":"2026-01-01T00:00:01.250+00:00","payload":{"duration_ms":250}}""",
            """{"eventType":"subagent.llm.started","timestamp":"2026-01-01T00:00:02+00:00"}""",
            """{"eventType":"subagent.llm.completed","timestamp":"2026-01-01T00:00:03+00:00"}""",
            """{"eventType":"subagent.round.completed","timestamp":"2026-01-01T00:00:03+00:00"}""",
            """{"eventType":"subagent.tool.completed","timestamp":"2026-01-01T00:00:03.250+00:00","payload":{"duration_ms":250}}""",
        })
        {
            aggregator.AddEventLine(line);
        }

        var breakdown = aggregator.Build("run_x");

        Assert.AreEqual(2, breakdown.RoundCount, "轮次只数 round.completed");
        Assert.AreEqual(2, breakdown.ToolCallCount);
        Assert.AreEqual(2000, breakdown.LlmDurationMs, "两段 started→completed 配对差之和");
        Assert.AreEqual(500, breakdown.ToolDurationMs, "duration_ms 在 payload 里");
        Assert.AreEqual(3250, breakdown.TotalDurationMs);
        Assert.AreEqual(750, breakdown.OverheadMs, "overhead = total - llm - tool");
        Assert.AreEqual(61.5, breakdown.LlmPct);
        Assert.AreEqual(15.4, breakdown.ToolPct);
        Assert.AreEqual(23.1, breakdown.OverheadPct);
    }

    // 正向对照：畸形行 / 缺字段行 / 未知类型都不得被当成「有耗时的事件」。
    [TestMethod]
    public void Aggregator_IgnoresMalformedAndIrrelevantLines()
    {
        var aggregator = new SubAgentLatencyAggregator();
        aggregator.AddEventLine(null);
        aggregator.AddEventLine(string.Empty);
        aggregator.AddEventLine("not-json");
        aggregator.AddEventLine("""{"noEventType":true}""");
        aggregator.AddEventLine("""{"eventType":"subagent.run.started","timestamp":"2026-01-01T00:00:00+00:00"}""");

        var breakdown = aggregator.Build("run_y");

        Assert.AreEqual(0, breakdown.RoundCount);
        Assert.AreEqual(0, breakdown.ToolCallCount);
        Assert.AreEqual(0, breakdown.LlmDurationMs);
        Assert.AreEqual(0, breakdown.ToolDurationMs);
        Assert.AreEqual(0, breakdown.TotalDurationMs, "只有一条无关事件时首尾相同，跨度为 0");
    }

    // 反向对照：根级 duration_ms 是旧格式，仍要认（不能只认 payload）。
    [TestMethod]
    public void Aggregator_AcceptsLegacyRootLevelDuration()
    {
        var aggregator = new SubAgentLatencyAggregator();
        aggregator.AddEventLine("""{"eventType":"subagent.tool.completed","timestamp":"2026-01-01T00:00:00+00:00","duration_ms":123}""");

        var breakdown = aggregator.Build("run_z");

        Assert.AreEqual(1, breakdown.ToolCallCount);
        Assert.AreEqual(123, breakdown.ToolDurationMs);
    }

    // logs 输出必须真的把「时间去哪了」摆出来，而不只是列出事件。
    [TestMethod]
    public void DisplayText_ContainsLatencySection()
    {
        using var temp = TemporaryDirectory.Create();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        var runDir = CreateRun(paths, "run_20260101_100000_lat00001");
        File.WriteAllLines(Path.Combine(runDir, "events.jsonl"),
        [
            """{"eventType":"subagent.llm.started","timestamp":"2026-01-01T00:00:00+00:00"}""",
            """{"eventType":"subagent.llm.completed","timestamp":"2026-01-01T00:00:01+00:00"}""",
            """{"eventType":"subagent.round.completed","timestamp":"2026-01-01T00:00:01+00:00"}""",
        ]);

        var snapshot = SubAgentRunLogReader.Read(runDir, tail: 10);

        Assert.IsNotNull(snapshot);
        Assert.IsNotNull(snapshot.Latency, "快照必须带延迟分解");
        var text = snapshot.ToDisplayText();
        StringAssert.Contains(text, "--- latency ---");
        StringAssert.Contains(text, "total=1000ms");
        StringAssert.Contains(text, "llm=1000ms");
        StringAssert.Contains(text, "rounds=1");
    }

    private static string CreateRun(PuddingDataPaths paths, string runId)
    {
        var runDir = paths.SubAgentRunRoot(WorkspaceId, AgentId, runId);
        Directory.CreateDirectory(runDir);

        var manifest = new
        {
            runId,
            parentSessionId = "parent-session",
            subSessionId = SubSessionId,
            workspaceId = WorkspaceId,
            agentInstanceId = AgentId,
            templateId = "workspace-task-agent",
            task = "count things",
            status = "running",
            startedAt = "2026-01-01T00:00:00+00:00",
        };

        File.WriteAllText(
            Path.Combine(runDir, "run.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        return runDir;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pudding-subagent-latency-tests",
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
