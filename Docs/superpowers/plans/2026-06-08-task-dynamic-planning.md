# Task Dynamic Planning Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a durable task dynamic planning layer where a Leader Agent can create a task plan, split it into auditable task nodes, delegate nodes to itself, workspace agents, or sub agents, enforce a default max delegation depth of 2, and inject each assignee's task-tree position and constraints into runtime context.

**Architecture:** Add a Task Planning domain above the existing runtime loop. `TaskPlanService` owns plan and node state, `TaskDelegationPolicy` enforces depth and team-management limits, `TaskAssignmentService` routes work through Message Fabric or `ISubAgentInvocationService`, and `TaskPlannerContextBuilder` injects system-generated constraints into `ContextPipeline`. Keep durable state in `PuddingPlatform` and expose runtime tools through native `IPuddingTool` implementations.

**Tech Stack:** .NET 10, EF Core SQLite, MSTest, existing `PuddingCore`, `PuddingPlatform`, `PuddingRuntime`, `PuddingAgent`, Message Fabric, SubAgent runtime, workspace agent file service, runtime activity telemetry.

---

## Source Inputs

- Design source: `Docs/superpowers/specs/2026-06-08-task-dynamic-planning-design.md`.
- Existing Message Fabric baseline: `Docs/superpowers/plans/2026-06-07-agent-to-agent-message-fabric-v1.md`.
- Existing context hook: `Source/PuddingRuntime/Services/ContextPipeline.cs`.
- Existing workspace agent roster hook: `Source/PuddingRuntime/Services/WorkspaceAgentsContextBuilder.cs`.
- Existing sub-agent contract boundary: `Source/PuddingRuntime/Services/SubAgentInvocationContracts.cs` and `Source/PuddingCore/Abstractions/ISubAgentManager.cs`.
- Existing workspace agent management service: `Source/PuddingPlatform/Services/WorkspaceAgentFileService.cs`.

## Implementation Sequence

### Task 1: Add Task Planning Core Contracts

**Goal:** Establish stable models and abstractions without persistence or dispatch behavior.

- [ ] Create `Source/PuddingCore/Models/TaskPlanningModels.cs` with:
  - `TaskPlanStatuses`: `Draft`, `Active`, `Completed`, `Failed`, `Cancelled`.
  - `TaskNodeStatuses`: `Draft`, `Planned`, `Assigned`, `Running`, `Blocked`, `Completed`, `Failed`, `Cancelled`, `Superseded`.
  - `TaskAssignmentKinds`: `Leader`, `WorkspaceAgent`, `SubAgent`, `Unassigned`.
  - `TaskPlanRun`, `TaskNode`, `TaskPlanCreateRequest`, `TaskNodeCreateRequest`, `TaskNodeStatusUpdateRequest`, `TaskPlanQuery`, `TaskNodeQuery`.
- [ ] Create `Source/PuddingCore/Configuration/TaskPlanningOptions.cs`.
- [ ] Create `Source/PuddingCore/Abstractions/ITaskPlanStore.cs`.
- [ ] Create `Source/PuddingCore/Abstractions/ITaskPlanService.cs`.
- [ ] Create `Source/PuddingCore/Abstractions/ITaskAssignmentService.cs`.
- [ ] Create `Source/PuddingCore/Abstractions/ITaskDelegationPolicy.cs`.
- [ ] Keep all models JSON-friendly and use `DateTimeOffset` for public contracts.

Core option shape:

```csharp
namespace PuddingCode.Configuration;

public sealed class TaskPlanningOptions
{
    public const string SectionName = "TaskPlanning";

    public int MaxDelegationDepth { get; init; } = 2;
    public bool DefaultAllowSubDelegation { get; init; } = true;
    public bool AllowAgentCreationByLeader { get; init; } = true;
    public int MaxActiveTaskNodesPerPlan { get; init; } = 50;
}
```

Store contract minimum:

