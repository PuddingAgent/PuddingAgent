using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class SubAgentToolTaskPlanningTests
{
    [TestMethod]
    public void SubAgentTool_Uses_Strongly_Typed_Tool_Base()
    {
        Assert.IsTrue(
            typeof(PuddingToolBase<SubAgentToolArgs>).IsAssignableFrom(typeof(SubAgentTool)),
            "SubAgentTool should derive from PuddingToolBase<SubAgentToolArgs>.");
    }

    [TestMethod]
    public async Task ExecuteAsync_StructuredDelegation_RendersProtocolTask()
    {
        var invocation = new RecordingSubAgentInvocationService();
        var services = CreateServices(
            invocation,
            new AllowingDelegationPolicy(),
            CreateStore(depth: 0, maxDepth: 2));
        var tool = new SubAgentTool(services, NullLogger<SubAgentTool>.Instance);

        var result = await tool.ExecuteAsync(CreateRequest("""
        {
          "question": "Which files still have risky TODO markers?",
          "scope": "Source/PuddingAgent and Source/PuddingRuntime",
          "already_known": "Ignore generated bin/obj files.",
          "effort": "quick",
          "stop_condition": "Stop after reporting the first 5 actionable files.",
          "sync": false
        }
        """));

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(invocation.LastRequest);
        StringAssert.Contains(invocation.LastRequest!.Task, "QUESTION: Which files still have risky TODO markers?");
        StringAssert.Contains(invocation.LastRequest.Task, "SCOPE: Source/PuddingAgent and Source/PuddingRuntime");
        StringAssert.Contains(invocation.LastRequest.Task, "OUTPUT: SUMMARY, CHANGES, EVIDENCE, RISKS, BLOCKERS");
        Assert.AreEqual("test", invocation.LastRequest.LlmProfile.ProviderId);
        Assert.AreEqual("subagent.conscious", invocation.LastRequest.LlmProfile.ProfileId);
        Assert.AreEqual("test-model", invocation.LastRequest.LlmProfile.ModelId);
        Assert.AreEqual("test-model", invocation.LastRequest.LlmConfig.ModelId);
    }

    [TestMethod]
    public async Task ExecuteAsync_SyncResult_WrapsStructuredOutputContract()
    {
        var invocation = new RecordingSubAgentInvocationService
        {
            NextStatus = "completed",
            NextReply = """
            SUMMARY: Checked the scope.
            CHANGES: none
            EVIDENCE:
            - Source/PuddingRuntime/Tools/BuiltIns/Agents/SubAgentTool.cs:42
            RISKS:
            - Contract drift if schema is not updated.
            BLOCKERS: none
            """
        };
        var services = CreateServices(
            invocation,
            new AllowingDelegationPolicy(),
            CreateStore(depth: 0, maxDepth: 2));
        var tool = new SubAgentTool(services, NullLogger<SubAgentTool>.Instance);

        var result = await tool.ExecuteAsync(CreateRequest("""{"task":"Check protocol output","sync":true}"""));

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "\"schema\": \"pudding-subagent-result\"");
        StringAssert.Contains(result.Output, "Checked the scope.");
        StringAssert.Contains(result.Output, "Source/PuddingRuntime/Tools/BuiltIns/Agents/SubAgentTool.cs:42");
    }

    [TestMethod]
    public async Task ExecuteAsync_AsyncSpawn_PropagatesTaskPlanningMetadata()
    {
        var invocation = new RecordingSubAgentInvocationService();
        var services = CreateServices(
            invocation,
            new AllowingDelegationPolicy(),
            CreateStore(depth: 1, maxDepth: 2));
        var tool = new SubAgentTool(services, NullLogger<SubAgentTool>.Instance);

        var result = await tool.ExecuteAsync(CreateRequest("""
        {
          "task": "Collect task planning risks.",
          "sync": false,
          "plan_id": "plan_1",
          "task_node_id": "task_1",
          "parent_task_node_id": "task_parent",
          "depth": 1,
          "max_depth": 2,
          "role_in_plan": "researcher"
        }
        """));

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(invocation.LastRequest);
        Assert.AreEqual("plan_1", invocation.LastRequest!.TaskPlanId);
        Assert.AreEqual("task_1", invocation.LastRequest.TaskNodeId);
        Assert.AreEqual("task_parent", invocation.LastRequest.ParentTaskNodeId);
        Assert.AreEqual(2, invocation.LastRequest.DelegationDepth);
        Assert.AreEqual(2, invocation.LastRequest.MaxDelegationDepth);
        Assert.AreEqual("researcher", invocation.LastRequest.RoleInPlan);
        Assert.IsFalse(invocation.LastRequest.AllowSubDelegation);
        Assert.AreEqual("Collect task planning risks.", invocation.LastRequest.AssignedObjective);
    }

    [TestMethod]
    public async Task ExecuteAsync_PropagatesExplicitMaxRounds()
    {
        var invocation = new RecordingSubAgentInvocationService
        {
            NextStatus = "completed",
        };
        var services = CreateServices(
            invocation,
            new AllowingDelegationPolicy(),
            CreateStore(depth: 0, maxDepth: 2));
        var tool = new SubAgentTool(services, NullLogger<SubAgentTool>.Instance);

        var result = await tool.ExecuteAsync(CreateRequest(
            """{"task":"Inspect the runtime","sync":true,"max_rounds":15}"""));

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(invocation.LastRequest);
        Assert.AreEqual(15, invocation.LastRequest!.MaxRounds);
    }

    [TestMethod]
    public async Task ExecuteAsync_PropagatesWorkingDirectoryAsExecutionSnapshot()
    {
        var invocation = new RecordingSubAgentInvocationService
        {
            NextStatus = "completed",
        };
        var services = CreateServices(
            invocation,
            new AllowingDelegationPolicy(),
            CreateStore(depth: 0, maxDepth: 2));
        var tool = new SubAgentTool(services, NullLogger<SubAgentTool>.Instance);
        var workingDirectory = Directory.GetCurrentDirectory();
        var arguments = System.Text.Json.JsonSerializer.Serialize(new
        {
            task = "Inspect the runtime",
            sync = true,
            working_directory = workingDirectory,
        });

        var result = await tool.ExecuteAsync(CreateRequest(arguments));

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(invocation.LastRequest);
        Assert.AreEqual(workingDirectory, invocation.LastRequest!.WorkingDirectory);
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsOutOfRangeMaxRounds()
    {
        var invocation = new RecordingSubAgentInvocationService();
        var services = CreateServices(
            invocation,
            new AllowingDelegationPolicy(),
            CreateStore(depth: 0, maxDepth: 2));
        var tool = new SubAgentTool(services, NullLogger<SubAgentTool>.Instance);

        var result = await tool.ExecuteAsync(CreateRequest(
            """{"task":"Inspect the runtime","sync":true,"max_rounds":201}"""));

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "max_rounds must be between 1 and 200");
        Assert.IsNull(invocation.LastRequest);
    }

    [TestMethod]
    public async Task ExecuteAsync_DeniesSpawnAtMaxDepth()
    {
        var invocation = new RecordingSubAgentInvocationService();
        var services = CreateServices(
            invocation,
            new DenyingDelegationPolicy("delegation_denied:max_depth_reached"),
            CreateStore(depth: 2, maxDepth: 2));
        var tool = new SubAgentTool(services, NullLogger<SubAgentTool>.Instance);

        var result = await tool.ExecuteAsync(CreateRequest("""
        {
          "task": "Spawn below max depth.",
          "sync": false,
          "plan_id": "plan_1",
          "task_node_id": "task_1",
          "depth": 2,
          "max_depth": 2
        }
        """));

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "delegation_denied:max_depth_reached");
        Assert.IsNull(invocation.LastRequest);
    }

    private static IServiceProvider CreateServices(
        RecordingSubAgentInvocationService invocation,
        ITaskDelegationPolicy policy,
        ITaskPlanStore store)
    {
        return new ServiceCollection()
            .AddSingleton<ILlmResolver>(new FakeLlmResolver())
            .AddSingleton<ISubAgentInvocationService>(invocation)
            .AddSingleton<ITaskDelegationPolicy>(policy)
            .AddSingleton<ITaskPlanStore>(store)
            .BuildServiceProvider();
    }

    private static ToolExecutionRequest CreateRequest(string input) => new()
    {
        ToolCallId = "call-1",
        ArgumentsJson = input,
        Context = new ToolExecutionContext
        {
            AgentInstanceId = "agent_parent",
            WorkspaceId = "workspace_1",
            SessionId = "session_parent",
        },
    };

    private static FakeTaskPlanStore CreateStore(int depth, int maxDepth)
    {
        var plan = new TaskPlanRun
        {
            PlanId = "plan_1",
            WorkspaceId = "workspace_1",
            RootSessionId = "session_parent",
            LeaderAgentId = "agent_parent",
            MaxDelegationDepth = maxDepth,
        };
        var node = new TaskNode
        {
            TaskNodeId = "task_1",
            PlanId = "plan_1",
            Depth = depth,
        };

        return new FakeTaskPlanStore(plan, node);
    }

    private sealed class RecordingSubAgentInvocationService : ISubAgentInvocationService
    {
        public SubAgentInvocationRequest? LastRequest { get; private set; }
        public string NextStatus { get; init; } = "running";
        public string? NextReply { get; init; }

        public Task<SubAgentInvocationResult> InvokeAsync(SubAgentInvocationRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new SubAgentInvocationResult
            {
                SubSessionId = "session_parent-sub-child",
                TaskId = request.TaskNodeId,
                Status = NextStatus,
                Reply = NextReply,
            });
        }

        public Task<SubAgentBatchInvocationResult> InvokeBatchAsync(
            SubAgentBatchInvocationRequest request,
            CancellationToken ct = default)
        {
            return Task.FromResult(new SubAgentBatchInvocationResult
            {
                BatchId = "subbatch-test",
                Status = "running",
                Results = request.Tasks
                    .Select(task => new SubAgentInvocationResult
                    {
                        SubSessionId = $"session_parent-sub-{task.TaskId}",
                        Status = "running",
                    })
                    .ToArray(),
            });
        }
    }

    private sealed class FakeLlmResolver : ILlmResolver
    {
        public Task<ResolvedLlmRoute> ResolveRouteAsync(
            string? modelRoute = null,
            IReadOnlyCollection<string>? requiredCapabilityTags = null,
            CancellationToken ct = default)
        {
            var route = string.IsNullOrWhiteSpace(modelRoute)
                ? ["test", "test-model"]
                : modelRoute.Split('/', 2, StringSplitOptions.TrimEntries);
            var providerId = route.Length == 2 ? route[0] : "test";
            var modelId = route.Length == 2 ? route[1] : route[0];
            return Task.FromResult(new ResolvedLlmRoute
            {
                ProviderId = providerId,
                ModelId = modelId,
                Config = CreateConfig(modelId),
            });
        }

        private static LlmConfig CreateConfig(string? modelId) => new()
        {
            Endpoint = "https://example.invalid/v1",
            ModelId = modelId ?? "test-model",
        };
    }

    private sealed class AllowingDelegationPolicy : ITaskDelegationPolicy
    {
        public Task<TaskDelegationDecision> CanSplitAsync(TaskNode node, TaskPlanRun plan, CancellationToken ct = default)
            => Task.FromResult(Allow(node, plan));

        public Task<TaskDelegationDecision> CanAssignAsync(
            TaskNode node,
            TaskPlanRun plan,
            TaskAssignmentKinds assignmentKind,
            CancellationToken ct = default)
            => Task.FromResult(Allow(node, plan));

        public Task<TaskDelegationDecision> CanCreateTeamAgentAsync(TaskPlanRun plan, TaskNode? currentNode, CancellationToken ct = default)
            => Task.FromResult(Allow(currentNode ?? new TaskNode { TaskNodeId = "root", PlanId = plan.PlanId }, plan));

        private static TaskDelegationDecision Allow(TaskNode node, TaskPlanRun plan) => new()
        {
            Allowed = true,
            Reason = "allowed",
            CurrentDepth = node.Depth,
            MaxDepth = plan.MaxDelegationDepth,
        };
    }

    private sealed class DenyingDelegationPolicy(string reason) : ITaskDelegationPolicy
    {
        public Task<TaskDelegationDecision> CanSplitAsync(TaskNode node, TaskPlanRun plan, CancellationToken ct = default)
            => Task.FromResult(Deny(node, plan));

        public Task<TaskDelegationDecision> CanAssignAsync(
            TaskNode node,
            TaskPlanRun plan,
            TaskAssignmentKinds assignmentKind,
            CancellationToken ct = default)
            => Task.FromResult(Deny(node, plan));

        public Task<TaskDelegationDecision> CanCreateTeamAgentAsync(TaskPlanRun plan, TaskNode? currentNode, CancellationToken ct = default)
            => Task.FromResult(Deny(currentNode ?? new TaskNode { TaskNodeId = "root", PlanId = plan.PlanId }, plan));

        private TaskDelegationDecision Deny(TaskNode node, TaskPlanRun plan) => new()
        {
            Allowed = false,
            Reason = reason,
            CurrentDepth = node.Depth,
            MaxDepth = plan.MaxDelegationDepth,
        };
    }

    private sealed class FakeTaskPlanStore(TaskPlanRun plan, TaskNode node) : ITaskPlanStore
    {
        public Task<TaskPlanRun> CreatePlanAsync(TaskPlanCreateRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<TaskPlanRun?> GetPlanAsync(string planId, CancellationToken ct = default) =>
            Task.FromResult(plan.PlanId == planId ? plan : null);

        public Task<IReadOnlyList<TaskPlanRun>> QueryPlansAsync(TaskPlanQuery query, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<TaskNode> CreateNodeAsync(TaskNodeCreateRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<TaskNode?> GetNodeAsync(string taskNodeId, CancellationToken ct = default) =>
            Task.FromResult(node.TaskNodeId == taskNodeId ? node : null);

        public Task<IReadOnlyList<TaskNode>> QueryNodesAsync(TaskNodeQuery query, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<TaskNode> UpdateNodeStatusAsync(TaskNodeStatusUpdateRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }
}
