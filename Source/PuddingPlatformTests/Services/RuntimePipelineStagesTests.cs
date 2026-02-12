using PuddingCode.Observability;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class RuntimePipelineStagesTests
{
    [TestMethod]
    public void Enrich_Normalizes_Custom_Stage_And_Preserves_Detail()
    {
        var metadata = new Dictionary<string, string>
        {
            ["stage"] = "chat.post.returned",
        };

        var enriched = RuntimePipelineStages.Enrich(metadata, RuntimePipelineStages.Unknown);

        Assert.AreEqual(RuntimePipelineStages.Complete, enriched["stage"]);
        Assert.AreEqual("chat.post.returned", enriched["stage_detail"]);
        Assert.AreEqual("normalized_from_stage", enriched["stage_normalization"]);
        Assert.IsFalse(enriched.ContainsKey("stage_warning"));
        Assert.AreEqual("110", enriched["stage_order"]);
    }

    [TestMethod]
    public void Enrich_Classifies_Sse_Completion_As_Stream_Delivery()
    {
        var metadata = new Dictionary<string, string>
        {
            ["stage"] = "sse.batch.flush.completed",
        };

        var enriched = RuntimePipelineStages.Enrich(metadata, RuntimePipelineStages.Unknown);

        Assert.AreEqual(RuntimePipelineStages.StreamDeliver, enriched["stage"]);
        Assert.AreEqual("sse.batch.flush.completed", enriched["stage_detail"]);
        Assert.AreEqual("normalized_from_stage", enriched["stage_normalization"]);
        Assert.IsFalse(enriched.ContainsKey("stage_warning"));
        Assert.AreEqual("090", enriched["stage_order"]);
    }

    [TestMethod]
    public void ResolveForMetric_Classifies_Steering_Created_As_Dispatch()
    {
        var stage = RuntimePipelineStages.ResolveForMetric(
            TelemetryMetricCategories.Session,
            "session.steering.created",
            TelemetryMetricStatuses.Recorded);

        Assert.AreEqual(RuntimePipelineStages.Dispatch, stage);
    }

    [TestMethod]
    public void ResolveForMetric_Classifies_Steering_Injected_As_LlmPrepare()
    {
        var stage = RuntimePipelineStages.ResolveForMetric(
            TelemetryMetricCategories.Session,
            "session.steering.injected",
            TelemetryMetricStatuses.Succeeded);

        Assert.AreEqual(RuntimePipelineStages.LlmPrepare, stage);
    }

    [TestMethod]
    public void ResolveForActivity_Classifies_Agent_Steering_Inject_As_LlmPrepare()
    {
        var stage = RuntimePipelineStages.ResolveForActivity(
            RuntimeActivityComponents.AgentExecution,
            "agent.steering.inject",
            RuntimeActivityStatuses.Succeeded);

        Assert.AreEqual(RuntimePipelineStages.LlmPrepare, stage);
    }

    [TestMethod]
    public void ResolveForActivity_Classifies_SubAgent_Lifecycle_As_Dispatch_And_Complete()
    {
        var spawnStage = RuntimePipelineStages.ResolveForActivity(
            RuntimeActivityComponents.SubAgent,
            "spawn",
            RuntimeActivityStatuses.Started);
        var completeStage = RuntimePipelineStages.ResolveForActivity(
            RuntimeActivityComponents.SubAgent,
            "complete",
            RuntimeActivityStatuses.Succeeded);

        Assert.AreEqual(RuntimePipelineStages.Dispatch, spawnStage);
        Assert.AreEqual(RuntimePipelineStages.Complete, completeStage);
    }

    [TestMethod]
    public void ResolveForActivity_Classifies_Async_SubAgent_Runtime_Operations()
    {
        var enqueueStage = RuntimePipelineStages.ResolveForActivity(
            RuntimeActivityComponents.EventQueue,
            "enqueue",
            RuntimeActivityStatuses.Succeeded);
        var executeStage = RuntimePipelineStages.ResolveForActivity(
            RuntimeActivityComponents.AgentExecution,
            "execute",
            RuntimeActivityStatuses.Started);
        var loopStartStage = RuntimePipelineStages.ResolveForActivity(
            RuntimeActivityComponents.AgentExecution,
            "agent.hooks.loop_start",
            RuntimeActivityStatuses.Started);

        Assert.AreEqual(RuntimePipelineStages.Dispatch, enqueueStage);
        Assert.AreEqual(RuntimePipelineStages.Dispatch, executeStage);
        Assert.AreEqual(RuntimePipelineStages.Dispatch, loopStartStage);
    }

    [TestMethod]
    public void ResolveForActivity_Classifies_HookPublish_As_Dispatch()
    {
        var stage = RuntimePipelineStages.ResolveForActivity(
            RuntimeActivityComponents.HookSystem,
            "hook.publish",
            RuntimeActivityStatuses.Succeeded);

        Assert.AreEqual(RuntimePipelineStages.Dispatch, stage);
    }

    [TestMethod]
    public void ResolveForMetric_Classifies_TokenUsage_As_Complete()
    {
        var stage = RuntimePipelineStages.ResolveForMetric(
            TelemetryMetricCategories.TokenUsage,
            "token.usage",
            TelemetryMetricStatuses.Recorded);

        Assert.AreEqual(RuntimePipelineStages.Complete, stage);
    }

    [TestMethod]
    public void ResolveForActivity_Classifies_RuntimeControlHandled_As_Complete()
    {
        var stage = RuntimePipelineStages.ResolveForActivity(
            RuntimeActivityComponents.AgentExecution,
            "chat.runtime_control.handled",
            RuntimeActivityStatuses.Succeeded);

        Assert.AreEqual(RuntimePipelineStages.Complete, stage);
    }

    [TestMethod]
    public void ResolveForMetric_Classifies_SystemCommandHandled_As_Complete()
    {
        var stage = RuntimePipelineStages.ResolveForMetric(
            TelemetryMetricCategories.Session,
            "session.system_command.handled",
            TelemetryMetricStatuses.Succeeded);

        Assert.AreEqual(RuntimePipelineStages.Complete, stage);
    }

    [TestMethod]
    public void Enrich_Throws_When_Raw_Stage_Is_Unclassified()
    {
        var metadata = new Dictionary<string, string>
        {
            ["stage"] = "custom.phase",
        };

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => RuntimePipelineStages.Enrich(metadata, RuntimePipelineStages.Tool));

        StringAssert.Contains(ex.Message, "custom.phase");
    }
}
