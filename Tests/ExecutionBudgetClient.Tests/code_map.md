# Tests/ExecutionBudgetClient.Tests

| File | Purpose |
|---|---|
| `ExecutionBudgetClient.Tests.csproj` | MSTest.Sdk/4.0.1 runner (`OutputType=Exe`, `UseVSTest=false`); only `ProjectReference` is the sample project; every API is an injected fake `IExecutionBudgetApi`, no real network, no real Core, no real Agent. |
| `BudgetDemoTests.cs` | Offline coverage for `BudgetDemo`: exactly one `GetAsync(accountId)`; the original snapshot is printed verbatim in one camelCase JSON line with every `accountId`/`revision`/`limits`/`actual`/`reserved`/`unknown`/`activeAttempts`/`cancelled`/`cancellationRevision`/`reasonCode` field (Unknown amounts stay nonzero, exhausted and cancelled snapshots stay exit 0); verbatim Chinese/space-padded account id; zero API calls for no/unknown command, wrong argument count and blank account id (exit 2); missing snapshot → `{"found":false}` with exit 3; `ExecutionBudgetException` reduced to `code=...` on stderr with exit 1 and no message leak; caller cancellation → exit 130 with no retry or second read; cancellation not requested by the caller and unexpected exceptions are not swallowed; null reference arguments rejected with `ArgumentNullException`. |
| `code_map.md` | This index. |
