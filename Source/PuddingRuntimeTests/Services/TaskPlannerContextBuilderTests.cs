using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingMemoryEngine.Services;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.TaskPlanning;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class TaskPlannerContextBuilderTests
{
    [TestMethod]
    public async Task BuildAsync_IncludesDepthAndMaxDepth()
    {
        var builder = CreateBuilder();
        var request = CreateTaskRequest(
            delegationDepth: 1,
            maxDelegationDepth: 2,
            assignedObjective: "Collect implementation risks.",
            expectedOutput: "Return findings and evidence.");

        var context = await builder.BuildAsync(request, CancellationToken.None);

        StringAssert.Contains(context, "--- TASK PLANNING CONTEXT ---");
        StringAssert.Contains(context, "plan_id: plan_1");
        StringAssert.Contains(context, "task_node_id: task_research");
        StringAssert.Contains(context, "parent_task_node_id: task_parent");
        StringAssert.Contains(context, "delegation_depth: 1");
        StringAssert.Contains(context, "max_delegation_depth: 2");
        StringAssert.Contains(context, "role_in_plan: researcher");
        StringAssert.Contains(context, "allowed_to_delegate: true");
        StringAssert.Contains(context, "allowed_to_create_agents: false");
        StringAssert.Contains(context, "Collect implementation risks.");
        StringAssert.Contains(context, "Return findings and evidence.");
        StringAssert.Contains(context, "- report completion through report_task_result");
    }

    [TestMethod]
    public async Task BuildAsync_AtMaxDepth_DisallowsDelegation()
    {
        var builder = CreateBuilder();
        var request = CreateTaskRequest(delegationDepth: 2, maxDelegationDepth: 2);

        var context = await builder.BuildAsync(request, CancellationToken.None);

        StringAssert.Contains(context, "allowed_to_delegate: false");
        StringAssert.Contains(context, "- complete this task yourself or report blockage instead of creating child tasks");
    }

    [TestMethod]
    public async Task BuildAsync_ReturnsEmptyLayerWhenPlanOrNodeMissing()
    {
        var builder = CreateBuilder();
        var request = CreateTaskRequest() with { TaskNodeId = null };

        var context = await builder.BuildAsync(request, CancellationToken.None);

        Assert.AreEqual(string.Empty, context);
    }

    [TestMethod]
    public async Task ContextPipeline_AssembleAsync_AddsTaskPlanningLayerBeforeTools()
    {
        var store = new ContextAssemblyStore();
        var pipeline = CreatePipeline(store);
        var request = CreateTaskRequest();

        var result = await pipeline.AssembleAsync(request, CancellationToken.None);

        var agentsIndex = result.SystemPrompt.IndexOf("--- LAYER: WORKSPACE AGENTS ---", StringComparison.Ordinal);
        var taskIndex = result.SystemPrompt.IndexOf("--- TASK PLANNING CONTEXT ---", StringComparison.Ordinal);
        var toolsIndex = result.SystemPrompt.IndexOf("--- LAYER: TOOLS ---", StringComparison.Ordinal);

        Assert.IsGreaterThanOrEqualTo(0, agentsIndex, "Workspace agents layer should be present.");
        Assert.IsGreaterThan(agentsIndex, taskIndex, "Task planning layer should appear after workspace agents.");
        Assert.IsGreaterThan(taskIndex, toolsIndex, "Tools layer should appear after task planning.");
        StringAssert.Contains(result.SystemPrompt, "task_node_id: task_research");

        Assert.IsTrue(store.TryGet(request.SessionId, out var snapshot));
        Assert.IsNotNull(snapshot);
        var layerNames = snapshot!.Layers.Select(layer => layer.LayerName).ToList();
        var rosterLayerIndex = layerNames.IndexOf("L0-AGENTS-ROSTER");
        var taskLayerIndex = layerNames.IndexOf("L0-TASK-PLANNING");
        var toolsLayerIndex = layerNames.IndexOf("L1-TOOLS");

        Assert.IsGreaterThanOrEqualTo(0, rosterLayerIndex);
        Assert.IsGreaterThan(rosterLayerIndex, taskLayerIndex);
        Assert.IsGreaterThan(taskLayerIndex, toolsLayerIndex);
    }

    [TestMethod]
    public async Task ContextPipeline_AssembleAsync_KeepsWorkspaceRootOutOfL0Environment()
    {
        var store = new ContextAssemblyStore();
        var pipeline = CreatePipeline(store);
        var request = CreateTaskRequest();

        var result = await pipeline.AssembleAsync(request, CancellationToken.None);

        var envIndex = result.SystemPrompt.IndexOf("--- LAYER: ENVIRONMENT ---", StringComparison.Ordinal);
        var workspaceEnvIndex = result.SystemPrompt.IndexOf("--- LAYER: WORKSPACE ENVIRONMENT ---", StringComparison.Ordinal);
        var workspaceRootIndex = result.SystemPrompt.IndexOf("WorkspaceRoot: E:\\workspaces\\workspace_1", StringComparison.Ordinal);

        Assert.IsGreaterThanOrEqualTo(0, envIndex, "Environment invariant layer should be present.");
        Assert.IsGreaterThanOrEqualTo(0, workspaceEnvIndex, "Workspace environment layer should be present.");
        Assert.IsGreaterThanOrEqualTo(0, workspaceRootIndex, "WorkspaceRoot should remain available in the assembled context.");
        Assert.IsGreaterThan(workspaceEnvIndex, workspaceRootIndex, "WorkspaceRoot should be inside the workspace environment layer.");

        var l0EnvironmentText = result.SystemPrompt[envIndex..workspaceEnvIndex];
        Assert.IsFalse(l0EnvironmentText.Contains("WorkspaceRoot:", StringComparison.Ordinal));

        Assert.IsTrue(store.TryGet(request.SessionId, out var snapshot));
        Assert.IsNotNull(snapshot);
        var layerNames = snapshot!.Layers.Select(layer => layer.LayerName).ToList();
        Assert.IsTrue(layerNames.Contains("L0-ENVIRONMENT", StringComparer.Ordinal));
        Assert.IsTrue(layerNames.Contains("L3-WORKSPACE-ENVIRONMENT", StringComparer.Ordinal));
    }

    private static TaskPlannerContextBuilder CreateBuilder(TaskPlanningOptions? options = null)
        => new(Options.Create(options ?? new TaskPlanningOptions()));

    private static ContextRequest CreateTaskRequest(
        int delegationDepth = 1,
        int maxDelegationDepth = 2,
        string? assignedObjective = "Collect implementation risks for task dynamic planning storage and dispatch.",
        string? expectedOutput = "Return findings, evidence, risks, and proposed next task nodes.")
    {
        return new ContextRequest
        {
            Template = new AgentTemplateDefinition
            {
                TemplateId = "workspace-task-agent",
                Name = "Task Agent",
                TemplateType = AgentTemplateType.Task,
                SystemPrompt = "You are a task agent.",
                Runtime = new RuntimeProfile { MaxContextTokens = 16000 },
            },
            WorkspaceId = "workspace_1",
            SessionId = "session_1",
            AgentTemplateId = "workspace-task-agent",
            UserMessage = "Continue assigned task.",
            AgentInstanceId = "agent_1",
            IsFirstMessage = true,
            TaskPlanId = "plan_1",
            TaskNodeId = "task_research",
            ParentTaskNodeId = "task_parent",
            DelegationDepth = delegationDepth,
            MaxDelegationDepth = maxDelegationDepth,
            RoleInPlan = "researcher",
            AllowSubDelegation = true,
            AllowAgentCreation = false,
            AssignedObjective = assignedObjective,
            ExpectedOutputContract = expectedOutput,
        };
    }

    private static ContextPipeline CreatePipeline(ContextAssemblyStore store)
    {
        var memory = new FakeMemoryEngine();
        var skillRegistry = new AgentSkillPackageRegistry();
        var sandbox = new SandboxExecutor(NullLogger<SandboxExecutor>.Instance);
        var skillRuntime = new SkillRuntime(Array.Empty<IAgentSkill>(), sandbox, NullLogger<SkillRuntime>.Instance);
        var workspaceProfile = new FakeWorkspaceProfileProvider();
        var promptBuilder = new SystemPromptBuilder(
            memory,
            skillRuntime,
            skillRegistry,
            NullLogger<SystemPromptBuilder>.Instance,
            new StartupEnvironmentInfo(),
            workspaceProfileProvider: workspaceProfile);

        return new ContextPipeline(
            memory,
            skillRuntime,
            skillRegistry,
            promptBuilder,
            new MemoryCache(new MemoryCacheOptions()),
            store,
            NullLogger<ContextPipeline>.Instance,
            new FakeExecutionEnvironmentProvider(),
            workspaceProfileProvider: workspaceProfile,
            workspaceAgentsContextBuilder: new WorkspaceAgentsContextBuilder(new FakeAgentRosterProvider()),
            taskPlannerContextBuilder: CreateBuilder());
    }

    private sealed class FakeMemoryEngine : IMemoryEngine
    {
        public string? BuildMemoryContext(
            string sessionId,
            string? workspaceId,
            string? agentId,
            string? parentSessionId = null) => null;

        public Task<string?> RecallWithIntentAsync(
            string userMessage,
            string workspaceId,
            string agentId,
            string? sessionId = null,
            int maxTokens = 2000,
            CancellationToken ct = default) => Task.FromResult<string?>(null);

        public void WriteBack(
            string llmReply,
            string sessionId,
            string? workspaceId,
            string source,
            string? agentId = null,
            string? parentSessionId = null)
        {
        }

        public void ClearSession(string sessionId)
        {
        }
    }

    private sealed class FakeWorkspaceProfileProvider : IWorkspaceProfileProvider
    {
        public Task<string?> GetWorkspaceUserProfileAsync(string workspaceId, CancellationToken ct = default)
            => Task.FromResult<string?>("Use concise task updates.");
    }

    private sealed class FakeAgentRosterProvider : IAgentRosterProvider
    {
        public Task<IReadOnlyList<AgentRosterItem>> ListAgentsAsync(
            string workspaceId,
            string roomId,
            bool includeBusy,
            bool includeFrozen,
            CancellationToken ct)
        {
            IReadOnlyList<AgentRosterItem> agents =
            [
                new(
                    "agent_audit",
                    "Audit Agent",
                    "agent:agent_audit",
                    "idle",
                    true,
                    ["template:audit-agent"],
                    null),
            ];

            return Task.FromResult(agents);
        }
    }

    private sealed class FakeExecutionEnvironmentProvider : IExecutionEnvironmentProvider
    {
        public string OsDescription => "TestOS";
        public string OsArchitecture => "X64";
        public string RuntimeVersion => "10.0";
        public string AppBaseDirectory => "E:\\app";
        public string PathSeparator => "\\";
        public bool IsContainer => false;
        public string DefaultShell => "powershell";
        public string EnvironmentFingerprint => "test-env";
        public string? GetWorkspaceRoot(string workspaceId) => $"E:\\workspaces\\{workspaceId}";
    }
}
