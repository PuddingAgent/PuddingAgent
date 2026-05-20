using PuddingCode.Observability;
using PuddingCode.Runtime;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class RuntimeActivityExecutionLifecycleRecorderTests
{
    [TestMethod]
    public async Task RecordInstantAsync_Writes_RuntimeActivity()
    {
        var sink = new CapturingRuntimeActivitySink();
        var recorder = new RuntimeActivityExecutionLifecycleRecorder(sink);

        var record = new ExecutionLifecycleRecord
        {
            ExecutionId = "exec_1",
            TraceId = "trace_1",
            WorkspaceId = "default",
            SessionId = "session_1",
            AgentInstanceId = "agent_1",
            Component = "llm_gateway",
            Operation = "chat",
            Status = "succeeded",
            StartedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            DurationMs = 150,
            Summary = "LLM call completed",
        };

        await recorder.RecordInstantAsync(record);

        Assert.AreEqual(1, sink.Records.Count);
        var activity = sink.Records[0];
        Assert.AreEqual("llm_gateway", activity.Component);
        Assert.AreEqual("chat", activity.Operation);
        Assert.AreEqual("succeeded", activity.Status);
        Assert.AreEqual(150, activity.DurationMs);
        Assert.AreEqual("LLM call completed", activity.Summary);
    }

    [TestMethod]
    public async Task StartAsync_And_CompleteAsync_Records_Paired_Activities()
    {
        var sink = new CapturingRuntimeActivitySink();
        var recorder = new RuntimeActivityExecutionLifecycleRecorder(sink);

        var record = new ExecutionLifecycleRecord
        {
            ExecutionId = "exec_2",
            TraceId = "trace_2",
            WorkspaceId = "default",
            SessionId = "session_2",
            AgentInstanceId = "agent_2",
            Component = RuntimeActivityComponents.AgentExecution,
            Operation = "execute",
            Status = "started",
            StartedAtUtc = DateTimeOffset.UtcNow,
        };

        var activityId = await recorder.StartAsync(record);
        Assert.IsNotNull(activityId);
        Assert.AreNotEqual("", activityId);

        await recorder.CompleteAsync(activityId, RuntimeActivityStatuses.Succeeded, summary: "done");

        Assert.AreEqual(2, sink.Records.Count);
        Assert.AreEqual(RuntimeActivityStatuses.Started, sink.Records[0].Status);
        Assert.AreEqual(RuntimeActivityStatuses.Succeeded, sink.Records[1].Status);
        Assert.AreEqual("done", sink.Records[1].Summary);
        Assert.IsNull(sink.Records[1].ErrorMessage);
    }

    [TestMethod]
    public async Task CompleteAsync_With_Error_Records_Failure()
    {
        var sink = new CapturingRuntimeActivitySink();
        var recorder = new RuntimeActivityExecutionLifecycleRecorder(sink);

        var record = new ExecutionLifecycleRecord
        {
            ExecutionId = "exec_3",
            TraceId = "trace_3",
            WorkspaceId = "default",
            SessionId = "session_3",
            AgentInstanceId = "agent_3",
            Component = RuntimeActivityComponents.ToolRunner,
            Operation = "execute_tool",
            Status = "started",
            StartedAtUtc = DateTimeOffset.UtcNow,
        };

        var activityId = await recorder.StartAsync(record);
        await recorder.CompleteAsync(activityId, RuntimeActivityStatuses.Failed, error: "tool not found");

        Assert.AreEqual(2, sink.Records.Count);
        Assert.AreEqual(RuntimeActivityStatuses.Failed, sink.Records[1].Status);
        Assert.AreEqual("tool not found", sink.Records[1].ErrorMessage);
    }

    [TestMethod]
    public async Task RecordInstantAsync_With_Metadata_Preserves_Metadata()
    {
        var sink = new CapturingRuntimeActivitySink();
        var recorder = new RuntimeActivityExecutionLifecycleRecorder(sink);

        var record = new ExecutionLifecycleRecord
        {
            ExecutionId = "exec_4",
            TraceId = "trace_4",
            WorkspaceId = "default",
            SessionId = "session_4",
            AgentInstanceId = "agent_4",
            Component = "llm_gateway",
            Operation = "chat",
            Status = "succeeded",
            StartedAtUtc = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string>
            {
                ["provider_id"] = "openai",
                ["model_id"] = "gpt-4o",
                ["prompt_tokens"] = "1200",
                ["completion_tokens"] = "300",
            },
        };

        await recorder.RecordInstantAsync(record);

        Assert.AreEqual(1, sink.Records.Count);
        var metadata = sink.Records[0].Metadata;
        Assert.IsNotNull(metadata);
        Assert.AreEqual("openai", metadata["provider_id"]);
        Assert.AreEqual("gpt-4o", metadata["model_id"]);
    }

    [TestMethod]
    public async Task StartAsync_With_Empty_Metadata_Has_Null_Metadata()
    {
        var sink = new CapturingRuntimeActivitySink();
        var recorder = new RuntimeActivityExecutionLifecycleRecorder(sink);

        var record = new ExecutionLifecycleRecord
        {
            ExecutionId = "exec_5",
            TraceId = "trace_5",
            WorkspaceId = "default",
            SessionId = "session_5",
            AgentInstanceId = "agent_5",
            Component = "test",
            Operation = "test",
            Status = "started",
            StartedAtUtc = DateTimeOffset.UtcNow,
        };

        await recorder.StartAsync(record);
        Assert.IsNull(sink.Records[0].Metadata);
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
