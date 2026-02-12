# Task 04: Swarm Mode Implementation (蜂群模式)

> **Implementation Plan for V0.8 + V0.9**
> 
> **Priority**: 🔴 P0 - Critical Path
> **Status**: Ready for Implementation (Design Complete)
> **Dependencies**: ✅ V0.1 Core, ✅ Task 11 (Permission), ✅ Task 12 (Sensory Filter), ✅ D06 (Git)
> **Blocks**: D08 (Swarm Contract Orchestration), Task 09/10/11/12 (Agent Intelligence Chain)

---

## TL;DR

> **Quick Summary**: Implement contract-first multi-agent collaboration system where Leader Agent defines interfaces/contracts and Worker Agents implement assigned modules in isolated scopes.
> 
> **Deliverables**:
> - 6 new interfaces (abstractions layer)
> - 12 new model classes
> - 7 core implementations
> - ScopedFileTool with scope isolation
> - File-based swarm messaging system
> - CLI integration (/swarm commands)
> - 50+ unit tests
> 
> **Estimated Effort**: Large (50+ tasks, 4 waves)
> **Parallel Execution**: YES - 4 waves with max 12 concurrent tasks
> **Critical Path**: Abstractions → Models → Core Components → Integration → Tests

---

## Context

### Original Request
User requested implementation of tasks from `Docs/Tasks.md` and `Docs/Tasks/` directory.

### Analysis Summary
After comprehensive analysis of Tasks.md and all task design documents:

**Completed** (✅ Verified):
- V0.1 Core: AgentOrchestrator, ToolRegistry, FileTool, ShellTool, OpenAiLlmGateway, CLI REPL
- Task 11: PermissionGuard (path sandbox + command whitelist)
- Task 12: DefaultDistiller (output truncation + error enhancement)
- D06: GitSnapshotService (snapshot/undo/history commands)

**Pending - Priority Order**:
1. **Task 04 (Swarm Mode)** - P0, design complete ← **THIS PLAN**
2. Task 09 (Agent Lifecycle) - P0, depends on Task 04
3. Task 10 (Agent Capability) - P0, depends on Task 04
4. D08 (Swarm Contract Orchestration) - P0, depends on Task 04

### Why Task 04 First?

| Reason | Explanation |
|--------|-------------|
| **Foundation** | Swarm Mode is the architectural foundation for all Agent intelligence features |
| **Design Complete** | task04-swarm.md is comprehensive (775 lines) with clear specifications |
| **All Prerequisites Met** | Permission, Distiller, Git all implemented and tested |
| **Blocks Critical Path** | Cannot implement D08, Task 09/10/11/12 without swarm foundation |
| **P0 Priority** | Marked as highest priority in Tasks.md |

### Implementation Phases

**This Plan Covers**:
- **Phase 1 (V0.8)**: Contract-first + Serial Execution
- **Phase 2 (V0.9)**: Parallel Execution + Git Worktree Isolation

**Deferred to Future Plans**:
- **Phase 3 (V1.0)**: P2P Distributed Swarm (requires network stack)
- **Phase 4 (V1.x)**: Ecosystem Enhancement (Model Router, cost dashboard)

---

## Work Objectives

### Core Objective
Implement contract-first multi-agent collaboration system enabling:
1. Leader Agent defines contracts (interfaces + specifications)
2. Worker Agents implement assigned modules within scope isolation
3. Git Worktree-based parallel execution without conflicts
4. File-based messaging for Agent communication

### Concrete Deliverables

**New Abstractions** (`Source/PuddingCode/Abstractions/`):
- `ISwarmOrchestrator.cs` - Main swarm orchestrator interface
- `IWorkerManager.cs` - Worker lifecycle management
- `IContractManager.cs` - Contract definition and validation
- `ILeaderElection.cs` - Leader election (stub for Phase 3)
- `ISwarmTransport.cs` - Swarm messaging abstraction
- `WorkerScope.cs` - Scope isolation model

**New Models** (`Source/PuddingCode/Models/`):
- `Contract.cs` - Contract definition
- `SwarmTask.cs` - Task model with status tracking
- `WorkerInfo.cs` - Worker metadata
- `WorkerRole.cs` - Role enumeration (Leader/Builder/QA/Docs)
- `SwarmNode.cs` - Network node (stub for Phase 3)
- `SwarmEvents.cs` - 10 new AgentEvent subclasses

**Core Implementations** (`Source/PuddingCode/Swarm/`):
- `SwarmOrchestrator.cs` - Main orchestrator implementation
- `WorkerManager.cs` - Worker spawning and management
- `ContractManager.cs` - Contract-first workflow
- `FileSwarmTransport.cs` - Local file-based messaging
- `ScopedFileTool.cs` - FileTool wrapper with scope enforcement
- `ContractValidator.cs` - Contract signature validation

**CLI Integration** (`Source/PuddingCodeCLI/`):
- `/swarm` - Manual swarm trigger
- `/swarm status` - View swarm status
- `/swarm cancel` - Cancel current swarm
- Swarm command parser and renderer

**Tests** (`Source/PuddingCodeTests/Swarm/`):
- 50+ unit tests covering all new components
- Integration tests for swarm workflow

### Definition of Done

