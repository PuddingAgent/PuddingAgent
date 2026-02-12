# Memory System v2 F5 Write Coordinator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the first F5 Write Coordinator layer so all memory write intents can be validated, dry-run, audited, and later routed to a single execution path.

**Architecture:** Add Core DTOs and validation for `MemoryWriteCommand`, then add a Runtime coordinator that supports `ValidateOnly` and `DryRun` without writing `MemoryLibrary`. Add an F4 mapper so accepted `MemoryMaintenancePlan` operations can become F5 commands, and persist compact audit envelopes through the existing job result path.

**Tech Stack:** C#/.NET 10, MSTest, existing `PuddingCore`, `PuddingRuntime`, `PuddingMemoryEngine` projects, existing `RuntimeActivity` and `TelemetryMetric` observability contracts.

---

## Scope

This plan implements F5a/F5b only:

- DTOs and constants.
- Command validator.
- Coordinator validate/dry-run.
- F4 operation -> F5 command mapper.
- Compact `MemoryWriteResultEnvelope`.
- Focused tests and docs status updates.

This plan does not:

- Execute real `MemoryLibrary` writes.
- Migrate `save_memory`.
- Migrate `MemoryLibraryConvenience.UpsertExperienceAsync`.
- Allow subconscious `delete` to true-delete memory.
- Implement TTL, migration, repair, or Admin UI.

---

## File Structure

- Create: `Source/PuddingCore/Models/MemoryWriteCommandModels.cs`
  - Owns F5 command/result DTOs, intent constants, mode constants, source kind constants, status constants, validation error constants, and validator.
- Modify: `Source/PuddingCore/PuddingCore.csproj`
  - No explicit item update expected unless project uses manual compile includes; inspect before editing.
- Create: `Source/PuddingCoreTests/Memory/MemoryWriteCommandValidatorTests.cs`
  - Core validator tests.
- Create: `Source/PuddingRuntime/Services/MemoryWriteCoordinator.cs`
  - Runtime service that validates and dry-runs commands, emits Trace/Metrics, and returns `MemoryWriteResultEnvelope`.
- Modify: `Source/PuddingRuntime/DependencyInjection.cs`
  - Register `MemoryWriteCommandValidator` and `IMemoryWriteCoordinator`.
- Create: `Source/PuddingRuntimeTests/Services/MemoryWriteCoordinatorTests.cs`
  - Coordinator dry-run and observability tests.
- Create: `Source/PuddingRuntime/Services/MemoryMaintenancePlanWriteCommandMapper.cs`
  - Converts F4 `MemoryMaintenanceOperation` into F5 command.
- Create: `Source/PuddingRuntimeTests/Services/MemoryMaintenancePlanWriteCommandMapperTests.cs`
  - Mapper tests, especially `delete` -> `delete_requested`.
- Modify: `Docs/superpowers/specs/2026-07-01-memory-v2-f5-write-coordinator-design.md`
  - Update status after implementation.
- Modify: `Docs/superpowers/specs/2026-07-01-memory-v2-foundation-prerequisites.md`
  - Update F5 status from `design` to `partial-dry-run`.
- Modify: `memory/memory-system-v2-requirements.md`
  - Append implementation record.
- Modify: `goal.md`
  - Append implementation decision log.

---

### Task 1: Core DTOs And Validator

**Files:**
- Create: `Source/PuddingCore/Models/MemoryWriteCommandModels.cs`
- Test: `Source/PuddingCoreTests/Memory/MemoryWriteCommandValidatorTests.cs`

- [ ] **Step 1: Write failing validator tests**

Create `Source/PuddingCoreTests/Memory/MemoryWriteCommandValidatorTests.cs`:

```csharp
using PuddingCode.Models;

namespace PuddingCoreTests.Memory;

[TestClass]
public sealed class MemoryWriteCommandValidatorTests
{
    [TestMethod]
    public void Validate_ShouldRejectMissingSource()
    {
        var command = ValidAppendCommand() with { Source = null! };
        var result = new MemoryWriteCommandValidator().Validate(command);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(e => e.Code == MemoryWriteValidationErrors.MissingSource));
    }

    [TestMethod]
    public void Validate_ShouldRejectCrossWorkspaceCandidate()
    {
        var command = ValidAppendCommand() with
        {
            Candidates =
            [
                new MemoryWriteCandidate
                {
                    WorkspaceId = "other-workspace",
                    ChapterId = "chapter-1",
                },
            ],
        };

        var result = new MemoryWriteCommandValidator().Validate(command);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(e => e.Code == MemoryWriteValidationErrors.CrossWorkspaceReference));
    }

    [TestMethod]
    public void Validate_ShouldRejectAppendWithoutContent()
    {
        var command = ValidAppendCommand() with { Payload = new MemoryWritePayload { Title = "Preference" } };
        var result = new MemoryWriteCommandValidator().Validate(command);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(e => e.Code == MemoryWriteValidationErrors.MissingRequiredField));
    }

    [TestMethod]
    public void Validate_ShouldRequireSubconsciousPlanIdentity()
    {
        var command = ValidAppendCommand() with
        {
            Source = new MemoryWriteSource
            {
                SourceKind = MemoryWriteSourceKinds.SubconsciousPlan,
                SessionId = "session-1",
            },
        };

        var result = new MemoryWriteCommandValidator().Validate(command);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(e => e.Code == MemoryWriteValidationErrors.MissingSourceIdentity));
    }

    [TestMethod]
    public void Validate_ShouldAcceptValidAppendDryRunCommand()
    {
        var result = new MemoryWriteCommandValidator().Validate(ValidAppendCommand());

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(0, result.Errors.Count);
    }

    private static MemoryWriteCommand ValidAppendCommand() =>
        new()
        {
            CommandId = "cmd-1",
            WorkspaceId = "workspace-1",
            Intent = MemoryWriteIntents.AppendNew,
            Mode = MemoryWriteExecutionModes.DryRun,
            Source = new MemoryWriteSource
            {
                SourceKind = MemoryWriteSourceKinds.RuntimeTool,
                SessionId = "session-1",
                ToolCallId = "tool-1",
            },
            Payload = new MemoryWritePayload
            {
                Title = "Preference",
                Content = "User prefers concise engineering summaries.",
                Confidence = 0.82,
            },
        };
}
```

