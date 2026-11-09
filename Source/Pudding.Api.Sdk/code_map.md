# Pudding.Api.Sdk

| File | Purpose |
|---|---|
| `PuddingClient.cs` | Workspace/Agent/configuration handles; no host ownership or shadow state. |
| `HttpAgentConfigurationApi.cs` | Authenticated HTTP adapter; no automatic retries; uncertain mutation returns unknown. |
| `AgentWorkClient.cs` | Existing Agent execution facade; bind with `For(AgentRef)` or `Agent.Work(api)`, no hidden host creation. |
| `HttpAgentWorkApi.cs` | Submit/query canonical work over authenticated HTTP; cancellation of observation does not cancel execution. |
| `OperatorsClient.cs` | Authorized operator catalog and typed `SkillValue.ScoreAsync` facade; host supplies the bound API; no HTTP transport or retries. |
| `ModelEvaluationsClient.cs` | Host-bound classification/review facade; callers do not manage parent budgets, model credentials or retries. |
| `ExecutionBudgetClient.cs` | PA-01 read-only budget facade; only reads an authorized account, never tops up or uncancels. |
| `ReviewJobsClient.cs` | PA-03 review-job facade; submit accepts a job, findings are advice and no retry/id synthesis. |
| `ModelCatalogClient.cs` | PA-06 model-catalog facade; authorized capability listing and explicit purpose-model resolution, no provider secret. |
| `AgentDirectoryClient.cs` | PA-02 borrowed-handle directory facade; list/get never create or start an agent. |
| `AgentDelegationClient.cs` | PA-02 delegation facade; parent identity/budget stay host-bound and lease acquire/release only forward. |
