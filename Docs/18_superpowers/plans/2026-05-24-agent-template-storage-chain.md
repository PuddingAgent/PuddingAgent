# Agent Template Storage Chain Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make global Agent template editing round-trip every configurable field through the canonical file-backed storage chain.

**Architecture:** Keep the Admin UI as a single grouped settings drawer, but normalize save requests before API calls. Move global template API persistence to `AgentTemplateFileService`, then converge runtime reads on the same file-backed template data. DB template tables become compatibility fallback only.

**Tech Stack:** React 19, Ant Design Pro, Umi request, Jest/Testing Library, ASP.NET Core, EF Core, file-backed JSON/Markdown config.

---

## File Structure

- Modify: `Source/PuddingPlatformAdmin/src/pages/global-agent-template/index.tsx`  
  Owns page-level create/edit/save state and request normalization.
- Modify: `Source/PuddingPlatformAdmin/src/pages/agent-template-settings/sections/BasicSection.tsx`  
  Add required token-adjacent validation only if field lives here.
- Modify: `Source/PuddingPlatformAdmin/src/pages/agent-template-settings/sections/GuardrailSection.tsx`  
  Add required validation for `maxContextTokens` and `maxReplyTokens`.
- Modify: `Source/PuddingPlatformAdmin/src/pages/agent-template-settings/sections/PromptPersonaSection.tsx`  
  Add `agentsPrompt` and `memoryPrompt` editors.
- Modify: `Source/PuddingPlatformAdmin/src/services/platform/api.ts`  
  Align frontend DTOs with backend fields.
- Create/Modify tests under `Source/PuddingPlatformAdmin/src/pages/global-agent-template/` and `Source/PuddingPlatformAdmin/src/pages/agent-template-settings/sections/`.
- Modify: `Source/PuddingCore/Configuration/PuddingConfigModels.cs`  
  Add missing manifest fields.
- Modify: `Source/PuddingPlatform/Services/AgentTemplateFileService.cs`  
  Persist and map every DTO field.
- Modify: `Source/PuddingPlatform/Controllers/Api/GlobalAgentTemplateApiController.cs`  
  Delegate to `AgentTemplateFileService`.
- Modify tests under `Source/PuddingPlatformTests/Services/`.

---

### Task 1: Frontend Request Normalization

**Files:**
- Modify: `Source/PuddingPlatformAdmin/src/pages/global-agent-template/index.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/agent-template-settings/sections/GuardrailSection.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/agent-template-settings/sections/PromptPersonaSection.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/services/platform/api.ts`
- Test: `Source/PuddingPlatformAdmin/src/pages/global-agent-template/globalAgentTemplateSave.test.tsx`

- [x] **Step 1: Write the failing test**

Create a test that renders the page with mocked platform API functions, opens edit twice, and asserts the saved request:

```tsx
it('normalizes capability, skill, prompt, model, and guardrail fields before saving', async () => {
  // Mock listGlobalAgentTemplates with one template.
  // Mock listCapabilities with default cap-http-fetch and grant cap-python.
  // Mock updateGlobalAgentTemplate and capture request body.
  // Open edit, move cap-python, save.
  // Assert request.selectedCapabilityIds === ['cap-http-fetch', 'cap-python'].
  // Assert request.selectedSkillPackageIds is an array.
  // Assert request.maxContextTokens and request.maxReplyTokens are present.
});
```

- [x] **Step 2: Run test to verify it fails**

Run:

```powershell
pnpm jest -- src/pages/global-agent-template/globalAgentTemplateSave.test.tsx --runInBand
```

Expected: FAIL because save uses raw `validateFields()` and missing fields can be absent.

- [x] **Step 3: Implement minimal frontend normalization**

Add a pure helper near the page component:

```ts
function uniqueStrings(values: string[]): string[] {
  return Array.from(new Set(values.filter(Boolean)));
}

function buildGlobalAgentTemplateRequest(
  values: UpsertGlobalAgentTemplateRequest,
  defaults: {
    defaultCapIds: string[];
    grantTargetKeys: string[];
    skillTargetKeys: string[];
  },
): UpsertGlobalAgentTemplateRequest {
  return {
    ...values,
    selectedCapabilityIds: uniqueStrings([...defaults.defaultCapIds, ...defaults.grantTargetKeys]),
    selectedSkillPackageIds: [...defaults.skillTargetKeys],
    memorySearchMode: values.memorySearchMode || 'deep',
    maxRounds: values.maxRounds ?? 200,
    maxElapsedSeconds: values.maxElapsedSeconds ?? 1200,
    maxToolCallsTotal: values.maxToolCallsTotal ?? 100,
    maxContextTokens: values.maxContextTokens ?? 8192,
    maxReplyTokens: values.maxReplyTokens ?? 2048,
    isEnabled: values.isEnabled ?? true,
    sortOrder: values.sortOrder ?? 100,
  };
}
```

Update `handleSave` to send this normalized request.

- [x] **Step 4: Reset edit state before setting values**

In `openEdit`, call `form.resetFields()` before loading models and `form.setFieldsValue(...)`. Keep `grantTargetKeys` and `skillTargetKeys` synchronized from the item.

- [x] **Step 5: Add missing prompt editors and API fields**

In `PromptPersonaSection`, add textareas:

```tsx
<ProFormTextArea name="agentsPrompt" label="子 Agent 协作规范（AGENTS.md）" rows={5} />
<ProFormTextArea name="memoryPrompt" label="记忆策略（MEMORY.md）" rows={5} />
```

