# Data Config E2E Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace hidden `.env`/database-centered configuration with file-backed JSON/Markdown configuration, deterministic directories, and an E2E-ready test foundation.

**Architecture:** Add a small core configuration layer in `PuddingCore` for typed JSON config, path resolution, LLM provider/model/profile resolution, and agent template/instance profile resolution. Runtime and host code will consume that layer incrementally while preserving existing DB/runtime persistence as derived indexes. Later tasks add a fake OpenAI-compatible provider, Playwright E2E harness, and frontend debug surface.

**Tech Stack:** .NET 10, MSTest, `System.Text.Json`, ASP.NET Core, Playwright, Docker Compose.

---

## File Map

- Create: `Source/PuddingCore/Configuration/PuddingDataPaths.cs`
  - Central resolver for `data`, `config`, `agent-templates`, `agents`, `workspaces`, `logs`, `runtime`, `databases`, and `tmp`.
- Create: `Source/PuddingCore/Configuration/PuddingConfigModels.cs`
  - Typed JSON models for system, LLM providers/profiles/roles, security, connectors, agent templates, agent instances, and workspace refs.
- Create: `Source/PuddingCore/Configuration/PuddingFileConfigLoader.cs`
  - Loads and validates JSON files from `data/config`.
- Create: `Source/PuddingCore/Configuration/LlmProfileResolver.cs`
  - Resolves conscious/subconscious LLM bindings from agent instance, template, and global role defaults.
- Create: `Source/PuddingCore/Agents/AgentProfileProvider.cs`
  - Reads `data/agent-templates/{templateId}` and `data/agents/{agentInstanceId}`.
- Test: `Source/PuddingCoreTests/Configuration/PuddingDataPathsTests.cs`
- Test: `Source/PuddingCoreTests/Configuration/PuddingFileConfigLoaderTests.cs`
- Test: `Source/PuddingCoreTests/Configuration/LlmProfileResolverTests.cs`
- Test: `Source/PuddingCoreTests/Agents/AgentProfileProviderTests.cs`
- Modify: `Source/PuddingRuntime/Services/PuddingConfigLoader.cs`
  - Later compatibility shim; new code should use `PuddingFileConfigLoader`.
- Modify: `Source/PuddingRuntime/Services/DirectLlmClient.cs`
  - Later remove environment fallback after resolver is wired.
- Modify: `Source/PuddingAgent/Program.cs`
  - Later wire `PUDDING_DATA_ROOT`, directory creation, default templates, and DB path defaults.
- Modify: `build-and-up.ps1`
  - Later remove `.env` messaging and validate JSON config files.

## Task 1: Data Path Resolver

**Files:**
- Create: `Source/PuddingCore/Configuration/PuddingDataPaths.cs`
- Test: `Source/PuddingCoreTests/Configuration/PuddingDataPathsTests.cs`

- [ ] **Step 1: Write failing tests**

Create tests for:

```csharp
[TestMethod]
public void FromRoot_Normalizes_All_Expected_Directories()
{
    var paths = PuddingDataPaths.FromRoot(@"C:\pudding\data");

    Assert.AreEqual(@"C:\pudding\data", paths.DataRoot);
    Assert.AreEqual(@"C:\pudding\data\config", paths.ConfigRoot);
    Assert.AreEqual(@"C:\pudding\data\agent-templates", paths.AgentTemplatesRoot);
    Assert.AreEqual(@"C:\pudding\data\agents", paths.AgentInstancesRoot);
    Assert.AreEqual(@"C:\pudding\data\workspaces", paths.WorkspacesRoot);
    Assert.AreEqual(@"C:\pudding\data\logs\sessions", paths.SessionLogsRoot);
    Assert.AreEqual(@"C:\pudding\data\databases", paths.DatabasesRoot);
}

[TestMethod]
public void AgentInstancePath_Uses_Instance_Id()
{
    var paths = PuddingDataPaths.FromRoot(@"C:\pudding\data");

    Assert.AreEqual(
        @"C:\pudding\data\agents\default.general-assistant-001\config\llm.json",
        paths.AgentInstanceConfigFile("default.general-assistant-001", "llm.json"));
}
```

- [ ] **Step 2: Run RED**

Run: `dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore --filter PuddingDataPathsTests --logger "console;verbosity=minimal"`

Expected: fail because `PuddingDataPaths` does not exist.

- [ ] **Step 3: Implement `PuddingDataPaths`**

Implement immutable path properties and helpers:

```csharp
public sealed record PuddingDataPaths
{
    public required string DataRoot { get; init; }
    public string ConfigRoot => Path.Combine(DataRoot, "config");
    public string AgentTemplatesRoot => Path.Combine(DataRoot, "agent-templates");
    public string AgentInstancesRoot => Path.Combine(DataRoot, "agents");
    public string WorkspacesRoot => Path.Combine(DataRoot, "workspaces");
    public string SessionLogsRoot => Path.Combine(DataRoot, "logs", "sessions");
    public string DatabasesRoot => Path.Combine(DataRoot, "databases");
    public static PuddingDataPaths FromRoot(string root) => new() { DataRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) };
    public string AgentInstanceConfigFile(string agentInstanceId, string fileName) => Path.Combine(AgentInstancesRoot, agentInstanceId, "config", fileName);
}
```

- [ ] **Step 4: Run GREEN**

Run the same filtered test. Expected: pass.

- [ ] **Step 5: Commit**

Run:

```powershell
git add Source/PuddingCore/Configuration/PuddingDataPaths.cs Source/PuddingCoreTests/Configuration/PuddingDataPathsTests.cs
git commit -m "feat: add pudding data path resolver"
```

## Task 2: Typed Config Models and Loader

**Files:**
- Create: `Source/PuddingCore/Configuration/PuddingConfigModels.cs`
- Create: `Source/PuddingCore/Configuration/PuddingFileConfigLoader.cs`
- Test: `Source/PuddingCoreTests/Configuration/PuddingFileConfigLoaderTests.cs`

- [ ] **Step 1: Write failing loader tests**

Test that valid `llm.providers.json` loads multi-provider/multi-model profiles and that missing role profile fails with a clear validation result.

- [ ] **Step 2: Run RED**

Run: `dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore --filter PuddingFileConfigLoaderTests --logger "console;verbosity=minimal"`

Expected: fail because loader/model types do not exist.

- [ ] **Step 3: Implement models**

Add records for:

- `PuddingSystemConfig`
- `PuddingLlmProvidersConfig`
- `PuddingLlmProviderConfig`
- `PuddingLlmModelConfig`
- `PuddingLlmProfileConfig`
- `PuddingSecurityConfig`
- `PuddingConnectorsConfig`
- `AgentTemplateManifest`
- `AgentInstanceManifest`
- `AgentInstanceLlmConfig`
- `WorkspaceAgentRef`

- [ ] **Step 4: Implement loader validation**

`PuddingFileConfigLoader` must load JSON with case-insensitive property names and validate:

- provider IDs are non-empty and unique.
- model IDs are non-empty and unique within provider.
- profile provider/model pair exists.
- `roles.conscious` and `roles.subconscious` refer to existing profiles.

- [ ] **Step 5: Run GREEN**

Run filtered loader tests. Expected: pass.

- [ ] **Step 6: Commit**

Run:

```powershell
git add Source/PuddingCore/Configuration/PuddingConfigModels.cs Source/PuddingCore/Configuration/PuddingFileConfigLoader.cs Source/PuddingCoreTests/Configuration/PuddingFileConfigLoaderTests.cs
git commit -m "feat: add file config loader"
```

## Task 3: Conscious/Subconscious LLM Resolution

**Files:**
- Create: `Source/PuddingCore/Configuration/LlmProfileResolver.cs`
- Test: `Source/PuddingCoreTests/Configuration/LlmProfileResolverTests.cs`

- [ ] **Step 1: Write failing resolver tests**

Cover:

- agent instance `config/llm.json` overrides role defaults.
- template default profiles are used when instance config is missing.
- global role defaults are used when template defaults are missing.
- selected model must belong to selected provider.
- separate conscious and subconscious outputs are returned.

- [ ] **Step 2: Run RED**

Run: `dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore --filter LlmProfileResolverTests --logger "console;verbosity=minimal"`

Expected: fail because resolver does not exist.

- [ ] **Step 3: Implement resolver**

Return a `ResolvedAgentLlmProfiles` object:

```csharp
public sealed record ResolvedAgentLlmProfiles(
    ResolvedLlmProfile Conscious,
    ResolvedLlmProfile Subconscious);
```

Each `ResolvedLlmProfile` includes provider ID, model ID, endpoint, api key or secret ref, reasoning effort, thinking mode, max context tokens, and max reply tokens.

- [ ] **Step 4: Run GREEN**

Run filtered resolver tests. Expected: pass.

- [ ] **Step 5: Commit**

Run:

```powershell
git add Source/PuddingCore/Configuration/LlmProfileResolver.cs Source/PuddingCoreTests/Configuration/LlmProfileResolverTests.cs
git commit -m "feat: resolve agent llm profiles"
```

## Task 4: Agent Profile Provider

**Files:**
- Create: `Source/PuddingCore/Agents/AgentProfileProvider.cs`
- Test: `Source/PuddingCoreTests/Agents/AgentProfileProviderTests.cs`

- [ ] **Step 1: Write failing provider tests**

Cover:

- reads template Markdown files from `data/agent-templates/{templateId}`.
- reads instance manifest from `data/agents/{agentInstanceId}/manifest.json`.
- reads instance `config/llm.json`.
- creates a resolved profile with source file paths.

