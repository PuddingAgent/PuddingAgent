# Pudding Agent Network

**A workspace-scoped agent operations platform for multi-channel ingress, multi-agent execution, and governed automation.**

![Version](https://img.shields.io/badge/version-v0.1.0-blue)
![Platform](https://img.shields.io/badge/platform-windows%20%7C%20linux%20%7C%20macos-lightgrey)
![License](https://img.shields.io/badge/license-MIT-green)

---

## What Is This?

Pudding Agent Network is the product and platform direction of this repository.

It is not just a coding CLI. It is a platform for running agents behind a unified control plane, with clear workspace boundaries, pluggable channels, auditable execution, and support for multi-agent and sub-agent collaboration.

At a high level:

- **PuddingPlatform** is the control plane: channel ingress, routing, auth, approvals, audit, workflow, and governance.
- **PuddingRuntime** is the execution plane: sessions, agent instances, memory, skills, tools, and sandboxed execution.
- **PuddingAgent** defines agent templates, policies, and capability profiles.
- **PuddingCLI** and **PuddingWeb** are clients and operator surfaces.
- **PuddingCore** holds shared abstractions, protocols, and models.

---

## Product Definition

Pudding Agent Network is a Workspace-centered platform that binds channels, users, agents, memory, approvals, and workflows into one governed operating model.

Its target shape is closer to an agent network than a single assistant:

- Multi-user, not only single-user local interaction
- Multi-channel, not only terminal input
- Multi-agent and sub-agent, not only one long-lived assistant
- Platform-governed execution, not only direct tool calling
- Audited and approval-aware operations, not only best-effort guardrails

---

## Core Goals

- Support multiple channels such as CLI, HTTP, WebSocket, and built-in Email
- Let one Workspace host multiple Agents and bind multiple channels
- Route inbound messages by Workspace, ChannelBinding, identity, policy, and intent
- Separate control plane and execution plane so Runtime nodes can scale independently
- Support governed automation with approvals, audit trails, memory boundaries, and sandboxing
- Support Swarm, SubAgent, workflow, and event-driven coordination as first-class primitives
- Keep channel integration pluggable so future channels can be added without rewriting core routing

---

## V1 Focus

The first implementation target is one real vertical slice:

```text
CLI -> Platform API -> Workspace routing -> ServiceSession -> Runtime Agent -> real LLM reply
```

V1 constraints and priorities:

- Built-in support for Email as a core channel
- One Workspace can bind multiple channels
- Each ChannelBinding can declare a default Agent and allowed Agent set
- Channel integration itself must be plugin-based
- Storage can start with local files and SQLite before moving to larger deployments

---

## Architecture Snapshot

```text
PuddingCLI / PuddingWeb / External Channels
                |
                v
         PuddingPlatform
  ingress, routing, auth, approval,
  workflow, audit, governance
                |
                v
         PuddingRuntime
  session runtime, agent runtime,
  memory runtime, skill runtime,
  sandboxed execution
                |
                v
           PuddingCore
  shared abstractions, models,
  tool and skill contracts
```

Design baseline:

- **Workspace is the main governance boundary**
- **Runtime is the authority for hot session state**
- **Platform owns global policy, routing, and control**
- **Channels are plugins, not hard-coded branches**
- **Public or low-trust input must not directly pollute long-term memory**

---

## Repository Status

This repository is in transition from the earlier **PuddingCode** coding-agent prototype toward the broader **Pudding Agent Network** platform.

That means:

- Some existing code still reflects the earlier CLI-first implementation
- The new Platform, Runtime, Agent, and governance layers are now the primary direction
- The architecture and task docs are currently ahead of the implementation in several areas

---

## Repository Structure

```text
PuddingAgentNetwork.slnx
Docs/
  架构.md
  Tasks.md
  Tasks/
Source/
  PuddingCore/        shared abstractions, protocols, models
  PuddingRuntime/     execution plane host
  PuddingPlatform/    control plane host
  PuddingGateway/     platform gateway module
  PuddingAgent/       agent templates and capability profiles
  PuddingCLI/         CLI client and operator surface
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

---

## Key Documents

- `Docs/架构.md` - full architecture blueprint
- `Docs/Tasks.md` - platform task board and priorities
- `Docs/Tasks/task24-platform-v1-first-slice.md` - class and API level breakdown for the first vertical slice

---

## License

MIT
