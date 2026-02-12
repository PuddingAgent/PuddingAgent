# LLM Gateway Unification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Route Runtime direct LLM sync and stream calls through the same OpenAI-compatible gateway path with traceable activity records.

**Architecture:** `DirectLlmClient` remains the Runtime-facing adapter, but it stops constructing its own Chat Completions JSON for non-streaming calls. Both `ChatAsync` and `ChatStreamAsync` resolve config once, create `OpenAiLlmGateway` with the named `DirectLlm` `HttpClient`, convert tool definitions through the same proxy adapter, and record runtime activities around provider execution.

**Tech Stack:** .NET 10, `IHttpClientFactory`, `OpenAiLlmGateway`, Pudding runtime observability contracts.

---

### Task 1: Normalize Direct LLM Gateway Creation

**Files:**
- Modify: `Source/PuddingRuntime/Services/DirectLlmClient.cs`

- [ ] **Step 1: Replace duplicate endpoint/api key/model resolution**

Create a private `ResolvedGatewayConfig` record and `ResolveGatewayConfigAsync` helper. The helper resolves KeyVault-backed request config first, then falls back to `ILlmConfigService.GetDefault()`, then environment config. It must preserve `ReasoningEffort` and JSON `ThinkingMode`.

- [ ] **Step 2: Share gateway and tool conversion**

Add `CreateGateway(ResolvedGatewayConfig config)` and `ToToolSpecs(IReadOnlyList<LlmToolDefinition>? tools)` helpers. Both sync and stream paths must call these helpers.

- [ ] **Step 3: Remove manual non-streaming JSON construction**

Delete the local `BuildRequestBody` and `SerializeParameters` helpers from `DirectLlmClient`. `OpenAiLlmGateway.BuildRequestBody` becomes the only direct Runtime serializer for OpenAI-compatible chat calls.

### Task 2: Add Runtime Activity Records

**Files:**
- Modify: `Source/PuddingRuntime/Services/DirectLlmClient.cs`

- [ ] **Step 1: Inject optional observability dependencies**

Add optional constructor dependencies for `IRuntimeActivitySink` and `IRuntimeTraceAccessor`. The constructor remains DI-compatible when a host has not registered observability services.

- [ ] **Step 2: Record sync call lifecycle**

`ChatAsync` records `llm_gateway/chat` `started`, `succeeded`, and `failed` activities. Metadata includes `model`, `endpoint`, `message_count`, `tool_count`, `agent_template_id`, and token usage on success.

- [ ] **Step 3: Record stream call lifecycle**

`ChatStreamAsync` records `llm_gateway/chat_stream` `started` before enumeration and `succeeded` after enumeration completes. Log provider failures and let exceptions propagate.

### Task 3: Verify and Commit

**Files:**
- Modify: `Source/PuddingRuntime/Services/DirectLlmClient.cs`
- Modify: `Docs/superpowers/plans/2026-05-18-llm-gateway-unification.md`

- [ ] **Step 1: Build Runtime**

Run: `dotnet build Source\PuddingRuntime\PuddingRuntime.csproj --no-restore --nologo`

- [ ] **Step 2: Build Agent host**

Run: `dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo`

- [ ] **Step 3: Commit plan and implementation**

Run: `git add Docs/superpowers/plans/2026-05-18-llm-gateway-unification.md Source/PuddingRuntime/Services/DirectLlmClient.cs`

Run: `git commit -m "refactor: unify direct llm gateway paths"`
