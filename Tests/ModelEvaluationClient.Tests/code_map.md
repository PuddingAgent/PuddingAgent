# Tests/ModelEvaluationClient.Tests

| File | Purpose |
|---|---|
| `ModelEvaluationClient.Tests.csproj` | MSTest.Sdk/4.0.1 runner (`OutputType=Exe`, `UseVSTest=false`); only `ProjectReference` is the sample project; every API is an injected fake `IModelEvaluationApi`, no real network, no real Core, no real Agent. |
| `EvaluationDemoTests.cs` | Offline coverage for `EvaluationDemo`: exact-once and mutually exclusive Classify/Review with zero List calls; verbatim Chinese text and options (no trimming, contract defaults preserved); zero API calls for no/unknown command, wrong argument count, blank argument, invalid JSON, `null` request and missing `input`/`options`; unknown JSON members rejected through the frozen contract; Abstain/Degraded/Reject receipts preserved as one camelCase line with exit 0 and no invented `verified` field; full provenance field set; `ModelEvaluationException` reduced to `code=...` on stderr with exit 1 and no message leak; caller cancellation (exit 130, no extra calls) and non-requested cancellation not swallowed; stdout exactly one JSON line; null reference arguments rejected. |
