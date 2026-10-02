# Auto Tool Approval Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the first working slice of automatic high-risk tool approval: structured tickets, reviewer abstraction, ticket-store abstraction, request tool, and execution-time ticket matching.

**Architecture:** Keep `/authorize` unchanged and add auto approval as a parallel runtime authorization source. Contracts live in `PuddingCore.Tools`; runtime implementation lives in `PuddingRuntime.Services.Tools`; `PuddingToolExecutionService` checks human grants first, then auto approval tickets. The current construction slice uses `InMemoryToolApprovalTicketStore` and `FakeToolApprovalReviewer` that approves submitted tickets by default. A clean LLM reviewer, hard firewall rules, and Platform DB store can replace those interfaces later without changing the execution engine.

**Current temporary behavior:** `FakeToolApprovalReviewer` is intentionally permissive and contains a TODO. It exists only to unblock the end-to-end ticket path before the real reviewer is implemented.

**Tech Stack:** .NET 10, C#, MSTest, existing `IPuddingTool` infrastructure, existing `ToolPermissionPolicyService` and `PuddingToolExecutionService`.

---

## File Structure

- Create `Source/PuddingCore/Tools/ToolApproval.cs`
  - Tool approval enums, ticket request/result records, execution check records, and `IToolApprovalService`.
- Create `Source/PuddingRuntime/Services/Tools/InMemoryToolApprovalService.cs`
  - Approval service, exact argument matching, once/session/timed scope consumption.
- Create `Source/PuddingRuntime/Services/Tools/InMemoryToolApprovalTicketStore.cs`
  - In-memory ticket store behind `IToolApprovalTicketStore`.
- Create `Source/PuddingRuntime/Services/Tools/FakeToolApprovalReviewer.cs`
  - Temporary fake reviewer behind `IToolApprovalReviewer`, default approving submitted tickets.
- Create `Source/PuddingRuntime/Services/Tools/RequestToolApprovalTool.cs`
  - Agent-facing `request_tool_approval` tool.
- Create `Source/PuddingRuntime/Services/Tools/ListToolApprovalsTool.cs`
  - Read-only `list_tool_approvals` tool for approval ticket observability.
- Modify `Source/PuddingRuntime/Services/Tools/PuddingToolRegistry.cs`
  - Add optional `IToolApprovalService` dependency and check it after human `/authorize` fails.
- Modify `Source/PuddingRuntime/Services/Tools/PuddingToolServiceCollectionExtensions.cs`
  - Register `IToolApprovalService`, ticket store, reviewer, request tool, and list tool.
- Modify `Source/PuddingRuntimeTests/Tools/PuddingToolInfrastructureTests.cs`
  - Add focused RED/GREEN tests for hard refusal, ticket approval, exact argument matching, and once consumption.

## Task 1: Contracts And Failing Tests

**Files:**
- Create: `Source/PuddingCore/Tools/ToolApproval.cs`
- Modify: `Source/PuddingRuntimeTests/Tools/PuddingToolInfrastructureTests.cs`

- [ ] **Step 1: Add failing execution-service test for auto approval**

Add a test that constructs an approval service, submits an approved ticket for `sample_high` with exact `{}` arguments, then verifies `PuddingToolExecutionService` allows the high-risk tool without human `/authorize`.

Expected test name:

```csharp
public async Task PuddingToolExecutionService_Allows_High_Tool_With_Auto_Approval()
```

- [ ] **Step 2: Add failing tests for guardrail hard refusal**

Add tests against the approval service:

```csharp
public async Task ToolApprovalService_Denies_Irreversible_Operation_Without_Backup_And_Rollback()
public async Task ToolApprovalService_Denies_Operation_Without_Detailed_Steps()
```

- [ ] **Step 3: Run focused tests and confirm RED**

Run:

```powershell
dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj --filter "FullyQualifiedName~PuddingToolInfrastructureTests"
```