- [ ] All abstractions defined and documented
- [ ] All model classes implemented with XML docs
- [ ] Core implementations pass unit tests
- [ ] Integration test: Leader creates contract → Worker implements → Validation passes
- [ ] CLI commands functional and tested
- [ ] No breaking changes to existing V0.1 functionality
- [ ] All tests pass: `dotnet test` returns 0 failures

### Must Have

1. **Contract-First Workflow**: Leader must be able to define contracts with specifications
2. **Scope Isolation**: Workers must be restricted to assigned files/symbols
3. **File-Based Messaging**: Agents must communicate via `.pudding/swarm/messages/`
4. **Git Worktree**: Each Worker must have isolated worktree
5. **Role-Based Permissions**: Leader vs Worker skill access control

### Must NOT Have (Guardrails)

- ❌ P2P networking (Phase 3 feature)
- ❌ Leader election logic (Phase 3 feature)
- ❌ Model routing / cost tracking (Phase 4 feature)
- ❌ Desktop UI visualization (separate task)
- ❌ Breaking changes to existing AgentOrchestrator API

---

## Verification Strategy

> **ZERO HUMAN INTERVENTION** — ALL verification is agent-executed. No exceptions.

### Test Decision

- **Infrastructure exists**: YES (MSTest.Sdk/4.0.1)
- **Automated tests**: YES (TDD workflow)
- **Framework**: MSTest (built-in)
- **If TDD**: Each task follows RED (failing test) → GREEN (minimal impl) → REFACTOR

### QA Policy

Every task MUST include agent-executed QA scenarios. Evidence saved to `.sisyphus/evidence/task-{N}-{scenario-slug}.{ext}`.

| Deliverable Type | Verification Tool | Method |
|------------------|-------------------|--------|
| Core Library | Bash (dotnet test) | Run unit tests, assert 0 failures |
| Integration | Bash (dotnet test) | End-to-end swarm workflow test |
| CLI Commands | interactive_bash (tmux) | Run /swarm commands, validate output |
| File System | Bash (PowerShell) | Verify .pudding/swarm/ structure |

---

## Execution Strategy

### Parallel Execution Waves

```
Wave 1 (Start Immediately — Abstractions + Models):
├── Task 1: ISwarmOrchestrator + IWorkerManager interfaces [quick]
├── Task 2: IContractManager + ILeaderElection interfaces [quick]
├── Task 3: ISwarmTransport + WorkerScope interfaces [quick]
├── Task 4: Contract + SwarmTask models [quick]
├── Task 5: WorkerInfo + WorkerRole + WorkerScope models [quick]
├── Task 6: SwarmEvents (10 event types) [quick]
└── Task 7: SwarmNode model (Phase 3 stub) [quick]

Wave 2 (After Wave 1 — Core Components, MAX PARALLEL):
├── Task 8: ContractManager implementation (depends: 2, 4) [deep]
├── Task 9: FileSwarmTransport implementation (depends: 3, 6) [unspecified-high]
├── Task 10: ScopedFileTool implementation (depends: 3, 5) [deep]
├── Task 11: ContractValidator implementation (depends: 4) [unspecified-high]
├── Task 12: WorkerManager skeleton (depends: 2, 5) [quick]
└── Task 13: .pudding/swarm/ directory structure [quick]

Wave 3 (After Wave 2 — Orchestrator + Integration):
├── Task 14: WorkerManager full implementation (depends: 12, 8, 9) [deep]
├── Task 15: SwarmOrchestrator core logic (depends: 1, 8, 9, 14) [deep]
├── Task 16: Extend AgentOrchestrator for Worker role (depends: 15) [unspecified-high]
├── Task 17: CLI /swarm command parser (depends: 15) [quick]
├── Task 18: CLI /swarm status renderer (depends: 17) [visual-engineering]
└── Task 19: CLI /swarm cancel implementation (depends: 17) [quick]

Wave 4 (After Wave 3 — Testing):
├── Task 20: Unit tests for ContractManager (depends: 8) [deep]
├── Task 21: Unit tests for FileSwarmTransport (depends: 9) [deep]
├── Task 22: Unit tests for ScopedFileTool (depends: 10) [deep]
├── Task 23: Unit tests for WorkerManager (depends: 14) [deep]
├── Task 24: Unit tests for SwarmOrchestrator (depends: 15) [deep]
├── Task 25: Integration test: Contract-first workflow (depends: 20-24) [deep]
├── Task 26: Integration test: Scope isolation enforcement (depends: 22) [deep]
└── Task 27: CLI manual QA tests (depends: 17-19) [unspecified-high]

Wave FINAL (After ALL tasks — independent review, 4 parallel):
├── Task F1: Plan compliance audit (oracle)
├── Task F2: Code quality review (unspecified-high)
├── Task F3: Real manual QA (unspecified-high)
└── Task F4: Scope fidelity check (deep)

Critical Path: Task 1-3 → Task 8-9 → Task 14-15 → Task 25-26
Parallel Speedup: ~75% faster than sequential
Max Concurrent: 7 (Waves 1 & 4)
```

### Dependency Matrix (abbreviated)

| Task | Depends On | Blocks | Wave |
|------|------------|--------|------|
| 1-3 | — | 8, 12, 15 | 1 |
| 4-7 | — | 8, 11, 15 | 1 |
| 8 | 2, 4 | 14, 15 | 2 |
| 9 | 3, 6 | 14, 15 | 2 |
| 10 | 3, 5 | 22, 26 | 2 |
| 15 | 1, 8, 9 | 16, 17 | 3 |
| 25 | 20-24 | F1-F4 | 4 |

