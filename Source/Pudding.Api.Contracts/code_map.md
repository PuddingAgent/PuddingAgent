# Pudding.Api.Contracts

| File | Purpose |
|---|---|
| `AgentConfiguration.cs` | Public configuration snapshots, patches, receipts, errors and bound API; BCL-only. |
| `AgentWork.cs` | One-turn submission, stable operation receipts and exact terminal evidence; completion is not Goal verification. |
| `OperatorEvaluation.cs` | Bound scoring API, typed skill observations, availability, nullable score and original provenance; caller-supplied input is not certified telemetry. |
| `ModelEvaluation.cs` | Bound classification and evidence review API; abstention, degraded answers, evidence checks, model/parent provenance and actual usage; no authority to end a Goal or grant approval. |
| `PluginEvents.cs` | PA-07A plugin-facing subscription/read/ack DTOs and `IPluginEventsApi`; owner/generation/grant/cursor stay host-bound, deliveries are already authorized and filtered. |
| `PluginJobs.cs` | PA-07A plugin-facing job submit/get/cancel DTOs and `IPluginJobsApi`; a ticket means accepted only, completion is queried separately, and owner/budget/deadline are host-bound. |
| `AgentDirectory.cs` | PA-02A borrowed-handle directory/factory DTOs and APIs; Workspace/actor are bound by the host, pagination carries Cursor/Complete, and create/archive use OperationId idempotency. |
| `AgentWorkControl.cs` | PA-02A observation and cancel DTOs; Observe binds an exact operation id and event cursor, cancel is a persistent command whose Requested effect is not termination. |
| `AgentDelegation.cs` | PA-02A delegation and lease DTOs; parent identity and budgets come from the bound context, child limits may only tighten, and leases are atomic acquisition receipts. |
| `ReviewJobs.cs` | PA-03 review job DTOs and `IReviewJobsApi`; a submit returns an accepted job (not a model result), the logical unique key freezes objective/acceptance/artifact/policy versions, and the finding is advice only. |
| `AgentModelPolicy.cs` | PA-06A effective model policy DTOs and `IAgentModelPolicyApi`; every field carries its source/revision, Clear removes an instance override, and write receipts separate persisted from applied revisions. |
| `ModelCapabilities.cs` | PA-06B model catalog DTOs and `IModelCatalogApi`; authorized model identity, limits, capabilities, availability and price source only — no endpoint secret or provider object. |
| `GoalDecision.cs` | PA-04 read-only decision snapshot and `IGoalDecisionApi` for SDK/plugin/UI display; no arbitrary GoalPhase write entry, and presenters must not invent conclusions. |
| `CapabilityCatalog.cs` | PA-05 read-only capability catalog DTOs and `ICapabilityCatalogApi`; authorized kind pages with Revision/Generation/Completeness, permission gaps separate from empty, and entries are never executable grants. |
