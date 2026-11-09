# Tests/AgentWorkClient.Tests

| File | Purpose |
|---|---|
| `AgentWorkClient.Tests.csproj` | MSTest.Sdk/4.0.1 runner; only `ProjectReference` is the sample project; every API is an injected fake `IAgentWorkApi`, no real network, no real Core, no real Agent. |
| `WorkDemoTests.cs` | Offline coverage for `WorkDemo`: exact-once Submit/Get, verbatim arguments (Chinese and leading/trailing spaces), zero API calls for every argument error, `unknown` effect (exit 4, no retry, no sensitive text), non-unknown failure (exit 1), not-found `{"found":false}` (exit 3), Failed/Cancelled and empty-reply queries still exit 0 without rewriting status, caller cancellation (exit 130, no extra calls) and non-requested cancellation not swallowed. |