```csharp
public interface ITaskPlanStore
{
    Task<TaskPlanRun> CreatePlanAsync(TaskPlanCreateRequest request, CancellationToken ct = default);
    Task<TaskPlanRun?> GetPlanAsync(string planId, CancellationToken ct = default);
    Task<IReadOnlyList<TaskPlanRun>> QueryPlansAsync(TaskPlanQuery query, CancellationToken ct = default);
    Task<TaskNode> CreateNodeAsync(TaskNodeCreateRequest request, CancellationToken ct = default);
    Task<TaskNode?> GetNodeAsync(string taskNodeId, CancellationToken ct = default);
    Task<IReadOnlyList<TaskNode>> QueryNodesAsync(TaskNodeQuery query, CancellationToken ct = default);
    Task<TaskNode> UpdateNodeStatusAsync(TaskNodeStatusUpdateRequest request, CancellationToken ct = default);
}
```

Tests:

- [ ] Add `Source/PuddingCoreTests/TaskPlanning/TaskPlanningModelTests.cs`.
- [ ] Verify default options and accepted status constants.

Run:

```powershell
dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --filter TaskPlanningModelTests --logger "console;verbosity=minimal" --no-restore
```

Commit:

```powershell
git add Source/PuddingCore Source/PuddingCoreTests
git commit -m "feat: add task planning core contracts"
```

### Task 2: Add Durable Plan and Node Store

**Goal:** Persist task plans and nodes in `PlatformDbContext` with SQLite-compatible schema upgrade.

- [ ] Create `Source/PuddingPlatform/Data/Entities/TaskPlanRunEntity.cs`.
- [ ] Create `Source/PuddingPlatform/Data/Entities/TaskNodeEntity.cs`.
- [ ] Update `Source/PuddingPlatform/Data/PlatformDbContext.cs` with `DbSet<TaskPlanRunEntity>` and `DbSet<TaskNodeEntity>`.
- [ ] Add indexes:
  - unique `plan_id`.
  - unique `task_node_id`.
  - `workspace_id`, `status`, `updated_at` on task plans.
  - `plan_id`, `parent_task_node_id`, `status` on task nodes.
  - `plan_id`, `depth`, `status` on task nodes.
- [ ] Implement `Source/PuddingPlatform/Services/TaskPlanning/TaskPlanStore.cs`.
- [ ] Add `Source/PuddingPlatform/Services/TaskPlanning/TaskPlanningSchemaBootstrapper.cs` for old SQLite databases.
- [ ] Call `TaskPlanningSchemaBootstrapper.EnsureCreatedAsync(db, app.Logger)` after `MessageFabricSchemaBootstrapper.EnsureCreatedAsync` in `Source/PuddingAgent/Program.cs`.
- [ ] Add EF migration under `Source/PuddingPlatform/Migrations/` after model changes.

Entity mapping requirements:

```csharp
modelBuilder.Entity<TaskPlanRunEntity>(e =>
{
    e.ToTable("task_plan_runs");
    e.HasIndex(x => x.PlanId).IsUnique();
    e.HasIndex(x => new { x.WorkspaceId, x.Status, x.UpdatedAt });
});

modelBuilder.Entity<TaskNodeEntity>(e =>
{
    e.ToTable("task_nodes");
    e.HasIndex(x => x.TaskNodeId).IsUnique();
    e.HasIndex(x => new { x.PlanId, x.ParentTaskNodeId, x.Status });
    e.HasIndex(x => new { x.PlanId, x.Depth, x.Status });
});
```

Tests:

- [ ] Add `Source/PuddingPlatformTests\Services\TaskPlanning\TaskPlanStoreTests.cs`.
- [ ] Add `CreatePlanAsync_CreatesRootNode_WithDepthZeroAndDefaultMaxDepth`.
- [ ] Add `CreateNodeAsync_RejectsDepthGreaterThanPlanMaxDepth`.
- [ ] Add `UpdateNodeStatusAsync_RecordsTerminalFieldsAndResult`.
- [ ] Add `QueryNodesAsync_ReturnsPlanTreeInCreatedOrder`.
- [ ] Add `Source/PuddingPlatformTests\Services\TaskPlanning\TaskPlanningSchemaBootstrapperTests.cs`.

