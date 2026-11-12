# Model evaluation integration

| File | Responsibility |
|---|---|
| `ModelWorkerTests.cs` | Real Worker classification/review, scoped provenance, parent allowance across reloads, cancellation/unload with late provider, unknown usage and denied capabilities |
| `ConfiguredModelTests.cs` | Real gateway over offline HTTP handler: bounded request, frozen prices, exact accounting, no retry, shared primary model gate and explicit unavailable configuration |
| `PuddingModelEvaluation.IntegrationTests.csproj` | Runtime and real fixture integration; no live product or credentials |