- [ ] **Step 2: Run tests and verify RED**

Run:

```powershell
dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --filter "FullyQualifiedName~MemoryWriteCommandValidatorTests" --no-restore --verbosity minimal
```

Expected: FAIL because `MemoryWriteCommand` and validator types do not exist.

- [ ] **Step 3: Add DTOs and validator**

Create `Source/PuddingCore/Models/MemoryWriteCommandModels.cs`:

```csharp
namespace PuddingCode.Models;

public static class MemoryWriteIntents
{
    public const string ReuseExisting = "reuse_existing";
    public const string AppendNew = "append_new";
    public const string SupersedeExisting = "supersede_existing";
    public const string MergeCandidates = "merge_candidates";
    public const string Archive = "archive";
    public const string DeleteRequested = "delete_requested";
    public const string UpdateIndex = "update_index";
    public const string UpdateSkillPointer = "update_skill_pointer";
}

public static class MemoryWriteExecutionModes
{
    public const string ValidateOnly = "validate_only";
    public const string DryRun = "dry_run";
    public const string Execute = "execute";
}

public static class MemoryWriteSourceKinds
{
    public const string RuntimeTool = "runtime_tool";
    public const string SubconsciousPlan = "subconscious_plan";
    public const string Admin = "admin";
    public const string Migration = "migration";
    public const string Test = "test";
}

public static class MemoryWriteResultStatuses
{
    public const string Accepted = "accepted";
    public const string DryRun = "dry_run";
    public const string Executed = "executed";
    public const string Reused = "reused";
    public const string Quarantined = "quarantined";
    public const string Rejected = "rejected";
}

public static class MemoryWriteValidationErrors
{
    public const string MissingSource = "missing_source";
    public const string MissingSourceIdentity = "missing_source_identity";
    public const string MissingRequiredField = "missing_required_field";
    public const string CrossWorkspaceReference = "cross_workspace_reference";
    public const string UnsupportedIntent = "unsupported_intent";
    public const string UnsupportedMode = "unsupported_mode";
    public const string AutonomousDeleteNotAllowed = "autonomous_delete_not_allowed";
}

public sealed record MemoryWriteCommand
{
    public required string CommandId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string Intent { get; init; }
    public required MemoryWriteSource Source { get; init; }
    public IReadOnlyList<MemoryWriteCandidate> Candidates { get; init; } = [];
    public MemoryWriteCandidate? Target { get; init; }
    public MemoryWritePayload? Payload { get; init; }
    public string Mode { get; init; } = MemoryWriteExecutionModes.DryRun;
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed record MemoryWriteSource
{
    public required string SourceKind { get; init; }
    public string? SessionId { get; init; }
    public string? HookEventId { get; init; }
    public string? SubconsciousJobId { get; init; }
    public string? PlanId { get; init; }
    public string? OperationId { get; init; }
    public string? ToolCallId { get; init; }
    public string? AdminUserId { get; init; }
    public string? AgentId { get; init; }
    public string? AgentTemplateId { get; init; }
}

public sealed record MemoryWriteCandidate
{
    public required string WorkspaceId { get; init; }
    public string? BookId { get; init; }
    public string? ChapterId { get; init; }
    public string? FactId { get; init; }
    public string? PointerId { get; init; }

    public string? StableReferenceId => ChapterId ?? FactId ?? PointerId ?? BookId;
}

public sealed record MemoryWritePayload
{
    public string? Title { get; init; }
    public string? Content { get; init; }
    public double Confidence { get; init; } = 1.0;
    public string? Rationale { get; init; }
    public IReadOnlyList<string> RiskFlags { get; init; } = [];
}

public sealed record MemoryWriteValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public IReadOnlyList<MemoryWriteValidationError> Errors { get; init; } = [];
}

public sealed record MemoryWriteValidationError(string Code, string Message);

public sealed record MemoryWriteResultEnvelope
{
    public string Schema { get; init; } = "pudding.memory_write_result.v1";
    public required string CommandId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string Status { get; init; }
    public required string Mode { get; init; }
    public required string Intent { get; init; }
    public string? Decision { get; init; }
    public string? BookId { get; init; }
    public string? ChapterId { get; init; }
    public string? SupersededChapterId { get; init; }
    public IReadOnlyList<string> ErrorCodes { get; init; } = [];
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed class MemoryWriteCommandValidator
{
    private static readonly HashSet<string> SupportedIntents = new(StringComparer.Ordinal)
    {
        MemoryWriteIntents.ReuseExisting,
        MemoryWriteIntents.AppendNew,
        MemoryWriteIntents.SupersedeExisting,
        MemoryWriteIntents.MergeCandidates,
        MemoryWriteIntents.Archive,
        MemoryWriteIntents.DeleteRequested,
        MemoryWriteIntents.UpdateIndex,
        MemoryWriteIntents.UpdateSkillPointer,
    };

    private static readonly HashSet<string> SupportedModes = new(StringComparer.Ordinal)
    {
        MemoryWriteExecutionModes.ValidateOnly,
        MemoryWriteExecutionModes.DryRun,
        MemoryWriteExecutionModes.Execute,
    };

    public MemoryWriteValidationResult Validate(MemoryWriteCommand command)
    {
        var errors = new List<MemoryWriteValidationError>();

        Require(command.CommandId, "commandId", errors);
        Require(command.WorkspaceId, "workspaceId", errors);
        Require(command.Intent, "intent", errors);
        Require(command.Mode, "mode", errors);

        if (!SupportedIntents.Contains(command.Intent))
            errors.Add(new MemoryWriteValidationError(MemoryWriteValidationErrors.UnsupportedIntent, $"Unsupported memory write intent: {command.Intent}"));

        if (!SupportedModes.Contains(command.Mode))
            errors.Add(new MemoryWriteValidationError(MemoryWriteValidationErrors.UnsupportedMode, $"Unsupported memory write mode: {command.Mode}"));

        ValidateSource(command.Source, errors);
        ValidateReference(command.Target, command.WorkspaceId, errors);
        foreach (var candidate in command.Candidates)
            ValidateReference(candidate, command.WorkspaceId, errors);

        if (command.Intent == MemoryWriteIntents.AppendNew)
            Require(command.Payload?.Content, "payload.content", errors);

        if (command.Intent == MemoryWriteIntents.SupersedeExisting)
        {
            Require(command.Target?.ChapterId, "target.chapterId", errors);
            Require(command.Payload?.Content, "payload.content", errors);
        }

        if (command.Intent == MemoryWriteIntents.ReuseExisting)
            Require(command.Target?.ChapterId, "target.chapterId", errors);

        if (command.Intent == MemoryWriteIntents.DeleteRequested)
            errors.Add(new MemoryWriteValidationError(MemoryWriteValidationErrors.AutonomousDeleteNotAllowed, "delete_requested cannot execute through the autonomous memory maintenance path."));

        return new MemoryWriteValidationResult { Errors = errors };
    }

    private static void ValidateSource(MemoryWriteSource? source, List<MemoryWriteValidationError> errors)
    {
        if (source is null)
        {
            errors.Add(new MemoryWriteValidationError(MemoryWriteValidationErrors.MissingSource, "Memory write source is required."));
            return;
        }

        Require(source.SourceKind, "source.sourceKind", errors);

        if (source.SourceKind == MemoryWriteSourceKinds.SubconsciousPlan
            && (string.IsNullOrWhiteSpace(source.SubconsciousJobId)
                || string.IsNullOrWhiteSpace(source.PlanId)
                || string.IsNullOrWhiteSpace(source.OperationId)))
        {
            errors.Add(new MemoryWriteValidationError(
                MemoryWriteValidationErrors.MissingSourceIdentity,
                "subconscious_plan source requires subconsciousJobId, planId and operationId."));
        }

        if (source.SourceKind == MemoryWriteSourceKinds.RuntimeTool
            && string.IsNullOrWhiteSpace(source.SessionId)
            && string.IsNullOrWhiteSpace(source.ToolCallId))
        {
            errors.Add(new MemoryWriteValidationError(
                MemoryWriteValidationErrors.MissingSourceIdentity,
                "runtime_tool source requires sessionId or toolCallId."));
        }

        if (source.SourceKind == MemoryWriteSourceKinds.Admin
            && string.IsNullOrWhiteSpace(source.AdminUserId))
        {
            errors.Add(new MemoryWriteValidationError(
                MemoryWriteValidationErrors.MissingSourceIdentity,
                "admin source requires adminUserId."));
        }
    }

    private static void ValidateReference(MemoryWriteCandidate? reference, string workspaceId, List<MemoryWriteValidationError> errors)
    {
        if (reference is null)
            return;

        if (!string.Equals(reference.WorkspaceId, workspaceId, StringComparison.Ordinal))
        {
            errors.Add(new MemoryWriteValidationError(
                MemoryWriteValidationErrors.CrossWorkspaceReference,
                "Memory write reference workspace does not match command workspace."));
        }
    }

    private static void Require(string? value, string fieldName, List<MemoryWriteValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add(new MemoryWriteValidationError(MemoryWriteValidationErrors.MissingRequiredField, $"Required field is missing: {fieldName}"));
    }
}
```