### Agent Dispatch Summary

| Wave | # Parallel | Tasks → Agent Category |
|------|------------|----------------------|
| 1 | **7** | T1-T7 → `quick` |
| 2 | **6** | T8 → `deep`, T9 → `unspecified-high`, T10 → `deep`, T11 → `unspecified-high`, T12 → `quick`, T13 → `quick` |
| 3 | **6** | T14 → `deep`, T15 → `deep`, T16 → `unspecified-high`, T17 → `quick`, T18 → `visual-engineering`, T19 → `quick` |
| 4 | **8** | T20-T26 → `deep`, T27 → `unspecified-high` |
| FINAL | **4** | F1 → `oracle`, F2 → `unspecified-high`, F3 → `unspecified-high`, F4 → `deep` |

---

## TODOs

> Implementation + Test = ONE Task. Never separate.
> EVERY task MUST have: Recommended Agent Profile + Parallelization info + QA Scenarios.

### Wave 1: Abstractions + Models

- [ ] 1. **ISwarmOrchestrator + IWorkerManager Interfaces**

  **What to do**:
  - Create `Source/PuddingCode/Abstractions/ISwarmOrchestrator.cs`
  - Define `ISwarmOrchestrator` with `ProcessSwarmAsync()` method
  - Create `Source/PuddingCode/Abstractions/IWorkerManager.cs`
  - Define `SpawnWorkerAsync()`, `DismissWorkerAsync()`, `GetActiveWorkers()`
  - Add XML documentation from design doc §10.2

  **Must NOT do**:
  - Do NOT implement the interfaces (separate task)
  - Do NOT add methods not in design spec

  **Recommended Agent Profile**:
  - **Category**: `quick`
  - **Skills**: None needed
  - **Reason**: Interface definition is straightforward from spec

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 1 (with Tasks 2-7)
  - **Blocks**: Tasks 8, 12, 15
  - **Blocked By**: None

  **References**:
  - `task04-swarm.md:§10.2` - Interface definitions
  - `Source/PuddingCode/Abstractions/IAgentOrchestrator.cs` - Existing pattern

  **Acceptance Criteria**:
  - [ ] Interfaces compile without errors
  - [ ] XML docs present for all members
  - [ ] Methods match design spec exactly

  **Commit**: YES
  - Message: `feat(abstractions): add ISwarmOrchestrator and IWorkerManager interfaces`
  - Files: `Source/PuddingCode/Abstractions/ISwarmOrchestrator.cs`, `Source/PuddingCode/Abstractions/IWorkerManager.cs`

  **QA Scenarios**:

  ```
  Scenario: Interfaces compile successfully
    Tool: Bash
    Steps:
      1. Run: dotnet build Source/PuddingCode/PuddingCode.csproj
      2. Verify exit code is 0
      3. Verify no warnings about unused interfaces
    Expected Result: Build succeeds with 0 errors, 0 warnings
    Evidence: .sisyphus/evidence/task-01-build.log
  ```

- [ ] 2. **IContractManager + ILeaderElection Interfaces**

  **What to do**:
  - Create `Source/PuddingCode/Abstractions/IContractManager.cs`
  - Define `DefineContractAsync()`, `ValidateContractAsync()`
  - Create `Source/PuddingCode/Abstractions/ILeaderElection.cs`
  - Define `ElectLeaderAsync()`, `IsCurrentLeaderAliveAsync()`
  - Mark ILeaderElection methods as "Phase 3 stub - throw NotImplementedException"

  **References**: `task04-swarm.md:§10.2`

  **Acceptance Criteria**: Interfaces defined per spec, Phase 3 methods marked as stubs

  **Commit**: YES (groups with 1)

- [ ] 3. **ISwarmTransport + WorkerScope Interfaces**

  **What to do**:
  - Create `Source/PuddingCode/Abstractions/ISwarmTransport.cs`
  - Define `SendAsync()`, `BroadcastAsync()`, `ReceiveAsync()`
  - Create `Source/PuddingCode/Abstractions/WorkerScope.cs` (record type)
  - Define `AllowedPaths`, `AllowedSymbols` properties

  **References**: `task04-swarm.md:§10.2`, `§5.2`

  **Acceptance Criteria**: Interfaces match design spec

  **Commit**: YES (groups with 1)

- [ ] 4. **Contract + SwarmTask Models**

  **What to do**:
  - Create `Source/PuddingCode/Models/Contract.cs`
  - Implement `Contract` record: `Id`, `Files`, `Symbols`, `Specification`
  - Create `Source/PuddingCode/Models/SwarmTask.cs`
  - Implement `SwarmTask` class with `SwarmTaskStatus` enum
  - Add XML documentation

  **References**: `task04-swarm.md:§3.3`, `§9.4`

  **Acceptance Criteria**: Models compile, enums defined correctly

  **Commit**: YES

- [ ] 5. **WorkerInfo + WorkerRole + WorkerScope Models**

  **What to do**:
  - Create `Source/PuddingCode/Models/WorkerRole.cs` (enum)
  - Create `Source/PuddingCode/Models/WorkerInfo.cs` (record)
  - Implement `WorkerScope` record (from interface task 3)

  **References**: `task04-swarm.md:§10.2`, `§5.2`

  **Acceptance Criteria**: All models compile

  **Commit**: YES (groups with 4)

