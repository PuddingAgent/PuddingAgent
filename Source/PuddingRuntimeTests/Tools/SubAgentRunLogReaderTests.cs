using System.Text.Json;
using PuddingCode.Configuration;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 2026-11-14：主代理查看子代理日志（pool_action=logs）的底层读取器。
/// 钉住三件事：按 subSessionId 定位最新 run、只回尾部 N 条、畸形行不污染输出。
/// </summary>
[TestClass]
public sealed class SubAgentRunLogReaderTests
{
    private const string WorkspaceId = "default";
    private const string AgentId = "default.agent-001";
    private const string SubSessionId = "parent-session-sub-abc123";

    [TestMethod]
    public void FindLatest_PicksNewestRunForSameSubSession()
    {
        using var temp = TemporaryDirectory.Create();
        var paths = PuddingDataPaths.FromRoot(temp.Path);

        var older = CreateRun(paths, "run_20260101_100000_older000", SubSessionId, "completed");
        var newer = CreateRun(paths, "run_20260101_120000_newer000", SubSessionId, "running");
        CreateRun(paths, "run_20260101_130000_other000", "someone-else", "running");

        var found = SubAgentRunLogReader.TryFindLatestRunDirectory(paths, SubSessionId);

        Assert.IsNotNull(found);
        Assert.AreEqual(newer, found, "同名 subSessionId 有多个 run 时必须取最新那个");
        Assert.AreNotEqual(older, found);
    }

    [TestMethod]
    public void FindLatest_ReturnsNull_WhenSubSessionIdIsUnknown()
    {
        using var temp = TemporaryDirectory.Create();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        CreateRun(paths, "run_20260101_100000_only0000", SubSessionId, "running");

        // 正向对照：同一个根下已知 id 必须找得到，否则「找不到」不算证据。
        Assert.IsNotNull(SubAgentRunLogReader.TryFindLatestRunDirectory(paths, SubSessionId));
        Assert.IsNull(SubAgentRunLogReader.TryFindLatestRunDirectory(paths, "no-such-sub-session"));
        Assert.IsNull(SubAgentRunLogReader.TryFindLatestRunDirectory(paths, "  "));
    }

    [TestMethod]
    public void Read_ReturnsOnlyTheTail()
    {
        using var temp = TemporaryDirectory.Create();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        var runDir = CreateRun(paths, "run_20260101_100000_tail0000", SubSessionId, "running");

        File.WriteAllLines(Path.Combine(runDir, "events.jsonl"),
            Enumerable.Range(1, 30).Select(i =>
                $$"""{"eventType":"subagent.round.completed","timestamp":"2026-01-01T00:00:{{i:00}}+00:00"}"""));
        File.WriteAllLines(Path.Combine(runDir, "tools.jsonl"),
            Enumerable.Range(1, 30).Select(i =>
                $$"""{"toolName":"file_read","success":true,"durationMs":{{i}},"outputLength":100}"""));

        var snapshot = SubAgentRunLogReader.Read(runDir, tail: 5);

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(30, snapshot.EventCount, "总行数仍是 30");
        Assert.AreEqual(30, snapshot.ToolCallCount);
        Assert.AreEqual(5, snapshot.EventLines.Count, "只回尾部 5 条");
        Assert.AreEqual(5, snapshot.ToolLines.Count);
        // 30 条里取尾部 5 条 ⇒ 第 26~30 条：首 "26ms"、末 "30ms"。
        StringAssert.Contains(snapshot.ToolLines[0], "26ms");
        StringAssert.Contains(snapshot.ToolLines[^1], "30ms");
    }