- [ ] **Step 4: Run tests and verify GREEN**

Run:

```powershell
dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --filter "FullyQualifiedName~MemoryWriteCommandValidatorTests" --no-restore --verbosity minimal
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add Source/PuddingCore/Models/MemoryWriteCommandModels.cs Source/PuddingCoreTests/Memory/MemoryWriteCommandValidatorTests.cs
git commit -m "feat(memory): add write command validator"
```

---

### Task 2: Runtime Coordinator Dry-Run

**Files:**
- Create: `Source/PuddingRuntime/Services/MemoryWriteCoordinator.cs`
- Modify: `Source/PuddingRuntime/DependencyInjection.cs`
- Test: `Source/PuddingRuntimeTests/Services/MemoryWriteCoordinatorTests.cs`

- [ ] **Step 1: Write failing coordinator tests**

Create `Source/PuddingRuntimeTests/Services/MemoryWriteCoordinatorTests.cs`:

```csharp
using PuddingCode.Models;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class MemoryWriteCoordinatorTests
{
    [TestMethod]
    public async Task CoordinateAsync_ShouldReturnRejectedEnvelope_WhenCommandIsInvalid()
    {
        var coordinator = new MemoryWriteCoordinator(new MemoryWriteCommandValidator());
        var command = ValidAppendCommand() with { Payload = null };

        var envelope = await coordinator.CoordinateAsync(command, CancellationToken.None);

        Assert.AreEqual(MemoryWriteResultStatuses.Rejected, envelope.Status);
        Assert.AreEqual(MemoryWriteExecutionModes.DryRun, envelope.Mode);
        Assert.IsTrue(envelope.ErrorCodes.Contains(MemoryWriteValidationErrors.MissingRequiredField));
    }

    [TestMethod]
    public async Task CoordinateAsync_ShouldReturnDryRunEnvelope_WithoutWritingMemory()
    {
        var coordinator = new MemoryWriteCoordinator(new MemoryWriteCommandValidator());

        var envelope = await coordinator.CoordinateAsync(ValidAppendCommand(), CancellationToken.None);

        Assert.AreEqual(MemoryWriteResultStatuses.DryRun, envelope.Status);
        Assert.AreEqual(MemoryWriteIntents.AppendNew, envelope.Intent);
        Assert.AreEqual("cmd-1", envelope.CommandId);
        Assert.IsTrue(envelope.Metadata.ContainsKey("source_kind"));
    }

    private static MemoryWriteCommand ValidAppendCommand() =>
        new()
        {
            CommandId = "cmd-1",
            WorkspaceId = "workspace-1",
            Intent = MemoryWriteIntents.AppendNew,
            Mode = MemoryWriteExecutionModes.DryRun,
            Source = new MemoryWriteSource
            {
                SourceKind = MemoryWriteSourceKinds.RuntimeTool,
                SessionId = "session-1",
            },
            Payload = new MemoryWritePayload
            {
                Title = "Preference",
                Content = "User prefers concise engineering summaries.",
                Confidence = 0.82,
            },
        };
}
```