Expected: compile fails because `IToolApprovalService` and related records do not exist yet.

## Task 2: Implement Approval Contracts

**Files:**
- Create: `Source/PuddingCore/Tools/ToolApproval.cs`

- [ ] **Step 1: Define enums**

Include:

```csharp
public enum ToolApprovalDecision { Approved, Denied, NeedHuman }
public enum ToolApprovalTicketStatus { Pending, Approved, Denied, Expired, Consumed }
public enum ToolApprovalScope { Once, Session, Timed }
public enum ToolApprovalUserConsentStatus { Explicit, Implied, Absent, Unknown }
```

- [ ] **Step 2: Define request and step records**

Include structured `ToolApprovalTicketRequest`, `ToolApprovalOperationStep`, `ToolApprovalIdentity`, `ToolApprovalTicketResult`, `ToolApprovalExecutionRequest`, and `ToolApprovalCheckResult`.

- [ ] **Step 3: Define service interface**

```csharp
public interface IToolApprovalService
{
    Task<ToolApprovalTicketResult> SubmitAsync(
        ToolApprovalTicketRequest request,
        ToolApprovalIdentity identity,
        ToolDescriptor descriptor,
        CancellationToken ct = default);

    Task<ToolApprovalCheckResult> CheckAsync(
        ToolApprovalExecutionRequest request,
        ToolDescriptor descriptor,
        CancellationToken ct = default);
}
```

## Task 3: Implement In-Memory Approval Service

**Files:**
- Create: `Source/PuddingRuntime/Services/Tools/InMemoryToolApprovalService.cs`

- [ ] **Step 1: Implement hard guardrail validation**

Reject before approval when:

- tool id is empty or mismatched with descriptor;
- requested scope is invalid;
- fact basis is empty;
- necessity is empty;
- operation context is empty;
- operation steps are empty;
- any operation step lacks command, target object, purpose, expected effect, reasonableness, or stop condition;
- destructive operation lacks explicit consent;
- irreversible operation lacks backup, rollback plan, operation steps, or explicit consent;
- destructive operation lacks rollback plan;
- destructive operation lacks operation steps;
- secret exposure lacks mitigation in risk notes or rollback plan;
- shell approval lacks exact planned arguments.

- [ ] **Step 2: Persist approved tickets in memory**

Store tickets keyed by generated `tap_<guid>` and match candidates by workspace, agent, user, tool, status, expiration, scope, and arguments hash.

- [ ] **Step 3: Implement consumption**

For `Once`, decrement/remove on first successful check and mark consumed.

## Task 4: Integrate Execution Check

**Files:**
- Modify: `Source/PuddingRuntime/Services/Tools/PuddingToolRegistry.cs`

- [ ] **Step 1: Add optional dependency**

Extend `PuddingToolExecutionService` constructor with:

```csharp
IToolApprovalService? approvalService = null
```

- [ ] **Step 2: Check approval after human authorization denial**

If `IToolAuthorizationService.CheckAsync` denies, call approval service with workspace/session/agent/user/tool/arguments hash. If approved, log `AutoApprovalAllowed` and continue. If not approved, return a 403 message mentioning `request_tool_approval` and `/authorize`.

- [ ] **Step 3: Preserve human authorization precedence**

If `/authorize` grants access, do not require auto approval.

## Task 5: Add Agent-Facing Request Tool

**Files:**
- Create: `Source/PuddingRuntime/Services/Tools/RequestToolApprovalTool.cs`
- Modify: `Source/PuddingRuntime/Services/Tools/PuddingToolServiceCollectionExtensions.cs`

- [ ] **Step 1: Implement `request_tool_approval`**

Use `[Tool]` with medium or low risk and no runtime authorization safety flags. Deserialize structured checklist fields, locate target descriptor through `IPuddingToolCatalogService`, and call `IToolApprovalService.SubmitAsync`.

- [ ] **Step 2: Return structured JSON**

