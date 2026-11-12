# Tests/CapabilityCatalogClient.Tests

| File | Purpose |
|---|---|
| `CapabilityCatalogClient.Tests.csproj` | MSTest.Sdk/4.0.1 runner (`OutputType=Exe`, `UseVSTest=false`); only `ProjectReference` is the sample project; every API is an injected fake `ICapabilityCatalogApi`, no real network, no tool invocation, no MCP session. |
| `CatalogDemoTests.cs` | Offline coverage for `CatalogDemo`: exactly one `ListAsync` with `CapabilityQuery(kind, cursor, 50)` and no automatic paging; verbatim kind/cursor (Chinese cursor and surrounding spaces); partial/empty pages exit 0 while preserving `partial`/`missingReason` without writing "没有能力"; Forbidden pages keep `partial`/`missingReason` instead of a fake complete empty list; older `generation` values preserved on the page and entries; zero API calls for no/unknown command, wrong argument count, invalid (case-sensitive) kind and blank kind/cursor (exit 2); null page → `code=capability.source_unavailable` with exit 1; `CapabilityCatalogException` reduced to `code=...` on stderr with exit 1 and no message leak; caller cancellation → exit 130 with no retry or second page; cancellation not requested by the caller and unexpected exceptions are not swallowed; stdout exactly one camelCase JSON line with every page and entry field; null reference arguments rejected with `ArgumentNullException`. |
| `code_map.md` | This index. |