- [ ] **Step 2: Run tests and verify RED**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~MemoryWriteCoordinatorTests" --no-restore --verbosity minimal
```

Expected: FAIL because `MemoryWriteCoordinator` does not exist.

- [ ] **Step 3: Implement coordinator contract and dry-run behavior**

Create `Source/PuddingRuntime/Services/MemoryWriteCoordinator.cs`:

```csharp
using PuddingCode.Models;

namespace PuddingRuntime.Services;

public interface IMemoryWriteCoordinator
{
    Task<MemoryWriteResultEnvelope> CoordinateAsync(
        MemoryWriteCommand command,
        CancellationToken ct = default);
}

public sealed class MemoryWriteCoordinator : IMemoryWriteCoordinator
{
    private readonly MemoryWriteCommandValidator _validator;

    public MemoryWriteCoordinator(MemoryWriteCommandValidator validator)
    {
        _validator = validator;
    }

    public Task<MemoryWriteResultEnvelope> CoordinateAsync(
        MemoryWriteCommand command,
        CancellationToken ct = default)
    {
        var validation = _validator.Validate(command);
        if (!validation.IsValid)
        {
            return Task.FromResult(ToRejectedEnvelope(command, validation));
        }

        var status = command.Mode == MemoryWriteExecutionModes.ValidateOnly
            ? MemoryWriteResultStatuses.Accepted
            : MemoryWriteResultStatuses.DryRun;

        if (command.Intent == MemoryWriteIntents.ReuseExisting)
            status = MemoryWriteResultStatuses.Reused;

        return Task.FromResult(new MemoryWriteResultEnvelope
        {
            CommandId = command.CommandId,
            WorkspaceId = command.WorkspaceId,
            Status = status,
            Mode = command.Mode,
            Intent = command.Intent,
            Decision = command.Intent,
            BookId = command.Target?.BookId,
            ChapterId = command.Target?.ChapterId,
            Metadata = BuildMetadata(command),
        });
    }

    private static MemoryWriteResultEnvelope ToRejectedEnvelope(
        MemoryWriteCommand command,
        MemoryWriteValidationResult validation) =>
        new()
        {
            CommandId = command.CommandId,
            WorkspaceId = command.WorkspaceId,
            Status = MemoryWriteResultStatuses.Rejected,
            Mode = command.Mode,
            Intent = command.Intent,
            Decision = "reject",
            ErrorCodes = validation.Errors.Select(e => e.Code).Distinct(StringComparer.Ordinal).ToArray(),
            Metadata = BuildMetadata(command),
        };

