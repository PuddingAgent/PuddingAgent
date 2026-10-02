# HTTP Fetch Enhancement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enhance `http_fetch` with a thin replaceable web client, Flurl-backed default transport, and context-friendly raw/Markdown/text/JSON output formats.

**Architecture:** `HttpFetchSkill` stays the agent-facing tool and only orchestrates arguments, transport, formatting, and logging. `IWebClient` is the thin transport seam, implemented first by Flurl; `IHttpFetchContentFormatter` owns output selection; `IHtmlToMarkdownConverter` owns replaceable HTML-to-Markdown conversion.

**Tech Stack:** .NET 10, Flurl.Http, ReverseMarkdown, MSTest.

---

### Task 1: RED Tests

**Files:**
- Modify: `Source/PuddingRuntimeTests/Tools/PuddingToolInfrastructureTests.cs`

- [ ] Add tests that execute `HttpFetchSkill` with a fake `IWebClient`.
- [ ] Cover Markdown extraction from HTML, plain text extraction, raw output preservation, JSON output metadata, unsupported URL scheme, and schema exposure for new parameters.
- [ ] Run `dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj --filter "FullyQualifiedName~PuddingToolInfrastructureTests"` and confirm compile/test failure because the new interfaces and parameters do not exist yet.

### Task 2: Core HTTP Fetch Types

**Files:**
- Create: `Source/PuddingRuntime/Tools/BuiltIns/Http/HttpFetchContracts.cs`
- Create: `Source/PuddingRuntime/Tools/BuiltIns/Http/HttpFetchContentFormatter.cs`
- Create: `Source/PuddingRuntime/Tools/BuiltIns/Http/HtmlContentExtractor.cs`
- Modify: `Source/PuddingRuntime/Tools/BuiltIns/Http/HttpFetchSkill.cs`

- [ ] Add `IWebClient`, `WebClientRequest`, and `WebClientResponse`.
- [ ] Add `IHttpFetchContentFormatter` and default formatter for `raw`, `markdown`, `text`, and `json`.
- [ ] Add `IHtmlToMarkdownConverter` and a lightweight HTML cleanup/extraction helper.
- [ ] Refactor `HttpFetchSkill` to use `IWebClient` and `IHttpFetchContentFormatter`.

### Task 3: Flurl And ReverseMarkdown Implementations

**Files:**
- Create: `Source/PuddingRuntime/Tools/BuiltIns/Http/FlurlWebClient.cs`
- Create: `Source/PuddingRuntime/Tools/BuiltIns/Http/ReverseMarkdownHtmlToMarkdownConverter.cs`
- Modify: `Source/PuddingRuntime/PuddingRuntime.csproj`
- Modify: `Source/PuddingRuntime/Tools/Platform/PuddingToolServiceCollectionExtensions.cs`
- Modify: `Source/PuddingAgent/Program.cs`

- [ ] Add `Flurl.Http` and `ReverseMarkdown` package references to `PuddingRuntime`.
- [ ] Register `IFlurlClientCache`, `IWebClient`, `IHttpFetchContentFormatter`, and `IHtmlToMarkdownConverter`.
- [ ] Keep the existing `HttpFetchSkill` registration path working through assembly scanning and explicit registration.

### Task 4: Tool Schema And Capability Compatibility

**Files:**
- Modify: `Source/PuddingRuntime/Tools/BuiltIns/Http/HttpFetchSkill.cs`
- Modify: `Source/PuddingRuntime/Tools/Platform/PuddingToolRegistry.cs`
- Modify: `Source/PuddingRuntimeTests/Tools/PuddingToolInfrastructureTests.cs`

- [ ] Add `headers`, `timeout_seconds`, `output_format`, `max_response_chars`, and `include_headers` parameters.
- [ ] Preserve `url`, `method`, `body`, and `content_type`.
- [ ] Update legacy known schema expectations so older adapter paths expose the new contract.

### Task 5: Verification

**Commands:**
- `dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj --filter "FullyQualifiedName~PuddingToolInfrastructureTests"`
- `dotnet build PuddingAgentNetwork.slnx`

- [ ] Confirm targeted tests pass.
- [ ] Confirm solution build passes or document any unrelated pre-existing failure.
- [ ] Review diff for unrelated changes and remove temporary artifacts.
