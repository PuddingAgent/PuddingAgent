# File Search Provider Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend `file_search` with explicit file search providers, including `BuiltInRecursiveFileSearch` and an Everything SDK provider.

**Architecture:** Keep `file_search` as the only agent-facing tool. Move search behavior behind provider classes, let the tool validate parameters and directories, and let providers execute only within the requested directory scope. Everything uses a local P/Invoke wrapper for `Everything64.dll`, with no NuGet dependency.

**Tech Stack:** .NET 10, MSTest, Pudding native tool SDK, voidtools Everything SDK `Everything64.dll`.

---

## File Structure

- Modify: `Source/PuddingRuntime/Services/Tools/FileTools.cs`
  - Add provider args `Action` and `Provider`.
  - Add provider abstractions and provider implementations.
  - Update `FileSearchTool` to list providers, require provider on search, validate directories, and append out-of-workspace warnings.
- Modify: `Source/PuddingRuntime/Services/Tools/PuddingToolServiceCollectionExtensions.cs`
  - Register file search providers and Everything SDK wrapper in DI.
- Modify: `Source/PuddingRuntime/PuddingRuntime.csproj`
  - Copy `runtimes/win-x64/native/Everything64.dll` to output.
- Add: `Source/PuddingRuntime/runtimes/win-x64/native/Everything64.dll`
  - Official voidtools x64 SDK DLL.
- Modify: `Source/PuddingRuntimeTests/Tools/PuddingToolInfrastructureTests.cs`
  - Add focused tests for provider list, required provider, directory validation, workspace warning, built-in provider behavior, provider exceptions, and Everything directory filtering.

## Task 1: Provider Contract And Tool Tests

**Files:**
- Modify: `Source/PuddingRuntimeTests/Tools/PuddingToolInfrastructureTests.cs`
- Modify later: `Source/PuddingRuntime/Services/Tools/FileTools.cs`

- [ ] **Step 1: Write failing tests for tool-facing behavior**

Add tests that assert:

- `FileSearchTool` descriptor includes `action` and `provider`.
- `{"action":"list"}` lists `Everything` and `BuiltInRecursiveFileSearch`.
- Search without `provider` fails and contains a `BuiltInRecursiveFileSearch` example.
- Missing `Directory` uses current workspace root, but missing provider still fails.
- Nonexistent directory fails before provider execution.
- Existing out-of-workspace directory is allowed and warning text is included.

- [ ] **Step 2: Run tests to verify they fail**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~PuddingToolInfrastructureTests"
```

Expected: new tests fail because `FileSearchTool` has no provider abstraction and constructor shape is unchanged.

- [ ] **Step 3: Implement provider abstractions and update tool**

In `FileTools.cs`, add:

- `FileSearchAction`
- `FileSearchProviderIds`
- `IFileSearchProvider`
- `FileSearchProviderKind`
- `FileSearchProviderRequest`
- `FileSearchProviderItem`
- `FileSearchProviderResult`
- `FileSearchProviderStatus`
- `BuiltInRecursiveFileSearchProvider`

Update `FileSearchTool` to:

- Accept `IEnumerable<IFileSearchProvider>` with a fallback constructor for tests.
- `Action=list` returns provider status.
- `Action=search` requires `Provider`.
- Resolve directory with `Path.GetFullPath`.
- Reject null characters and invalid paths.
- Check `Directory.Exists(...)` before provider execution.
- Allow out-of-workspace directories.
- Prefix warning text for out-of-workspace results and failures.

- [ ] **Step 4: Run tests to verify green for built-in behavior**

Run the same focused test command. Expected: provider list and built-in provider tests pass.

## Task 2: Everything SDK Provider

**Files:**
- Modify: `Source/PuddingRuntime/Services/Tools/FileTools.cs`
- Modify: `Source/PuddingRuntimeTests/Tools/PuddingToolInfrastructureTests.cs`

- [ ] **Step 1: Write failing tests for Everything provider filtering**

Add fake SDK tests:

- Fake SDK returns files both inside and outside requested directory; provider returns only inside.
- `Recursive=false` returns only files whose parent directory equals requested directory.
- SDK unavailable returns provider unavailable fail.
- SDK exception returns provider fail.

- [ ] **Step 2: Run tests to verify they fail**

Run:

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~PuddingToolInfrastructureTests"
```

Expected: tests fail because Everything provider and SDK wrapper do not exist.

- [ ] **Step 3: Implement Everything SDK wrapper and provider**

In `FileTools.cs`, add:

- `IEverythingSdk`
- `EverythingQueryRequest`
- `EverythingQueryResult`
- `EverythingQueryItem`
- `EverythingSdk`
- `EverythingFileSearchProvider`

Wrapper behavior:

- Use official `DllImport("Everything64.dll")` function declarations.
- Use request flags for path, file name, size, and modified date where practical.
- Convert `DllNotFoundException`, `BadImageFormatException`, `EntryPointNotFoundException`, and SDK last errors into readable failures.
- Serialize SDK calls using `SemaphoreSlim`.

Provider behavior:

- Construct an Everything query scoped by directory and pattern.
- Execute query.
- Apply final in-process filtering so results must be inside `RootDirectory`.
- Apply non-recursive parent-directory filtering.
- Apply built-in pattern matching semantics.

- [ ] **Step 4: Run tests to verify green for Everything provider**

Run the focused test command. Expected: fake SDK tests pass without requiring Everything installed.

## Task 3: DI And Runtime DLL

**Files:**
- Modify: `Source/PuddingRuntime/Services/Tools/PuddingToolServiceCollectionExtensions.cs`
- Modify: `Source/PuddingRuntime/PuddingRuntime.csproj`
- Add: `Source/PuddingRuntime/runtimes/win-x64/native/Everything64.dll`

- [ ] **Step 1: Register providers in DI**

Register:

- `IFileSearchProvider, BuiltInRecursiveFileSearchProvider`
- `IFileSearchProvider, EverythingFileSearchProvider`
- `IEverythingSdk, EverythingSdk`

- [ ] **Step 2: Add official SDK DLL**

Download official Everything SDK zip from voidtools and copy only `Everything64.dll` into:

```text
Source/PuddingRuntime/runtimes/win-x64/native/Everything64.dll
```

- [ ] **Step 3: Copy DLL to output**

Update `PuddingRuntime.csproj`:

```xml
<ItemGroup>
  <None Include="runtimes\win-x64\native\Everything64.dll" CopyToOutputDirectory="Always">
    <TargetPath>Everything64.dll</TargetPath>
  </None>
</ItemGroup>
```

- [ ] **Step 4: Verify build copies DLL**

Run:

```powershell
dotnet build Source\PuddingRuntime\PuddingRuntime.csproj
```

Expected: build succeeds and output contains `Everything64.dll`.

## Task 4: Verification And Docs

**Files:**
- Modify if needed: `Docs/07架构/tool-infrastructure-layering.md`
- Existing spec: `Docs/superpowers/specs/2026-06-03-file-search-provider-design.md`

- [ ] **Step 1: Run focused tests**

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~PuddingToolInfrastructureTests"
```

- [ ] **Step 2: Run broader runtime tests if focused tests pass**

```powershell
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj
```

- [ ] **Step 3: Review diff**

```powershell
git diff -- Source\PuddingRuntime Source\PuddingRuntimeTests Docs\superpowers
```

Confirm only intended files changed.
