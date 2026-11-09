# Plugin Agent SDK

| File | Responsibility |
|---|---|
| `PluginAgentConfiguration.cs` | `Context.AgentConfiguration()` facade; typed public contracts over current invocation transport; no credentials or target selection; unknown writes are not retried |
| `PluginOperators.cs` | `Context.Operators()` facade; scoped catalog/scoring callbacks, typed failures and receipt validation; unavailable scores cannot become placeholder zero |
| `PluginModelEvaluations.cs` | `Context.ModelEvaluations()` Core API 1.3 facade; scope-free requests, parent/model receipts, evidence consistency and no retries |
| `PluginEvents.cs` | PA-07A frozen plugin event observation contract: validated `PluginEventEnvelope` with optional `review.completed` payload, host-bound `IPluginEventContext`, scoped idempotent `IPluginAuditApi` (`OperationId=EventId`) and the `IPluginEventObserver.HandleAsync` callback shape; no host services exposed || `PluginJobs.cs` | PA-07 07B frozen plugin job contract: host-bound `PluginJobEnvelope` (generation/idempotency key/deadline/attempts), explicit `PluginJobState` result (Unknown is a third outcome, never success/failure), `IPluginJobHandler`; no host services |
| `PluginExtensions.cs` | PA-07 07B frozen extension declarations/handlers: Observer/Advice/Transform/Guard, bounded declaration, transform/guard result forms; host enforces grant/generation/protected fields |
| `Pudding.ToolPlugins.AgentSdk.csproj` | Compile-time reference whitelist: plugin contracts and public SDK/contracts only |
