# Plugin Host Tests
> 源指纹: *.cs=1-be4e775aaff5, *.csproj=1-e19a1a4f0542 · 条目数: 2 · 最近整理: 2026-11-09

| File | Responsibility |
|---|---|
| `PluginHostLoopTests.cs` | Real Buffered/Streaming Agent loops consume generation-scoped plugin tools; capability and effect gates use scoped descriptors |
| `../../TestScripts/test-pudding-tool-plugins.ps1` | Reproducible isolated build/test entry point; no production DataRoot or external LLM |
