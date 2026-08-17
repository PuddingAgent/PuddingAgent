# Pudding Agent

<p align="center">
  <img src="me.png" alt="Pudding" width="200"/>
</p>

<p align="center">
  <strong>A local-first Windows desktop AI assistant and Agent IDE.</strong><br/>
  <sub><a href="README_zh-CN.md">中文 README</a></sub>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/version-v0.1.0-blue" alt="Version"/>
  <img src="https://img.shields.io/badge/platform-Windows%20First-0078D4" alt="Windows First"/>
  <img src="https://img.shields.io/badge/runtime-.NET%2010-512BD4" alt=".NET 10"/>
  <img src="https://img.shields.io/badge/license-Apache%202.0-green" alt="License"/>
</p>

Pudding is a personal Agent system that combines conversation, tools, delegated agents, durable orchestration, local memory, background learning, and desktop integration. The product entry point is `PuddingDesktop.exe`: a WPF shell supervises a separate Core service and communicates with it through authenticated loopback HTTP and WebSocket bridges.

Pudding is still under active development. The architecture below is the direction of travel, not a claim that every extension point is already complete.

## What makes Pudding different

- **Windows-first, local-first** — desktop lifecycle, local data, tray operation, runtime recovery, and browser/IDE workflows are product concerns rather than wrappers around a chat page.
- **Durable work, not only conversation** — tasks, child runs, orchestration runs, approvals, artifacts, retries, and recovery are explicit persisted facts.
- **Six-layer memory and learning** — context, memory books, skills, goals, extraction, Auto-Dream, and guarded skill evolution form a long-lived learning system.
- **Delegation as a first-class operation** — agents can invoke specialized child agents with explicit identity, routing, capabilities, budgets, and traceable results.
- **A visual control plane** — chat explains the narrative, orchestration graphs explain causality, inspectors explain detail, and timelines provide evidence.

## Architecture principles

Our target is summarized by three deliberately constrained statements:

1. **Everything that is a business capability is contributed by a plugin.** A plugin may contribute tools, Agent functions, hooks, event consumers, projections, schedulers, policies, UI presentations, or configuration schemas. This does not mean every DTO or internal helper becomes a separately deployed plugin.
2. **Every critical operation exposes typed hooks.** Hooks are deterministic interception seams around an operation. They are not a synonym for all notifications, and a post-commit fact is an event rather than a hook.
3. **Every committed state transition produces an event.** Events make state changes observable, replayable, and auditable. Queries and direct capability calls remain typed functions; they are not forced through an asynchronous event bus.

Five contracts keep those ideas precise:

| Contract | Meaning | Typical use |
|:---|:---|:---|
| **Command** | A request to change state | Start a run, approve a step, cancel a task |
| **Function / Capability** | A typed invocation that returns a result | Invoke an Agent, tool, graph, model, or artifact transform |
| **Hook / Interceptor** | A bounded extension point inside an operation | Guard, transform, wrap, or observe execution |
| **Event** | An immutable fact after a state change | Run completed, task blocked, tool result committed |
| **Projection** | A rebuildable read model derived from events | Chat status, Admin lists, graph overlays, audit timelines |

The default rule is therefore:

> Commands express intent. Functions do work. Hooks govern work. Events record facts. Projections explain facts.

## Agent as a finite state and effect loop

An Agent is not a recursive chat handler. It is a finite-state transition loop whose model calls, tool calls, child-Agent calls, waits, and messages are explicit effects:

```text
Transition(State, Event, ContextSnapshot)
    -> NewState + Effects + DomainEvents

EffectHost(Effect)
    -> EffectSucceeded | EffectFailed | EffectDeferred
```

The transition core should be deterministic and side-effect free. The effect host performs external work and feeds outcomes back as events. Durable inboxes, idempotency keys, leases, fencing tokens, budgets, and terminal-state monotonicity allow the loop to pause, resume, retry, settle, and recover without relying on model goodwill or heartbeat timing.

`completed` and `settled` are distinct: an Agent can finish producing an answer before all hooks, projections, supervision jobs, and delivery obligations have reached a stable terminal state.

## Agents and graphs are composable functions

Pudding's orchestration direction is to give every invokable unit a common function descriptor:

```text
AgentFunction<Input, Output>
ToolFunction<Input, Output>
GraphFunction<Input, Output>
GateFunction<Input, Output>
HumanInputFunction<Input, Output>
```

Each descriptor declares identity and version, input/output schemas, required capabilities, side-effect class, idempotency and retry semantics, timeout and cost policy, and presentation metadata. A graph node references a frozen descriptor and contract hash; typed edges map one function's output to the next function's input.

An Agent may draft a graph, call another Agent as a function, or use a child graph as a function. Generated graphs must still pass compilation, policy, capability, budget, and approval checks and become an immutable Revision before execution. Hidden recursive Agent calls are not a workflow primitive: iteration uses an explicit bounded loop, sub-orchestration, or child Run with depth, cost, lease, and fence limits.

## Plugin and Hook model

A complete plugin is more than a manifest and an assembly:

- a versioned package and dependency declaration;
- typed contributions registered into immutable scoped snapshots;
- explicit owner, lifetime, effects, permissions, and disposal;
- schema and configuration contributions that round-trip through Admin;
- backend contributions and declarative UI presentation contributions;
- health, diagnostics, compatibility, drain, upgrade, and rollback behavior.

Hook pipelines follow the familiar middleware/interceptor shape, while preserving stronger semantics:

```text
Guard -> Transform -> Around/Execute -> Post-Transform -> Commit -> Event
```