- [ ] 6. **SwarmEvents (10 Event Types)**

  **What to do**:
  - Create `Source/PuddingCode/Models/SwarmEvents.cs`
  - Implement 10 event types from §10.3:
    - `SwarmStartedEvent`, `ContractDefinedEvent`, `WorkerSpawnedEvent`
    - `TaskAssignedEvent`, `TaskCompletedEvent`, `TaskFailedEvent`
    - `ContractValidatedEvent`, `MergeEvent`, `LeaderElectedEvent`, `SwarmCompletedEvent`
  - All must inherit from `AgentEvent`

  **References**: `task04-swarm.md:§10.3`

  **Acceptance Criteria**: All 10 event types defined, inherit from AgentEvent

  **Commit**: YES

- [ ] 7. **SwarmNode Model (Phase 3 Stub)**

  **What to do**:
  - Create `Source/PuddingCode/Models/SwarmNode.cs`
  - Implement `SwarmNode` record: `NodeId`, `Role`, `Addresses`, `Capabilities`
  - Implement `SwarmNodeRole` enum
  - Add comment: "Phase 3 feature - stub for P2P distributed swarm"

  **References**: `task04-swarm.md:§7.3`

  **Acceptance Criteria**: Model defined, marked as Phase 3 stub

  **Commit**: YES (groups with 4)

---

### Wave 2: Core Components

- [ ] 8. **ContractManager Implementation**

  **What to do**:
  - Create `Source/PuddingCode/Swarm/ContractManager.cs`
  - Implement `IContractManager` interface
  - Implement `DefineContractAsync()`: parse specification, extract files/symbols, create Contract object
  - Implement `ValidateContractAsync()`: verify Worker implementation matches contract signatures
  - Save contracts to `.pudding/swarm/contracts/contract-{id}.json`
  - Use reflection or Roslyn for signature validation

  **Must NOT do**:
  - Do NOT implement full Roslyn analysis (use simple reflection for Phase 1/2)
  - Do NOT add P2P features

  **Recommended Agent Profile**:
  - **Category**: `deep`
  - **Skills**: None needed
  - **Reason**: Complex logic but straightforward from spec

  **Parallelization**:
  - **Can Run In Parallel**: YES
  - **Parallel Group**: Wave 2 (with Tasks 9-13)
  - **Blocks**: Tasks 14, 15
  - **Blocked By**: Tasks 2, 4

  **References**:
  - `task04-swarm.md:§3` - Contract-first workflow
  - `Source/PuddingCode/Models/Contract.cs` - Contract model
  - `Source/PuddingCode/Abstractions/IContractManager.cs` - Interface to implement

  **Acceptance Criteria**:
  - [ ] `DefineContractAsync()` creates Contract with correct files/symbols
  - [ ] `ValidateContractAsync()` verifies method signatures match
  - [ ] Contracts persisted to `.pudding/swarm/contracts/`
  - [ ] Unit tests pass

  **Commit**: YES

  **QA Scenarios**:

  ```
  Scenario: Define contract from specification
    Tool: Bash (dotnet test)
    Preconditions: Test project setup
    Steps:
      1. Create unit test: Create ContractManager
      2. Call DefineContractAsync with sample spec
      3. Assert Contract.Files contains expected paths
      4. Assert Contract.Symbols contains expected method names
    Expected Result: Contract created correctly, saved to disk
    Failure Indicators: Contract null, files/symbols missing, file not saved
    Evidence: .sisyphus/evidence/task-08-contract-define.trx

  Scenario: Validate contract implementation
    Tool: Bash (dotnet test)
    Steps:
      1. Create contract with interface
      2. Create implementation class
      3. Call ValidateContractAsync
      4. Assert validation passes
    Expected Result: Validation returns true for matching signatures
    Evidence: .sisyphus/evidence/task-08-contract-validate.trx
  ```

- [ ] 9. **FileSwarmTransport Implementation**

  **What to do**:
  - Create `Source/PuddingCode/Swarm/FileSwarmTransport.cs`
  - Implement `ISwarmTransport` interface
  - Implement `SendAsync()`: write message to `.pudding/swarm/messages/{target}.inbox.json`
  - Implement `BroadcastAsync()`: write to `broadcast.json`
  - Implement `ReceiveAsync()`: poll inbox files, yield messages
  - Use file system watchers for real-time delivery (optional)

  **References**: `task04-swarm.md:§9.2`, `§9.4`

  **Acceptance Criteria**: Messages sent/received via file system, no data loss

  **Commit**: YES

  **QA Scenarios**:

  ```
  Scenario: Send and receive message
    Tool: Bash (dotnet test)
    Steps:
      1. Create FileSwarmTransport
      2. Send message from "leader" to "worker-1"
      3. Verify file created in .pudding/swarm/messages/worker-1.inbox.json
      4. Receive message as "worker-1"
      5. Assert message content matches
    Expected Result: Message delivered successfully
    Evidence: .sisyphus/evidence/task-09-message-delivery.trx
  ```