    private static IReadOnlyDictionary<string, string> BuildMetadata(MemoryWriteCommand command)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source_kind"] = command.Source?.SourceKind ?? "missing",
        };

        if (command.Source is null)
            return metadata;

        if (!string.IsNullOrWhiteSpace(command.Source.SessionId))
            metadata["session_id"] = command.Source.SessionId;
        if (!string.IsNullOrWhiteSpace(command.Source.SubconsciousJobId))
            metadata["subconscious_job_id"] = command.Source.SubconsciousJobId;
        if (!string.IsNullOrWhiteSpace(command.Source.PlanId))
            metadata["plan_id"] = command.Source.PlanId;
        if (!string.IsNullOrWhiteSpace(command.Source.OperationId))
            metadata["operation_id"] = command.Source.OperationId;

        return metadata;
    }
}
```

Modify `Source/PuddingRuntime/DependencyInjection.cs` near existing runtime service registrations:

```csharp
services.TryAddSingleton<MemoryWriteCommandValidator>();
services.TryAddScoped<IMemoryWriteCoordinator, MemoryWriteCoordinator>();
```

If the file already uses `TryAddSingleton<MemoryMaintenancePlanValidator>()`, place the new validator next to it.

- [ ] **Step 4: Run tests and verify GREEN**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~MemoryWriteCoordinatorTests" --no-restore --verbosity minimal
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add Source/PuddingRuntime/Services/MemoryWriteCoordinator.cs Source/PuddingRuntime/DependencyInjection.cs Source/PuddingRuntimeTests/Services/MemoryWriteCoordinatorTests.cs
git commit -m "feat(memory): add write coordinator dry run"
```

---

### Task 3: F4 Plan Operation Mapper

**Files:**
- Create: `Source/PuddingRuntime/Services/MemoryMaintenancePlanWriteCommandMapper.cs`
- Test: `Source/PuddingRuntimeTests/Services/MemoryMaintenancePlanWriteCommandMapperTests.cs`

- [ ] **Step 1: Write failing mapper tests**

Create `Source/PuddingRuntimeTests/Services/MemoryMaintenancePlanWriteCommandMapperTests.cs`:

```csharp
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
        var command = MemoryMaintenancePlanWriteCommandMapper.MapOperation(plan, plan.Operations[0], MemoryWriteExecutionModes.DryRun);

        Assert.AreEqual(MemoryWriteIntents.AppendNew, command.Intent);
        Assert.AreEqual(MemoryWriteSourceKinds.SubconsciousPlan, command.Source.SourceKind);
        Assert.AreEqual("job-1", command.Source.SubconsciousJobId);
        Assert.AreEqual("plan-1", command.Source.PlanId);
        Assert.AreEqual("op-1", command.Source.OperationId);
        Assert.AreEqual("Updated memory.", command.Payload?.Content);
    }

    [TestMethod]
    public void MapOperation_ShouldMapDeleteToDeleteRequested()
    {
        var plan = BasePlan(MemoryMaintenanceActions.Delete);
        var command = MemoryMaintenancePlanWriteCommandMapper.MapOperation(plan, plan.Operations[0], MemoryWriteExecutionModes.DryRun);

        Assert.AreEqual(MemoryWriteIntents.DeleteRequested, command.Intent);
        Assert.AreEqual(MemoryWriteExecutionModes.DryRun, command.Mode);
    }

    [TestMethod]
    public void MapOperation_ShouldMapSupersedeTarget()
    {
        var plan = BasePlan(MemoryMaintenanceActions.SupersedeExisting);
        var command = MemoryMaintenancePlanWriteCommandMapper.MapOperation(plan, plan.Operations[0], MemoryWriteExecutionModes.DryRun);

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
```

- [ ] **Step 2: Run tests and verify RED**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~MemoryMaintenancePlanWriteCommandMapperTests" --no-restore --verbosity minimal
```

Expected: FAIL because mapper does not exist.

- [ ] **Step 3: Implement mapper**

Create `Source/PuddingRuntime/Services/MemoryMaintenancePlanWriteCommandMapper.cs`:

```csharp
using PuddingCode.Models;

namespace PuddingRuntime.Services;

