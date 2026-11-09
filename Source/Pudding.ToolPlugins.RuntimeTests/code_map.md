# Plugin Runtime Tests

| File | Responsibility |
|---|---|
| `PluginRuntimeTests.cs` | Installation/path/schema boundaries, scope/generation, load/unload race, advice failure and Core service quotas |
| `AgentCallbackLifecycleTests.cs` | Core API versions, frozen grants, per-tool effects, callback drain, uncertain stops and capacity retention |
| `OperatorManifestTests.cs` | Deterministic capabilities require Core API 1.2; bounded model evaluation requires 1.3; arbitrary model, override, work and execution creation remain rejected |
| `../../Tests/PuddingToolPlugins.IntegrationTests/RealWorkerTests.cs` | Separate real DLL/Windows supervisor acceptance with crash, timeout, output flood and child-tree fixtures |
