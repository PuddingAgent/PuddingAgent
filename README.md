# Pudding Agent Network

**An Agent OS and collaboration network platform for multi-channel ingress, multi-agent execution, governed automation, and workspace-scoped knowledge infrastructure.**

![Version](https://img.shields.io/badge/version-v0.1.0-blue)
![Platform](https://img.shields.io/badge/platform-windows%20%7C%20linux%20%7C%20macos-lightgrey)
![License](https://img.shields.io/badge/license-MIT-green)

---

## What Is This?

Pudding Agent Network is the product and platform direction of this repository.

It is not just a coding CLI, and not merely a single-agent framework. It is closer to an Agent OS with a governed collaboration network: a platform for running, coordinating, auditing, and controlling multiple agents behind a unified platform and control model.

At a high level:

- **PuddingPlatform** is the upper platform layer for business logic, product semantics, and collaboration-network orchestration.
- **PuddingController** is the lower control plane: routing, auth, approvals, audit, workflow, and governance.
- **PuddingGateway** is a PuddingController module for ingress protocols and boundary access.
- **PuddingRuntime** is the execution plane: sessions, agent instances, memory, skills, tools, and sandboxed execution.
- **PuddingAgent** defines agent templates, policies, and capability profiles.
- **PuddingCLI**, **PuddingWeb**, and **PuddingAvalonia** are client and operator surfaces that work through Controller and Gateway interfaces.
- **PuddingCore** holds shared abstractions, protocols, and models.

---

## Product Definition

Pudding Agent Network is a Workspace-centered Agent OS that binds channels, users, agents, memory, approvals, and workflows into one governed operating model.

Its target shape is closer to an Agent OS and agent network than a single assistant:

- Multi-user, not only single-user local interaction
- Multi-channel, not only terminal input
- Multi-agent and sub-agent, not only one long-lived assistant
- Platform-governed execution, not only direct tool calling
- Audited and approval-aware operations, not only best-effort guardrails
- Workspace-scoped knowledge, storage, and graph services, not only chat history and prompt context

---

## Core Goals

- Support multiple channels such as CLI, HTTP, WebSocket, and built-in Email
- Let one Workspace host multiple Agents and bind multiple channels
- Route inbound messages by Workspace, ChannelBinding, identity, policy, and intent
- Separate control plane and execution plane so Runtime nodes can scale independently
- Support governed automation with approvals, audit trails, memory boundaries, and sandboxing
- Support Swarm, SubAgent, workflow, and event-driven coordination as first-class primitives
- Keep channel integration pluggable so future channels can be added without rewriting core routing
- Provide Workspace-owned knowledge bases, knowledge graphs, and unified storage without exposing infrastructure details to agents
- Support system-controlled approvals, including client-side voice approval, outside business-agent decision loops
- Require at least one audit agent per Workspace for supervision, freeze control, and governance escalation

---

## V1 Focus

The first implementation target is one real vertical slice:

```text
CLI / Avalonia -> Controller API -> Workspace routing -> ServiceSession -> Runtime Agent -> real LLM reply
```

V1 constraints and priorities:

- Built-in support for Email as a core channel
- One Workspace can bind multiple channels
- Each ChannelBinding can declare a default Agent and allowed Agent set
- Channel integration itself must be plugin-based
- Storage can start with local files and SQLite before moving to larger deployments
- Workspace knowledge base, unified storage, and knowledge graph must be exposed through Controller-owned services
- Voice approval is a system-controlled capability, not a business-agent capability
- Each Workspace should include at least one audit agent

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
- **Platform owns upper business logic and product semantics**
- **Controller owns lower-level policy, routing, and control**
- **Channels are plugins, not hard-coded branches**
- **Public or low-trust input must not directly pollute long-term memory**
- **Knowledge, storage, and graph services belong to the Workspace and are mediated by Controller**
- **Approvals are system-controlled and can be triggered from trusted clients such as Avalonia**
- **Audit agents are required governance actors, not optional assistant personas**

---

## Repository Status

This repository is in transition from the earlier **PuddingCode** coding-agent prototype toward the broader **Pudding Agent Network** platform.

That means:

- Some existing code still reflects the earlier CLI-first implementation
- The new Platform, Runtime, Agent, and governance layers are now the primary direction
- The architecture and task docs are currently ahead of the implementation in several areas
- Several planned components, including PuddingController and PuddingAvalonia, are defined at the architecture level before full code realization

---

## Repository Structure

```text
PuddingAgentNetwork.slnx
Docs/
  架构.md
  07架构/
  Tasks.md
  Tasks/
Source/
  PuddingCore/        shared abstractions, protocols, models
  PuddingRuntime/     execution plane host
  PuddingController/  planned control plane host
  PuddingPlatform/    platform layer and network governance host
  PuddingGateway/     transitional project, target module under PuddingController
  PuddingAgent/       agent templates and capability profiles
  PuddingCLI/         CLI client using Controller and Gateway interfaces
  PuddingAvalonia/    planned desktop client for user-side control and voice approval
  PuddingWeb/         web frontend
  PuddingCode/        legacy coding-agent implementation still being absorbed
  PuddingCodeCLI/     legacy CLI entry point kept during transition
  PuddingCoreTests/
  PuddingCLITests/
```

---

## Getting Started

Build the solution:

```bash
dotnet build PuddingAgentNetwork.slnx
```

Run the current CLI surface:

```bash
dotnet run --project Source/PuddingCLI
```

The platform is being reshaped around the new architecture, so the most accurate source of intent and near-term scope is the docs set below.

In practice, the current repository should be read as an evolving transition from a coding-agent prototype into an Agent OS with a governed collaboration network, Workspace-owned knowledge services, and multiple control clients.

---

## Key Documents

- `Docs/架构.md` - architecture overview and reading map
- `Docs/07架构/README.md` - module-level architecture index
- `Docs/Tasks.md` - platform task board and priorities
- `Docs/Tasks/task24-platform-v1-first-slice.md` - class and API level breakdown for the first vertical slice

---

## License

MIT
