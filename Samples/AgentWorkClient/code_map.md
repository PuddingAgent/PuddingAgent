# Samples/AgentWorkClient

| File | Purpose |
|---|---|
| `AgentWorkClient.csproj` | net10.0 console sample (TFM inherited from the root build properties); only `ProjectReference` is `Pudding.Api.Sdk`; no packages. |
| `WorkDemo.cs` | Fixed public entry point `RunAsync(args, api, output, error, ct)`: `submit <workspaceId> <agentId> <operationId> <text>` (strict 5 args) or `get <workspaceId> <agentId> <operationId>` (strict 4 args). Argument errors → fixed usage on stderr + exit 2 with zero API calls; success → one camelCase JSON line of the original receipt/snapshot (exit 0); not found → `{"found":false}` (exit 3); `AgentWorkException` → code/effectOutcome/operationId on stderr, `unknown` → exit 4 else exit 1; caller cancellation → exit 130. Exactly one Submit or one Get call; no polling, retry, status query or cancel command. |
| `Program.cs` | Offline shell only: writes the fixed notice “这是可注入 IAgentWorkApi 的离线示例；请由上层提供已授权的 API。” to stderr and returns 2. No credential reading, no online connection, no host wiring. |
| `code_map.md` | This index. |

Registered under `/Samples/` in [the solution](../../PuddingAgentNetwork.slnx); project navigation is in [the root code map](../../code_map.md).