    [TestMethod]
    public void Read_FormatsEventAndToolLinesCompactly()
    {
        using var temp = TemporaryDirectory.Create();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        var runDir = CreateRun(paths, "run_20260101_100000_fmt00000", SubSessionId, "running");

        File.WriteAllLines(Path.Combine(runDir, "events.jsonl"),
        [
            """{"eventType":"subagent.round.completed","timestamp":"2026-01-01T09:07:05+00:00","payload":{"round":1}}""",
        ]);
        File.WriteAllLines(Path.Combine(runDir, "tools.jsonl"),
        [
            """{"toolName":"file_read","success":true,"durationMs":12,"outputLength":3400}""",
            """{"toolName":"shell","success":false,"durationMs":7,"outputLength":0}""",
        ]);

        var snapshot = SubAgentRunLogReader.Read(runDir, tail: 10);

        Assert.IsNotNull(snapshot);
        Assert.AreEqual("09:07:05 subagent.round.completed", snapshot.EventLines[0]);
        Assert.AreEqual("file_read ok 12ms 3400B", snapshot.ToolLines[0]);
        Assert.AreEqual("shell FAIL 7ms 0B", snapshot.ToolLines[1], "success=false 必须显式标 FAIL");
    }

    [TestMethod]
    public void Read_SkipsMalformedAndBlankLines()
    {
        using var temp = TemporaryDirectory.Create();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        var runDir = CreateRun(paths, "run_20260101_100000_bad00000", SubSessionId, "running");

        File.WriteAllLines(Path.Combine(runDir, "events.jsonl"),
        [
            """{"eventType":"subagent.run.started","timestamp":"2026-01-01T00:00:00+00:00"}""",
            "",
            "not-json-at-all",
            """{"noEventType":true}""",
            """{"eventType":"subagent.round.completed","timestamp":"2026-01-01T00:00:01+00:00"}""",
        ]);

        var snapshot = SubAgentRunLogReader.Read(runDir, tail: 10);

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(2, snapshot.EventLines.Count, "只剩两条能解析出 eventType 的行");
        CollectionAssert.DoesNotContain(snapshot.EventLines.ToList(), "not-json-at-all");
    }

    [TestMethod]
    public void Read_ReturnsNull_ForMissingDirectory()
    {
        using var temp = TemporaryDirectory.Create();

        Assert.IsNull(SubAgentRunLogReader.Read(Path.Combine(temp.Path, "does-not-exist"), 10));
        Assert.IsNull(SubAgentRunLogReader.Read(null, 10));
        Assert.IsNull(SubAgentRunLogReader.ReadLatest(PuddingDataPaths.FromRoot(temp.Path), SubSessionId, 10));
    }

    [TestMethod]
    public void ReadLatest_EndToEnd_WritesRunHeader()
    {
        using var temp = TemporaryDirectory.Create();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        var runDir = CreateRun(paths, "run_20260101_100000_e2e00000", SubSessionId, "running");
        File.WriteAllLines(Path.Combine(runDir, "events.jsonl"),
        [
            """{"eventType":"subagent.round.completed","timestamp":"2026-01-01T00:00:01+00:00"}""",
        ]);
        File.WriteAllLines(Path.Combine(runDir, "tools.jsonl"),
        [
            """{"toolName":"file_search","success":true,"durationMs":9,"outputLength":21843}""",
        ]);

        var snapshot = SubAgentRunLogReader.ReadLatest(paths, SubSessionId, tail: 10);

        Assert.IsNotNull(snapshot);
        var text = snapshot.ToDisplayText();
        StringAssert.Contains(text, "run=run_20260101_100000_e2e00000");
        StringAssert.Contains(text, "status=running");
        StringAssert.Contains(text, "events=1");
        StringAssert.Contains(text, "tool_calls=1");
        StringAssert.Contains(text, "file_search ok 9ms 21843B");
    }

    // ===== 辅助 =====

    private static string CreateRun(
        PuddingDataPaths paths,
        string runId,
        string subSessionId,
        string status)
    {
        var runDir = paths.SubAgentRunRoot(WorkspaceId, AgentId, runId);
        Directory.CreateDirectory(runDir);

        var manifest = new
        {
            runId,
            parentSessionId = "parent-session",
            subSessionId,
            workspaceId = WorkspaceId,
            agentInstanceId = AgentId,
            templateId = "workspace-task-agent",
            task = "count things",
            status,
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
                "pudding-subagent-log-tests",
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