public static class MemoryMaintenancePlanWriteCommandMapper
{
    public static MemoryWriteCommand MapOperation(
        MemoryMaintenancePlan plan,
        MemoryMaintenanceOperation operation,
        string mode)
    {
        return new MemoryWriteCommand
        {
            CommandId = $"{plan.PlanId}:{operation.OperationId}",
            WorkspaceId = plan.WorkspaceId,
            Intent = MapIntent(operation.Action),
            Mode = mode,
            Source = new MemoryWriteSource
            {
                SourceKind = MemoryWriteSourceKinds.SubconsciousPlan,
                SessionId = plan.Source.SessionId,
                HookEventId = plan.Source.HookEventId,
                SubconsciousJobId = plan.Source.SubconsciousJobId,
                PlanId = plan.PlanId,
                OperationId = operation.OperationId,
                AgentId = plan.Source.AgentId,
                AgentTemplateId = plan.Source.AgentTemplateId,
            },
            Target = MapReference(operation.Target),
            Candidates = plan.CandidateReads.Select(MapReference).Where(r => r is not null).Cast<MemoryWriteCandidate>().ToArray(),
            Payload = new MemoryWritePayload
            {
                Title = operation.ProposedTitle,
                Content = operation.ProposedContent,
                Confidence = operation.Confidence,
                Rationale = operation.Rationale,
                RiskFlags = operation.RiskFlags,
            },
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["plan_confidence"] = plan.Confidence.ToString("0.###"),
                ["operation_action"] = operation.Action,
            },
        };
    }

    private static string MapIntent(string action) =>
        action switch
        {
            MemoryMaintenanceActions.ReuseExisting => MemoryWriteIntents.ReuseExisting,
            MemoryMaintenanceActions.AppendNew => MemoryWriteIntents.AppendNew,
            MemoryMaintenanceActions.SupersedeExisting => MemoryWriteIntents.SupersedeExisting,
            MemoryMaintenanceActions.MergeCandidates => MemoryWriteIntents.MergeCandidates,
            MemoryMaintenanceActions.Deprecate => MemoryWriteIntents.Archive,
            MemoryMaintenanceActions.Delete => MemoryWriteIntents.DeleteRequested,
            MemoryMaintenanceActions.UpdateIndex => MemoryWriteIntents.UpdateIndex,
            MemoryMaintenanceActions.UpdateSkillPointer => MemoryWriteIntents.UpdateSkillPointer,
            _ => action,
        };

    private static MemoryWriteCandidate? MapReference(MemoryPlanReference? reference) =>
        reference is null
            ? null
            : new MemoryWriteCandidate
            {
                WorkspaceId = reference.WorkspaceId,
                BookId = reference.BookId,
                ChapterId = reference.ChapterId,
                FactId = reference.FactId,
                PointerId = reference.PointerId,
            };
}
```

- [ ] **Step 4: Run tests and verify GREEN**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~MemoryMaintenancePlanWriteCommandMapperTests" --no-restore --verbosity minimal
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add Source/PuddingRuntime/Services/MemoryMaintenancePlanWriteCommandMapper.cs Source/PuddingRuntimeTests/Services/MemoryMaintenancePlanWriteCommandMapperTests.cs
git commit -m "feat(memory): map maintenance plans to write commands"
```

---

### Task 4: Subconscious Plan Dry-Run Through F5

**Files:**
- Modify: `Source/PuddingRuntime/Services/SubconsciousPlanGenerationService.cs`
- Test: `Source/PuddingRuntimeTests/Services/SubconsciousPlanGenerationServiceTests.cs`

- [ ] **Step 1: Add failing test for F5 dry-run result metadata**

In `Source/PuddingRuntimeTests/Services/SubconsciousPlanGenerationServiceTests.cs`, add a test that verifies accepted F4 plans can be mapped and dry-run through F5 without executing writes:

```csharp
[TestMethod]
public async Task AcceptedPlan_ShouldMapToF5DryRunCommand()
{
    var generation = await CreateService(ValidPlanJson).GenerateDryRunAsync(BaseRequest(), CancellationToken.None);
    Assert.IsNotNull(generation.Plan);

    var command = MemoryMaintenancePlanWriteCommandMapper.MapOperation(
        generation.Plan,
        generation.Plan.Operations[0],
        MemoryWriteExecutionModes.DryRun);

    var coordinator = new MemoryWriteCoordinator(new MemoryWriteCommandValidator());
    var writeResult = await coordinator.CoordinateAsync(command, CancellationToken.None);

    Assert.AreEqual(MemoryWriteResultStatuses.DryRun, writeResult.Status);
    Assert.AreEqual(MemoryWriteIntents.AppendNew, writeResult.Intent);
    Assert.AreEqual("job-1", writeResult.Metadata["subconscious_job_id"]);
}
```

If `ValidPlanJson` currently uses `supersede_existing`, assert the matching intent instead of `AppendNew`.

- [ ] **Step 2: Run test and verify RED if imports or mapper are missing**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~SubconsciousPlanGenerationServiceTests.AcceptedPlan_ShouldMapToF5DryRunCommand" --no-restore --verbosity minimal
```

Expected: PASS if Task 3 is already complete and test imports are correct; otherwise fail for missing mapper/using. Fix only missing test imports at this step.

- [ ] **Step 3: Keep production path unchanged**

Do not modify `SubconsciousWorkerService` yet. F5 dry-run is proven through tests and explicit mapper/coordinator calls only. This preserves the current boundary: F4 produces dry-run plan result, F5 dry-run exists, but no worker execution or memory write is connected.

- [ ] **Step 4: Run focused test set**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~SubconsciousPlanGenerationServiceTests|FullyQualifiedName~MemoryMaintenancePlanWriteCommandMapperTests|FullyQualifiedName~MemoryWriteCoordinatorTests" --no-restore --verbosity minimal
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add Source/PuddingRuntimeTests/Services/SubconsciousPlanGenerationServiceTests.cs
git commit -m "test(memory): prove f5 dry run for accepted plans"
```

---

### Task 5: Observability Hooks For Coordinator

**Files:**
- Modify: `Source/PuddingRuntime/Services/MemoryWriteCoordinator.cs`
- Test: `Source/PuddingRuntimeTests/Services/MemoryWriteCoordinatorTests.cs`

