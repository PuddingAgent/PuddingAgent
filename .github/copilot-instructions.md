# Project Guidelines

## Project Orientation

- This repository is transitioning from the earlier `PuddingCode` prototype to the V1 single-process Pudding Agent model.
- When current implementation and design intent differ, treat `README.md`, `Docs/架构.md`, `Docs/07架构/README.md`, and `Docs/Tasks.md` as the source of truth for direction and boundaries. Task status lives in Todo API, not hardcoded in documents.
- Link to existing docs instead of duplicating architecture details in code comments or new instruction files.

## Architecture

- V1 架构为单进程模型，运行在一个 .NET 进程中：
  - 内嵌 ASP.NET Core（Controller + Gateway）
  - 内嵌 Runtime（Agent 执行、Memory、Tool、Session）
  - 内嵌 SQLite（EF Core 持久化）
  - 内嵌 React 前端（Web Chat UI）
- P2P 通信通过 mDNS 发现 + HTTP/gRPC 直连实现
- 任务管理使用 Todo API，禁止在代码或文档中硬编码任务状态
- 架构文档参考：
  - `Docs/架构.md`
  - `Docs/07架构/README.md`

## Build and Test

- For broad .NET validation from the repo root, use `dotnet build PuddingAgentNetwork.slnx`.
- Prefer targeted builds for smaller backend changes to reduce noise:
  - `dotnet build Source/PuddingAgent/PuddingAgent.csproj`
  - `dotnet build Source/PuddingCore/PuddingCore.csproj`
  - `dotnet build Source/PuddingRuntime/PuddingRuntime.csproj`
- Frontend app: `Source/PuddingPlatformAdmin`
  - Requires Node.js `>=20`
  - Common commands: `npm install`, `npm run build`, `npm run lint`, `npm run test`
- Full local stack bootstrap is scripted in `build-and-up.ps1`; it builds frontend and backend artifacts, applies EF Core migrations, and starts Docker Compose services.
- Python scripts under `TestScripts/` are integration checks against running services, not a replacement for project-level build validation.

## Conventions

- Do not manually edit database schema; use EF Core migrations under `Source/PuddingPlatform` and `Source/PuddingController`（V1 中均为内嵌模块，不再独立部署）。
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


使用todo-api管理任务状态，避免在代码或文档中硬编码任务进度。

临时脚本（python或者其他语言脚本）或一次性验证可以直接放在 `temp` 目录下，不需要放在 `Source/` 内或者根目录。

Docs\架构.md 和 Docs\07架构\README.md 是架构设计的主要文档，任何新的设计决策都应该先更新这两个文档，而不是直接在代码里添加注释或者创建新的 instruction 文件。