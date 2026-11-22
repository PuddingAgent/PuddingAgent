# Operator API Integration Tests
> 源指纹: *.cs=2-8f36ab99e5e8, *.csproj=1-8dd8410b917a · 条目数: 3 · 最近整理: 2026-11-12

| File | Responsibility |
|---|---|
| `OperatorAdapterTests.cs` | Original scorer equivalence, no-data versus real zero, invalid or overflowing observations |
| `OperatorWorkerTests.cs` | Actual Worker → Runtime broker → independent grant → original scorer; capability denial, scope injection, unload and stale tool rejection; scoring failure cannot certify earlier tool writes as absent |
| `../../TestScripts/test-operator-api.ps1` | Isolated build/test runner; same Windows apphost launch form for controller and Worker; no live product or LLM |
