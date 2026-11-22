# Model evaluation integration
> 源指纹: *.cs=2-266dbc99d6c2, *.csproj=1-c25cac1f039a · 条目数: 3 · 最近整理: 2026-11-12

| File | Responsibility |
|---|---|
| `ModelWorkerTests.cs` | Real Worker classification/review, scoped provenance, parent allowance across reloads, cancellation/unload with late provider, unknown usage and denied capabilities |
| `ConfiguredModelTests.cs` | Real gateway over offline HTTP handler: bounded request, frozen prices, exact accounting, no retry, shared primary model gate and explicit unavailable configuration |
| `PuddingModelEvaluation.IntegrationTests.csproj` | Runtime and real fixture integration; no live product or credentials |
