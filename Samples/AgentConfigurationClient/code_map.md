# Samples/AgentConfigurationClient

| File | Purpose |
|---|---|
| `AgentConfigurationClient.csproj` | net10.0 console sample; only `ProjectReference` is `Pudding.Api.Sdk`; no packages; exposes internals to the test project only. |
| `Program.cs` | Process shell: `--base-url/--credential-file/--workspace/--agent` parsing (exit 2), base-URL allow rules, credential-file loading (never echoed), Bearer `HttpClient` with 15 s timeout and Ctrl+C cancellation. |
| `ConfigurationInspector.cs` | Fixed read-only entry point: SDK facade → one JSON line on stdout (workspaceId/agentId/revision/displayName/description/templateId); `ConfigurationApiException` → code on stderr (exit 1); cancellation → exit 130; never calls Update/ReadChanges. |
| `README.md` | Build/run, parameter and credential-placeholder format, base-URL rules, exit codes and the read-only boundary. |

Index registration in the solution and root `code_map.md` is owned by the main agent.