- [ ] 10. **ScopedFileTool Implementation**

  **What to do**:
  - Create `Source/PuddingCode/Swarm/ScopedFileTool.cs`
  - Wrap existing `FileTool` class
  - Add `WorkerScope` parameter
  - Override `ExecuteAsync()`: check if path in AllowedPaths before calling inner FileTool
  - Return error message if path outside scope
  - Support both file paths and symbol-based scope

  **References**: `task04-swarm.md:§5.2`, `§10.2`

  **Acceptance Criteria**: File operations outside scope rejected with clear error

  **Commit**: YES

  **QA Scenarios**:

  ```
  Scenario: Allow file write within scope
    Tool: Bash (dotnet test)
    Steps:
      1. Create ScopedFileTool with scope: ["src/Auth/*"]
      2. Attempt write to "src/Auth/Login.cs"
      3. Assert operation succeeds
    Expected Result: Write succeeds within scope
    Evidence: .sisyphus/evidence/task-10-scope-allow.trx

  Scenario: Block file write outside scope
    Tool: Bash (dotnet test)
    Steps:
      1. Create ScopedFileTool with scope: ["src/Auth/*"]
      2. Attempt write to "src/Api/Controller.cs"
      3. Assert operation fails with scope error message
    Expected Result: Write rejected with "outside scope" error
    Evidence: .sisyphus/evidence/task-10-scope-block.trx
  ```

- [ ] 11. **ContractValidator Implementation**

  **What to do**:
  - Create `Source/PuddingCode/Swarm/ContractValidator.cs`
  - Implement signature validation logic
  - Compare Contract.Symbols against actual implementation
  - Use reflection to check method signatures
  - Return validation result with detailed error messages

  **References**: `task04-swarm.md:§3.4`, `§4.2`

  **Acceptance Criteria**: Validates method signatures, reports mismatches

  **Commit**: YES (groups with 8)

- [ ] 12. **WorkerManager Skeleton**

  **What to do**:
  - Create `Source/PuddingCode/Swarm/WorkerManager.cs` (skeleton)
  - Implement `IWorkerManager` interface stubs
  - `SpawnWorkerAsync()`: throw NotImplementedException with "Phase 2" comment
  - `DismissWorkerAsync()`: throw NotImplementedException
  - `GetActiveWorkers()`: return empty list
  - Add TODO comments for Phase 2 implementation

  **References**: `task04-swarm.md:§10.2`

  **Acceptance Criteria**: Interface implemented with stubs, compiles

  **Commit**: YES (groups with 8)

- [ ] 13. **Create .pudding/swarm/ Directory Structure**

  **What to do**:
  - Create runtime directory structure on first swarm启动
  - `.pudding/swarm/config.json` - swarm metadata
  - `.pudding/swarm/contracts/` - contract files
  - `.pudding/swarm/tasks/` - task files
  - `.pudding/swarm/messages/` - message inboxes
  - `.pudding/swarm/worktrees/` - worktree registry
  - Implement in SwarmOrchestrator initialization

  **References**: `task04-swarm.md:§9.1`

  **Acceptance Criteria**: Directory structure created automatically

  **Commit**: YES (groups with 8)

---

### Wave 3: Orchestrator + Integration

- [ ] 14. **WorkerManager Full Implementation**

  **What to do**:
  - Complete `WorkerManager.cs` from Task 12 skeleton
  - Implement `SpawnWorkerAsync()`: create AgentOrchestrator instance, assign role, create worktree
  - Implement `DismissWorkerAsync()`: cleanup worktree, release resources
  - Implement Git worktree creation: `git worktree add .pudding/worktrees/{worker-id} swarm/{task}`
  - Track active workers in memory

  **Must NOT do**:
  - Do NOT implement P2P worker spawning (Phase 3)

  **Recommended Agent Profile**:
  - **Category**: `deep`
  - **Skills**: `git-master`
  - **Reason**: Complex orchestration logic + Git integration

  **Parallelization**:
  - **Can Run In Parallel**: NO (depends on Task 12)
  - **Parallel Group**: Sequential after Task 12
  - **Blocks**: Task 15
  - **Blocked By**: Tasks 12, 8, 9

  **References**:
  - `task04-swarm.md:§6` - Git Worktree mechanism
  - `Source/PuddingCode/Core/GitSnapshotService.cs` - Existing Git integration

  **Acceptance Criteria**:
  - [ ] Workers spawned with unique IDs
  - [ ] Git worktree created for each worker
  - [ ] Workers tracked and can be dismissed
  - [ ] Unit tests pass

  **Commit**: YES

  **QA Scenarios**:

  ```
  Scenario: Spawn worker with worktree
    Tool: Bash (dotnet test + git)
    Steps:
      1. Create WorkerManager
      2. Spawn worker with role=Builder, task="Implement AuthService"
      3. Verify .pudding/worktrees/worker-{id} created
      4. Verify worktree on correct branch
    Expected Result: Worker spawned, worktree created
    Evidence: .sisyphus/evidence/task-14-spawn-worker.trx

  Scenario: Dismiss worker and cleanup
    Tool: Bash (dotnet test + git)
    Steps:
      1. Spawn worker
      2. Dismiss worker
      3. Verify worktree removed
      4. Verify worker not in active list
    Expected Result: Worker dismissed, worktree cleaned up
    Evidence: .sisyphus/evidence/task-14-dismiss-worker.trx
  ```