In `api.ts`, add:

```ts
agentsPrompt?: string;
memoryPrompt?: string;
consciousProfileId?: string;
subconsciousProfileId?: string;
```

to both `GlobalAgentTemplateDto` and `UpsertGlobalAgentTemplateRequest`.

- [x] **Step 6: Add guardrail required validation**

Set required rules on `maxContextTokens` and `maxReplyTokens`:

```tsx
<ProFormDigit name="maxContextTokens" label="上下文 tokens" min={1024} rules={[{ required: true }]} />
<ProFormDigit name="maxReplyTokens" label="最大回复 tokens" min={128} rules={[{ required: true }]} />
```

- [x] **Step 7: Run frontend tests**

Run:

```powershell
pnpm jest -- src/pages/global-agent-template/globalAgentTemplateSave.test.tsx src/pages/agent-template-settings/sections/CapabilitySkillSection.test.tsx --runInBand
pnpm build
```

Expected: PASS.

---

### Task 2: File Service Full Field Round Trip

**Files:**
- Modify: `Source/PuddingCore/Configuration/PuddingConfigModels.cs`
- Modify: `Source/PuddingPlatform/Services/AgentTemplateFileService.cs`
- Test: `Source/PuddingPlatformTests/Services/AgentTemplateFileServiceTests.cs`

- [x] **Step 1: Write failing file service test**

Extend `AgentTemplateFileServiceTests` with a test that creates a template with every field, updates it, then reads it back:

```csharp
[TestMethod]
public async Task TemplateRoundTrip_ShouldPreserveAllEditableGlobalTemplateFields()
{
    // Create request with prompts, agentsPrompt, memoryPrompt, skill ids,
    // capability ids, model fields, guardrails, container image.
    // Assert GetTemplateAsync returns the same values.
}
```

- [x] **Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test Source/PuddingPlatformTests/PuddingPlatformTests.csproj --filter AgentTemplateFileServiceTests
```

Expected: FAIL for missing `agentsPrompt`, `memoryPrompt`, skill IDs, container image, and guardrail persistence.

- [x] **Step 3: Extend manifest model**

In `AgentTemplateManifest`, add:

```csharp
public string? SystemPrompt { get; init; }
public string? UserPromptTemplate { get; init; }
public int MaxRounds { get; init; } = 200;
public int MaxElapsedSeconds { get; init; } = 1200;
public int MaxToolCallsTotal { get; init; } = 100;
public string? ContainerImage { get; init; }
public List<string> SkillPackageIds { get; init; } = [];
```

- [x] **Step 4: Persist fields in AgentTemplateFileService**

Update Create/Update to write every new manifest field and markdown file:

```text
SOUL.md <- PersonaPrompt
TOOLS.md <- ToolsDescription
BOOTSTRAP.md <- BootstrapTemplate
AGENTS.md <- AgentsPrompt
MEMORY.md <- MemoryPrompt
```

When a request field is `null`, delete or preserve according to update semantics:

- Create: omit missing files.
- Update: if field is non-null, overwrite file; if empty string, write empty file to express explicit clear.

- [x] **Step 5: Map DTO from file service**

`MapToDtoAsync` must read all Markdown files and all manifest fields into `GlobalAgentTemplateDto`.

- [x] **Step 6: Run backend tests**

Run:

```powershell
dotnet test Source/PuddingPlatformTests/PuddingPlatformTests.csproj --filter AgentTemplateFileServiceTests
```

Expected: PASS.

---

### Task 3: Switch Global Template API to File-Backed

**Files:**
- Modify: `Source/PuddingPlatform/Controllers/Api/GlobalAgentTemplateApiController.cs`
- Test: add or extend Web/API tests where available.

- [x] **Step 1: Write failing API test**

Add a test proving `PUT /api/global-agent-templates/{id}` writes a file-backed template and `GET` reads it back without depending on `GlobalAgentTemplates` DB rows.

- [x] **Step 2: Run test to verify it fails**

Run the relevant API test project:

```powershell
dotnet test Source/PuddingPlatformTests/PuddingPlatformTests.csproj --filter GlobalAgentTemplateApiControllerTests
```

Expected: FAIL because controller reads/writes DB.

- [x] **Step 3: Delegate controller to AgentTemplateFileService**

Change constructor:

```csharp
public class GlobalAgentTemplateApiController(AgentTemplateFileService templates) : ControllerBase
```

Change actions:

```csharp
List => templates.ListTemplatesAsync(enabledOnly, ct)
Get => templates.GetTemplateAsync(templateId, ct)
Create => templates.CreateTemplateAsync(req, ct)
Update => templates.UpdateTemplateAsync(templateId, req, ct)
Delete => templates.DeleteTemplateAsync(templateId, ct)
```

- [x] **Step 4: Run backend API tests**

Run:

```powershell
dotnet test Source/PuddingPlatformTests/PuddingPlatformTests.csproj --filter GlobalAgentTemplateApiControllerTests
```

Expected: PASS.

---

## Self-Review

- Spec coverage: covers frontend request normalization, file-backed storage, API delegation, and field round-trip validation.
- Placeholder scan: no implementation step relies on TBD behavior.
- Type consistency: field names match `GlobalAgentTemplateDto`, `UpsertGlobalAgentTemplateRequest`, and proposed `AgentTemplateManifest` additions.