- [ ] **Step 1: Add failing observability test**

Extend `MemoryWriteCoordinatorTests` with recording sinks that implement the existing `IRuntimeActivitySink` and `ITelemetryMetricSink` interfaces:

```csharp
[TestMethod]
public async Task CoordinateAsync_ShouldEmitMemoryWriteMetric()
{
    var metrics = new RecordingTelemetryMetricSink();
    var activities = new RecordingRuntimeActivitySink();
    var coordinator = new MemoryWriteCoordinator(
        new MemoryWriteCommandValidator(),
        activities,
        metrics);

    await coordinator.CoordinateAsync(ValidAppendCommand(), CancellationToken.None);

    Assert.IsTrue(metrics.Metrics.Any(e => e.Name == "memory_write.command"));
    Assert.IsTrue(activities.Activities.Any(e => e.Operation == "memory_write.dry_run"));
}

private sealed class RecordingTelemetryMetricSink : ITelemetryMetricSink
{
    public List<TelemetryMetric> Metrics { get; } = [];

    public Task RecordAsync(TelemetryMetric metric, CancellationToken ct = default)
    {
        Metrics.Add(metric);
        return Task.CompletedTask;
    }
}

private sealed class RecordingRuntimeActivitySink : IRuntimeActivitySink
{
    public List<RuntimeActivity> Activities { get; } = [];

    public Task RecordAsync(RuntimeActivity activity, CancellationToken ct = default)
    {
        Activities.Add(activity);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RuntimeActivity>> QueryAsync(
        RuntimeActivityQuery query,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RuntimeActivity>>(Activities);
}
```

Add these imports at the top of the test file:

```csharp
using PuddingCode.Observability;
```

- [ ] **Step 2: Run test and verify RED**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~MemoryWriteCoordinatorTests.CoordinateAsync_ShouldEmitMemoryWriteMetric" --no-restore --verbosity minimal
```

Expected: FAIL because coordinator does not accept observability dependencies.

- [ ] **Step 3: Add optional observability dependencies**

Modify `MemoryWriteCoordinator` constructor:

```csharp
private readonly IRuntimeActivitySink? _activitySink;
private readonly ITelemetryMetricSink? _telemetrySink;

public MemoryWriteCoordinator(
    MemoryWriteCommandValidator validator,
    IRuntimeActivitySink? activitySink = null,
    ITelemetryMetricSink? telemetrySink = null)
{
    _validator = validator;
    _activitySink = activitySink;
    _telemetrySink = telemetrySink;
}
```

Add a helper that emits both activity and metric:

```csharp
private async Task RecordDecisionAsync(
    MemoryWriteCommand command,
    MemoryWriteResultEnvelope envelope,
    CancellationToken ct)
{
    var operation = envelope.Status == MemoryWriteResultStatuses.Rejected
        ? "memory_write.reject"
        : command.Mode == MemoryWriteExecutionModes.ValidateOnly
            ? "memory_write.validate"
            : "memory_write.dry_run";

    var status = envelope.Status == MemoryWriteResultStatuses.Rejected
        ? RuntimeActivityStatuses.Failed
        : RuntimeActivityStatuses.Succeeded;

    var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["command_id"] = command.CommandId,
        ["workspace_id"] = command.WorkspaceId,
        ["source_kind"] = command.Source?.SourceKind ?? "missing",
        ["intent"] = command.Intent,
        ["mode"] = command.Mode,
        ["status"] = envelope.Status,
        ["dry_run"] = (command.Mode == MemoryWriteExecutionModes.DryRun).ToString().ToLowerInvariant(),
    };

    if (envelope.ErrorCodes.Count > 0)
        metadata["error_code"] = envelope.ErrorCodes[0];

    if (_activitySink is not null)
    {
        await _activitySink.RecordAsync(new RuntimeActivity
        {
            Trace = RuntimeTraceContext.CreateNew(
                sessionId: command.Source?.SessionId,
                workspaceId: command.WorkspaceId,
                eventId: command.Source?.HookEventId),
            Component = RuntimeActivityComponents.Memory,
            Operation = operation,
            Status = status,
            Summary = $"Memory write {envelope.Status}",
            Metadata = metadata,
            ErrorCode = envelope.ErrorCodes.FirstOrDefault(),
        }, ct);
    }

    if (_telemetrySink is not null)
    {
        await _telemetrySink.RecordAsync(new TelemetryMetric
        {
            Trace = RuntimeTraceContext.CreateNew(
                sessionId: command.Source?.SessionId,
                workspaceId: command.WorkspaceId,
                eventId: command.Source?.HookEventId),
            Source = "pudding.memory.write_coordinator",
            Category = TelemetryMetricCategories.Memory,
            Name = "memory_write.command",
            Status = status == RuntimeActivityStatuses.Failed
                ? TelemetryMetricStatuses.Failed
                : TelemetryMetricStatuses.Succeeded,
            CountValue = 1,
            Unit = "command",
            Severity = status == RuntimeActivityStatuses.Failed ? "warning" : "info",
            Summary = $"Memory write {envelope.Status}",
            Dimensions = metadata,
            ErrorCode = envelope.ErrorCodes.FirstOrDefault(),
        }, ct);
    }
}
```

Change `CoordinateAsync` to `async Task<MemoryWriteResultEnvelope>`, assign the envelope to a local variable, call `await RecordDecisionAsync(command, envelope, ct);`, then return the envelope.

Emit:

- `memory_write.validate` for `ValidateOnly`.
- `memory_write.dry_run` for valid dry-run.
- `memory_write.reject` for invalid command.

Metric:

- category: `memory`
- name: `memory_write.command`
- dimensions: `workspace_id`, `source_kind`, `intent`, `mode`, `status`, `dry_run`, and first `error_code` when present.

Do not log payload content.

- [ ] **Step 4: Run tests**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~MemoryWriteCoordinatorTests" --no-restore --verbosity minimal
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add Source/PuddingRuntime/Services/MemoryWriteCoordinator.cs Source/PuddingRuntimeTests/Services/MemoryWriteCoordinatorTests.cs
git commit -m "feat(memory): observe write coordinator decisions"
```

