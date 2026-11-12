# Tests/AgentDirectoryClient.Tests

| File | Purpose |
|---|---|
| `AgentDirectoryClient.Tests.csproj` | MSTest.Sdk/4.0.1 runner (`OutputType=Exe`, `UseVSTest=false`); only `ProjectReference` is the sample project; every API is an injected fake `IAgentDirectoryApi`, no real network, no real Core, no real Agent. |
| `DirectoryDemoTests.cs` | Offline coverage for `DirectoryDemo`: exactly one mutually exclusive `Get`/`List` call with the frozen `MaxCount=50` and verbatim Chinese/space-padded workspace, agent and cursor (cross-workspace ids not mixed); zero API calls for no/unknown command, wrong argument count and blank fields; missing entry → `{"found":false}` with exit 3; empty page preserved as `{"agents":[],"cursor":null,"complete":true}` with exit 0; `AgentDirectoryException` reduced to `code=...` on stderr with exit 1 and no message leak; caller cancellation → exit 130 with no retry or second query; cancellation not requested by the caller and unexpected exceptions are not swallowed; stdout exactly one camelCase JSON line; null reference arguments rejected with `ArgumentNullException`. |
| `code_map.md` | This index. |
