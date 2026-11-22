# Plugin Runtime Tests
> 源指纹: *.cs=4-ca0c03067c9d, *.csproj=1-42d206944d5d · 条目数: 5 · 最近整理: 2026-11-10

| File | Responsibility |
|---|---|
| `HostToolMountTests.cs` | Service-backed plugin revocation, drain, cross-session rejection and idempotent reload boundaries |
| `PluginRuntimeTests.cs` | Installation/path/schema boundaries, scope/generation, load/unload race, advice failure and Core service quotas |
| `AgentCallbackLifecycleTests.cs` | Core API versions, frozen grants, per-tool effects, callback drain, uncertain stops and capacity retention |
| `OperatorManifestTests.cs` | Deterministic capabilities require Core API 1.2; bounded model evaluation requires 1.3; arbitrary model, override, work and execution creation remain rejected |
| `../../Tests/PuddingToolPlugins.IntegrationTests/RealWorkerTests.cs` | Separate real DLL/Windows supervisor acceptance with crash, timeout, output flood and child-tree fixtures |
