using System.Text.Json;
using PuddingCode.Configuration;
using PuddingCode.Observability;
using PuddingPlatform.Services.Diagnostics;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class SessionTimelineRecorderTests
{
    [TestMethod]
    public async Task RecordAsync_WhenEnabled_WritesRuntimeActivityAndJsonl()
    {
        var root = CreateTempRoot();
        var sink = new CapturingRuntimeActivitySink();
        var recorder = new SessionTimelineRecorder(
            PuddingDataPaths.FromRoot(root),
            sink,
            new SessionTimelineRecorderOptions { Enabled = true });

        var trace = RuntimeTraceContext.CreateNew(
            sessionId: "session_1",
            workspaceId: "default",
            userId: "admin");

        await recorder.RecordAsync(new SessionTimelineRecord
        {
            Trace = trace,
            Component = RuntimeActivityComponents.AgentExecution,
            Stage = "chat.post.received",
            Operation = "chat.send",
            Status = RuntimeActivityStatuses.Succeeded,
            RecordedAtUtc = new DateTimeOffset(2026, 5, 31, 8, 9, 10, TimeSpan.Zero),
            DurationMs = 123,
            Metadata = new Dictionary<string, string>
            {
                ["messageId"] = "msg_1",
                ["dataChars"] = "42",
            },
        });

        Assert.AreEqual(1, sink.Records.Count);
        Assert.AreEqual(RuntimeActivityComponents.AgentExecution, sink.Records[0].Component);
        Assert.AreEqual("chat.send", sink.Records[0].Operation);
        Assert.AreEqual(RuntimePipelineStages.Request, sink.Records[0].Metadata!["stage"]);
        Assert.AreEqual("chat.post.received", sink.Records[0].Metadata!["stage_detail"]);
        Assert.AreEqual("msg_1", sink.Records[0].Metadata!["messageId"]);

        var file = Path.Combine(
            root,
            "logs",
            "diagnostics",
            "session-timeline",
            "20260531",
            "session_1.jsonl");
        Assert.IsTrue(File.Exists(file), $"Expected timeline JSONL at {file}");

        var line = File.ReadAllLines(file).Single();
        using var doc = JsonDocument.Parse(line);
        Assert.AreEqual(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual("session_1", doc.RootElement.GetProperty("sessionId").GetString());
        Assert.AreEqual("trace", doc.RootElement.GetProperty("recordKind").GetString());
        Assert.AreEqual(RuntimePipelineStages.Request, doc.RootElement.GetProperty("stage").GetString());
        Assert.AreEqual("chat.post.received", doc.RootElement.GetProperty("metadata").GetProperty("stage_detail").GetString());
        Assert.AreEqual("msg_1", doc.RootElement.GetProperty("metadata").GetProperty("messageId").GetString());
    }

    [TestMethod]
    public async Task RecordAsync_WhenDisabled_DoesNotWriteRuntimeActivityOrJsonl()
    {
        var root = CreateTempRoot();
        var sink = new CapturingRuntimeActivitySink();
        var recorder = new SessionTimelineRecorder(
            PuddingDataPaths.FromRoot(root),
            sink,
            new SessionTimelineRecorderOptions { Enabled = false });

        await recorder.RecordAsync(new SessionTimelineRecord
        {
            Trace = RuntimeTraceContext.CreateNew(sessionId: "session_2"),
            Component = RuntimeActivityComponents.SessionState,
            Stage = "sse.frame.flush.completed",
            Operation = "sse.flush",
            Status = RuntimeActivityStatuses.Succeeded,
            RecordedAtUtc = new DateTimeOffset(2026, 5, 31, 8, 9, 10, TimeSpan.Zero),
        });

        Assert.AreEqual(0, sink.Records.Count);
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "logs", "diagnostics")));
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-timeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class CapturingRuntimeActivitySink : IRuntimeActivitySink
    {
        public List<RuntimeActivity> Records { get; } = new();

        public Task RecordAsync(RuntimeActivity activity, CancellationToken ct = default)
        {
            Records.Add(activity);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RuntimeActivity>> QueryAsync(RuntimeActivityQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RuntimeActivity>>(Records);
    }
}