Run:

```powershell
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter TaskPlanStoreTests --logger "console;verbosity=minimal" --no-restore
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter TaskPlanningSchemaBootstrapperTests --logger "console;verbosity=minimal" --no-restore
```

Commit:

```powershell
git add Source/PuddingPlatform Source/PuddingPlatformTests Source/PuddingAgent
git commit -m "feat: persist task planning runs and nodes"
```

### Task 3: Implement Backend Delegation Policy

**Goal:** Enforce max depth, active node count, sub-delegation, and team-management rules outside prompt text.

- [ ] Implement `Source/PuddingRuntime/Services/TaskPlanning/TaskDelegationPolicy.cs`.
- [ ] Register `TaskPlanningOptions` and `ITaskDelegationPolicy` in `Source/PuddingRuntime/DependencyInjection.cs`.
- [ ] Register the same options in `Source/PuddingAgent/Program.cs` when the Web app composes services manually.
- [ ] Add default config sample to `Source/PuddingAgent/appsettings.json`.
- [ ] Make policy methods return structured deny reasons that tools can surface to agents.

Policy shape:

```csharp
public sealed record TaskDelegationDecision(
    bool Allowed,
    string Reason,
    int CurrentDepth,
    int MaxDepth);

public interface ITaskDelegationPolicy
{
    Task<TaskDelegationDecision> CanSplitAsync(TaskNode node, TaskPlanRun plan, CancellationToken ct = default);
    Task<TaskDelegationDecision> CanAssignAsync(TaskNode node, TaskPlanRun plan, string assignmentKind, CancellationToken ct = default);
    Task<TaskDelegationDecision> CanCreateTeamAgentAsync(TaskPlanRun plan, TaskNode? currentNode, CancellationToken ct = default);
}
```

Tests:

- [ ] Add `Source/PuddingRuntimeTests\Services\TaskDelegationPolicyTests.cs`.
- [ ] Add `CanSplitAsync_DeniesAtMaxDepth`.
- [ ] Add `CanSplitAsync_DeniesWhenNodeDisallowsSubDelegation`.
- [ ] Add `CanCreateTeamAgentAsync_UsesOptionsFlag`.
- [ ] Add `CanAssignAsync_DeniesWhenActiveNodeLimitReached`.

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter TaskDelegationPolicyTests --logger "console;verbosity=minimal" --no-restore
```

Commit:

```powershell
git add Source/PuddingRuntime Source/PuddingRuntimeTests Source/PuddingAgent
git commit -m "feat: enforce task planning delegation policy"
```

### Task 4: Propagate Task Planning Context Through Runtime Requests

**Goal:** Ensure every delegated run carries plan identity, node identity, depth, and max depth through the dispatch path.

- [ ] Extend `Source/PuddingCore/Platform/MessageContracts.cs` `RuntimeDispatchRequest` with:
  - `TaskPlanId`
  - `TaskNodeId`
  - `ParentTaskNodeId`
  - `DelegationDepth`
  - `MaxDelegationDepth`
  - `RoleInPlan`
  - `AllowSubDelegation`
  - `AllowAgentCreation`
  - `AssignedObjective`
  - `ExpectedOutputContract`
- [ ] Extend `Source/PuddingRuntime/Services/ContextPipeline.cs` `ContextRequest` with the same values needed by context assembly.
- [ ] Update `Source/PuddingRuntime/Services/AgentExecutionService.cs` wherever it builds `ContextRequest`.
- [ ] Update `Source/PuddingRuntime/Services/ContextAssemblyService.cs` to pass task planning fields from `ContextAssemblyRequest` after `ContextAssemblyRequest` is extended in `Source/PuddingCore/Runtime`.
- [ ] Update dispatch creators in:
  - `Source/PuddingController/Services/SessionRouter.cs`.
  - `Source/PuddingAgent/Services/Events/AgentEventHandler.cs`.
  - `Source/PuddingRuntime/Services/Messaging/MessageDeliveryDispatcher.cs`.
  - `Source/PuddingPlatform/Services/SubAgentManager.cs`.
- [ ] Keep new fields nullable so non-task-planning execution remains unchanged.

Tests:

- [ ] Add `Source/PuddingRuntimeTests\Services\TaskPlannerContextBuilderTests.cs`.
- [ ] Add `Source\PuddingRuntimeTests\Services\AgentExecutionTaskPlanningContextTests.cs` if existing test helpers make `AgentExecutionService` practical.
- [ ] Add a focused contract test that `RuntimeDispatchRequest` serializes and deserializes the new fields.

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter TaskPlannerContextBuilderTests --logger "console;verbosity=minimal" --no-restore
dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --filter RuntimeDispatchRequest --logger "console;verbosity=minimal" --no-restore
```