Output ticket id, status, decision reason, allowed scope, expiration, and next step.

- [ ] **Step 3: Register service and tool**

Register `IToolApprovalService` and `RequestToolApprovalTool` in tool service collection.

## Task 6: Verify And Review

**Files:**
- Test: `Source/PuddingRuntimeTests/Tools/PuddingToolInfrastructureTests.cs`

- [ ] **Step 1: Run focused tests**

Run:

```powershell
dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj --filter "FullyQualifiedName~PuddingToolInfrastructureTests"
```

Expected: PASS.

- [ ] **Step 2: Run broader runtime tests**

Run:

```powershell
dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj
```

Expected: PASS.

- [ ] **Step 3: Self-review diff**

Check that `/authorize` behavior is unchanged, capability policy still gates first, destructive auto approval refuses hard, and no unrelated dirty files were reverted.

## Task 7: Approval Observability

**Files:**
- Create: `Source/PuddingRuntime/Services/Tools/ListToolApprovalsTool.cs`
- Modify: `Source/PuddingRuntime/Services/Tools/RequestToolApprovalTool.cs`
- Modify: `Source/PuddingRuntime/Services/Tools/PuddingToolServiceCollectionExtensions.cs`
- Test: `Source/PuddingRuntimeTests/Tools/PuddingToolInfrastructureTests.cs`

- [x] **Step 1: Return arguments hash from request tool**

`request_tool_approval` returns `argumentsHash` alongside ticket id, decision, status, scope, expiration, and next step. The hash is safe to expose and lets agents correlate the exact approved arguments without returning raw sensitive arguments.

- [x] **Step 2: Add read-only list tool**

`list_tool_approvals` supports filters for `ticket_id`, `tool_id`, `status`, `workspace_id`, `session_id`, `agent_instance_id`, `user_id`, and `limit`. It lists ticket state, scope, argument hash, identity, timestamps, and decision reason, but does not return raw requested arguments.

- [x] **Step 3: Register list tool**

`AddPuddingToolRegistry` registers `ListToolApprovalsTool` as a default low-risk security/read-only tool.

- [x] **Step 4: Verify**

Run:

```powershell
dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj
dotnet build Source/PuddingRuntime/PuddingRuntime.csproj
```

Expected: PASS, except existing warnings.

## Task 12: Workspace Audit Agent Approval Routing

**Files:**
- Modify: `Source/PuddingRuntime/Services/Tools/LlmToolApprovalReviewer.cs`
- Modify: `Source/PuddingPlatform/Services/WorkspaceAgentFileService.cs`
- Modify: `Source/PuddingAgent/Program.cs`
- Test: `Source/PuddingRuntimeTests/Tools/PuddingToolInfrastructureTests.cs`
- Test: `Source/PuddingPlatformTests/Services/WorkspaceAgentFileServiceTests.cs`

- [x] **Step 1: Add failing resolver tests**

Cover two cases: a workspace audit-agent provider returns no enabled Audit agent, and the approval LLM profile resolver returns null; a provider returns one enabled Audit agent, and the resolver uses that agent instance/template/profile for the approval invocation.

- [x] **Step 2: Add a narrow audit-agent provider contract**

Add `IWorkspaceAuditAgentProvider` and `WorkspaceAuditAgentProfile` to the approval reviewer boundary. This keeps Runtime tests independent from file storage while allowing the host to wire the real workspace agent source.

- [x] **Step 3: Resolve approval through the workspace Audit agent**

`StrictConfiguredToolApprovalLlmProfileResolver` first asks the workspace audit-agent provider for the current `workspaceId`. If the workspace has an enabled Audit agent, it returns that audit agent as the approval caller. If none exists, the client fails closed with `need_human` and the user-actionable reason `当前工作空间不具有审计类型的agent`.

- [x] **Step 4: Implement the file-backed provider source**

