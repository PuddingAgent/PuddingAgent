# Plugin Agent API Integration Tests
> 源指纹: *.cs=1-40b11a77e286, *.csproj=1-396bfc5ac950 · 条目数: 2 · 最近整理: 2026-11-09

| File | Responsibility |
|---|---|
| `PluginAgentApiTests.cs` | Real Worker + canonical manifest writer: configuration reads, updates, change feed, CAS, idempotency across reload, scope/write-effect denial and stale background callback rejection |
| `../../TestScripts/test-plugin-agent-api.ps1` | Isolated component/SDK/runtime and real Windows Worker acceptance; no product Core or production data |