Commit:

```powershell
git add Source/PuddingCore Source/PuddingRuntime Source/PuddingRuntimeTests Source/PuddingController Source/PuddingAgent Source/PuddingPlatform
git commit -m "feat: propagate task planning runtime context"
```

### Task 5: Inject System-Generated Task Planner Context

**Goal:** Add a non-leader-controlled context layer that tells each assignee its plan position and constraints.

- [ ] Create `Source/PuddingRuntime/Services/TaskPlanning/TaskPlannerContextBuilder.cs`.
- [ ] Register it in `Source/PuddingRuntime/DependencyInjection.cs` and `Source/PuddingAgent/Program.cs`.
- [ ] Inject it in `Source/PuddingRuntime/Services/ContextPipeline.cs` after `L0-AGENTS-ROSTER` and before dynamic tools.
- [ ] Add a `ContextLayerInfo` entry named `L0-TASK-PLANNING`.
- [ ] Return an empty layer only when `TaskPlanId` or `TaskNodeId` is missing.

Context output format:

```text
--- TASK PLANNING CONTEXT ---
plan_id: plan_20260608_0001
task_node_id: task_20260608_research
parent_task_node_id: task_parent
delegation_depth: 1
max_delegation_depth: 2
role_in_plan: researcher
allowed_to_delegate: true
allowed_to_create_agents: false
assigned_objective:
Collect implementation risks for task dynamic planning storage and dispatch.
expected_output:
Return findings, evidence, risks, and proposed next task nodes.
constraints:
- do not exceed max delegation depth
- do not create agents unless allowed
- report completion through report_task_result
```

Tests:

