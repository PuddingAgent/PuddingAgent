# Plugin Agent API Integration Tests

| File | Responsibility |
|---|---|
| `PluginAgentApiTests.cs` | Real Worker + canonical manifest writer: configuration reads, updates, change feed, CAS, idempotency across reload, scope/write-effect denial and stale background callback rejection |
| `../../TestScripts/test-plugin-agent-api.ps1` | Isolated component/SDK/runtime and real Windows Worker acceptance; no product Core or production data |
