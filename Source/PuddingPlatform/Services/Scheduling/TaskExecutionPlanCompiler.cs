using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PuddingCode.Scheduling;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>
/// Pure compiler from the canonical structured Task snapshot to a versioned
/// execution plan. It never inspects title prose and performs no I/O or model call.
/// </summary>
public static class TaskExecutionPlanCompiler
{
    private const string PlanKind = "workspace-task-v1";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static bool TryCompile(
        WorkspaceTaskEntity task,
        TaskTypeRouteOptions? typeRoute,
        out TaskExecutionPlanSnapshot? plan,
        out string code)
    {
        ArgumentNullException.ThrowIfNull(task);
        plan = null;
        code = "execution_plan_unavailable";

        if (!TryResolveKinds(task.TaskType, out var kinds))
            return false;
        if (!TryReadCapabilities(task.RequiredCapabilitiesJson, out var taskCapabilities))
        {
            code = "execution_plan_capabilities_invalid";
            return false;
        }

        var capabilities = taskCapabilities
            .Concat(typeRoute?.RequiredCapabilityIds ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var units = new List<TaskWorkUnitSnapshot>(kinds.Length);
        for (var index = 0; index < kinds.Length; index++)
        {
            var kind = kinds[index];
            var unitId = $"wu-{index + 1:D2}-{kind.ToString().ToLowerInvariant()}";
            units.Add(new TaskWorkUnitSnapshot
            {
                WorkUnitId = unitId,
                Sequence = index + 1,
                Kind = kind,
                Objective = Objective(kind),
                DependsOn = index == 0 ? [] : [units[index - 1].WorkUnitId],
                RequiredCapabilityIds = capabilities,
                ConflictScopes = kind is TaskWorkUnitKind.Change or TaskWorkUnitKind.Test
                    ? [$"workspace:{task.WorkspaceId}:default-checkout"]
                    : [],
                Budget = Budget(kind),
                RetryPolicy = "bounded-transient-v1",
            });
        }

        var material = new
        {
            schemaVersion = TaskExecutionPlanSnapshot.CurrentSchemaVersion,
            planVersion = 1,
            workspaceId = task.WorkspaceId,
            taskId = task.TaskId,
            taskVersion = task.Version,
            taskType = task.TaskType.Trim().ToLowerInvariant(),
            planKind = PlanKind,
            workUnits = units,
        };
        var fingerprint = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(material, JsonOpts))))
            .ToLowerInvariant();
        plan = new TaskExecutionPlanSnapshot
        {
            SchemaVersion = material.schemaVersion,
            PlanVersion = material.planVersion,
            WorkspaceId = material.workspaceId,
            TaskId = material.taskId,
            TaskVersion = material.taskVersion,
            TaskType = material.taskType,
            PlanKind = material.planKind,
            WorkUnits = units,
            Fingerprint = fingerprint,
        };
        code = "execution_plan_compiled";
        return true;
    }

    private static bool TryResolveKinds(string? taskType, out TaskWorkUnitKind[] kinds)
    {
        kinds = (taskType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "implementation" or "operations" or "deployment" =>
                [TaskWorkUnitKind.Explore, TaskWorkUnitKind.Plan, TaskWorkUnitKind.Change,
                    TaskWorkUnitKind.Test, TaskWorkUnitKind.Review],
            "test" =>
                [TaskWorkUnitKind.Explore, TaskWorkUnitKind.Plan, TaskWorkUnitKind.Test,
                    TaskWorkUnitKind.Review],
            "research" =>
                [TaskWorkUnitKind.Explore, TaskWorkUnitKind.Plan, TaskWorkUnitKind.Review],
            "review" => [TaskWorkUnitKind.Explore, TaskWorkUnitKind.Review],
            "documentation" =>
                [TaskWorkUnitKind.Explore, TaskWorkUnitKind.Plan, TaskWorkUnitKind.Change,
                    TaskWorkUnitKind.Review],
            _ => [],
        };
        return kinds.Length > 0;
    }

    private static bool TryReadCapabilities(string? json, out string[] capabilities)
    {
        try
        {
            capabilities = string.IsNullOrWhiteSpace(json)
                ? []
                : JsonSerializer.Deserialize<string[]>(json) ?? [];
            return capabilities.All(value => !string.IsNullOrWhiteSpace(value));
        }
        catch (JsonException)
        {
            capabilities = [];
            return false;
        }
    }

    private static string Objective(TaskWorkUnitKind kind) => kind switch
    {
        TaskWorkUnitKind.Explore => "Collect canonical repository and runtime evidence before deciding changes.",
        TaskWorkUnitKind.Plan => "Freeze the implementation path, ownership boundaries and acceptance gates.",
        TaskWorkUnitKind.Change => "Apply the bounded implementation changes within the declared conflict scope.",
        TaskWorkUnitKind.Test => "Run focused verification and capture reproducible evidence and failures.",
        TaskWorkUnitKind.Review => "Review acceptance evidence, unresolved risk and completion proof.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static TaskWorkUnitBudget Budget(TaskWorkUnitKind kind) => kind switch
    {
        TaskWorkUnitKind.Explore => NewBudget(25, 60, 30, 150_000, 20_000, 1.00m),
        TaskWorkUnitKind.Plan => NewBudget(20, 30, 20, 100_000, 20_000, 0.75m),
        TaskWorkUnitKind.Change => NewBudget(40, 120, 60, 250_000, 40_000, 2.50m),
        TaskWorkUnitKind.Test => NewBudget(30, 100, 60, 200_000, 30_000, 1.75m),
        TaskWorkUnitKind.Review => NewBudget(25, 60, 30, 150_000, 25_000, 1.00m),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static TaskWorkUnitBudget NewBudget(
        int rounds,
        int toolCalls,
        int minutes,
        long inputTokens,
        long outputTokens,
        decimal cost) => new()
    {
        MaxRounds = rounds,
        MaxToolCalls = toolCalls,
        MaxDurationSeconds = checked(minutes * 60),
        MaxInputTokens = inputTokens,
        MaxOutputTokens = outputTokens,
        MaxCost = cost,
    };
}