- [ ] Add `BuildAsync_IncludesDepthAndMaxDepth`.
- [ ] Add `BuildAsync_AtMaxDepth_DisallowsDelegation`.
- [ ] Add `ContextPipeline_AssembleAsync_AddsTaskPlanningLayerBeforeTools`.

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter TaskPlannerContextBuilderTests --logger "console;verbosity=minimal" --no-restore
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter ContextPipeline --logger "console;verbosity=minimal" --no-restore
```

Commit:

```powershell
git add Source/PuddingRuntime Source/PuddingRuntimeTests Source/PuddingAgent
git commit -m "feat: inject task planning context"
```

### Task 6: Extend SubAgent Contracts and Enforce Depth

**Goal:** Make `spawn_sub_agent` task-aware and prevent recursive spawning beyond max depth.

- [ ] Extend `Source/PuddingRuntime/Services/SubAgentInvocationContracts.cs` `SubAgentInvocationRequest` with `PlanId`, `ParentTaskNodeId`, `Depth`, `MaxDepth`, `TaskNodeId`.
- [ ] Extend `Source/PuddingCore/Abstractions/ISubAgentManager.cs` `SubAgentSpawnRequest` with the same planning fields.
- [ ] Update `Source/PuddingRuntime/Services/SubAgentInvocationService.cs` to map the new fields.
- [ ] Update `Source/PuddingPlatform/Services/SubAgentManager.cs` to persist planning metadata into `SubAgentRunEntity` metadata or dedicated nullable columns.
- [ ] Add nullable columns to `Source/PuddingPlatform/Data/Entities/SubAgentRunEntity.cs` if dedicated columns are chosen.
- [ ] Update `Source/PuddingRuntime/Tools/BuiltIns/Agents/SubAgentTool.cs`:
  - Accept optional `plan_id`, `parent_task_node_id`, `task_node_id`, `depth`, `max_depth`, `role_in_plan`.
  - Call `ITaskDelegationPolicy` before spawning when `plan_id` and task node context are present.
  - Pass planning metadata into `ISubAgentInvocationService`.
  - Return a clear policy-denied `SkillResult` when depth is exceeded.

Tests:

- [ ] Add `Source\PuddingRuntimeTests\Tools\SubAgentToolTaskPlanningTests.cs`.
- [ ] Add `ExecuteAsync_AsyncSpawn_PropagatesTaskPlanningMetadata`.
- [ ] Add `ExecuteAsync_DeniesSpawnAtMaxDepth`.
- [ ] Add `Source\PuddingPlatformTests\Services\SubAgentManagerTaskPlanningTests.cs` if dedicated persistence columns are added.

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter SubAgentToolTaskPlanningTests --logger "console;verbosity=minimal" --no-restore
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter SubAgentManagerTaskPlanningTests --logger "console;verbosity=minimal" --no-restore
```

Commit:

```powershell
git add Source/PuddingCore Source/PuddingRuntime Source/PuddingRuntimeTests Source/PuddingPlatform Source/PuddingPlatformTests
git commit -m "feat: make sub agents task planning aware"
```

### Task 7: Add Planning Tools for Split, Assign, Report, and Query

**Goal:** Give agents auditable tools to create and update the durable task tree.

- [ ] Create `Source/PuddingRuntime/Tools/BuiltIns/TaskPlanning/SplitTaskTool.cs`.
- [ ] Create `Source/PuddingRuntime/Tools/BuiltIns/TaskPlanning/AssignTaskTool.cs`.
- [ ] Create `Source/PuddingRuntime/Tools/BuiltIns/TaskPlanning/ReportTaskResultTool.cs`.
- [ ] Create `Source/PuddingRuntime/Tools/BuiltIns/TaskPlanning/QueryTaskPlanTool.cs`.
- [ ] Implement each as native `IPuddingTool` using `PuddingToolBase<TArgs>` and `ToolAttribute`.
- [ ] Use `ITaskPlanStore` and `ITaskDelegationPolicy`; do not write directly to `PlatformDbContext` from tools.
- [ ] Tool IDs:
  - `split_task`
  - `assign_task`
  - `report_task_result`
  - `query_task_plan`
- [ ] Add `ToolCategory.Orchestration` and `ToolPermissionLevel.Medium`.
- [ ] Ensure assembly scanning registers tools in `AddPuddingRuntime`; add explicit `AddPuddingAgentTool<T>` registration in `Source/PuddingAgent/Program.cs` only if tests show Web composition misses native tool scanning.

Tool argument minimum:

```csharp
public sealed record SplitTaskArgs
{
    [ToolParam("Task plan id.")]
    public required string PlanId { get; init; }

    [ToolParam("Parent task node id.")]
    public required string ParentTaskNodeId { get; init; }

    [ToolParam("Child tasks to create.")]
    public required IReadOnlyList<SplitTaskChildArgs> Children { get; init; }
}
```

Tests:

- [ ] Add `Source\PuddingRuntimeTests\Tools\TaskPlanningToolsTests.cs`.
- [ ] Add `SplitTask_CreatesChildNodesBelowMaxDepth`.
- [ ] Add `SplitTask_DeniesChildrenAtMaxDepth`.
- [ ] Add `ReportTaskResult_CompletesNodeAndStoresSummary`.
- [ ] Add `QueryTaskPlan_ReturnsPlanAndNodes`.

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter TaskPlanningToolsTests --logger "console;verbosity=minimal" --no-restore
```

Commit:

```powershell
git add Source/PuddingRuntime Source/PuddingRuntimeTests Source/PuddingAgent
git commit -m "feat: add task planning agent tools"
```

### Task 8: Add Assignment Service and Dispatch Routing

**Goal:** Turn `assign_task` into real execution routing for leader, workspace agents, and sub agents.

- [ ] Implement `Source/PuddingRuntime/Services/TaskPlanning/TaskAssignmentService.cs`.
- [ ] Register `ITaskAssignmentService`.
- [ ] For `assigned_to_kind = leader`, mark node `running` and return instructions for the current agent to execute the node directly.
- [ ] For `assigned_to_kind = workspace_agent`, send a public message through `IMessageSystem` with metadata:
  - `plan_id`
  - `task_node_id`
  - `parent_task_node_id`
  - `delegation_depth`
  - `max_delegation_depth`
  - `role_in_plan`
  - `intent=task_assignment`
- [ ] Update `Source/PuddingRuntime/Services/Messaging/MessageDeliveryDispatcher.cs` so task assignment metadata is copied into `RuntimeDispatchRequest`.
- [ ] For `assigned_to_kind = sub_agent`, call `ISubAgentInvocationService.InvokeAsync` with planning metadata.
- [ ] Update `AssignTaskTool` to delegate to `ITaskAssignmentService` and persist node status transitions.
- [ ] Keep `TaskAssignmentService` free of LLM execution loops.

Tests:

- [ ] Add `Source\PuddingRuntimeTests\Services\TaskAssignmentServiceTests.cs`.
- [ ] Add `AssignWorkspaceAgentAsync_SendsMessageWithTaskMetadata`.
- [ ] Add `MessageDeliveryDispatcher_TaskAssignment_CopiesMetadataToRuntimeDispatch`.
- [ ] Add `AssignSubAgentAsync_InvokesSubAgentWithDepthMetadata`.
- [ ] Add `AssignLeaderAsync_MarksNodeRunningWithoutDispatch`.

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter TaskAssignmentServiceTests --logger "console;verbosity=minimal" --no-restore
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter MessageDeliveryDispatcherTests --logger "console;verbosity=minimal" --no-restore
```

Commit:

```powershell
git add Source/PuddingRuntime Source/PuddingRuntimeTests Source/PuddingAgent
git commit -m "feat: route task planning assignments"
```

### Task 9: Add Leader Team Management Tools

**Goal:** Allow a Leader to manage workspace agents through policy-gated tools.

- [ ] Add `IWorkspaceAgentAdministration` to `Source/PuddingCore/Abstractions/IWorkspaceAgentAdministration.cs`.
- [ ] Implement it on or beside `WorkspaceAgentFileService` in `Source/PuddingPlatform/Services/WorkspaceAgentAdministration.cs`.
- [ ] Register the abstraction in `Source/PuddingAgent/Program.cs` and any Platform DI extension used by tests.
- [ ] Create runtime tools:
  - `Source/PuddingRuntime/Tools/BuiltIns/TaskPlanning/ListTeamAgentsTool.cs`
  - `Source/PuddingRuntime/Tools/BuiltIns/TaskPlanning/CreateTeamAgentTool.cs`
  - `Source/PuddingRuntime/Tools/BuiltIns/TaskPlanning/UpdateTeamAgentTool.cs`
  - `Source/PuddingRuntime/Tools/BuiltIns/TaskPlanning/RetireTeamAgentTool.cs`
- [ ] Reuse existing `CreateWorkspaceAgentRequest` and `UpdateWorkspaceAgentRequest` mapping where possible.
- [ ] `create_team_agent` must call `ITaskDelegationPolicy.CanCreateTeamAgentAsync`.
- [ ] `retire_team_agent` should disable or delete according to existing `WorkspaceAgentFileService` behavior; use disable first when a reversible update is available.

