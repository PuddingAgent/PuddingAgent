# Operator API Integration Tests

| File | Responsibility |
|---|---|
| `OperatorAdapterTests.cs` | Original scorer equivalence, no-data versus real zero, invalid or overflowing observations |
| `OperatorWorkerTests.cs` | Actual Worker → Runtime broker → independent grant → original scorer; capability denial, scope injection, unload and stale tool rejection; scoring failure cannot certify earlier tool writes as absent |
| `../../TestScripts/test-operator-api.ps1` | Isolated build/test runner; same Windows apphost launch form for controller and Worker; no live product or LLM |
