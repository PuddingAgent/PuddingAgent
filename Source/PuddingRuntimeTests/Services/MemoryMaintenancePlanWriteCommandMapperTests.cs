using PuddingCode.Models;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class MemoryMaintenancePlanWriteCommandMapperTests
{
    [TestMethod]
    public void MapOperation_ShouldMapAppendNew()
    {
        var plan = BasePlan(MemoryMaintenanceActions.AppendNew);
        var command = MemoryMaintenancePlanWriteCommandMapper.MapOperation(
            plan,
            plan.Operations[0],
            MemoryWriteExecutionModes.DryRun);

        Assert.AreEqual(MemoryWriteIntents.AppendNew, command.Intent);
        Assert.AreEqual(MemoryWriteSourceKinds.SubconsciousPlan, command.Source.SourceKind);
        Assert.AreEqual("job-1", command.Source.SubconsciousJobId);
        Assert.AreEqual("plan-1", command.Source.PlanId);
        Assert.AreEqual("op-1", command.Source.OperationId);
        Assert.AreEqual("agent-1", command.Source.AgentId);
        Assert.AreEqual("library-1", command.Source.MemoryLibraryId);
        Assert.AreEqual("Updated memory.", command.Payload?.Content);
    }

    [TestMethod]
    public void MapOperation_ShouldMapDeleteToDeleteRequested()
    {
        var plan = BasePlan(MemoryMaintenanceActions.Delete);
        var command = MemoryMaintenancePlanWriteCommandMapper.MapOperation(
            plan,
            plan.Operations[0],
            MemoryWriteExecutionModes.DryRun);

        Assert.AreEqual(MemoryWriteIntents.DeleteRequested, command.Intent);
        Assert.AreEqual(MemoryWriteExecutionModes.DryRun, command.Mode);
    }

    [TestMethod]
    public void MapOperation_ShouldMapSupersedeTarget()
    {
        var plan = BasePlan(MemoryMaintenanceActions.SupersedeExisting);
        var command = MemoryMaintenancePlanWriteCommandMapper.MapOperation(
            plan,
            plan.Operations[0],
            MemoryWriteExecutionModes.DryRun);

        Assert.AreEqual(MemoryWriteIntents.SupersedeExisting, command.Intent);
        Assert.AreEqual("chapter-1", command.Target?.ChapterId);
    }

    private static MemoryMaintenancePlan BasePlan(string action) =>
        new()
        {
            PlanId = "plan-1",
            WorkspaceId = "workspace-1",
            Source = new MemoryMaintenancePlanSource
            {
                WorkspaceId = "workspace-1",
                SessionId = "session-1",
                SubconsciousJobId = "job-1",
                AgentId = "agent-1",
                AgentTemplateId = "template-1",
                MemoryLibraryId = "library-1",
            },
            CandidateReads =
            [
                new MemoryPlanReference
                {
                    WorkspaceId = "workspace-1",
                    ChapterId = "chapter-1",
                },
            ],
            Operations =
            [
                new MemoryMaintenanceOperation
                {
                    OperationId = "op-1",
                    Action = action,
                    Target = new MemoryPlanReference
                    {
                        WorkspaceId = "workspace-1",
                        ChapterId = "chapter-1",
                    },
                    ProposedTitle = "Memory",
                    ProposedContent = "Updated memory.",
                    Confidence = 0.91,
                    Rationale = "Session evidence supports this write.",
                },
            ],
            Confidence = 0.91,
        };
}