Tests:

- [ ] Add `Source\PuddingRuntimeTests\Tools\TeamManagementToolsTests.cs`.
- [ ] Add `CreateTeamAgent_DeniesWhenOptionDisabled`.
- [ ] Add `CreateTeamAgent_CreatesAgentFromTemplate`.
- [ ] Add `RetireTeamAgent_DisablesAgent`.
- [ ] Add `ListTeamAgents_UsesWorkspaceCatalog`.

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter TeamManagementToolsTests --logger "console;verbosity=minimal" --no-restore
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter WorkspaceAgentFileServiceTests --logger "console;verbosity=minimal" --no-restore
```

Commit:

```powershell
git add Source/PuddingCore Source/PuddingPlatform Source/PuddingRuntime Source/PuddingRuntimeTests Source/PuddingPlatformTests Source/PuddingAgent
git commit -m "feat: add leader team management tools"
```

### Task 10: Add Research and Planner Templates

**Goal:** Provide first-class templates that Leader Agents can use for research and planning nodes.

- [ ] Add built-in templates to `Source/PuddingCore/Agents/BuiltInAgentTemplates.cs`:
  - `workspace-research-agent`
  - `workspace-planner-agent`
- [ ] Add default template data under `Source/PuddingAgent/default-data/templates/workspace-research-agent/`.
- [ ] Add default template data under `Source/PuddingAgent/default-data/templates/workspace-planner-agent/`.
- [ ] Ensure `TOOLS.md` for these templates includes `query_task_plan`, `split_task`, `assign_task`, and `report_task_result` only where appropriate.
- [ ] Research Agent should prefer evidence capture and should not create team agents by default.
- [ ] Planner Agent should split and assign, but still obey backend policy.

Tests:

- [ ] Add `Source\PuddingCoreTests\Agents\BuiltInAgentTemplatesTaskPlanningTests.cs`.
- [ ] Add `ResearchAndPlannerTemplates_AreDiscoverable`.
- [ ] Add `ResearchTemplate_DoesNotExposeTeamCreationByDefault`.
- [ ] Add or extend data seed tests if template seeding validates default-data folders.

Run:

```powershell
dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --filter BuiltInAgentTemplatesTaskPlanningTests --logger "console;verbosity=minimal" --no-restore
```

Commit:

```powershell
git add Source/PuddingCore Source/PuddingCoreTests Source/PuddingAgent/default-data
git commit -m "feat: add research and planner agent templates"
```

### Task 11: Add Plan Query API and Diagnostics

**Goal:** Make plans and task trees inspectable outside agent tools.

- [ ] Create `Source/PuddingPlatform/Controllers/Api/TaskPlanApiController.cs`.
- [ ] Add endpoints:
  - `GET /api/workspaces/{workspaceId}/task-plans`
  - `GET /api/workspaces/{workspaceId}/task-plans/{planId}`
  - `GET /api/workspaces/{workspaceId}/task-plans/{planId}/nodes`
- [ ] Add DTOs to `Source/PuddingPlatform/Data/Dtos/TaskPlanningDtos.cs`.
- [ ] Emit runtime activity events from `TaskPlanService` and `TaskAssignmentService` for:
  - `task_plan.created`
  - `task_node.created`
  - `task_node.assigned`
  - `task_node.completed`
  - `task_node.failed`
  - `task_node.superseded`
- [ ] Use existing `IRuntimeActivitySink` or telemetry sink conventions; do not invent a parallel observability store.

Tests:

- [ ] Add `Source\PuddingPlatformTests\Controllers\TaskPlanApiControllerTests.cs`.
- [ ] Add `ListPlans_FiltersByWorkspace`.
- [ ] Add `GetPlan_ReturnsNodesWhenRequested`.
- [ ] Add `TaskAssignmentService_EmitsRuntimeActivity`.

Run:

```powershell
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter TaskPlanApiControllerTests --logger "console;verbosity=minimal" --no-restore
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter TaskAssignmentServiceTests --logger "console;verbosity=minimal" --no-restore
```

Commit:

```powershell
git add Source/PuddingPlatform Source/PuddingPlatformTests Source/PuddingRuntime Source/PuddingRuntimeTests
git commit -m "feat: expose task planning diagnostics"
```

### Task 12: Add End-to-End Integration Coverage and Docs

**Goal:** Verify the whole flow and update operator-facing documentation.

- [ ] Add `Source\PuddingRuntimeTests\Integration\TaskDynamicPlanningFlowTests.cs`.
- [ ] Test flow:
  - Create plan with Leader root node.
  - Split into research and execution nodes.
  - Assign research node to sub-agent at depth 1.
  - Sub-agent attempts split at depth 2 and is denied.
  - Report result marks node completed.
  - Query plan returns full tree and terminal result.
- [ ] Add a workspace-agent assignment integration test using fake `IMessageSystem` and fake `IRuntimeAgentDispatcher`.
- [ ] Update `Docs/superpowers/specs/2026-06-08-task-dynamic-planning-design.md` with actual implemented file paths and any scope decisions.
- [ ] Add a new implementation baseline section to this plan after execution begins, matching the message fabric plan style.

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter TaskDynamicPlanningFlowTests --logger "console;verbosity=minimal" --no-restore
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter TaskPlan --logger "console;verbosity=minimal" --no-restore
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter TaskPlanning --logger "console;verbosity=minimal" --no-restore
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore
```