`WorkspaceAgentFileService` scans `data/workspaces/{workspaceId}/agents`, loads each enabled instance, resolves its source global template, and returns the first enabled template whose role is `Audit`. The profile uses instance conscious LLM binding first, then source template defaults.

- [x] **Step 5: Enforce one Audit agent per workspace**

`WorkspaceAgentFileService` rejects creating a second Audit agent or updating another workspace agent to an Audit template. The API maps that business conflict to HTTP 409 with the existing Audit agent id, so the frontend can show a precise error.

- [x] **Step 6: Wire the host**

`PuddingAgent` registers `WorkspaceAuditAgentProvider` after `WorkspaceAgentFileService`. Plain Runtime tests and hosts without workspace file services keep the older explicit profile resolver behavior.

- [x] **Step 7: Verify**

Run:

```powershell
dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj --filter "FullyQualifiedName~WorkspaceAuditAgent"
dotnet test Source/PuddingPlatformTests/PuddingPlatformTests.csproj --filter "FullyQualifiedName~WorkspaceAgentFileService"
dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj
dotnet build Source/PuddingRuntime/PuddingRuntime.csproj
dotnet build Source/PuddingAgent/PuddingAgent.csproj -o $env:TEMP\pudding-agent-verify-build
```

Expected: PASS, except existing warnings.

## Task 13: Close Audit-Agent Approval Execution Gap

**Files:**
- Modify: `Source/PuddingAgent/appsettings.json`
- Test: `Source/PuddingRuntimeTests/Tools/PuddingToolInfrastructureTests.cs`

- [x] **Step 1: Add full-chain approval execution test**

Add an integration-style Runtime test that uses the real `request_tool_approval` tool, an LLM reviewer configured through DI, a workspace Audit agent provider, and the real high-risk tool execution service. The test verifies that the approval LLM call uses the Audit agent identity and that the exact approved high-risk arguments are subsequently allowed by the execution engine.

- [x] **Step 2: Enable LLM reviewer in the host config**

Set `ToolApproval:Reviewer` to `llm` in the PuddingAgent host settings so production runtime uses the workspace Audit agent approval path instead of the construction-stage fake reviewer.

- [x] **Step 3: Verify**

Run:

```powershell
dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj --filter "FullyQualifiedName~ToolApprovalService|FullyQualifiedName~RequestToolApprovalTool|FullyQualifiedName~WorkspaceAuditAgent|FullyQualifiedName~InvocationToolApprovalLlmClient|FullyQualifiedName~PuddingToolExecutionService_Allows_High_Tool_With_Auto_Approval|FullyQualifiedName~ServiceCollectionExtension_Uses_Llm_Tool_Approval_Reviewer|FullyQualifiedName~ServiceCollectionExtension_Binds_Tool_Approval_Reviewer"
dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj
dotnet build Source/PuddingAgent/PuddingAgent.csproj -o $env:TEMP\pudding-agent-verify-build
```

Expected: PASS, except existing warnings.

## Task 11: Profile-Backed Approval LLM Resolution

**Files:**
- Modify: `Source/PuddingCore/Abstractions/ILlmConfigService.cs`
- Modify: `Source/PuddingCore/Configuration/PuddingFileLlmConfigService.cs`
- Modify: `Source/PuddingCore/Abstractions/JsonLlmConfigService.cs`
- Modify: `Source/PuddingRuntime/Services/Tools/LlmToolApprovalReviewer.cs`
- Modify: `Source/PuddingRuntime/Services/LlmProfileResolver.cs`
- Test: `Source/PuddingCoreTests/Configuration/PuddingFileLlmConfigServiceTests.cs`
- Test: `Source/PuddingRuntimeTests/Tools/PuddingToolInfrastructureTests.cs`

- [x] **Step 1: Expose profile metadata from the LLM config service**

`ILlmConfigService.ResolveProfile(profileId)` returns provider id, profile id, model id, and the resolved `LlmConfig`. The file-backed implementation resolves only enabled providers and non-deprecated models.