- [ ] 15. **SwarmOrchestrator Implementation**

  **What to do**:
  - Create `Source/PuddingCode/Swarm/SwarmOrchestrator.cs`
  - Implement `ISwarmOrchestrator` interface
  - Main orchestration loop:
    1. Analyze user input
    2. Spawn Leader Agent
    3. Leader defines contracts
    4. Leader spawns Workers
    5. Monitor progress (parallel with Workers)
    6. Validate contracts
    7. Merge worktrees
    8. Run final tests
    9. Dismiss swarm
  - Emit AgentEvent stream for UI rendering

  **Must NOT do**:
  - Do NOT implement P2P coordination (Phase 3)
  - Do NOT implement Leader election (Phase 3)

  **Recommended Agent Profile**:
  - **Category**: `deep`
  - **Skills**: None needed
  - **Reason**: Core orchestration logic, complex but well-specified

  **Parallelization**:
  - **Can Run In Parallel**: NO (depends on all core components)
  - **Parallel Group**: Sequential after Task 14
  - **Blocks**: Tasks 16, 17
  - **Blocked By**: Tasks 1, 8, 9, 14

  **References**:
  - `task04-swarm.md:§2` - Core architecture
  - `task04-swarm.md:§4` - Orchestration protocol
  - `Source/PuddingCode/Core/AgentOrchestrator.cs` - Existing orchestrator pattern

  **Acceptance Criteria**:
  - [ ] Full swarm workflow implemented
  - [ ] Events emitted for each step
  - [ ] Contract validation integrated
  - [ ] Integration tests pass

  **Commit**: YES

  **QA Scenarios**:

  ```
  Scenario: Full swarm workflow (serial)
    Tool: Bash (dotnet test)
    Steps:
      1. Create SwarmOrchestrator
      2. ProcessSwarmAsync with task "Create auth service"
      3. Verify events: SwarmStarted → ContractDefined → WorkerSpawned → TaskCompleted → SwarmCompleted
      4. Verify contract created
      5. Verify worker implementation exists
    Expected Result: Full workflow completes successfully
    Evidence: .sisyphus/evidence/task-15-full-workflow.trx
  ```

- [ ] 16. **Extend AgentOrchestrator for Worker Role**

  **What to do**:
  - Modify `Source/PuddingCode/Core/AgentOrchestrator.cs`
  - Add `AgentRole` property (Leader/Builder/QA/Docs)
  - Inject role-specific System Prompt
  - Filter skills based on role (use SkillRegistry if available)
  - Support scope injection for Worker role

  **Must NOT do**:
  - Do NOT break existing API (backward compatible)

  **References**: `task04-swarm.md:§5.1`

  **Acceptance Criteria**: AgentOrchestrator supports roles, backward compatible

  **Commit**: YES

- [ ] 17. **CLI /swarm Command Parser**

  **What to do**:
  - Create `Source/PuddingCodeCLI/Commands/SwarmCommands.cs`
  - Parse `/swarm`, `/swarm status`, `/swarm cancel`
  - Wire to SwarmOrchestrator
  - Handle errors gracefully

  **References**: `task04-swarm.md:§10.4`

  **Acceptance Criteria**: Commands parse and route correctly

  **Commit**: YES

- [ ] 18. **CLI /swarm Status Renderer**

  **What to do**:
  - Implement status display
  - Show worker list, task progress, contract completion
  - Use Spectre.Console for formatting (tables, progress bars)

  **References**: `task04-swarm.md:§10.4`

  **Acceptance Criteria**: Status displayed with worker/task info

  **Commit**: YES (groups with 17)

- [ ] 19. **CLI /swarm Cancel Implementation**

  **What to do**:
  - Implement cancel logic
  - Stop active workers
  - Cleanup worktrees
  - Rollback unmerged changes

  **References**: `task04-swarm.md:§10.4`

  **Acceptance Criteria**: Swarm cancelled, resources cleaned up

  **Commit**: YES (groups with 17)

---

### Wave 4: Testing

- [ ] 20. **Unit Tests for ContractManager**

  **What to do**:
  - Create `Source/PuddingCodeTests/Swarm/ContractManagerTests.cs`
  - Test: DefineContract creates correct structure
  - Test: ValidateContract validates signatures
  - Test: Contract persistence
  - Follow TDD: write test first, then implement

  **Acceptance Criteria**: All tests pass

  **Commit**: YES (groups with 8)

- [ ] 21. **Unit Tests for FileSwarmTransport**

  **What to do**:
  - Create `Source/PuddingCodeTests/Swarm/FileSwarmTransportTests.cs`
  - Test: Send/receive messages
  - Test: Broadcast messages
  - Test: Message persistence

  **Acceptance Criteria**: All tests pass

  **Commit**: YES (groups with 9)

- [ ] 22. **Unit Tests for ScopedFileTool**

  **What to do**:
  - Create `Source/PuddingCodeTests/Swarm/ScopedFileToolTests.cs`
  - Test: Allow operations within scope
  - Test: Block operations outside scope
  - Test: Error messages clear

  **Acceptance Criteria**: All tests pass

  **Commit**: YES (groups with 10)

- [ ] 23. **Unit Tests for WorkerManager**

  **What to do**:
  - Create `Source/PuddingCodeTests/Swarm/WorkerManagerTests.cs`
  - Test: Spawn worker
  - Test: Dismiss worker
  - Test: Worktree creation/cleanup

  **Acceptance Criteria**: All tests pass

  **Commit**: YES (groups with 14)

- [ ] 24. **Unit Tests for SwarmOrchestrator**

  **What to do**:
  - Create `Source/PuddingCodeTests/Swarm/SwarmOrchestratorTests.cs`
  - Test: Full workflow
  - Test: Event emission
  - Test: Error handling

  **Acceptance Criteria**: All tests pass

  **Commit**: YES (groups with 15)

