# Data, Configuration, and E2E Foundation Design

**Date:** 2026-05-18  
**Status:** Proposed  
**Scope:** System configuration, LLM/provider configuration, agent file layout, Docker data mount layout, and automated end-to-end testing.

## Problem

Pudding currently has multiple overlapping configuration paths:

- Root `.env` and environment-variable fallback paths.
- `data/conf/pudding-config.json`.
- `data/llm/config.json`.
- `config/llm-config.json`.
- Agent template fields persisted in SQLite, with only partial file-based persona support.

This makes configuration opaque to users, hard to back up, and hard to reason about during Docker-based development. It also weakens testability because many behaviors depend on hidden process environment state or manually edited database rows.

End-to-end testing is similarly weak. The admin frontend has a Playwright dependency and a small `e2e` folder, but the tests assume a manually running Docker service, do not control test data, do not use a fake LLM, and do not assert runtime diagnostics. Browser-based manual QA is still the main path for discovering integration regressions.

## Goals

1. Make JSON files the only machine-readable source of system and LLM configuration.
2. Remove `.env` as an application configuration mechanism.
3. Make agent identity, behavior, and capability configuration inspectable through files.
4. Keep Markdown for natural-language agent content and JSON for structured configuration.
5. Standardize all mutable runtime state under a single Docker-mounted `data` directory.
6. Add deterministic E2E automation that can run locally and in CI without real LLM calls.
7. Add a frontend debug mode that exposes runtime state and makes E2E assertions robust.

## Non-Goals

- Do not introduce YAML in this phase.
- Do not make the database the source of truth for agent configuration.
- Do not require a real external LLM for baseline E2E tests.
- Do not redesign the admin UI in this phase.
- Do not remove SQLite persistence for runtime/session/memory data.

## Design Principles

- **Files are the source of truth:** user-editable configuration and agent definitions live under `data`.
- **Database is a runtime index:** SQLite stores queryable runtime state, events, sessions, memory, and derived indexes.
- **One format per purpose:** JSON for structured settings, Markdown for prompts/persona/rules, JSONL for append-only event/session logs.
- **Deterministic tests first:** E2E uses a fake LLM provider by default.
- **Mount once:** Docker mounts `./data:/app/data`; all mutable runtime state stays inside that mount.
- **No implicit secrets:** production secrets should be explicit JSON references or local-only JSON values excluded from Git.

## Target Data Layout

```text
data/
  config/
    system.json
    llm.providers.json
    security.json
    connectors.json
  agents/
    {agentId}/
      manifest.json
      SOUL.md
      AGENTS.md
      TOOLS.md
      BOOTSTRAP.md
      MEMORY.md
  workspaces/
    {workspaceId}/
      manifest.json
      agents/
        {agentInstanceId}/
          manifest.json
          workspace/
  logs/
    system/
    diagnostics/
    sessions/
      {sessionId}/
        session-{yyyyMMdd}.jsonl
  runtime/
    traces/
    event-queue/
  memory/
  databases/
    pudding_platform.db
    pudding_memory.db
    pudding_controller.db
  backups/
  tmp/
```

Existing `data/jsonl` and `data/logs/sessions` should migrate into `data/logs/sessions`. Existing database files should migrate into `data/databases`. The migration must be non-destructive: if old files exist, the application should read them during a compatibility window and write new files to the target layout.

## Configuration Files

### `data/config/system.json`

Owns non-secret host/runtime settings:

```json
{
  "environment": "development",
  "http": {
    "port": 8080,
    "publicBaseUrl": "http://localhost:5000"
  },
  "logging": {
    "level": "Debug",
    "structuredJson": true
  },
  "runtime": {
    "maxAgentRounds": 200,
    "enableRuntimeDiagnostics": true,
    "enableFrontendDebug": true
  },
  "paths": {
    "dataRoot": "/app/data"
  }
}
```

### `data/config/llm.providers.json`

Owns providers, models, and role defaults:

```json
{
  "defaultProviderId": "fake",
  "defaultModelId": "fake-chat",
  "providers": [
    {
      "providerId": "fake",
      "name": "Fake LLM",
      "protocol": "openai",
      "baseUrl": "http://localhost:5000/__fake_llm/v1",
      "apiKey": "local-dev-only",
      "isEnabled": true,
      "models": [
        {
          "modelId": "fake-chat",
          "name": "Fake Chat",
          "maxContextTokens": 65536,
          "maxOutputTokens": 4096,
          "capabilityTags": ["text", "function-calling", "streaming"],
          "isDefault": true,
          "sortOrder": 1
        }
      ]
    }
  ],
  "roles": {
    "conscious": {
      "providerId": "fake",
      "modelId": "fake-chat",
      "reasoningEffort": "medium",
      "thinkingMode": "disabled"
    },
    "memory": {
      "providerId": "fake",
      "modelId": "fake-chat",
      "reasoningEffort": "low",
      "thinkingMode": "disabled"
    }
  }
}
```