- [x] **Step 2: Resolve approval profiles through the service**

`StrictConfiguredToolApprovalLlmProfileResolver` can now resolve `ToolApproval:Llm:ProfileId` through `ILlmConfigService`. Optional provider/model settings are treated as assertions and must match the resolved profile.

- [x] **Step 3: Preserve fail-closed approval behavior**

If the approval profile is missing, disabled, deprecated, or mismatched, the approval resolver returns no profile. It does not fall back to conscious, subconscious, default provider, or legacy direct model behavior.

- [x] **Step 4: Let runtime invocation use provider/model config**

`PuddingRuntime.Services.LlmProfileResolver` uses `ILlmConfigService` when available so invocation receives endpoint/key/model metadata from managed provider configuration. Legacy direct resolution remains only for non-configured runtime paths.

- [x] **Step 5: Verify**

Run:

```powershell
dotnet test Source/PuddingCoreTests/PuddingCoreTests.csproj --filter "FullyQualifiedName~PuddingFileLlmConfigServiceTests"
dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj --filter "FullyQualifiedName~StrictConfiguredToolApprovalLlmProfileResolver_Resolves_Profile_From_Llm_Config_Service|FullyQualifiedName~RuntimeLlmProfileResolver_Uses_Llm_Config_Service_For_Profile_Config"
dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj
dotnet build Source/PuddingRuntime/PuddingRuntime.csproj
```

Expected: PASS, except existing warnings.

## Task 10: Explicit Reviewer Selection

**Files:**
- Modify: `Source/PuddingRuntime/Services/Tools/LlmToolApprovalReviewer.cs`
- Modify: `Source/PuddingRuntime/Services/Tools/PuddingToolServiceCollectionExtensions.cs`
- Modify: `Source/PuddingRuntime/DependencyInjection.cs`
- Modify: `Source/PuddingAgent/Program.cs`
- Test: `Source/PuddingRuntimeTests/Tools/PuddingToolInfrastructureTests.cs`

- [x] **Step 1: Add reviewer switch**

`ToolApprovalRuntimeOptions` introduces `ToolApproval:Reviewer`. Empty or `fake` keeps the construction-stage fake reviewer; `llm` explicitly selects `LlmToolApprovalReviewer`.

- [x] **Step 2: Reject unknown reviewer values**

Unknown values throw `InvalidOperationException` instead of silently falling back to fake or another model path. This prevents typo-driven safety downgrades.

- [x] **Step 3: Bind configuration explicitly**

`AddPuddingToolRegistry` accepts optional `IConfiguration` and binds `ToolApproval` plus `ToolApproval:Llm`. `PuddingAgent` passes `builder.Configuration`; `AddPuddingRuntime` can also accept and forward configuration.

- [x] **Step 4: Verify**

Run:

```powershell
dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj --filter "FullyQualifiedName~ServiceCollectionExtension_Registers_Fake_Tool_Approval_Reviewer|FullyQualifiedName~ServiceCollectionExtension_Uses_Llm_Tool_Approval_Reviewer|FullyQualifiedName~ServiceCollectionExtension_Binds_Tool_Approval_Reviewer_From_Configuration|FullyQualifiedName~ServiceCollectionExtension_Rejects_Unknown_Tool_Approval_Reviewer_Config"
dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj
dotnet build Source/PuddingRuntime/PuddingRuntime.csproj
```

Expected: PASS, except existing warnings.

## Task 8: Clean Reviewer Skeleton

**Files:**
- Modify: `Source/PuddingCore/Tools/ToolApproval.cs`
- Create: `Source/PuddingRuntime/Services/Tools/ToolApprovalPromptBuilder.cs`
- Create: `Source/PuddingRuntime/Services/Tools/ToolApprovalReviewParser.cs`
- Create: `Source/PuddingRuntime/Services/Tools/LlmToolApprovalReviewer.cs`
- Test: `Source/PuddingRuntimeTests/Tools/PuddingToolInfrastructureTests.cs`

