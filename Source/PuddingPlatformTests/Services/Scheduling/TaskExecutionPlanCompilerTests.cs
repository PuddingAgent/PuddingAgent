using PuddingCode.Scheduling;
using PuddingCode.Tasks;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Scheduling;

namespace PuddingPlatformTests.Services.Scheduling;

[TestClass]
public sealed class TaskExecutionPlanCompilerTests
{
    [TestMethod]
    public void ImplementationPlan_IsDeterministicVersionedAndSequential()
    {
        var task = Task("implementation");
        var route = new TaskTypeRouteOptions
        {
            RequiredCapabilityIds = ["cap-shell", "cap-file-write"],
        };

        Assert.IsTrue(TaskExecutionPlanCompiler.TryCompile(task, route, out var first, out var code));
        Assert.IsTrue(TaskExecutionPlanCompiler.TryCompile(task, route, out var second, out _));

        Assert.AreEqual("execution_plan_compiled", code);
        Assert.AreEqual(TaskExecutionPlanSnapshot.CurrentSchemaVersion, first!.SchemaVersion);
        Assert.AreEqual(1, first.PlanVersion);
        Assert.AreEqual(first.Fingerprint, second!.Fingerprint);
        Assert.AreEqual(64, first.Fingerprint.Length);
        CollectionAssert.AreEqual(
            new[] { TaskWorkUnitKind.Explore, TaskWorkUnitKind.Plan, TaskWorkUnitKind.Change,
                TaskWorkUnitKind.Test, TaskWorkUnitKind.Review },
            first.WorkUnits.Select(unit => unit.Kind).ToArray());
        CollectionAssert.AreEqual(
            new[] { "cap-file-write", "cap-shell" },
            first.WorkUnits[0].RequiredCapabilityIds.ToArray());
        Assert.AreEqual(first.WorkUnits[0].WorkUnitId, first.WorkUnits[1].DependsOn.Single());
        Assert.HasCount(0, first.WorkUnits[0].ConflictScopes);
        Assert.AreEqual("workspace:ws:default-checkout", first.WorkUnits[2].ConflictScopes.Single());
        Assert.IsGreaterThan(0, first.WorkUnits[2].Budget.MaxRounds);
        Assert.IsGreaterThan(0, first.WorkUnits[2].Budget.MaxInputTokens);
    }

    [TestMethod]
    public void WorkUnitBudgetTemplates_ConvergeRoundsToWorkUnitDesignWindow()
    {
        // P0-06580c4d Phase 3：全部模板默认轮次必须落在 25-40 设计区间，
        // 禁止靠 LargeTaskMaxRounds(600) 硬撑。
        Assert.IsTrue(TaskExecutionPlanCompiler.TryCompile(Task("implementation"), null, out var plan, out _));

        foreach (var unit in plan!.WorkUnits)
        {
            Assert.IsTrue(
                unit.Budget.MaxRounds is >= 25 and <= 40,
                $"{unit.Kind} budget rounds={unit.Budget.MaxRounds} outside [25,40].");
        }
    }

    [TestMethod]
    public void TaskVersionOrTypeChange_ChangesFingerprint()
    {
        Assert.IsTrue(TaskExecutionPlanCompiler.TryCompile(Task("implementation"), null, out var baseline, out _));
        Assert.IsTrue(TaskExecutionPlanCompiler.TryCompile(Task("implementation", withVersion: 2), null, out var changedVersion, out _));
        Assert.IsTrue(TaskExecutionPlanCompiler.TryCompile(Task("test"), null, out var changedType, out _));

        Assert.AreNotEqual(baseline!.Fingerprint, changedVersion!.Fingerprint);
        Assert.AreNotEqual(baseline.Fingerprint, changedType!.Fingerprint);
    }

    [TestMethod]
    public void GeneralTaskType_FailsClosed()
    {
        Assert.IsFalse(TaskExecutionPlanCompiler.TryCompile(Task("general"), null, out var plan, out var code));
        Assert.IsNull(plan);
        Assert.AreEqual("execution_plan_unavailable", code);
    }

    private static WorkspaceTaskEntity Task(string taskType, int withVersion = 1) => new()
    {
        TaskId = "task-1",
        WorkspaceId = "ws",
        Title = "Implement scheduler",
        Status = WorkspaceTaskStatus.Ready,
        Priority = TaskPriority.P1,
        ExecutionWindow = TaskExecutionWindow.Anytime,
        TaskType = taskType,
        RequiredCapabilitiesJson = "[]",
        Version = withVersion,
        CreatedAtUtc = DateTimeOffset.Parse("2026-08-28T00:00:00Z"),
        UpdatedAtUtc = DateTimeOffset.Parse("2026-08-28T00:00:00Z"),
    };
}
