# Samples/AgentModelPolicyClient

| File | Purpose |
|---|---|
| `AgentModelPolicyClient.csproj` | net10.0 console sample (TFM inherited from the root build properties); only `ProjectReference` is `Pudding.Api.Sdk`; no packages. |
| `PolicyDemo.cs` | Fixed public entry point `RunAsync(args, api, output, error, ct)`: only `get <workspaceId> <agentId>` (strict 3 tokens). Strings are passed verbatim to exactly one `IAgentModelPolicyApi.GetEffectiveAsync(new AgentRef(workspaceId, agentId))` call. Argument, unknown-command, wrong-count and blank errors → fixed usage on stderr + exit 2 with zero API calls; success → one camelCase JSON line of the original `EffectiveAgentModelPolicy` (all `purposes` fields `purpose`/`modelId`/`modelRevision`/`source`/`sourceRevision`/`missingReason`/`inheritPrimary` and all `workspaceId`/`agentId`/`effectiveRevision`/`appliedRevision`/`policyGeneration`/`resolvedAt`/`pendingReasons` fields preserved verbatim, exit 0); missing snapshot → `{"found":false}` and exit 3; `AgentModelPolicyException` → only `code={Code}` on stderr, exit 1; caller cancellation → exit 130 with no further calls; every other exception propagates. Never calls `UpdateAsync`, reads configuration files or environment variables, sends a model request, invents a default model or computes a price. |
| `Program.cs` | Offline shell only: writes the fixed notice “这是可注入 IAgentModelPolicyApi 的离线示例；请由上层提供已授权的 API。” to stderr and returns 2. No credential reading, no online connection, no host wiring. |
| `code_map.md` | This index. |
