using System.Text.Json;
using PuddingCode.Configuration;
using PuddingCode.Models;

namespace PuddingCoreTests.TaskPlanning;

[TestClass]
public sealed class TaskPlanningModelTests
{
    [TestMethod]
    public void TaskPlanningOptions_Has_Expected_Defaults()
    {
        var options = new TaskPlanningOptions();

        Assert.AreEqual(2, options.MaxDelegationDepth);
        Assert.IsTrue(options.DefaultAllowSubDelegation);
        Assert.IsTrue(options.AllowAgentCreationByLeader);
        Assert.AreEqual(50, options.MaxActiveTaskNodesPerPlan);
        Assert.IsFalse(string.IsNullOrWhiteSpace(TaskPlanningOptions.SectionName));
    }

    [TestMethod]
    public void TaskPlanStatuses_And_TaskNodeStatuses_And_AssignmentKinds_Are_Available()
    {
        var planStatuses = Enum.GetNames(typeof(TaskPlanStatuses));
        var nodeStatuses = Enum.GetNames(typeof(TaskNodeStatuses));
        var assignmentKinds = Enum.GetNames(typeof(TaskAssignmentKinds));

        CollectionAssert.AreEquivalent(
            new[] { "Draft", "Active", "Completed", "Failed", "Cancelled" },
            planStatuses);

        CollectionAssert.AreEquivalent(
            new[]
            {
                "Draft", "Planned", "Assigned", "Running", "Blocked", "Completed", "Failed", "Cancelled", "Superseded"
            },
            nodeStatuses);

        CollectionAssert.AreEquivalent(
            new[] { "Leader", "WorkspaceAgent", "SubAgent", "Unassigned" },
            assignmentKinds);
    }

    [TestMethod]
    public void Query_Requests_Default_To_Page_Size_100_From_Offset_0()
    {
        var planQuery = new TaskPlanQuery();
        var nodeQuery = new TaskNodeQuery();

        Assert.AreEqual(100, planQuery.Limit);
        Assert.AreEqual(0, planQuery.Offset);
        Assert.AreEqual(100, nodeQuery.Limit);
        Assert.AreEqual(0, nodeQuery.Offset);
    }

    [TestMethod]
    public void TaskAssignmentRequest_Represents_Workspace_And_SubAgent_Assignment()
    {
        var workspaceAssignment = new TaskAssignmentRequest
        {
            TaskNodeId = "task_node_001",
            AssignedToKind = TaskAssignmentKinds.WorkspaceAgent,
            AssignedToId = "agent.workspace.research",
            Objective = "Compile findings for leadership",
            RoleInPlan = "researcher",
            ExpectedOutputContract = "findings,evidence"
        };
        var subAgentAssignment = new TaskAssignmentRequest
        {
            TaskNodeId = "task_node_002",
            AssignedToKind = TaskAssignmentKinds.SubAgent,
            AssignedTemplateId = "template-planner",
            Objective = "Create follow-up tasks",
            RoleInPlan = "planner"
        };

        Assert.AreEqual("agent.workspace.research", workspaceAssignment.AssignedToId);
        Assert.AreEqual("template-planner", subAgentAssignment.AssignedTemplateId);
        Assert.AreEqual(TaskAssignmentKinds.WorkspaceAgent, workspaceAssignment.AssignedToKind);
        Assert.AreEqual(TaskAssignmentKinds.SubAgent, subAgentAssignment.AssignedToKind);
    }

    [TestMethod]
    public void TaskPlanning_Models_Are_DateTimeOffset_Friendly()
    {
        var expectedCreatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var planJson = JsonSerializer.Serialize(new TaskPlanRun
        {
            PlanId = "plan_demo",
            WorkspaceId = "default",
            RootSessionId = "session_demo",
            LeaderAgentId = "agent_leader",
            CreatedAt = expectedCreatedAt,
            ResultSummary = "all done",
            ErrorMessage = null
        });

        var plan = JsonSerializer.Deserialize<TaskPlanRun>(planJson);

        Assert.IsNotNull(plan);
        Assert.AreEqual(expectedCreatedAt, plan!.CreatedAt);
        Assert.IsNull(plan.CompletedAt);
        Assert.AreEqual("all done", plan.ResultSummary);
        Assert.AreEqual(2, plan.MaxDelegationDepth);
        Assert.IsTrue(planJson.Contains("\"createdAt\"") || planJson.Contains("\"CreatedAt\""));
    }

    [TestMethod]
    public void TaskPlanRun_Terminal_Details_Serialize()
    {
        var planJson = JsonSerializer.Serialize(new TaskPlanRun
        {
            PlanId = "plan_terminal",
            WorkspaceId = "default",
            RootSessionId = "session_demo",
            LeaderAgentId = "agent_leader",
            Status = TaskPlanStatuses.Completed,
            ResultSummary = "completed successfully",
            ErrorMessage = "none"
        });

        var plan = JsonSerializer.Deserialize<TaskPlanRun>(planJson);

        Assert.IsNotNull(plan);
        Assert.AreEqual("completed successfully", plan!.ResultSummary);
        Assert.AreEqual("none", plan.ErrorMessage);
    }

    [TestMethod]
    public void TaskNode_Serializes_With_DateTimeOffset()
    {
        var expectedCreatedAt = new DateTimeOffset(2024, 2, 2, 0, 0, 0, TimeSpan.Zero);
        var nodeJson = JsonSerializer.Serialize(new TaskNode
        {
            TaskNodeId = "task_node_demo",
            PlanId = "plan_demo",
            Objective = "collect context",
            CreatedAt = expectedCreatedAt
        });

        var node = JsonSerializer.Deserialize<TaskNode>(nodeJson);
        Assert.IsNotNull(node);
        Assert.AreEqual(expectedCreatedAt, node!.CreatedAt);
        Assert.IsTrue(nodeJson.Contains("\"createdAt\"") || nodeJson.Contains("\"CreatedAt\""));
    }
}
