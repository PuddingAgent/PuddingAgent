# Agent Runtime SKILL Filesystem Design

Date: 2026-06-16

## Goal

Implement the V1 SKILL foundation for runtime Agent instances. A SKILL is stored as files under the agent instance data directory, can be read and managed by backend services, can be exposed to the Agent through tools, and contributes only stable index and summary information to the session context.

This phase should be testable as an isolated C# service first, then integrated into runtime tools and context assembly.

## Scope

In scope:

- Store runtime-private SKILL files at `data/agents/{agentInstanceId}/skills/{skillId}/`.
- Provide a filesystem service for create, read, update, delete, list, and path resolution.
- Maintain an index file at `data/agents/{agentInstanceId}/skills/index.json`.
- Return physical storage paths from service and tool responses.
- Expose read and management tools to the Agent.
- Add a context layer that injects SKILL index entries and summaries only.
- Keep prompt content stable for cache hits by sorting and hashing deterministically.
- Cover the filesystem service with standalone C# unit tests before runtime integration.

Out of scope for V1:

- `data/agent-templates/{templateId}/skills/...` template-level files.
- UI management screens.
- Executing arbitrary scripts from SKILL directories.
- Injecting full SKILL contents into the system prompt.

## Storage Layout

Each runtime Agent owns its SKILL directory:

```text
data/agents/{agentInstanceId}/skills/
  index.json
  {skillId}/
    manifest.json
    SKILL.md
    files...
```

`PuddingDataPaths.AgentInstanceRoot(agentInstanceId)` is the source of truth for path construction. The service must not use `AppContext.BaseDirectory/data` directly, because local development may set `PUDDING_DATA_ROOT` to another data root.

## Data Contracts

`AgentSkillManifest`:

- `skillId`: stable directory-safe id.
- `name`: display name.
- `version`: version string, default `1.0.0`.
- `description`: short human description.
- `summary`: concise prompt-safe summary.
- `tags`: optional labels.
- `enabled`: whether the SKILL is visible to the Agent.
- `createdAt`, `updatedAt`: UTC timestamps.
- `contentHash`: SHA-256 hash over manifest-visible content plus indexed file content.

`AgentSkillIndex`:

- `agentInstanceId`
- `generatedAt`
- `skills`: sorted list of enabled and disabled index entries.

`AgentSkillIndexEntry`:

- `skillId`, `name`, `version`, `description`, `summary`, `tags`, `enabled`
- `relativePath`: path relative to the agent instance root.
- `physicalPath`: absolute path returned to API/tool callers, but not required in prompt text.
- `contentHash`
- `updatedAt`

## Filesystem Service

Add a service tentatively named `AgentSkillFileService` under the runtime layer or a neutral shared service namespace if tests require minimal dependencies.

Responsibilities:

- Resolve the skills root for an `agentInstanceId`.
- Validate `agentInstanceId`, `skillId`, and relative file paths.
- Create SKILL directories with `manifest.json` and `SKILL.md`.
- Read SKILL metadata and file content.
- Update manifest fields and `SKILL.md`.
- Delete a SKILL directory.
- List all SKILL manifests.
- Rebuild and read `index.json`.
- Return physical paths in result DTOs.

Path safety:

- `skillId` should accept only letters, numbers, `_`, and `-`.
- All computed paths must be normalized with `Path.GetFullPath`.
- Any resolved path must remain under the expected `skills` root.
- File operations should reject absolute input paths and `..` traversal.

Write behavior:

- Use a per-service write lock or per-agent lock to avoid corrupting `manifest.json` and `index.json`.
- Write JSON with stable camelCase formatting.
- Rebuild `index.json` after create, update, delete, and enabled-state changes.
- Derive `summary` from manifest first; if missing, extract a short summary from front matter or the first meaningful paragraphs of `SKILL.md`.

## Tools

Build tools on top of the filesystem service, not by duplicating file logic.

Read-only low-risk tools:

- `list_agent_skills`: list indexed SKILL entries for the current agent.
- `read_agent_skill`: read `SKILL.md` or a named file inside one SKILL directory.
- `search_agent_skills`: search SKILL names, summaries, tags, and optionally markdown content.

Management medium-risk tool:

- `manage_agent_skill`: `create`, `update`, `delete`, `set_enabled`, `rebuild_index`.

Tool context must use the runtime `AgentInstanceId` from `ToolExecutionContext`. The Agent should not be able to manage another Agent's SKILL directory by passing a different agent id.

Tool responses should include:

- status
- `skillId`
- `physicalPath`
- changed files
- updated index hash or generated timestamp
- concise error messages

## Context Injection

Add a SKILL index layer to `ContextPipeline` after tool availability and before dynamic memory. The layer should include only:

- stable heading
- count of enabled SKILL entries
- sorted entries with `skillId`, `name`, `version`, and summary
- brief instruction to use SKILL tools for full content

It must not include full `SKILL.md` content.

Cache behavior:

- Sort entries by `skillId`, then `version`.
- Keep unchanged fields and ordering stable.
- Use `contentHash` to record change boundaries.
- Cache per `agentInstanceId` and invalidate when `index.json` changes.
- Avoid including volatile physical paths in the prompt unless needed; physical paths should be returned by tools.

## Tests

Filesystem service tests should be written first:

- Creates a SKILL at the expected physical path.
- Rejects invalid `skillId` and path traversal.
- Reads `manifest.json` and `SKILL.md`.
- Updates summary/content and refreshes `contentHash`.
- Deletes the SKILL directory.
- Rebuilds `index.json` in deterministic order.
- Keeps disabled SKILLs out of the prompt-ready index view.

Integration tests after service tests:

- Tool list/read/manage operations use the current `AgentInstanceId`.
- Context pipeline includes SKILL index summaries, not full markdown.
- Context layer ordering remains stable between runs when files do not change.

## Risks

- Path traversal is the primary security risk; it must be tested directly.
- Large SKILL directories could make indexing expensive; V1 should index markdown and manifest metadata only.
- Context bloat is avoided by injecting summaries only.
- Agent-managed deletes are destructive; management tool should be medium permission and return clear results.

## Acceptance Criteria

- A standalone C# unit test suite passes for the filesystem service.
- A runtime Agent can create, list, read, update, disable, and delete its own SKILL through tools.
- Context assembly includes enabled SKILL index summaries for the current Agent instance.
- Full SKILL content is available only through read/search tools, not injected by default.
- Physical storage paths are returned by service and tool responses.