- [x] **Step 1: Extend review result shape**

`ToolApprovalReviewResult` now carries normalized reviewer output: allowed scope/duration, human-authorization requirement, checklist findings, missing requirements, recommended fix, and reviewer model metadata.

- [x] **Step 2: Build clean-room reviewer prompt**

`ToolApprovalPromptBuilder` creates a system prompt and structured JSON user prompt for a single approval review. The prompt explicitly forbids relying on chat history, prior memory, hidden context, or assumptions outside the submitted ticket.

- [x] **Step 3: Parse strict reviewer JSON**

`ToolApprovalReviewParser` parses `approved`, `denied`, and `need_human` decisions. Empty or invalid JSON fails closed to `NeedHuman` with a retry or `/authorize` recommendation.

- [x] **Step 4: Add LLM reviewer wrapper**

`LlmToolApprovalReviewer` depends only on a narrow `IToolApprovalLlmClient`, calls the prompt builder, and parses the response. It is not registered as the default reviewer yet; `FakeToolApprovalReviewer` remains the default permissive TODO implementation until a real clean LLM client is wired.

- [x] **Step 5: Verify**

Run:

```powershell
dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj --filter "FullyQualifiedName~ToolApprovalPromptBuilder|FullyQualifiedName~ToolApprovalReviewParser|FullyQualifiedName~LlmToolApprovalReviewer"
dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj
dotnet build Source/PuddingRuntime/PuddingRuntime.csproj
```

Expected: PASS, except existing warnings.

## Task 9: Explicit Approval LLM Profile

**Files:**
- Modify: `Source/PuddingCore/Tools/ToolApproval.cs`
- Modify: `Source/PuddingRuntime/Services/Tools/LlmToolApprovalReviewer.cs`
- Modify: `Source/PuddingRuntime/Services/Tools/RequestToolApprovalTool.cs`
- Modify: `Source/PuddingRuntime/Services/Tools/PuddingToolServiceCollectionExtensions.cs`
- Test: `Source/PuddingRuntimeTests/Tools/PuddingToolInfrastructureTests.cs`

- [x] **Step 1: Preserve approval identity context**

`ToolApprovalIdentity` now carries optional `AgentTemplateId`, and `request_tool_approval` forwards it from `ToolExecutionContext`. This gives the approval LLM resolver enough execution context without reading chat history.

- [x] **Step 2: Add explicit approval LLM profile contract**

`ToolApprovalLlmProfile`, `IToolApprovalLlmProfileResolver`, and `ToolApprovalLlmOptions` model an explicit `approval` reviewer profile. Missing provider, profile, or model returns no profile.

- [x] **Step 3: Enforce no fallback**

`StrictConfiguredToolApprovalLlmProfileResolver` deliberately does not fall back to conscious, subconscious, or platform defaults. `InvocationToolApprovalLlmClient` returns `need_human` when the approval profile is missing, LLM invocation fails, or the model returns empty output.

- [x] **Step 4: Route real approval review through LLM invocation facade**

`InvocationToolApprovalLlmClient` calls `ILlmInvocationService` with `Role = "approval"` and a clean two-message request. It does not reuse main conversation messages.

- [x] **Step 5: Keep default fake reviewer unchanged**

DI registers the approval LLM profile resolver and invocation client, but `IToolApprovalReviewer` still defaults to `FakeToolApprovalReviewer`. Switching to `LlmToolApprovalReviewer` must be explicit.

- [x] **Step 6: Verify**

Run:

```powershell
dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj --filter "FullyQualifiedName~InvocationToolApprovalLlmClient|FullyQualifiedName~StrictConfiguredToolApprovalLlmProfileResolver|FullyQualifiedName~LlmToolApprovalReviewer"
dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj
dotnet build Source/PuddingRuntime/PuddingRuntime.csproj
```

Expected: PASS, except existing warnings.