- **Guard** decisions are monotonic: a later extension cannot silently reopen a denied operation.
- **Transform** hooks produce a new typed value rather than mutating shared state.
- **Around** hooks have explicit timeout, cancellation, and failure policies.
- **Observer** hooks cannot alter the committed outcome.
- Pipeline order is deterministic and inspectable; plugin load order never becomes an accidental security policy.

## Event model

Pudding distinguishes three planes:

- **Durable domain events** — committed state facts with an outbox, schema version, consumer checkpoints, replay, dead letters, and redaction policy.
- **Live stream events** — low-latency progress such as model deltas; useful for UI but not automatically part of the permanent global event log.
- **Capability-local events** — bounded signals inside a plugin or execution scope.

State and its outbox event commit atomically. Every consumer group owns its checkpoint and retry state. Long-running LLM or tool work is scheduled from durable intent; it is never performed inside the event dispatcher transaction.

## Product and UI philosophy

Pudding learns from Pi's small composable Agent core and extension ergonomics, and from DeepSeek Harness's capability seams, typed lifecycle, generated event maps, and calm control-plane UI. These are alignment references, not skins to copy.

Pudding's own identity is a quiet Windows companion and durable local workbench:

- **Conversation is narrative; graph is causality; inspector is detail; timeline is evidence.**
- Prefer semantic design tokens, restrained motion, clear hierarchy, and progressive disclosure over dashboard chrome.
- Show **why** an Agent is waiting, blocked, deferred, sleeping, questioning, or requesting approval—not only a colored status.
- Chat, Admin, Desktop, and automation views consume the same projections instead of reconstructing state independently.
- Plugin UI starts declarative and sandboxable. Trusted code modules are exceptional, signed, capability-scoped, and unloadable.
- Accessibility, keyboard operation, reduced motion, and large-graph performance are architecture requirements.

The goal is not another DeepSeek Harness. Pudding combines plugin architecture with local memory, task and Goal supervision, peak/off-peak fences, graph orchestration, Windows lifecycle management, and a companion-like interaction model.

## Current foundation and gaps

| Area | Foundation today | Direction still required |
|:---|:---|:---|
| Tools | Registry-first discovery, validation, permission filtering, workspace sources | Unify ownership, lifecycle, diagnostics, and plugin contribution metadata |
| Plugins | Package validation and manifest discovery | Real activation host, dependency graph, scopes, grants, drain/unload, rollback |
| Hooks | Agent-loop lifecycle callbacks and internal notifications | Typed Guard/Transform/Around/Observer pipelines with explicit failure policy |
| Events | Conversation store, internal bus, durable priority queue, orchestration events | Unified event envelope, transactional outbox, per-consumer checkpoints, schema/replay tooling |
| Agent Loop | Durable sessions, tools, streaming, sub-agents, Goal and background paths | Explicit reducer/effect FSM, inbox ordering, completion/settlement contract |
| Orchestration | Immutable revisions, typed ports, runs, leases/fences, real node executors | Common function registry, bounded loops/subgraphs, Agent authoring tools, policy-aware deployment |
| Frontend | Chat, Admin, run views, graph editor, component UI registry | Shared projections, plugin presentation catalog, pipeline/event inspectors, semantic token convergence |

Near-term priorities are:

1. freeze the common Command/Function/Hook/Event/Projection vocabulary and envelopes;
2. introduce a real plugin contribution host without replacing the working tool registry;
3. extract the Agent transition reducer and effect boundary;
4. make functions the common contract for Agent, tool, and graph execution;
5. add durable outbox/checkpoints and generated event/Hook maps;
6. expose composition, policy decisions, event flow, cost, and blockers in Admin;
7. migrate existing capabilities incrementally, with conformance and replay tests at every seam.

## Product topology

```text
PuddingDesktop.exe
  WPF Shell · WebView2 Workbench · Tray · Runtime Center
        |
        | authenticated loopback HTTP / WebSocket bridge
        v
core/PuddingAgent.exe --desktop-child
  Core API · Agent Runtime · Connectors · Orchestration · Memory · SQLite
        |
        +-- Plugin Contributions
        +-- Function Registry
        +-- Hook Pipelines
        +-- Durable Events / Projections
```

Desktop remains operable when Core is unavailable so users can repair configuration and start, stop, or restart the service. Core business logic does not move into WPF. `dev-up.py` is only a source-development supervisor and is not part of the shipped product lifecycle.

## Source development

Requirements: Windows, PowerShell, .NET 10 SDK, Node.js, and Python.

```powershell
python .\dev-up.py --status
python .\dev-up.py --restart
python .\dev-up.py --frontend-only
python .\dev-up.py --down
```

Focused builds:

```powershell
dotnet build PuddingRuntime --no-restore
dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --no-restore --nologo
```

Runtime user data is stored under the configured DataRoot and must not be used as build or test output.

## Design documents

- [Plugin, Hook, Event, Agent Loop, and function-graph architecture](Docs/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md)
- [General Agent orchestration ADR](Docs/07架构/82ADR-071通用Agent编排平台完整设计方案ADR.md)
- [Orchestration backend execution plan](Docs/07架构/83通用Agent编排后端执行内核与ControlPlane施工图.md)
- [Orchestration editor and component UI plan](Docs/07架构/84通用Agent编排蓝图编辑器与组件系统施工图.md)
- [Workspace TODO, off-peak automation, questioner, and Goal mode](Docs/Features/工作区TODO与峰谷节能任务编排设计方案.md)

## License

Apache License 2.0

<p align="center">
  <em>A quiet companion in the corner: reading, thinking, learning, and leaving evidence for every step.</em><br/>
  <sub>“Leave it to me.”</sub>
</p>