The application must not fall back to `LLM_API_KEY`, `LLM_ENDPOINT`, `LLM_MODEL`, `MEMORY_LLM_API_KEY`, `MEMORY_LLM_ENDPOINT`, or `MEMORY_LLM_MODEL_ID` after this migration. Missing or invalid JSON should fail startup with an actionable error.

### `data/config/security.json`

Owns application security settings:

```json
{
  "jwt": {
    "issuer": "pudding-platform",
    "audience": "pudding-admin",
    "expiryHours": 8,
    "key": "local-dev-key-change-me-32plus"
  },
  "keyVault": {
    "mode": "local-file",
    "masterKeyRef": "local"
  }
}
```

This file is local runtime data and must remain ignored by Git when it contains real secrets. A sanitized template belongs under `Source/PuddingAgent/default-data/config/security.json`.

### `data/config/connectors.json`

Owns connector enablement and local ports:

```json
{
  "http": { "enabled": true },
  "websocket": { "enabled": true },
  "mqtt": { "enabled": false },
  "p2p": {
    "enabled": true,
    "port": 9527
  }
}
```

## Agent File Model

Each agent has a directory under `data/agents/{agentId}`.

### `manifest.json`

```json
{
  "agentId": "general-assistant",
  "name": "Pudding",
  "description": "General assistant",
  "role": "Service",
  "preferredProviderId": "fake",
  "preferredModelId": "fake-chat",
  "memorySearchMode": "deep",
  "reasoningEffort": "medium",
  "maxContextTokens": 65536,
  "maxReplyTokens": 4096,
  "isBuiltIn": true,
  "isEnabled": true,
  "capabilities": {
    "allowTools": true,
    "allowedToolIds": []
  }
}
```

### Markdown Files

- `SOUL.md`: identity, personality, values, conversational stance.
- `AGENTS.md`: operating rules, task boundaries, collaboration rules.
- `TOOLS.md`: tool-use rules and permissions.
- `BOOTSTRAP.md`: initial interaction guidance.
- `MEMORY.md`: memory behavior, personal data rules, consolidation rules.

`AgentPersonaFileProvider` should evolve into `AgentProfileProvider`, reading `manifest.json` plus Markdown files. Existing DB-backed template fields remain as derived cache during the migration, not the canonical source.

## Startup and Migration

Startup should run in this order:

1. Resolve `PUDDING_DATA_ROOT`, defaulting to `/app/data` in Docker and repo `data` in local development.
2. Ensure target directories exist.
3. If `data/config/*.json` is missing, copy from packaged `default-data/config`.
4. Validate JSON schema and fail fast on missing required fields.
5. Migrate old paths non-destructively:
   - `data/conf/pudding-config.json` into `data/config/system.json` and `data/config/llm.providers.json`.
   - `data/llm/config.json` into `data/config/llm.providers.json`.
   - session JSONL/log files into `data/logs/sessions`.
6. Seed database indexes from file source.
7. Start runtime services.

The migration should write `data/runtime/migrations/config-layout-{timestamp}.json` with source paths, target paths, and skipped conflicts.

## Docker Mounting

`docker-compose.yml` should keep one mutable mount:

```yaml
volumes:
  - ./data:/app/data
```

Environment variables remain only for container mechanics:

- `ASPNETCORE_ENVIRONMENT`
- `ASPNETCORE_HTTP_PORTS`
- `PUDDING_DATA_ROOT`

Application configuration should not be passed via `.env`. `build-and-up.ps1` should stop checking for `.env` and should report the expected JSON config files instead.

## Fake LLM Provider

Add a local fake OpenAI-compatible provider for tests:

- `POST /__fake_llm/v1/chat/completions`
- Supports sync and SSE streaming.
- Returns deterministic content based on request messages.
- Emits deterministic `usage`.
- Supports tool-call responses through simple trigger phrases.
- Can inject errors through test-only request metadata.

This provider is disabled unless `system.runtime.enableFakeLlm` or test mode is enabled.

## External E2E Testing

The main E2E runner should be Playwright, not browser self-tests.

Target location:

```text
Tests/
  E2E/
    Pudding.E2E/
      playwright.config.ts
      tests/
        smoke.spec.ts
        chat-stream.spec.ts
        diagnostics.spec.ts
        agent-config.spec.ts
      fixtures/
        data-template/
      scripts/
        start-stack.ps1
        reset-data.ps1
```

Baseline tests:

