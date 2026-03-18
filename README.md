# Pudding Agent Network

**An event-driven Agent OS and governed collaboration platform for workspace-scoped multi-agent execution.**

[中文说明](README_zh-CN.md)

![Version](https://img.shields.io/badge/version-v0.1.0-blue)
![Platform](https://img.shields.io/badge/platform-windows%20%7C%20linux%20%7C%20macos-lightgrey)
![License](https://img.shields.io/badge/license-Apache%202.0-green)

---

## What Is Pudding?

Pudding Agent Network is the product and platform direction of this repository.

It is not just a coding CLI, and not merely a single-agent framework. It is being shaped as an **Agent OS** with a governed collaboration network: a platform for routing, coordinating, auditing, approving, and controlling multiple agents behind a unified control and execution model.

In one sentence:

> **Pudding is a Workspace-centered, event-driven multi-agent operating system.**

---

## Why This Exists

Pudding is designed around a few hard problems that typical assistant-style systems do not solve well:

- **Real work is workflow-shaped, not only chat-shaped.** Pudding treats `Workflow` and `TaskMap` as first-class system primitives rather than afterthought orchestration.
- **Multi-task work creates context pollution.** Pudding uses `Workspace` as a governance boundary for agents, memory, tools, events, and workflows.
- **Polling is weak coordination.** Pudding is built around an event bus so agents can be awakened by system events instead of repeatedly asking whether something happened.
- **Enterprise ingress is messy.** Channels and protocols are expected to be plug-in based rather than hard-coded into one client surface.
- **Governance cannot be bolted on later.** Approvals, audit, memory boundaries, and supervisory controls are part of the platform model from the start.

---

## Core Concepts at a Glance

- **Workspace** — the main governance boundary for memory, tools, events, workflows, channels, and agent configuration.
- **Controller** — the control authority for routing, auth, approvals, audit, policy, workflow control, and governance.
- **Runtime** — the execution authority for hot session state, agent instances, memory access, tools, and sandboxed execution.
- **Event Bus** — the collaboration backbone that connects ingress, control, runtime wakeups, workflow transitions, and system feedback.
- **Workflow / TaskMap** — first-class execution structures for orchestrated and collaborative multi-agent work.
- **Governance** — approvals, audit agents, freeze controls, and policy boundaries are native parts of the system, not optional add-ons.

---

## Architecture Snapshot

```text
PuddingPlatform
  ├─ PuddingController
  │   └─ PuddingGateway
  ├─ PuddingRuntime
  ├─ PuddingAgent
  └─ PuddingCLI / PuddingWeb / PuddingAvalonia / External Channels
```

Design baseline:

- **Workspace is the main governance boundary**
- **Runtime is the authority for hot session state**
- **Controller is the authority for routing, policy, approvals, audit, and governance**
- **Platform owns upper-layer product semantics and business surfaces**
- **Agent templates define capability and policy, but do not own hot execution state**
- **Event Bus is the backbone of ingress, wakeup, workflow, and coordination**
- **Workflow and TaskMap are first-class execution models**
- **Channels are plugins, not hard-coded branches**
- **Public or low-trust input must not directly pollute long-term memory**
- **Knowledge, storage, and graph services belong to the Workspace and are mediated by Controller**
- **Approvals are system-controlled and can be triggered from trusted clients such as Avalonia**
- **Audit agents are required governance actors, not optional assistant personas**

---

## Current Status

This repository is in transition from the earlier **PuddingCode** coding-agent prototype toward the broader **Pudding Agent Network** platform.

That means the architecture direction is already much clearer than the current implementation surface.

| Area | Status | Notes |
|---|---|---|
| Architecture docs | Active | The current source of truth for platform direction |
| CLI vertical slice | Partial | A runnable surface still exists and remains useful for iteration |
| Controller / Runtime split | In transition | Core responsibilities are defined, implementation is still catching up |
| Workflow / event-driven model | Designed | Architecture is established, implementation remains incremental |
| Platform governance surfaces | Planned + partial | Product/admin direction is defined ahead of full realization |

In practice, treat this repository as an evolving transition from a coding-agent prototype into an Agent OS with governed collaboration, workspace-owned knowledge services, and multiple control clients.

---

## V1 Focus

The first implementation target is a real vertical slice:

```text
CLI / Avalonia -> Controller API -> Workspace routing -> ServiceSession -> Runtime Agent -> real LLM reply
```

Current V1 constraints and priorities:

- Built-in support for Email as a core channel
- One Workspace can bind multiple channels
- Each ChannelBinding can declare a default Agent and allowed Agent set
- Channel integration itself must be plugin-based
- Storage can start with local files and SQLite before moving to larger deployments
- Workspace knowledge base, unified storage, and knowledge graph must be exposed through Controller-owned services
- Voice approval is a system-controlled capability, not a business-agent capability
- Each Workspace should include at least one audit agent

---

## Getting Started

### Build the solution

```bash
dotnet build PuddingAgentNetwork.slnx
```

### Run the current CLI surface

```bash
dotnet run --project Source/PuddingCLI
```

### Read the architecture first

The platform is being reshaped around the new architecture, so the most accurate source of intent and near-term scope is the docs set below.

- Start with `Docs/架构.md` for the high-level reading map
- Continue with `Docs/07架构/README.md` for module-level architecture
- Use `Docs/Tasks.md` to understand the current roadmap and work breakdown

---

## Repository Map

### Core system modules

- `Source/PuddingCore` — shared abstractions, protocols, and common models
- `Source/PuddingRuntime` — execution-plane host
- `Source/PuddingController` — control-plane host in progress
- `Source/PuddingPlatform` — platform and governance host
- `Source/PuddingAgent` — agent templates and capability profiles
- `Source/PuddingMemoryEngine` — runtime memory subsystem

### Client and operator surfaces

- `Source/PuddingCLI` — CLI surface using Controller and Gateway interfaces
- `Source/PuddingWeb` — web frontend
- `Source/PuddingAvalonia` — planned desktop operator/client surface
- `Source/PuddingGateway` — ingress and adapter boundary module

### Transitional and legacy modules

- `Source/PuddingCode` — earlier coding-agent implementation being absorbed into the broader platform direction
- `Source/PuddingCodeCLI` — legacy CLI entry point retained during transition

### Docs and planning

- `Docs/架构.md` — high-level architecture overview and reading map
- `Docs/07架构/` — module-level architecture set
- `Docs/Tasks.md` and `Docs/Tasks/` — roadmap, task board, and implementation breakdown

---

## Key Documents

- `Docs/架构.md` — architecture overview and reading map
- `Docs/07架构/README.md` — module-level architecture index
- `Docs/Tasks.md` — platform task board and priorities
- `Docs/Tasks/task24-platform-v1-first-slice.md` — class and API level breakdown for the first vertical slice

---

## License

Apache License 2.0
