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
        Assert.AreEqual(2, first.PlanVersion);
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
    public void WorkUnitBudgetTemplates_HaveIndependentInputCapacityAndCumulativeAllowances()
    {
        // 输入容量和累计成本不存在等价消耗点；各轴仍有独立的正值护栏。
        Assert.IsTrue(TaskExecutionPlanCompiler.TryCompile(Task("implementation"), null, out var plan, out _));

        foreach (var unit in plan!.WorkUnits)
        {
            Assert.IsTrue(
                unit.Budget.MaxOutputTokens >= unit.Budget.MaxRounds * 4_000,
                $"{unit.Kind} MaxOutputTokens={unit.Budget.MaxOutputTokens} binds before rounds axis MaxRounds={unit.Budget.MaxRounds}.");
            Assert.IsGreaterThan(0, unit.Budget.MaxInputTokens);
            Assert.IsGreaterThan(0, unit.Budget.MaxOutputTokens);
            Assert.IsGreaterThan(0m, unit.Budget.MaxCost);
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
    public void GeneralTaskType_UsesConservativeKindSet()
    {
        // ADR-092 S1：general 是未分类默认值，必须能启动 Task-bound Goal，
        // 但不默认授予 Change/Test 能力。
        Assert.IsTrue(TaskExecutionPlanCompiler.TryCompile(
            Task("general"), null, out var plan, out var code));
        Assert.AreEqual("execution_plan_compiled", code);
        CollectionAssert.AreEqual(
            new[] { TaskWorkUnitKind.Explore, TaskWorkUnitKind.Plan, TaskWorkUnitKind.Review },
            plan!.WorkUnits.Select(unit => unit.Kind).ToArray());
    }

    [TestMethod]
    public void EmptyOrNullTaskType_IsNormalizedToGeneral()
    {
        foreach (var taskType in new string?[] { "", "   ", null })
        {
            var label = taskType ?? "<null>";
            Assert.IsTrue(TaskExecutionPlanCompiler.TryCompile(
                Task(taskType!), null, out var plan, out _),
                $"taskType='{label}' should compile with the general kind set.");
            CollectionAssert.AreEqual(
                new[] { TaskWorkUnitKind.Explore, TaskWorkUnitKind.Plan, TaskWorkUnitKind.Review },
                plan!.WorkUnits.Select(unit => unit.Kind).ToArray());
            Assert.AreEqual("general", plan.TaskType);
        }
    }

    [TestMethod]
    public void CaseVariantTaskType_NormalizesLikeCanonical()
    {
        Assert.IsTrue(TaskExecutionPlanCompiler.TryCompile(
            Task("  General "), null, out var plan, out _));
        CollectionAssert.AreEqual(
            new[] { TaskWorkUnitKind.Explore, TaskWorkUnitKind.Plan, TaskWorkUnitKind.Review },
            plan!.WorkUnits.Select(unit => unit.Kind).ToArray());
    }

    [TestMethod]
    public void UnknownTaskType_FailsWithDedicatedCodeAndDiagnostics()
    {
        Assert.IsFalse(TaskExecutionPlanCompiler.TryCompile(
            Task("nonsense"), null, out var plan, out var code, out var failureDetail));
        Assert.IsNull(plan);
        Assert.AreEqual("execution_plan_task_type_unsupported", code);
        Assert.IsNotNull(failureDetail);
        StringAssert.Contains(failureDetail!, "task_type='nonsense'");
        foreach (var supported in new[]
        {
            "implementation", "operations", "deployment", "test",
            "research", "review", "documentation", "general",
        })
        {
            StringAssert.Contains(failureDetail!, supported);
        }

        StringAssert.Contains(failureDetail!, "suggestion=");
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
