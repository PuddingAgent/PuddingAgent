# Memory v2 Minimal V1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the minimal Memory v2 V1 loop from compaction `memoryNotes` to subconscious page update and deterministic Book/Page upsert replace.

**Architecture:** Reuse the existing `session.compressed` hook, durable job queue, `SubconsciousWorkerService`, `IMemoryLlmClient`, and `IMemoryLibrary`. Add a small page-update service that parses `pudding.memory_wiki_page_update.v1` and writes Book/Chapter records by title/path, replacing page content instead of merging fragments.

**Tech Stack:** C#/.NET, MSTest, existing Pudding runtime and memory engine services.

---

### Task 1: Carry Memory Notes Through Compaction Job

**Files:**
- Modify: `Source/PuddingCore/Runtime/ContextCompactionContracts.cs`
- Modify: `Source/PuddingCore/Platform/SubconsciousDtos.cs`
- Modify: `Source/PuddingRuntime/Services/AgentContextCompactionSummaryGenerator.cs`
- Modify: `Source/PuddingRuntime/Services/ContextCompactionService.cs`
- Modify: `Source/PuddingRuntime/Services/Hooks/SessionCompressedMemoryMaintenanceHook.cs`
- Test: `Source/PuddingRuntimeTests/Services/SessionCompressedMemoryMaintenanceHookTests.cs`

- [ ] Add `IReadOnlyList<string> MemoryNotes` to compaction result/hook/job DTOs.
- [ ] Update the compaction prompt to request a lightweight `## Memory Notes` section.
- [ ] Extract memory notes from the summary markdown and publish them in `session.compressed`.
- [ ] Enqueue durable jobs with `MemoryNotes`.
- [ ] Add/adjust tests proving `MemoryNotes` reach `ConsolidationJob`.

### Task 2: Add Page Update Plan Service

**Files:**
- Create: `Source/PuddingRuntime/Services/MemoryWikiPageUpdateService.cs`
- Test: `Source/PuddingRuntimeTests/Services/MemoryWikiPageUpdateServiceTests.cs`
- Modify: `Source/PuddingRuntime/DependencyInjection.cs`

- [ ] Define `MemoryWikiPageUpdatePlan` and `MemoryWikiPageUpdate` DTOs in the service file.
- [ ] Generate page update JSON from `memoryNotes` and current minimal context.
- [ ] Validate only JSON shape, schema, non-empty updates, and non-empty `book/page/content`.
- [ ] Register the service in runtime DI.

### Task 3: Add Minimal Wiki Page Write Entry

**Files:**
- Create: `Source/PuddingRuntime/Services/WikiPageWriteEntry.cs`
- Test: `Source/PuddingRuntimeTests/Services/WikiPageWriteEntryTests.cs`
- Modify: `Source/PuddingRuntime/DependencyInjection.cs`

- [ ] Resolve or create the first workspace library.
- [ ] Resolve or create Book by title using existing `FindBookByTitleAsync` / `CreateBookAsync`.
- [ ] Resolve Page by normalized path from active chapters in the Book.
- [ ] Create missing Page with `AddChapterAsync`.
- [ ] Replace existing Page content with `UpdateChapterContentAsync`.
- [ ] Add tests for create, replace, and no duplicate Book/Page.

### Task 4: Wire V1 Into Subconscious Worker

**Files:**
- Modify: `Source/PuddingRuntime/Services/Background/SubconsciousWorkerService.cs`
- Test: `Source/PuddingRuntimeTests/Services/SubconsciousWorkerServiceTests.cs`

- [ ] Inject `MemoryWikiPageUpdateService` and `WikiPageWriteEntry`.
- [ ] In durable job processing, if `MemoryNotes` exists, run the V1 path before the old F4/F5 dry-run path.
- [ ] Complete no-op jobs when `MemoryNotes` is empty.
- [ ] Record a small job result envelope for V1 success/failure using existing result storage.
- [ ] Add tests proving V1 bypasses old orchestrator and old dry-run coordinator.

### Task 5: Verify

**Commands:**

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --no-restore --filter "MemoryWikiPageUpdateServiceTests|WikiPageWriteEntryTests|SubconsciousWorkerServiceTests|SessionCompressedMemoryMaintenanceHookTests" --logger "console;verbosity=minimal"
dotnet test Source\PuddingMemoryEngineTests\PuddingMemoryEngineTests.csproj --no-restore --filter "MemoryLibraryTests" --logger "console;verbosity=minimal"
```

Expected result: targeted tests pass. If the broad `MemoryLibraryTests` filter is too large or slow, run only the specific tests touched by write entry behavior.