- [ ] 25. **Integration Test: Contract-First Workflow**

  **What to do**:
  - Create `Source/PuddingCodeTests/Swarm/Integration/ContractFirstWorkflowTests.cs`
  - End-to-end test: Leader defines contract → Worker implements → Validation passes
  - Use real file system, real Git

  **Acceptance Criteria**: Full workflow passes

  **Commit**: YES

  **QA Scenarios**:

  ```
  Scenario: End-to-end contract workflow
    Tool: Bash (dotnet test)
    Steps:
      1. Initialize test project
      2. Create swarm
      3. Leader defines contract for IAuthService
      4. Worker implements AuthService
      5. Validate implementation matches contract
      6. Assert all files created correctly
    Expected Result: Full workflow completes, all assertions pass
    Failure Indicators: Contract mismatch, files missing, validation fails
    Evidence: .sisyphus/evidence/task-25-e2e-workflow.trx
  ```

- [ ] 26. **Integration Test: Scope Isolation Enforcement**

  **What to do**:
  - Create `Source/PuddingCodeTests/Swarm/Integration/ScopeIsolationTests.cs`
  - Test: Worker blocked from modifying files outside scope
  - Test: Multiple workers with non-overlapping scopes
  - Test: Attempted scope violations logged

  **Acceptance Criteria**: Scope isolation enforced

  **Commit**: YES

  **QA Scenarios**:

  ```
  Scenario: Block worker from outside-scope file
    Tool: Bash (dotnet test)
    Steps:
      1. Create worker with scope: ["src/Auth/*"]
      2. Attempt write to "src/Api/Controller.cs"
      3. Assert operation blocked
      4. Verify error logged
    Expected Result: Write blocked, error logged
    Evidence: .sisyphus/evidence/task-26-scope-enforcement.trx
  ```

- [ ] 27. **CLI Manual QA Tests**

  **What to do**:
  - Test all CLI commands manually via tmux
  - `/swarm` - triggers swarm
  - `/swarm status` - displays status
  - `/swarm cancel` - cancels swarm
  - Capture terminal output as evidence

  **Acceptance Criteria**: All commands work as expected

  **Commit**: YES

  **QA Scenarios**:

  ```
  Scenario: CLI /swarm command
    Tool: interactive_bash (tmux)
    Steps:
      1. Launch CLI: dotnet run --project Source/PuddingCodeCLI
      2. Send: /swarm Create authentication service
      3. Observe swarm events in terminal
      4. Wait for completion
      5. Send: /exit
    Expected Result: Swarm starts, events displayed, completes successfully
    Evidence: .sisyphus/evidence/task-27-cli-swarm.txt

  Scenario: CLI /swarm status command
    Tool: interactive_bash (tmux)
    Steps:
      1. Launch CLI with active swarm
      2. Send: /swarm status
      3. Verify table shows workers and progress
    Expected Result: Status table displayed with worker info
    Evidence: .sisyphus/evidence/task-27-cli-status.txt
  ```

---

## Final Verification Wave

> 4 review agents run in PARALLEL. ALL must APPROVE. Rejection → fix → re-run.

- [ ] F1. **Plan Compliance Audit** — `oracle`
  Read the plan end-to-end. For each "Must Have": verify implementation exists. For each "Must NOT Have": search codebase for forbidden patterns. Check evidence files exist in .sisyphus/evidence/. Compare deliverables against plan.
  Output: `Must Have [N/N] | Must NOT Have [N/N] | Tasks [N/N] | VERDICT: APPROVE/REJECT`

- [ ] F2. **Code Quality Review** — `unspecified-high`
  Run `tsc --noEmit` + linter + `dotnet test`. Review all changed files for: `as any`/`@ts-ignore`, empty catches, console.log in prod, commented-out code, unused imports. Check AI slop: excessive comments, over-abstraction, generic names (data/result/item/temp).
  Output: `Build [PASS/FAIL] | Lint [PASS/FAIL] | Tests [N pass/N fail] | Files [N clean/N issues] | VERDICT`

- [ ] F3. **Real Manual QA** — `unspecified-high` (+ `playwright` skill if UI)
  Start from clean state. Execute EVERY QA scenario from EVERY task — follow exact steps, capture evidence. Test cross-task integration (features working together, not isolation). Test edge cases: empty state, invalid input, rapid actions. Save to `.sisyphus/evidence/final-qa/`.
  Output: `Scenarios [N/N pass] | Integration [N/N] | Edge Cases [N tested] | VERDICT`

- [ ] F4. **Scope Fidelity Check** — `deep`
  For each task: read "What to do", read actual diff (git log/diff). Verify 1:1 — everything in spec was built (no missing), nothing beyond spec was built (no creep). Check "Must NOT do" compliance. Detect cross-task contamination: Task N touching Task M's files. Flag unaccounted changes.
  Output: `Tasks [N/N compliant] | Contamination [CLEAN/N issues] | Unaccounted [CLEAN/N files] | VERDICT`

---

## Commit Strategy

