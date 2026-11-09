# Plugin Agent SDK Tests

| File | Responsibility |
|---|---|
| `AgentSdkTests.cs` | Scope-free wire arguments, typed facade, foreign receipts, denial and lost-write-response semantics |
| `PluginOperatorsTests.cs` | Scope-free scoring arguments, malformed catalog/results, placeholder zero, denial and cancellation without retries |
| `PluginModelEvaluationTests.cs` | Scope/call/usage corruption rejection, evidence-free accept rejection, abstention, typed budget failure and pre-cancellation |
| `PluginEventObserverTests.cs` | PA-07A frozen envelope/audit contract: review payload validity, stable `OperationId=EventId`, replayed receipt semantics || `PluginJobContractTests.cs` | PA-07 07B job envelope bounds and explicit result states (Failed needs code, Completed forbids it, Unknown is neither) |
| `PluginExtensionContractTests.cs` | PA-07 07B declaration bounds and transform/guard result forms |
