using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 2026-11-14：run 目录定位从「全量枚举 events.jsonl」改成三层目录列举。
/// 这里钉住它在标准布局下仍能命中（含跨 workspace），以及未知 runId 时明确返回 null。
/// </summary>
[TestClass]
public sealed class SubAgentDiagnosticsRunLookupTests
{
    [TestMethod]
    public async Task LatencyBreakdown_FindsRunUnderStandardLayout()
    {
        using var temp = TemporaryDirectory.Create();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        var runId = "run_20260101_100000_lookup1";
        var runDir = CreateRun(paths, "ws-a", "agent-1", runId);
        WriteEvents(runDir);

        var breakdown = await CreateService(paths).GetRunLatencyBreakdownAsync(runId);

        Assert.IsNotNull(breakdown, "标准布局下必须能找到 run 目录");
        Assert.AreEqual(runId, breakdown.RunId);
        Assert.AreEqual(1, breakdown.RoundCount);
        Assert.AreEqual(1, breakdown.ToolCallCount);
        Assert.AreEqual(500, breakdown.LlmDurationMs);
    }

    // 跨 workspace：定位不得只在第一个 workspace 里找。
    [TestMethod]
    public async Task LatencyBreakdown_SearchesAllWorkspaces()
    {
        using var temp = TemporaryDirectory.Create();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        CreateRun(paths, "ws-aaa", "agent-1", "run_20260101_090000_other01");
        var targetRunId = "run_20260101_100000_lookup2";
        var runDir = CreateRun(paths, "ws-zzz", "agent-9", targetRunId);
        WriteEvents(runDir);

        var breakdown = await CreateService(paths).GetRunLatencyBreakdownAsync(targetRunId);

        Assert.IsNotNull(breakdown, "目标 run 在第二个 workspace 下，也必须找到");
        Assert.AreEqual(targetRunId, breakdown.RunId);
    }

    [TestMethod]
    public async Task LatencyBreakdown_ReturnsNull_ForUnknownOrEmptyRunId()
    {
        using var temp = TemporaryDirectory.Create();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        var runDir = CreateRun(paths, "ws-a", "agent-1", "run_20260101_100000_lookup3");
        WriteEvents(runDir);

        var service = CreateService(paths);

        // 正向对照：同一个根下已知 runId 必须找得到，否则「找不到」不算证据。
        Assert.IsNotNull(await service.GetRunLatencyBreakdownAsync("run_20260101_100000_lookup3"));
        Assert.IsNull(await service.GetRunLatencyBreakdownAsync("run_20260101_999999_missing"));
        Assert.IsNull(await service.GetRunLatencyBreakdownAsync("   "));
    }

    // ===== 辅助 =====

    private static SubAgentDiagnosticsService CreateService(PuddingDataPaths paths) =>
        new(paths, NullLogger<SubAgentDiagnosticsService>.Instance);

    private static string CreateRun(
        PuddingDataPaths paths,
        string workspaceId,
        string agentInstanceId,
        string runId)
    {
        var runDir = paths.SubAgentRunRoot(workspaceId, agentInstanceId, runId);
        Directory.CreateDirectory(runDir);

        var manifest = new
        {
            runId,
            parentSessionId = "parent-session",
            subSessionId = "parent-session-sub-x",
            workspaceId,
            agentInstanceId,
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

    private static void WriteEvents(string runDir)
    {
        File.WriteAllLines(Path.Combine(runDir, "events.jsonl"),
        [
            """{"eventType":"subagent.llm.started","timestamp":"2026-01-01T00:00:00+00:00"}""",
            """{"eventType":"subagent.llm.completed","timestamp":"2026-01-01T00:00:00.500+00:00"}""",
            """{"eventType":"subagent.round.completed","timestamp":"2026-01-01T00:00:00.500+00:00"}""",
            """{"eventType":"subagent.tool.completed","timestamp":"2026-01-01T00:00:01+00:00","payload":{"duration_ms":200}}""",
        ]);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pudding-run-lookup-tests",
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