- [ ] **Step 2: Run RED**

Run: `dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore --filter AgentProfileProviderTests --logger "console;verbosity=minimal"`

Expected: fail because provider does not exist.

- [ ] **Step 3: Implement provider**

Implement file reads only. Do not wire DB yet.

- [ ] **Step 4: Run GREEN**

Run filtered provider tests. Expected: pass.

- [ ] **Step 5: Commit**

Run:

```powershell
git add Source/PuddingCore/Agents/AgentProfileProvider.cs Source/PuddingCoreTests/Agents/AgentProfileProviderTests.cs
git commit -m "feat: load agent file profiles"
```

## Task 5: Host Wiring and Script Cleanup

**Files:**
- Modify: `Source/PuddingAgent/Program.cs`
- Modify: `Source/PuddingRuntime/Services/PuddingConfigLoader.cs`
- Modify: `Source/PuddingRuntime/Services/DirectLlmClient.cs`
- Modify: `build-and-up.ps1`
- Test: add focused tests if path/config behavior moves into testable services.

- [ ] **Step 1: Add tests before wiring**

Move host-independent behavior into testable services first. Do not test `Program.cs` directly.

- [ ] **Step 2: Wire `PUDDING_DATA_ROOT`**

Default to `/app/data` in Docker and repo `data` in local development.

- [ ] **Step 3: Remove `.env` script messaging**

`build-and-up.ps1` should validate JSON config paths and stop mentioning `LLM_API_KEY`.

- [ ] **Step 4: Remove LLM environment fallback**

Runtime LLM paths should use resolved file config and fail fast on missing config.

- [ ] **Step 5: Build**

Run:

```powershell
dotnet build Source\PuddingRuntime\PuddingRuntime.csproj --no-restore --nologo
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo
```

- [ ] **Step 6: Commit**

Run:

```powershell
git add Source/PuddingAgent/Program.cs Source/PuddingRuntime/Services/PuddingConfigLoader.cs Source/PuddingRuntime/Services/DirectLlmClient.cs build-and-up.ps1
git commit -m "refactor: wire file-backed runtime config"
```

## Task 6: Fake LLM and E2E Harness

**Files:**
- Create: `Source/PuddingAgent/Controllers/FakeLlmController.cs`
- Create: `Tests/E2E/Pudding.E2E/package.json`
- Create: `Tests/E2E/Pudding.E2E/playwright.config.ts`
- Create: `Tests/E2E/Pudding.E2E/tests/smoke.spec.ts`
- Create: `Tests/E2E/Pudding.E2E/tests/chat-stream.spec.ts`
- Create: `Tests/E2E/Pudding.E2E/tests/diagnostics.spec.ts`
- Create: `Tests/E2E/Pudding.E2E/fixtures/data-template/`

- [ ] **Step 1: Write controller tests or minimal integration checks**

Add fake LLM behavior tests before implementing endpoint.

- [ ] **Step 2: Implement fake OpenAI-compatible sync and stream responses**

Support deterministic content, usage, and SSE chunks.

- [ ] **Step 3: Add Playwright stack tests**

Tests must use a fixture `data` directory configured for fake LLM.

- [ ] **Step 4: Verify**

Run:

```powershell
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo
cd Tests\E2E\Pudding.E2E
npm test
```

- [ ] **Step 5: Commit**

Run:

```powershell
git add Source/PuddingAgent/Controllers/FakeLlmController.cs Tests/E2E/Pudding.E2E
git commit -m "feat: add fake llm e2e harness"
```

## Task 7: Frontend Debug Mode

**Files:**
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/`
- Add tests under `Source/PuddingPlatformAdmin/e2e` or root E2E harness.

- [ ] **Step 1: Write failing Playwright assertion**

Assert `window.__PUDDING_DEBUG__` exists when debug mode is enabled and can return `traceId`, frames, and session state.

- [ ] **Step 2: Implement debug store and browser API**

Expose read-only debug data. Redact secrets.

- [ ] **Step 3: Add debug drawer**

Show session, trace, frame count, context layers, and last runtime activity.

- [ ] **Step 4: Verify**

Run frontend lint/build and E2E debug assertion.

- [ ] **Step 5: Commit**

Run:

```powershell
git add Source/PuddingPlatformAdmin Tests/E2E/Pudding.E2E
git commit -m "feat: add frontend runtime debug mode"
```

## Self-Review

- Spec coverage: config files, provider/model profiles, per-agent config/workspace, Docker data mount, fake LLM, E2E, and frontend debug mode are covered.
- Placeholder scan: no placeholders remain; later phases are scoped with concrete file paths and expected verification commands.
- Type consistency: `conscious` and `subconscious` terms match the design spec and planned resolver output.
