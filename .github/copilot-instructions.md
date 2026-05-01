# Project Guidelines

## Project Orientation

- This repository is transitioning from the earlier `PuddingCode` prototype to the broader `Pudding Agent Network` platform.
- When current implementation and design intent differ, treat `README.md`, `Docs/架构.md`, `Docs/07架构/README.md`, and `Docs/Tasks.md` as the source of truth for direction and boundaries.
- Link to existing docs instead of duplicating architecture details in code comments or new instruction files.

## Architecture

- Preserve the documented authority split:
  - `Source/PuddingPlatform` owns product semantics, workspace management, and upper-layer orchestration.
  - `Source/PuddingController` owns routing, auth, approvals, audit, policy, workflow control, and governance.
  - `Source/PuddingRuntime` owns hot session state, agent execution, memory access, tool execution, and sandboxed runtime behavior.
  - `Source/PuddingCore` should contain stable abstractions, protocols, and shared models, not hot runtime state.
  - `Source/PuddingGateway` is the ingress and adapter boundary under the control-plane design.
- `Workspace` is the main isolation and governance boundary for memory, tools, events, workflows, and agent configuration.
- Avoid designs that let public or low-trust input directly pollute long-term memory.
- For architecture-sensitive work, read the corresponding docs first:
  - `Docs/07架构/01总览与分层.md`
  - `Docs/07架构/02PuddingCore.md`
  - `Docs/07架构/03PuddingRuntime.md`
  - `Docs/07架构/04PuddingController与Gateway.md`
  - `Docs/07架构/05PuddingPlatform.md`

## Build and Test

- For broad .NET validation from the repo root, use `dotnet build PuddingAgentNetwork.slnx`.
- Prefer targeted builds for smaller backend changes to reduce noise:
  - `dotnet build Source/PuddingCore/PuddingCore.csproj`
  - `dotnet build Source/PuddingController/PuddingController.csproj`
  - `dotnet build Source/PuddingRuntime/PuddingRuntime.csproj`
  - `dotnet build Source/PuddingPlatform/PuddingPlatform.csproj`
- Frontend app: `Source/PuddingPlatformAdmin`
  - Requires Node.js `>=20`
  - Common commands: `npm install`, `npm run build`, `npm run lint`, `npm run test`
- Full local stack bootstrap is scripted in `build-and-up.ps1`; it builds frontend and backend artifacts, applies EF Core migrations, and starts Docker Compose services.
- Python scripts under `TestScripts/` are integration checks against running services, not a replacement for project-level build validation.

## Conventions

- Do not manually edit database schema; use EF Core migrations under `Source/PuddingPlatform` and `Source/PuddingController`.
- Docker packaging depends on host-generated build artifacts:
  - backend publishes to `publish/`
  - frontend builds to `dist/`
  Preserve that flow when changing Dockerfiles or deployment scripts.
- If a task requires local secrets or service startup, copy `.env.example` to `.env` and fill at least `LLM_API_KEY` and `JWT_KEY` before running the full stack.
- Existing custom agent definitions live under `.github/agents/`; keep workspace instructions generic and avoid duplicating agent-specific workflow logic here.
- Prefer linking to `Docs/` for roadmap, architecture, and task decomposition:
  - `Docs/README.md`
  - `Docs/07架构/README.md`
  - `Docs/Tasks.md`
