using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Orchestration;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class DesignCouncilRuntimeServiceTests
{
    [TestMethod]
    public async Task DispatchReadyAsync_UsesExactRouteAndPersistsChildIdentityAndOutput()
    {
        var invocation = new RecordingInvocationService
        {
            Handler = request => Task.FromResult(Completed(request, """
                {
                  "schema": "pudding-moa-member-result",
                  "version": 1,
                  "summary": "Intent is supported by the supplied evidence.",
                  "output": { "canProceed": true, "criticalQuestions": [] },
                  "contextGaps": [],
                  "requiresUserInput": false,
                  "blockingQuestions": []
                }
                """))
        };
        var runtime = CreateRuntime(invocation, CreateLlmConfigs());
        await CreateAndActivateAsync(runtime);

        var result = await runtime.DispatchReadyAsync(new DesignCouncilDispatchRequest
        {
            RunId = "run-001",
            RequestedCount = 4,
            WorkingDirectory = "C:\\workspace"
        });

        Assert.IsTrue(result.Success, FormatIssues(result.Issues));
        Assert.HasCount(1, invocation.Requests);
        var request = invocation.Requests.Single();
        Assert.AreEqual("deepseek", request.LlmProfile.ProviderId);
        Assert.AreEqual("deepseek-v4-flash", request.LlmProfile.ModelId);
        Assert.AreEqual("responses", request.LlmConfig.Protocol);
        Assert.AreEqual("moa.intent-and-market-researcher", request.LlmProfile.ProfileId);
        Assert.AreEqual("design_council", request.OriginToolId);
        Assert.AreEqual("run-001/work/context-audit/001/claim/01", request.InvocationId);
        Assert.IsNull(request.ParentContextSnapshot);
        Assert.IsFalse(request.AllowSubDelegation);
        Assert.IsFalse(request.AllowAgentCreation);
        Assert.IsFalse(request.CapabilityPolicy!.AllowFileWrite);
        Assert.Contains("file_read", request.CapabilityPolicy.AllowedToolNames);
        Assert.DoesNotContain("spawn_sub_agent", request.CapabilityPolicy.AllowedToolNames);

        var state = result.Snapshot!.WorkItems.Single(item => item.WorkItemId == request.TaskNodeId);
        Assert.AreEqual(SubAgentOrchestrationWorkItemStatus.Succeeded, state.Status);
        Assert.AreEqual("child-run-001", state.ExternalRunId);
        Assert.AreEqual("child-session-001", state.ExternalSubSessionId);
        StringAssert.Contains(state.OutputText, "canProceed");
        Assert.AreEqual("subagent://child-session-001/runs/child-run-001", state.OutputReference);
    }

    [TestMethod]
    public async Task DispatchReadyAsync_ContextGapPausesAndResumePersistsResolution()
    {
        var invocation = new RecordingInvocationService
        {
            Handler = request => Task.FromResult(Completed(request, """
                {
                  "schema": "pudding-moa-member-result",
                  "version": 1,
                  "summary": "A product boundary is missing.",
                  "output": { "canProceed": false },
                  "contextGaps": ["The target user segment is unknown."],
                  "requiresUserInput": true,
                  "blockingQuestions": ["Which user segment is the primary target?"]
                }
                """))
        };
        var runtime = CreateRuntime(invocation, CreateLlmConfigs());
        await CreateAndActivateAsync(runtime);

        var dispatched = await runtime.DispatchReadyAsync(new DesignCouncilDispatchRequest
        {
            RunId = "run-001",
            RequestedCount = 1
        });

        Assert.AreEqual(SubAgentOrchestrationPlanStatus.AwaitingUserInput, dispatched.Snapshot!.Status);
        CollectionAssert.Contains(
            dispatched.Snapshot.BlockingQuestions.ToArray(),
            "Which user segment is the primary target?");

        var resumed = await runtime.ResumeAsync("run-001", new SubAgentOrchestrationContextResolution
        {
            ResolutionId = "resolution-001",
            ProvidedBy = "admin",
            Response = "Windows developers using Pudding as a local IDE assistant."
        });

        Assert.IsTrue(resumed.Success, FormatIssues(resumed.Issues));
        Assert.AreEqual(SubAgentOrchestrationPlanStatus.Active, resumed.Snapshot!.Status);
        Assert.AreEqual(
            SubAgentOrchestrationStageKind.Research,
            resumed.Snapshot.Plan.Stages.Single(stage => stage.StageId == resumed.Snapshot.CurrentStageId).Kind);
        Assert.HasCount(1, resumed.Snapshot.ContextResolutions);
    }

    [TestMethod]
    public async Task DispatchReadyAsync_UnconfiguredExactRouteRecordsFailureWithoutFallback()
    {
        var invocation = new RecordingInvocationService();
        var runtime = CreateRuntime(invocation, new FakeLlmConfigService());
        await CreateAndActivateAsync(runtime);

        var result = await runtime.DispatchReadyAsync(new DesignCouncilDispatchRequest
        {
            RunId = "run-001",
            RequestedCount = 1
        });

        Assert.HasCount(0, invocation.Requests);
        var work = result.Snapshot!.WorkItems.Single(item => item.WorkItemId.Contains("context-audit", StringComparison.Ordinal));
        Assert.AreEqual(SubAgentOrchestrationWorkItemStatus.Failed, work.Status);
        StringAssert.Contains(work.Error, "Exact MOA route");
        Assert.AreEqual(SubAgentOrchestrationPlanStatus.Failed, result.Snapshot.Status);
    }

    [TestMethod]
    public async Task ConcurrentDispatchers_InvokeAClaimedWorkItemOnlyOnce()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = new RecordingInvocationService
        {
            Handler = async request =>
            {
                invoked.TrySetResult();
                await release.Task;
                return Completed(request, "context audit complete");
            }
        };
        var runtime = CreateRuntime(invocation, CreateLlmConfigs());
        await CreateAndActivateAsync(runtime);
        var request = new DesignCouncilDispatchRequest { RunId = "run-001", RequestedCount = 1 };

        var first = runtime.DispatchReadyAsync(request);
        await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await runtime.DispatchReadyAsync(request);

        Assert.HasCount(0, second.WorkItems);
        Assert.HasCount(1, invocation.Requests);
        release.TrySetResult();
        var completed = await first;
        Assert.IsTrue(completed.WorkItems.Single().CompletionPersisted);
        Assert.HasCount(1, invocation.Requests);
    }

    [TestMethod]
    public async Task DispatchReadyAsync_PreservesProposalIndependenceAndTargetOnlyCritiqueVisibility()
    {
        var invocation = new RecordingInvocationService
        {
            Handler = request => Task.FromResult(Completed(request, JsonSerializer.Serialize(new
            {
                schema = "pudding-moa-member-result",
                version = 1,
                summary = $"completed {request.TaskNodeId}",
                output = $"output::{request.TaskNodeId}",
                contextGaps = Array.Empty<string>(),
                requiresUserInput = false,
                blockingQuestions = Array.Empty<string>()
            })))
        };
        var runtime = CreateRuntime(invocation, CreateLlmConfigs());
        await CreateAndActivateAsync(runtime);
        await runtime.DispatchReadyAsync(new DesignCouncilDispatchRequest { RunId = "run-001", RequestedCount = 1 });
        await runtime.DispatchReadyAsync(new DesignCouncilDispatchRequest { RunId = "run-001", RequestedCount = 1 });

        var proposals = await runtime.DispatchReadyAsync(new DesignCouncilDispatchRequest
        {
            RunId = "run-001",
            RequestedCount = 4
        });
        Assert.HasCount(3, proposals.WorkItems);
        var proposalRequests = invocation.Requests
            .Where(request => request.TaskNodeId!.Contains("independent-proposal", StringComparison.Ordinal))
            .ToArray();
        Assert.HasCount(3, proposalRequests);
        foreach (var proposalRequest in proposalRequests)
        {
            StringAssert.Contains(proposalRequest.Task, "output::run-001/work/research/001");
            Assert.IsFalse(
                proposalRequest.Task.Contains("output::run-001/work/independent-proposal", StringComparison.Ordinal));
        }

        var critiques = await runtime.DispatchReadyAsync(new DesignCouncilDispatchRequest
        {
            RunId = "run-001",
            RequestedCount = 4
        });
        Assert.HasCount(4, critiques.WorkItems);
        var critiqueRequests = invocation.Requests
            .Where(request => request.TaskNodeId!.Contains("cross-critique", StringComparison.Ordinal))
            .ToArray();
        Assert.HasCount(4, critiqueRequests);
        var allProposalIds = proposals.Snapshot!.Plan.WorkItems
            .Where(item => item.Kind == SubAgentOrchestrationStageKind.IndependentProposal)
            .Select(item => item.WorkItemId)
            .ToArray();
        foreach (var critiqueRequest in critiqueRequests)
        {
            var targetId = critiqueRequest.ParentTaskNodeId!;
            StringAssert.Contains(critiqueRequest.Task, $"output::{targetId}");
            foreach (var otherProposalId in allProposalIds.Where(id => id != targetId))
            {
                Assert.IsFalse(
                    critiqueRequest.Task.Contains($"output::{otherProposalId}", StringComparison.Ordinal));
            }
        }
    }

    [TestMethod]
    public async Task InMemoryStore_RejectsAStaleSnapshotUpdate()
    {
        var store = new InMemorySubAgentOrchestrationRunStore();
        var plan = CreatePlan();
        var stateMachine = new DesignCouncilRunStateMachine();
        var created = stateMachine.CreateRun(plan, "run-001", DateTimeOffset.UtcNow);
        Assert.IsTrue((await store.TryCreateAsync(created)).Success);
        var activated = stateMachine.Activate(created, DateTimeOffset.UtcNow).Snapshot;
        Assert.IsTrue((await store.TryUpdateAsync(activated, created.Version)).Success);

        var stale = stateMachine.Activate(created, DateTimeOffset.UtcNow).Snapshot;
        var write = await store.TryUpdateAsync(stale, created.Version);

        Assert.AreEqual(SubAgentOrchestrationStoreWriteStatus.VersionConflict, write.Status);
        Assert.AreEqual(activated.Version, write.CurrentSnapshot!.Version);
    }

    private static DesignCouncilRuntimeService CreateRuntime(
        ISubAgentInvocationService invocation,
        ILlmConfigService configs)
        => new(
            new InMemorySubAgentOrchestrationRunStore(),
            invocation,
            configs,
            new DesignCouncilRunStateMachine(),
            NullLogger<DesignCouncilRuntimeService>.Instance);

    private static async Task CreateAndActivateAsync(DesignCouncilRuntimeService runtime)
    {
        var created = await runtime.CreateRunAsync(CreatePlan(), "run-001");
        Assert.IsTrue(created.Success, FormatIssues(created.Issues));
        var activated = await runtime.ActivateAsync("run-001");
        Assert.IsTrue(activated.Success, FormatIssues(activated.Issues));
    }

    private static SubAgentOrchestrationPlan CreatePlan()
    {
        var result = new DesignCouncilPlanCompiler().Compile(CreateCompileRequest());
        Assert.IsTrue(result.Success, string.Join("; ", result.Issues.Select(issue => issue.Message)));
        return result.Plan!;
    }

    private static DesignCouncilPlanCompileRequest CreateCompileRequest() => new()
    {
        PlanId = "run-001",
        DesignRequest = new DesignRequest
        {
            RequestId = "design-001",
            WorkspaceId = "default",
            ParentSessionId = "session-001",
            RequestedByAgentId = "main-agent",
            UserIntent = "Design a durable MOA orchestration core.",
            ProblemStatement = "Specialists must produce independent designs and critique them.",
            IntentEvidence = ["The user explicitly requested staged design work."],
            KnownContext = ["Pudding already supports isolated sub-agent runs."],
            ResearchQuestions = ["Which comparable review topologies work?"],
            AcceptanceCriteria = ["Every child route is exact and auditable."],
            RequestedDeliverables = ["An inspectable design decision."]
        },
        ExpertGroup = new ExpertGroupDefinition
        {
            GroupId = "design-council",
            ChairMemberId = "architect",
            MinimumMembers = 5,
            MinimumSuccessfulProposals = 3,
            MinimumDistinctProposalRoutes = 3,
            MaxConcurrency = 4,
            CritiqueTopology = CritiqueTopology.DoubleReview,
            AllowFallback = false,
            Members =
            [
                new ExpertGroupMemberDefinition
                {
                    MemberId = "context-research",
                    Role = "intent-and-market-researcher",
                    TemplateId = "researcher",
                    RouteKey = "deepseek/deepseek-v4-flash",
                    Capabilities = ExpertMemberCapabilities.ContextAudit |
                                   ExpertMemberCapabilities.Research |
                                   ExpertMemberCapabilities.Critique
                },
                new ExpertGroupMemberDefinition
                {
                    MemberId = "architect",
                    Role = "architecture-chair",
                    TemplateId = "architect",
                    RouteKey = "deepseek/deepseek-v4-pro",
                    Capabilities = ExpertMemberCapabilities.Propose |
                                   ExpertMemberCapabilities.Critique |
                                   ExpertMemberCapabilities.Synthesize
                },
                new ExpertGroupMemberDefinition
                {
                    MemberId = "frontend",
                    Role = "frontend-developer",
                    TemplateId = "frontend-developer",
                    RouteKey = "opencode/kimi-k3",
                    Capabilities = ExpertMemberCapabilities.Propose |
                                   ExpertMemberCapabilities.Critique
                },
                new ExpertGroupMemberDefinition
                {
                    MemberId = "backend",
                    Role = "backend-expert",
                    TemplateId = "backend-expert",
                    RouteKey = "opencode/glm-5.2",
                    Capabilities = ExpertMemberCapabilities.Propose |
                                   ExpertMemberCapabilities.Critique
                },
                new ExpertGroupMemberDefinition
                {
                    MemberId = "reviewer",
                    Role = "independent-reviewer",
                    TemplateId = "reviewer",
                    RouteKey = "opencode/qwen3.8-max",
                    Capabilities = ExpertMemberCapabilities.Critique |
                                   ExpertMemberCapabilities.FinalReview
                }
            ]
        }
    };

    private static FakeLlmConfigService CreateLlmConfigs() => new()
    {
        Routes =
        {
            ["deepseek/deepseek-v4-flash"] = new LlmConfig
            {
                Endpoint = "https://example.invalid/v1",
                ModelId = "deepseek-v4-flash",
                Protocol = "responses"
            },
            ["deepseek/deepseek-v4-pro"] = new LlmConfig
            {
                Endpoint = "https://example.invalid/v1",
                ModelId = "deepseek-v4-pro",
                Protocol = "responses"
            },
            ["opencode/kimi-k3"] = new LlmConfig
            {
                Endpoint = "https://example.invalid/v1",
                ModelId = "kimi-k3",
                Protocol = "responses"
            },
            ["opencode/glm-5.2"] = new LlmConfig
            {
                Endpoint = "https://example.invalid/v1",
                ModelId = "glm-5.2",
                Protocol = "responses"
            },
            ["opencode/qwen3.8-max"] = new LlmConfig
            {
                Endpoint = "https://example.invalid/v1",
                ModelId = "qwen3.8-max",
                Protocol = "responses"
            }
        }
    };

    private static SubAgentInvocationResult Completed(SubAgentInvocationRequest request, string reply)
        => new()
        {
            SubSessionId = "child-session-001",
            RunId = "child-run-001",
            TaskId = request.TaskNodeId,
            Status = "completed",
            Reply = reply
        };

    private static string FormatIssues(IReadOnlyList<SubAgentOrchestrationOperationIssue> issues)
        => string.Join("; ", issues.Select(issue => $"{issue.Code}: {issue.Message}"));

    private sealed class RecordingInvocationService : ISubAgentInvocationService
    {
        private readonly object _gate = new();
        private readonly List<SubAgentInvocationRequest> _requests = [];

        public Func<SubAgentInvocationRequest, Task<SubAgentInvocationResult>>? Handler { get; init; }
        public IReadOnlyList<SubAgentInvocationRequest> Requests
        {
            get
            {
                lock (_gate)
                    return _requests.ToArray();
            }
        }

        public Task<SubAgentInvocationResult> InvokeAsync(
            SubAgentInvocationRequest request,
            CancellationToken ct = default)
        {
            lock (_gate)
                _requests.Add(request);
            return Handler?.Invoke(request) ?? Task.FromResult(new SubAgentInvocationResult
            {
                SubSessionId = "child-session-001",
                RunId = "child-run-001",
                TaskId = request.TaskNodeId,
                Status = "completed",
                Reply = "completed"
            });
        }

        public Task<SubAgentBatchInvocationResult> InvokeBatchAsync(
            SubAgentBatchInvocationRequest request,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeLlmConfigService : ILlmConfigService
    {
        public Dictionary<string, LlmConfig> Routes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<LlmProviderInfo> GetEnabledProviders() => [];
        public IReadOnlyList<LlmModelInfo> GetAllModels() => [];
        public LlmConfig? Resolve(string providerId, string modelId)
            => Routes.GetValueOrDefault($"{providerId}/{modelId}");
        public LlmProfileInfo? ResolveProfile(string profileId) => null;
        public LlmConfig? GetMemoryConfig() => null;
        public LlmConfig? GetEmbeddingConfig() => null;
        public LlmProviderStrategy? GetProviderStrategy(string providerId) => null;
        public LlmProviderStrategy? GetModelStrategy(string providerId, string modelId) => null;
        public void Reload(object config) { }
    }
}