---

### Task 6: Documentation And Verification

**Files:**
- Modify: `Docs/superpowers/specs/2026-07-01-memory-v2-f5-write-coordinator-design.md`
- Modify: `Docs/superpowers/specs/2026-07-01-memory-v2-foundation-prerequisites.md`
- Modify: `memory/memory-system-v2-requirements.md`
- Modify: `goal.md`

- [ ] **Step 1: Update F5 design status**

Change header in `Docs/superpowers/specs/2026-07-01-memory-v2-f5-write-coordinator-design.md`:

```markdown
> Status: partial-dry-run
```

Append an implementation record:

```markdown
## 15. Implementation Status

2026-07-01 first implementation:

- Added `MemoryWriteCommand` DTOs, validator and result envelope.
- Added `MemoryWriteCoordinator` validate/dry-run path.
- Added F4 `MemoryMaintenancePlan` operation mapper.
- Verified accepted F4 plans can produce F5 dry-run result envelopes.
- Still not connected to real `MemoryLibrary` writes.
```

- [ ] **Step 2: Update foundation prerequisites**

In `Docs/superpowers/specs/2026-07-01-memory-v2-foundation-prerequisites.md`, change F5 status from `design` to `partial-dry-run` and add:

```markdown
- F5 Memory Write Coordinator：DTO、validator、dry-run coordinator 和 F4 operation mapper 已完成第一实现；尚未接 `save_memory`、Admin/API 或真实 `MemoryLibrary` 写入。
```

- [ ] **Step 3: Update requirements and goal**

Append to `memory/memory-system-v2-requirements.md` implementation table:

```markdown
| 2026-07-01 | F5 Memory Write Coordinator dry-run 第一实现 | partial-dry-run | 新增 `MemoryWriteCommand`、validator、`MemoryWriteCoordinator` dry-run、F4 operation mapper 和 `MemoryWriteResultEnvelope`；合法 plan 可转换成 F5 dry-run 结果，非法 command 被拒绝并记录错误。该阶段仍不接真实 `MemoryLibrary` 写入。 |
```

Append to `goal.md`:

```markdown
- 2026-07-01: F5 Memory Write Coordinator dry-run 第一实现完成 — 新增统一写入 command、validator、dry-run coordinator、F4 operation mapper 和 result envelope；该阶段只验证和审计写入意图，不执行真实 `MemoryLibrary` 写入。下一步迁移 `MemoryLibraryConvenience.UpsertExperienceAsync` 或先补 Job result 衔接，需单独确认。
```

- [ ] **Step 4: Run full focused verification**

Run:

```powershell
dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --filter "FullyQualifiedName~MemoryWriteCommandValidatorTests" --no-restore --verbosity minimal
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~MemoryWriteCoordinatorTests|FullyQualifiedName~MemoryMaintenancePlanWriteCommandMapperTests|FullyQualifiedName~SubconsciousPlanGenerationServiceTests" --no-restore --verbosity minimal
dotnet test Source\PuddingMemoryEngineTests\PuddingMemoryEngineTests.csproj --filter "FullyQualifiedName~SubconsciousJobQueueTests" --no-restore --verbosity minimal
git diff --check
```

Expected:

- All tests pass.
- `git diff --check` has no whitespace errors.
- Existing NuGet/MSTest warnings may remain.

- [ ] **Step 5: Commit**

```powershell
git add Docs/superpowers/specs/2026-07-01-memory-v2-f5-write-coordinator-design.md Docs/superpowers/specs/2026-07-01-memory-v2-foundation-prerequisites.md memory/memory-system-v2-requirements.md goal.md
git commit -m "docs(memory): record f5 dry run status"
```

---

## Self-Review Checklist

- Spec coverage: Covers F5 DTOs, validator, dry-run, F4 mapper, audit envelope and docs status.
- Explicit non-goals: Real `MemoryLibrary` writes, `save_memory` migration and Admin/API migration are outside this first implementation.
- Safety: Subconscious `delete` maps to `delete_requested` and validator returns review/reject behavior; it does not true-delete.
- Test-first: Each behavior task starts with a focused failing test.
- No content hash: The plan does not introduce hash-based semantic equality.
- Rollback: Each task is independently committable and leaves current runtime write paths unchanged until a later migration task.
