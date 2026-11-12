# Samples/AgentDirectoryClient

| File | Purpose |
|---|---|
| `AgentDirectoryClient.csproj` | net10.0 console sample (TFM inherited from the root build properties); only `ProjectReference` is `Pudding.Api.Sdk`; no packages. |
| `DirectoryDemo.cs` | Fixed public entry point `RunAsync(args, api, output, error, ct)`: `get <workspaceId> <agentId>` (strict 3 tokens) or `list <workspaceId> [cursor]` (2 or 3 tokens). Strings are passed verbatim to exactly one `IAgentDirectoryApi` call — `GetAsync(new AgentRef(workspaceId, agentId))` or `ListAsync(new AgentListQuery(workspaceId, 50, cursor))` with the absent cursor kept `null`. Argument, unknown-command, wrong-count and blank errors → fixed usage on stderr + exit 2 with zero API calls; success → one camelCase JSON line of the original DTO (exit 0; `list` keeps `agents`/`cursor`/`complete`, and an empty page with `complete=true` is still exit 0); missing entry → `{"found":false}` and exit 3; `AgentDirectoryException` → only `code={Code}` on stderr, exit 1; caller cancellation → exit 130 with no further calls; every other exception propagates. No paging, implicit second query, retry, create, archive, cancel or destroy. |
| `Program.cs` | Offline shell only: writes the fixed notice “这是可注入 IAgentDirectoryApi 的离线示例；请由上层提供已授权的 API。” to stderr and returns 2. No credential reading, no online connection, no host wiring. |
| `code_map.md` | This index. |