If the known external `github.hyfree.GM` project reference still blocks full build, record the exact error in the final implementation summary and rely on targeted test evidence above.

Commit:

```powershell
git add Source Docs
git commit -m "test: cover task dynamic planning flow"
```

## Cross-Cutting Requirements

- [ ] Never rely on prompt-only depth checks. Depth must be enforced by `TaskDelegationPolicy` and persisted in `TaskNode`.
- [ ] New task planning fields must be nullable on existing runtime request contracts so legacy chat, event, and message flows keep working.
- [ ] Tool denial messages must be short and machine-readable enough for agents to recover, for example `delegation_denied:max_depth_reached`.
- [ ] `TaskAssignmentService` must not execute LLM rounds directly.
- [ ] `AgentExecutionService` should only receive and forward task planning context; it should not own planning algorithms.
- [ ] SQLite bootstrap DDL must be idempotent and tolerate duplicate-column errors in the same style as `MessageFabricSchemaBootstrapper`.
- [ ] All tool IDs must be stable snake_case.
- [ ] All public JSON fields should be lower camel case through existing serializer defaults.
- [ ] Keep V1 focused on backend and tool behavior; add UI only after plan/query APIs are stable.

## Final Verification

Run targeted tests first:

```powershell
dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --filter TaskPlanning --logger "console;verbosity=minimal" --no-restore
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter TaskPlan --logger "console;verbosity=minimal" --no-restore
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter TaskPlanning --logger "console;verbosity=minimal" --no-restore
```

Then run broader regression checks:

```powershell
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --filter MessageFabric --logger "console;verbosity=minimal" --no-restore
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter SubAgent --logger "console;verbosity=minimal" --no-restore
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter MessageDeliveryDispatcherTests --logger "console;verbosity=minimal" --no-restore
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore
```

Expected final behavior:

- Leader can create a durable plan.
- Leader can split root task into child nodes.
- Child nodes cannot exceed configured `MaxDelegationDepth`.
- Assignment to workspace agents carries plan metadata through Message Fabric.
- Assignment to sub agents carries plan metadata through `ISubAgentInvocationService`.
- Delegated agents receive `--- TASK PLANNING CONTEXT ---`.
- Agents report completion through `report_task_result`.
- Plan and node history remains queryable after process restart.