| After Task | Message | Files | Verification |
|------------|---------|-------|--------------|
| 1-3 | `feat(abstractions): add swarm orchestrator interfaces` | `ISwarmOrchestrator.cs`, `IWorkerManager.cs`, `IContractManager.cs`, `ILeaderElection.cs`, `ISwarmTransport.cs` | `dotnet build` |
| 4-7 | `feat(models): add swarm mode model classes` | `Contract.cs`, `SwarmTask.cs`, `WorkerInfo.cs`, `WorkerRole.cs`, `SwarmEvents.cs`, `SwarmNode.cs` | `dotnet build` |
| 8-13 | `feat(core): implement swarm core components` | `ContractManager.cs`, `FileSwarmTransport.cs`, `ScopedFileTool.cs`, etc. | `dotnet test --filter Category=Unit` |
| 14-19 | `feat(swarm): implement orchestrator and CLI` | `SwarmOrchestrator.cs`, `WorkerManager.cs`, CLI updates | `dotnet test` |
| 20-27 | `test(swarm): add comprehensive test suite` | Test files in `PuddingCodeTests/Swarm/` | `dotnet test --no-build` |
| F1-F4 | `chore: final verification passed` | — | All verification passed |

---

## Success Criteria

### Verification Commands

```bash
# Build verification
dotnet build Source/PuddingCode/PuddingCode.csproj  # Expected: 0 errors, 0 warnings

# Unit tests
dotnet test Source/PuddingCodeTests/PuddingCodeTests.csproj --filter Category=Unit  # Expected: All pass

# Integration tests
dotnet test Source/PuddingCodeTests/PuddingCodeTests.csproj --filter Category=Integration  # Expected: All pass

# CLI smoke test
dotnet run --project Source/PuddingCodeCLI/PuddingCodeCLI.csproj -- /swarm --help  # Expected: Help output
```

### Final Checklist

- [ ] All "Must Have" present (Contract-first, Scope Isolation, File Messaging, Git Worktree, Role Permissions)
- [ ] All "Must NOT Have" absent (No P2P, No Leader Election, No Model Router)
- [ ] All tests pass: `dotnet test` returns 0 failures
- [ ] CLI commands functional: `/swarm`, `/swarm status`, `/swarm cancel`
- [ ] No breaking changes to existing API
- [ ] Evidence files captured for all QA scenarios
- [ ] All 4 final verification tasks APPROVE

---

## Appendix: File Structure

```
Source/PuddingCode/
├── Abstractions/
│   ├── ISwarmOrchestrator.cs          (NEW - Task 1)
│   ├── IWorkerManager.cs              (NEW - Task 1)
│   ├── IContractManager.cs            (NEW - Task 2)
│   ├── ILeaderElection.cs             (NEW - Task 2)
│   ├── ISwarmTransport.cs             (NEW - Task 3)
│   └── WorkerScope.cs                 (NEW - Task 3)
├── Models/
│   ├── Contract.cs                    (NEW - Task 4)
│   ├── SwarmTask.cs                   (NEW - Task 4)
│   ├── WorkerInfo.cs                  (NEW - Task 5)
│   ├── WorkerRole.cs                  (NEW - Task 5)
│   ├── SwarmEvents.cs                 (NEW - Task 6)
│   └── SwarmNode.cs                   (NEW - Task 7)
├── Swarm/
│   ├── ContractManager.cs             (NEW - Task 8)
│   ├── ContractValidator.cs           (NEW - Task 11)
│   ├── FileSwarmTransport.cs          (NEW - Task 9)
│   ├── ScopedFileTool.cs              (NEW - Task 10)
│   ├── WorkerManager.cs               (NEW - Task 12/14)
│   └── SwarmOrchestrator.cs           (NEW - Task 15)
├── Core/
│   └── AgentOrchestrator.cs           (MODIFIED - Task 16)
└── Tools/
    └── FileTool.cs                    (UNCHANGED - wrapped by ScopedFileTool)

Source/PuddingCodeCLI/
├── Program.cs                         (MODIFIED - Task 17-19)
└── Commands/
    └── SwarmCommands.cs               (NEW - Task 17-19)

Source/PuddingCodeTests/
├── Swarm/
│   ├── ContractManagerTests.cs        (NEW - Task 20)
│   ├── FileSwarmTransportTests.cs     (NEW - Task 21)
│   ├── ScopedFileToolTests.cs         (NEW - Task 22)
│   ├── WorkerManagerTests.cs          (NEW - Task 23)
│   ├── SwarmOrchestratorTests.cs      (NEW - Task 24)
│   └── Integration/
│       ├── ContractFirstWorkflowTests.cs (NEW - Task 25)
│       └── ScopeIsolationTests.cs        (NEW - Task 26)

.pudding/swarm/                        (Runtime directory - Task 13)
├── config.json
├── contracts/
├── tasks/
├── messages/
└── worktrees/
```

---

## Appendix: Design Document References

| Section | Content | File |
|---------|---------|------|
| §2 | Core Architecture: Leader-Worker Model | task04-swarm.md |
| §3 | Contract-First Workflow | task04-swarm.md |
| §4 | Swarm Orchestration Protocol | task04-swarm.md |
| §5 | Worker Roles + Scope Isolation | task04-swarm.md |
| §6 | Git Worktree Mechanism | task04-swarm.md |
| §9 | Task Board + Communication | task04-swarm.md |
| §10 | Integration with Existing Architecture | task04-swarm.md |
| §12 | Implementation Roadmap | task04-swarm.md |

---

*Plan generated: 2026-02-20*
*Next Action: Run `/start-work` to begin execution*
