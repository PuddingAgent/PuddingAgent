# Samples/ExecutionBudgetClient

| File | Purpose |
|---|---|
| `ExecutionBudgetClient.csproj` | net10.0 console sample (TFM inherited from the root build properties); only `ProjectReference` is `Pudding.Api.Sdk`; no packages. |
| `BudgetDemo.cs` | Fixed public entry point `RunAsync(args, api, output, error, ct)`: only `get <accountId>` (strict 2 tokens). The account id is passed verbatim to exactly one `IExecutionBudgetApi.GetAsync(accountId, ct)` call. Argument, unknown-command, wrong-count and blank errors → fixed usage on stderr + exit 2 with zero API calls; success → one camelCase JSON line of the original `ExecutionBudgetSnapshot` with every `accountId`/`revision`/`limits`/`actual`/`reserved`/`unknown`/`activeAttempts`/`cancelled`/`cancellationRevision`/`reasonCode` field preserved verbatim (exit 0 even when the budget is exhausted, cancelled or contains unknown amounts); missing snapshot → `{"found":false}` and exit 3; `ExecutionBudgetException` → only `code={Code}` on stderr, exit 1; caller cancellation → exit 130 with no further calls; every other exception propagates. Never implements a ledger, budget calculation, derived available balance, polling, retry or cancel, and never turns Unknown into zero. |
| `Program.cs` | Offline shell only: writes the fixed notice “这是可注入 IExecutionBudgetApi 的离线示例；请由上层提供已授权的 API。” to stderr and returns 2. No credential reading, no online connection, no host wiring. |
| `code_map.md` | This index. |
