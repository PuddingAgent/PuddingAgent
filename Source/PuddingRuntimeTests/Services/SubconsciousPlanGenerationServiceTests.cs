using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class SubconsciousPlanGenerationServiceTests
{
    [TestMethod]
    public async Task GenerateDryRunAsync_ShouldReturnValidPlanAndRecordSucceededMetric()
    {
        var llm = new RecordingMemoryLlmClient(ValidPlanJson);
        var telemetry = new RecordingTelemetryMetricSink();
        var activities = new RecordingRuntimeActivitySink();
        var service = new SubconsciousPlanGenerationService(
            llm,
            new MemoryMaintenancePlanValidator(),
            telemetry,
            activities);

        var result = await service.GenerateDryRunAsync(CreateRequest());

        Assert.IsTrue(result.Validation.IsValid, string.Join("; ", result.Validation.Errors.Select(e => e.Message)));
        Assert.IsNotNull(result.Plan);
        Assert.AreEqual("plan-1", result.Plan!.PlanId);
        Assert.AreEqual("workspace-1", result.Plan.WorkspaceId);
        Assert.AreEqual(1, llm.CallCount);
        StringAssert.Contains(llm.LastSystemPrompt!, "MemoryMaintenancePlan");
        StringAssert.Contains(llm.LastUserMessage!, "session evidence");
        Assert.AreSame(CreateMemoryConfig, llm.LastConfig);
        Assert.IsNotNull(llm.LastScope);
        Assert.AreEqual("workspace-1", llm.LastScope!.WorkspaceId);
        Assert.AreEqual("agent-1", llm.LastScope.AgentId);
        Assert.AreEqual("template-1", llm.LastScope.AgentTemplateId);
        Assert.AreEqual("session-1", llm.LastScope.SessionId);
        Assert.AreEqual("library-1", llm.LastScope.MemoryLibraryId);
        StringAssert.Contains(llm.LastUserMessage!, "\"memoryScope\"");

        var metric = telemetry.Metrics.Single(m => m.Name == "memory_maintenance_plan.validation");
        Assert.AreEqual(TelemetryMetricCategories.Memory, metric.Category);
        Assert.AreEqual(TelemetryMetricStatuses.Succeeded, metric.Status);
        Assert.AreEqual("true", metric.Dimensions!["dry_run"]);
        Assert.AreEqual("1", metric.Dimensions!["operation_count"]);

        var activity = activities.Activities.Single(a => a.Operation == "memory_maintenance_plan.validate");
        Assert.AreEqual(RuntimeActivityStatuses.Succeeded, activity.Status);
    }

    [TestMethod]
    public async Task GenerateDryRunAsync_ShouldRejectInvalidJsonAndRecordFailedMetric()
    {
        var llm = new RecordingMemoryLlmClient("{not-json");
        var telemetry = new RecordingTelemetryMetricSink();
        var service = new SubconsciousPlanGenerationService(
            llm,
            new MemoryMaintenancePlanValidator(),
            telemetry);

        var result = await service.GenerateDryRunAsync(CreateRequest());

        Assert.IsFalse(result.Validation.IsValid);
        Assert.IsNull(result.Plan);
        Assert.IsTrue(result.Validation.Errors.Any(e => e.Code == MemoryMaintenancePlanValidationErrors.InvalidJson));

        var metric = telemetry.Metrics.Single(m => m.Name == "memory_maintenance_plan.validation");
        Assert.AreEqual(TelemetryMetricStatuses.Failed, metric.Status);
        Assert.AreEqual(MemoryMaintenancePlanValidationErrors.InvalidJson, metric.ErrorCode);
    }

    [TestMethod]
    public async Task GenerateDryRunAsync_ShouldCreateAcceptedJobResultEnvelope()
    {
        var llm = new RecordingMemoryLlmClient(ValidPlanJson);
        var service = new SubconsciousPlanGenerationService(
            llm,
            new MemoryMaintenancePlanValidator());

        var result = await service.GenerateDryRunAsync(CreateRequest());
        var envelope = result.ToJobResultEnvelope();

        Assert.AreEqual("pudding.subconscious_job_result.v1", envelope.Schema);
        Assert.AreEqual(SubconsciousJobResultKinds.MemoryMaintenancePlanDryRun, envelope.Kind);
        Assert.AreEqual(SubconsciousJobResultStatuses.Accepted, envelope.Status);
        Assert.AreEqual(SubconsciousJobResultDecisions.AcceptForExecution, envelope.Decision);
        Assert.AreEqual(SubconsciousJobResultNextActions.EnqueueForExecution, envelope.NextAction);
        Assert.AreEqual("plan-1", envelope.PlanId);
        Assert.IsTrue(envelope.Valid);
        Assert.AreEqual(1, envelope.OperationCount);
        Assert.AreEqual(0, envelope.ErrorCount);
        Assert.AreEqual("workspace-1", envelope.Metadata["workspace_id"]);
        Assert.AreEqual("job-1", envelope.Metadata["subconscious_job_id"]);
    }

    [TestMethod]
    public async Task GenerateDryRunAsync_ShouldCreateRejectedJobResultEnvelope()
    {
        var llm = new RecordingMemoryLlmClient("{not-json");
        var service = new SubconsciousPlanGenerationService(
            llm,
            new MemoryMaintenancePlanValidator());

        var result = await service.GenerateDryRunAsync(CreateRequest());
        var envelope = result.ToJobResultEnvelope();

        Assert.AreEqual(SubconsciousJobResultStatuses.Rejected, envelope.Status);
        Assert.AreEqual(SubconsciousJobResultDecisions.RetryLater, envelope.Decision);
        Assert.AreEqual(SubconsciousJobResultNextActions.RetryJob, envelope.NextAction);
        Assert.IsFalse(envelope.Valid);
        Assert.AreEqual(0, envelope.OperationCount);
        Assert.AreEqual(1, envelope.ErrorCount);
        CollectionAssert.Contains(envelope.ErrorCodes.ToArray(), MemoryMaintenancePlanValidationErrors.InvalidJson);
    }

    [TestMethod]
    public async Task GenerateDryRunAsync_ShouldCreateQuarantineEnvelopeForLowConfidence()
    {
        var llm = new RecordingMemoryLlmClient(LowConfidencePlanJson);
        var service = new SubconsciousPlanGenerationService(
            llm,
            new MemoryMaintenancePlanValidator());

        var result = await service.GenerateDryRunAsync(CreateRequest());
        var envelope = result.ToJobResultEnvelope();

        Assert.AreEqual(SubconsciousJobResultStatuses.Quarantined, envelope.Status);
        Assert.AreEqual(SubconsciousJobResultDecisions.DeferForRecheck, envelope.Decision);
        Assert.AreEqual(SubconsciousJobResultNextActions.CompleteQuarantined, envelope.NextAction);
        Assert.IsFalse(envelope.Valid);
        CollectionAssert.Contains(envelope.ErrorCodes.ToArray(), MemoryMaintenancePlanValidationErrors.LowConfidence);
    }

    [TestMethod]
    public async Task AcceptedPlan_ShouldMapToF5DryRunCommand()
    {
        var llm = new RecordingMemoryLlmClient(ValidPlanJson);
        var service = new SubconsciousPlanGenerationService(
            llm,
            new MemoryMaintenancePlanValidator());

        var result = await service.GenerateDryRunAsync(CreateRequest());

        Assert.IsNotNull(result.Plan);
        var command = MemoryMaintenancePlanWriteCommandMapper.MapOperation(
            result.Plan!,
            result.Plan.Operations[0],
            MemoryWriteExecutionModes.DryRun);
        var coordinator = new MemoryWriteCoordinator(new MemoryWriteCommandValidator());

        var writeResult = await coordinator.CoordinateAsync(command, CancellationToken.None);

        Assert.AreEqual(MemoryWriteResultStatuses.DryRun, writeResult.Status);
        Assert.AreEqual(MemoryWriteIntents.AppendNew, writeResult.Intent);
        Assert.AreEqual("job-1", writeResult.Metadata["subconscious_job_id"]);
        Assert.AreEqual("plan-1", writeResult.Metadata["plan_id"]);
    }

    [TestMethod]
    public async Task ToJobResultEnvelope_ShouldIncludeF5DryRunWriteResults()
    {
        var llm = new RecordingMemoryLlmClient(ValidPlanJson);
        var service = new SubconsciousPlanGenerationService(
            llm,
            new MemoryMaintenancePlanValidator());

        var result = await service.GenerateDryRunAsync(CreateRequest());

        Assert.IsNotNull(result.Plan);
        var command = MemoryMaintenancePlanWriteCommandMapper.MapOperation(
            result.Plan!,
            result.Plan.Operations[0],
            MemoryWriteExecutionModes.DryRun);
        var coordinator = new MemoryWriteCoordinator(new MemoryWriteCommandValidator());
        var writeResult = await coordinator.CoordinateAsync(command, CancellationToken.None);

        var envelope = result.ToJobResultEnvelope([writeResult]);

        Assert.AreEqual(1, envelope.MemoryWriteResults.Count);
        Assert.AreEqual("plan-1:op-1", envelope.MemoryWriteResults[0].CommandId);
        Assert.AreEqual(MemoryWriteResultStatuses.DryRun, envelope.MemoryWriteResults[0].Status);
        Assert.AreEqual(MemoryWriteIntents.AppendNew, envelope.MemoryWriteResults[0].Intent);
    }

    private static readonly MemoryLlmConfig CreateMemoryConfig = new("https://memory.local", "key", "memory-model");
    private const string ValidPlanJson = """
        {
          "planId": "plan-1",
          "workspaceId": "workspace-1",
          "source": {
            "workspaceId": "workspace-1",
            "sessionId": "session-1",
            "subconsciousJobId": "job-1",
            "agentId": "agent-1",
            "agentTemplateId": "template-1",
            "memoryLibraryId": "library-1"
          },
          "candidateReads": [
            { "workspaceId": "workspace-1", "chapterId": "chapter-1" }
          ],
          "operations": [
            {
              "operationId": "op-1",
              "action": "append_new",
              "proposedContent": "User prefers concise engineering summaries.",
              "confidence": 0.84,
              "rationale": "Stable preference from session evidence."
            }
          ],
          "confidence": 0.84,
          "rationale": "Dry-run plan only."
        }
        """;
    private const string LowConfidencePlanJson = """
        {
          "planId": "plan-low-confidence",
          "workspaceId": "workspace-1",
          "source": {
            "workspaceId": "workspace-1",
            "sessionId": "session-1",
            "subconsciousJobId": "job-1",
            "agentId": "agent-1",
            "agentTemplateId": "template-1",
            "memoryLibraryId": "library-1"
          },
          "operations": [
            {
              "operationId": "op-1",
              "action": "append_new",
              "proposedContent": "Maybe the user prefers short summaries.",
              "confidence": 0.42,
              "rationale": "Weak evidence."
            }
          ],
          "confidence": 0.42,
          "rationale": "Weak dry-run plan."
        }
        """;

    private static SubconsciousPlanGenerationRequest CreateRequest() => new()
    {
        WorkspaceId = "workspace-1",
        SessionId = "session-1",
        AgentId = "agent-1",
        AgentTemplateId = "template-1",
        MemoryScope = new SubconsciousMemoryScope
        {
            WorkspaceId = "workspace-1",
            AgentId = "agent-1",
            AgentTemplateId = "template-1",
            SessionId = "session-1",
            MemoryLibraryId = "library-1",
        },
        HookEventId = "evt-1",
        SubconsciousJobId = "job-1",
        EvidenceSummary = "session evidence: user prefers concise engineering summaries",
        CandidateReads =
        [
            new MemoryPlanReference
            {
                WorkspaceId = "workspace-1",
                ChapterId = "chapter-1",
            },
        ],
        AllowedReferenceIds = new HashSet<string>(StringComparer.Ordinal) { "chapter-1" },
        MemoryLlmConfig = CreateMemoryConfig,
    };

    private sealed class RecordingMemoryLlmClient(string response) : IMemoryLlmClient
    {
        public int CallCount { get; private set; }
        public string? LastSystemPrompt { get; private set; }
        public string? LastUserMessage { get; private set; }
        public MemoryLlmConfig? LastConfig { get; private set; }
        public SubconsciousMemoryScope? LastScope { get; private set; }

        public Task<MemoryClassification> ClassifyAsync(string messageText, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string?> SummarizeAsync(IReadOnlyList<string> memoryContents, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<MemoryQueryIntent?> ParseIntentAsync(string userMessage, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> ChatAsync(
            string systemPrompt,
            string userMessage,
            IReadOnlyList<object>? tools = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> ChatWithConfigAsync(
            string systemPrompt,
            string userMessage,
            MemoryLlmConfig? memoryLlmConfig,
            IReadOnlyList<object>? tools = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string> ChatWithScopedConfigAsync(
            string systemPrompt,
            string userMessage,
            MemoryLlmConfig? memoryLlmConfig,
            SubconsciousMemoryScope targetScope,
            IReadOnlyList<object>? tools = null,
            CancellationToken ct = default)
        {
            CallCount++;
            LastSystemPrompt = systemPrompt;
            LastUserMessage = userMessage;
            LastConfig = memoryLlmConfig;
            LastScope = targetScope;
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingTelemetryMetricSink : ITelemetryMetricSink
    {
        public List<TelemetryMetric> Metrics { get; } = [];

        public Task RecordAsync(TelemetryMetric metric, CancellationToken ct = default)
        {
            Metrics.Add(metric);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRuntimeActivitySink : IRuntimeActivitySink
    {
        public List<RuntimeActivity> Activities { get; } = [];

        public Task RecordAsync(RuntimeActivity activity, CancellationToken ct = default)
        {
            Activities.Add(activity);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RuntimeActivity>> QueryAsync(
            RuntimeActivityQuery query,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RuntimeActivity>>(Activities);
    }
}
