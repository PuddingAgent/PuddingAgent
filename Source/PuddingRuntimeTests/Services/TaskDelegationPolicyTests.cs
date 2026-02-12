using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingPlatform.Data;
using PuddingRuntime;
using PuddingRuntime.Services.TaskPlanning;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class TaskDelegationPolicyTests
{
    [TestMethod]
    public async Task CanSplitAsync_DeniesAtMaxDepth()
    {
        var plan = CreatePlan(maxDelegationDepth: 2);
        var policy = CreatePolicy(plan, planNodes: []);
        var node = CreateNode("plan_1", depth: 2);

        var decision = await policy.CanSplitAsync(node, plan, CancellationToken.None);

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("delegation_denied:max_depth_reached", decision.Reason);
        Assert.AreEqual(2, decision.CurrentDepth);
        Assert.AreEqual(2, decision.MaxDepth);
    }

    [TestMethod]
    public async Task CanSplitAsync_DeniesWhenNodeDisallowsSubDelegation()
    {
        var node = CreateNode("plan_1", depth: 1, allowSubDelegation: false);
        var plan = CreatePlan(maxDelegationDepth: 4);
        var policy = CreatePolicy(plan);

        var decision = await policy.CanSplitAsync(node, plan, CancellationToken.None);

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("delegation_denied:sub_delegation_disabled", decision.Reason);
    }

    [TestMethod]
    public async Task CanCreateTeamAgentAsync_UsesOptionsFlag()
    {
        var options = new TaskPlanningOptions { AllowAgentCreationByLeader = false, MaxDelegationDepth = 4 };
        var plan = CreatePlan(allowAgentCreationByLeader: true, maxDelegationDepth: 4);
        var policy = CreatePolicy(plan, options: options);

        var decision = await policy.CanCreateTeamAgentAsync(plan, null, CancellationToken.None);

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("delegation_denied:agent_creation_disabled", decision.Reason);
    }

    [TestMethod]
    public async Task CanAssignAsync_DeniesWhenActiveNodeLimitReached()
    {
        var plan = CreatePlan(maxActiveNodes: 2);
        var node = CreateNode("plan_1", depth: 0);
        var activePlanNodes = new[]
        {
            CreateNode("plan_1", status: TaskNodeStatuses.Draft),
            CreateNode("plan_1", status: TaskNodeStatuses.Assigned),
            CreateNode("other", status: TaskNodeStatuses.Draft),
        };
        var policy = CreatePolicy(plan, activePlanNodes);

        var decision = await policy.CanAssignAsync(node, plan, TaskAssignmentKinds.WorkspaceAgent, CancellationToken.None);

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("delegation_denied:active_node_limit_reached", decision.Reason);
    }

    [TestMethod]
    public async Task CanAssignAsync_DeniesActiveLimit_WhenQueryWindowMustExpandBeyondDefault()
    {
        var plan = CreatePlan(maxActiveNodes: 120);
        var node = CreateNode("plan_1", depth: 0);
        var activePlanNodes = Enumerable
            .Range(0, 121)
            .Select(_ => CreateNode("plan_1", status: TaskNodeStatuses.Draft))
            .ToArray();

        var policy = CreatePolicy(plan, activePlanNodes);

        var decision = await policy.CanAssignAsync(node, plan, TaskAssignmentKinds.WorkspaceAgent, CancellationToken.None);

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("delegation_denied:active_node_limit_reached", decision.Reason);
    }

    [TestMethod]
    public async Task CanSplitAsync_UsesTaskPlanningOptionsWhenPlanDepthUnset()
    {
        var options = new TaskPlanningOptions { MaxDelegationDepth = 4 };
        var plan = CreatePlan(maxDelegationDepth: 0);
        var node = CreateNode("plan_1", depth: 4);
        var policy = CreatePolicy(plan, options: options);

        var decision = await policy.CanSplitAsync(node, plan, CancellationToken.None);

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("delegation_denied:max_depth_reached", decision.Reason);
        Assert.AreEqual(4, decision.MaxDepth);
    }

    [TestMethod]
    public async Task CanAssignAsync_AllowsWorkspaceAgentAtMaxDepth()
    {
        var plan = CreatePlan(maxDelegationDepth: 2, maxActiveNodes: 5);
        var node = CreateNode("plan_1", depth: 2);
        var policy = CreatePolicy(plan);

        var decision = await policy.CanAssignAsync(node, plan, TaskAssignmentKinds.WorkspaceAgent, CancellationToken.None);

        Assert.IsTrue(decision.Allowed);
        Assert.AreEqual("allowed", decision.Reason);
        Assert.AreEqual(2, decision.CurrentDepth);
        Assert.AreEqual(2, decision.MaxDepth);
    }

    [TestMethod]
    public async Task CanAssignAsync_DeniesSubAgentAtMaxDepth()
    {
        var plan = CreatePlan(maxDelegationDepth: 3);
        var node = CreateNode("plan_1", depth: 3);
        var policy = CreatePolicy(plan);

        var decision = await policy.CanAssignAsync(node, plan, TaskAssignmentKinds.SubAgent, CancellationToken.None);

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("delegation_denied:max_depth_reached", decision.Reason);
    }

    [TestMethod]
    public async Task CanCreateTeamAgentAsync_DeniesWhenCurrentNodeDisallowsAgentCreation()
    {
        var node = CreateNode("plan_1", allowAgentCreation: false);
        var plan = CreatePlan();
        var policy = CreatePolicy(plan);

        var decision = await policy.CanCreateTeamAgentAsync(plan, node, CancellationToken.None);

        Assert.IsFalse(decision.Allowed);
        Assert.AreEqual("delegation_denied:agent_creation_disabled", decision.Reason);
    }

    [TestMethod]
    public void AddPuddingRuntime_ResolvesPolicyAndStore()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddDbContext<PlatformDbContext>(options =>
            {
                options.UseSqlite("Data Source=:memory:");
            })
            .AddPuddingRuntime()
            .BuildServiceProvider();

        using var scope = provider.CreateScope();

        var policy = scope.ServiceProvider.GetService<ITaskDelegationPolicy>();
        var store = scope.ServiceProvider.GetService<ITaskPlanStore>();

        Assert.IsNotNull(policy);
        Assert.IsNotNull(store);
    }

    private static TaskDelegationPolicy CreatePolicy(
        TaskPlanRun plan,
        IReadOnlyList<TaskNode>? planNodes = null,
        TaskPlanningOptions? options = null)
    {
        return new TaskDelegationPolicy(
            new FakeTaskPlanStore(planNodes ?? Array.Empty<TaskNode>()),
            Options.Create(options ?? new TaskPlanningOptions()));
    }

    private static TaskPlanRun CreatePlan(
        int? maxDelegationDepth = null,
        bool? allowAgentCreationByLeader = null,
        int? maxActiveNodes = null)
    {
        return new TaskPlanRun
        {
            PlanId = "plan_1",
            WorkspaceId = "default",
            RootSessionId = "session_1",
            LeaderAgentId = "leader",
            MaxDelegationDepth = maxDelegationDepth ?? 2,
            AllowAgentCreationByLeader = allowAgentCreationByLeader ?? true,
            MaxActiveTaskNodesPerPlan = maxActiveNodes ?? 50,
        };
    }

    private static TaskNode CreateNode(
        string planId,
        int depth = 0,
        TaskNodeStatuses status = TaskNodeStatuses.Draft,
        bool allowSubDelegation = true,
        bool allowAgentCreation = true)
    {
        return new TaskNode
        {
            TaskNodeId = Guid.NewGuid().ToString("N"),
            PlanId = planId,
            ParentTaskNodeId = null,
            Depth = depth,
            Status = status,
            AllowSubDelegation = allowSubDelegation,
            AllowAgentCreation = allowAgentCreation,
        };
    }

    private sealed class FakeTaskPlanStore(IReadOnlyList<TaskNode> nodes) : ITaskPlanStore
    {
        public Task<TaskPlanRun> CreatePlanAsync(TaskPlanCreateRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<TaskPlanRun?> GetPlanAsync(string planId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<TaskPlanRun>> QueryPlansAsync(TaskPlanQuery query, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<TaskNode> CreateNodeAsync(TaskNodeCreateRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<TaskNode?> GetNodeAsync(string taskNodeId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<TaskNode>> QueryNodesAsync(TaskNodeQuery query, CancellationToken ct = default)
        {
            var results = nodes
                .Where(item => query.PlanId is null || item.PlanId == query.PlanId)
                .Where(item => query.Status is null || item.Status == query.Status)
                .ToList();

            var offset = Math.Max(0, query.Offset);
            var limit = Math.Max(1, query.Limit);
            results = results
                .Skip(offset)
                .Take(limit)
                .ToList();

            return Task.FromResult<IReadOnlyList<TaskNode>>(results);
        }

        public Task<TaskNode> UpdateNodeStatusAsync(TaskNodeStatusUpdateRequest request, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }
}