1. `smoke`: Docker starts, `/health` returns healthy, admin app loads.
2. `login`: default local user can authenticate.
3. `chat-stream`: send message, receive user frame, context frame, assistant deltas, done frame.
4. `diagnostics`: after chat, query runtime diagnostics by trace ID and assert ordered components exist.
5. `agent-config`: edit `data/agents/test-agent/SOUL.md`, restart or reload, assert prompt source changes.
6. `fake-llm-error`: inject provider failure and assert UI plus diagnostics record the failure.
7. `event-sub-agent`: trigger a deterministic event that spawns a sub-agent and assert trace linkage.

Artifacts:

```text
data/logs/e2e/{runId}/
  playwright-report/
  screenshots/
  traces/
  server.log
  docker-compose.log
  diagnostics.json
```

## Frontend Debug Mode

Frontend debug mode should support E2E and human diagnosis, but it should not replace external tests.

Enablement:

- Query parameter: `?debug=1`
- Local storage: `pudding.debug=true`
- Server flag: `system.runtime.enableFrontendDebug`

Debug surface:

- Read-only debug drawer on chat/session pages.
- Shows `sessionId`, `traceId`, active agent, model, SSE frame count, current context layers, last runtime activity, last error.
- Can export debug snapshot as JSON.

Browser automation API:

```ts
window.__PUDDING_DEBUG__ = {
  getSessionState(): unknown;
  getFrames(): unknown[];
  getTraceId(): string | undefined;
  waitForFrame(type: string, timeoutMs?: number): Promise<unknown>;
  sendMessage(text: string): Promise<void>;
  dump(): unknown;
};
```

Playwright should use this API for robust assertions when available, while still verifying visible UI behavior.

## Observability Requirements

Every E2E chat run should be diagnosable from persisted runtime data:

- `traceId` must be visible in frontend debug mode.
- Runtime diagnostics must show component order.
- Session logs must link to trace ID and agent ID.
- LLM calls must include model, provider, duration, token usage, and error status.
- Sub-agent activities must include parent/child execution IDs.

## Implementation Phases

### Phase 1: Configuration Source Consolidation

- Add typed config models and loader for `data/config`.
- Add default templates under packaged app assets.
- Add startup validation.
- Remove `.env` checks from scripts.
- Remove LLM environment fallback paths.

### Phase 2: Directory Layout and Migration

- Add `DataPathOptions` and central path resolver.
- Create target directories at startup.
- Add non-destructive migration from old `data/conf`, `data/llm`, and session log paths.
- Move DB connection string defaults to `data/databases`.

### Phase 3: Agent File Profiles

- Add `AgentProfileProvider`.
- Read `manifest.json` plus Markdown files.
- Seed DB from file profile.
- Add reload endpoint or file watcher later if needed.

### Phase 4: Fake LLM Provider

- Add test-only fake OpenAI-compatible route.
- Support sync, streaming, tool calls, deterministic usage, and error injection.

### Phase 5: E2E Harness

- Move/replace current frontend `e2e` tests with root-level stack tests.
- Add deterministic data fixture.
- Capture artifacts under `data/logs/e2e`.
- Add CI job that starts the Docker stack and runs the tests.

### Phase 6: Frontend Debug Mode

- Add debug store and drawer.
- Add `window.__PUDDING_DEBUG__`.
- Wire debug mode to SSE frame recording and runtime diagnostics.

## Risks and Mitigations

- **Risk:** Removing environment fallback breaks existing local installs.  
  **Mitigation:** Provide one-time migration and clear startup error messages.

- **Risk:** Real secrets already exist in local JSON files.  
  **Mitigation:** Keep `data` ignored, add sanitized default templates, and document key rotation.

- **Risk:** DB-backed admin edits conflict with file-based source of truth.  
  **Mitigation:** Treat admin edits as file writes or staged changes; do not silently diverge DB from files.

- **Risk:** E2E tests become flaky due real LLM latency.  
  **Mitigation:** Fake LLM is the default E2E provider; real-provider tests are opt-in.

- **Risk:** Frontend debug API leaks sensitive information.  
  **Mitigation:** Gate by server config and debug mode; redact API keys and secrets from snapshots.

## Acceptance Criteria

- No application LLM behavior depends on `.env` or LLM environment variables.
- `data/config/*.json` is sufficient to boot the Docker stack.
- `build-and-up.ps1` no longer asks for `.env`.
- Agent identity and behavior can be inspected in `data/agents/{agentId}`.
- Docker uses a single `./data:/app/data` mutable mount.
- E2E can start a clean stack, run chat, verify diagnostics, and shut down.
- Frontend debug mode exposes trace/session state for Playwright and human debugging.
- Baseline builds and tests run without real LLM credentials.

## Self-Review

- Placeholder scan: no `TBD` or open-ended implementation placeholders remain.
- Scope check: the design is intentionally multi-phase; each phase can be implemented and verified independently.
- Format consistency: JSON is used for structured config, Markdown for agent natural-language content, JSONL for logs.
- Migration consistency: existing paths are handled through non-destructive migration before old fallbacks are removed.
