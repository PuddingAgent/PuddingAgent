# Memory System v2 F4 Subconscious Plan Protocol

> Date: 2026-07-01
> Status: partial-degradation-policy
> Scope: 潜意识 LLM 计划协议、validator、dry-run 生成、validator 结果可观测、Job result envelope 和降级策略

---

## 1. 目标

F4 的目标是把潜意识 LLM 的输出固定为可校验计划，而不是直接写 `MemoryLibrary`。

潜意识运行时被定义为受限后台 Agent：它只读显意识会话证据、压缩摘要、已有记忆和候选集；只输出结构化 `MemoryMaintenancePlan`；不调用普通工具、终端、文件系统、浏览器、网络或外部 API；不等待人审计。框架只能把结果自动执行、自动拒绝、自动重试或自动隔离。

F4 已分两步推进：

1. 定义 `MemoryMaintenancePlan` schema。
2. 定义 `MemoryMaintenancePlanValidator`。
3. 提供最小 fixtures 和回归测试，证明非法计划会被拒绝。
4. 新增 `SubconsciousPlanGenerationService`，让潜意识 LLM 只生成 dry-run `MemoryMaintenancePlan`。
5. 对 LLM 返回计划执行 validator，并记录 `RuntimeActivity` 与 `TelemetryMetric`。
6. 新增 `SubconsciousJobResultEnvelope` 和 `SubconsciousJobs.ResultJson`，保存 dry-run plan 的 accepted/rejected 结果。
7. 在 envelope 中加入 `decision` / `nextAction`，把 validator 结果转换为后续处理建议。

F4 当前仍不做：

- 不执行 plan。
- 不写 `MemoryLibrary`。
- 不持久化任何已执行写入结果。
- 不做语义一致性内容 hash。

---

## 2. Plan Schema

```json
{
  "planId": "plan-1",
  "workspaceId": "workspace-1",
  "source": {
    "workspaceId": "workspace-1",
    "sessionId": "session-1",
    "hookEventId": "evt-1",
    "subconsciousJobId": "job-1",
    "agentId": "agent-1",
    "agentTemplateId": "template-1",
    "memoryLibraryId": "library-1"
  },
  "candidateReads": [
    {
      "workspaceId": "workspace-1",
      "chapterId": "chapter-1"
    }
  ],
  "operations": [
    {
      "operationId": "op-1",
      "action": "supersede_existing",
      "target": {
        "workspaceId": "workspace-1",
        "chapterId": "chapter-1"
      },
      "proposedContent": "Updated stable memory content.",
      "confidence": 0.91,
      "rationale": "Later evidence explicitly replaces the old chapter.",
      "riskFlags": []
    }
  ],
  "confidence": 0.88,
  "rationale": "Generated from session compression evidence.",
  "riskFlags": []
}
```

Supported `action` values:

- `reuse_existing`
- `append_new`
- `supersede_existing`
- `merge_candidates`
- `deprecate`
- `delete`
- `update_index`
- `update_skill_pointer`

---

## 3. Validation Rules

`MemoryMaintenancePlanValidator` currently enforces:

- Plan and source workspace must match the validation context.
- When `SubconsciousMemoryScope` is provided, plan source must match its workspace, agent, session and optional memory library.
- Plan must contain at least one operation.
- Operation action must be in the supported action set.
- Operation confidence must be greater than or equal to `MinimumOperationConfidence`.
- `append_new` must include `proposedContent`.
- Any target/source reference must stay inside the current workspace.
- Any referenced candidate ID must be in `AllowedReferenceIds` when that set is provided.
- Malformed JSON is rejected before plan validation.

This validator is intentionally conservative. It does not decide whether memory should be written; it only decides whether a plan is structurally eligible for later execution by F5.

---

## 4. Dry-Run Generation

`SubconsciousPlanGenerationService` is the first runtime adapter around the plan protocol.

Design constraints:

- The service calls `IMemoryLlmClient.ChatWithScopedConfigAsync` with an explicit memory LLM config and target `SubconsciousMemoryScope` when provided.
- The system prompt instructs the LLM to return only JSON `MemoryMaintenancePlan`.
- The user prompt includes session evidence, source identity, target memory scope, candidate reads, allowed reference IDs and minimum confidence.
- The service validates the raw JSON immediately and returns `Plan = null` when validation fails.
- The service records `memory_maintenance_plan.validate` as `RuntimeActivity`.
- The service records `memory_maintenance_plan.validation` as `TelemetryMetricCategories.Memory`.
- Dimensions include `workspace_id`, `session_id`, `dry_run`, `valid`, `operation_count`, `candidate_count`, `error_count`, and optional source IDs.

Current boundary:

- A valid dry-run result is only a preview for later F5 execution.
- Invalid JSON or invalid references are rejected before any write path.
- A plan that points to another agent, session or memory library is rejected before any write path.
- No plan execution and no `MemoryLibrary` write happens in F4.

---

## 5. Job Result Envelope

`SubconsciousJobResultEnvelope` is the durable audit envelope for F4 dry-run output.

Schema:

```json
{
  "schema": "pudding.subconscious_job_result.v1",
  "kind": "memory_maintenance_plan.dry_run",
  "status": "accepted",
  "decision": "accept_for_execution",
  "nextAction": "enqueue_for_execution",
  "planId": "plan-1",
  "valid": true,
  "operationCount": 1,
  "errorCount": 0,
  "errorCodes": [],
  "summary": "Dry-run memory maintenance plan accepted.",
  "metadata": {
    "workspace_id": "workspace-1",
    "session_id": "session-1",
    "subconscious_job_id": "job-1"
  }
}
```

Design constraints:

- `SubconsciousJobs.ResultJson` stores only the compact result envelope.
- The envelope records accepted/rejected status, plan id, operation count and validator error codes.
- The queue only allows the current lease owner to call `RecordResultAsync`.
- `GetResultAsync` returns the envelope for diagnostics or later execution review.
- Raw LLM output is not stored in this envelope.
- Storing a result does not mark a job completed and does not execute memory writes.

---

## 6. Autonomous Degradation Policy

F4 的降级策略只做无人值守后续处理建议，不直接改变 Job 状态，也不执行 plan。

| Validator result | envelope status | decision | nextAction | 说明 |
| --- | --- | --- | --- | --- |
| valid | `accepted` | `accept_for_execution` | `enqueue_for_execution` | 进入 F5 执行器候选，但 F4 不执行 |
| `low_confidence` | `quarantined` | `defer_for_recheck` | `complete_quarantined` | 证据弱，自动隔离为诊断事实，等待未来证据重新触发，不写入 |
| `invalid_json` | `rejected` | `retry_later` | `retry_job` | 可能是 LLM 格式瞬时失败，可由 Worker 后续决定 retry |
| workspace/reference/action/required-field errors | `rejected` | `reject_complete` | `complete_rejected` | 结构或边界错误，不进入执行器 |

Hard constraints:

- `accept_for_execution` is only a candidate signal; F5 must validate again before writes.
- `quarantined` must not silently become `append_new`.
- `retry_job` must use queue retry limits, not infinite retry.
- `complete_rejected` means no memory write and no plan execution.
- No degradation path may wait for human review; subconscious jobs are unattended.

---

## 7. Fixtures

### 7.1 append_new

```json
{
  "planId": "plan-append",
  "workspaceId": "workspace-1",
  "source": { "workspaceId": "workspace-1", "sessionId": "session-1", "subconsciousJobId": "job-1" },
  "candidateReads": [],
  "operations": [
    {
      "operationId": "op-append",
      "action": "append_new",
      "proposedContent": "User prefers concise engineering summaries.",
      "confidence": 0.82,
      "rationale": "Stable preference from session evidence."
    }
  ],
  "confidence": 0.82,
  "rationale": "One new preference is safe to append."
}
```

### 7.2 supersede_existing

```json
{
  "planId": "plan-supersede",
  "workspaceId": "workspace-1",
  "source": { "workspaceId": "workspace-1", "sessionId": "session-1", "subconsciousJobId": "job-1" },
  "candidateReads": [{ "workspaceId": "workspace-1", "chapterId": "chapter-1" }],
  "operations": [
    {
      "operationId": "op-supersede",
      "action": "supersede_existing",
      "target": { "workspaceId": "workspace-1", "chapterId": "chapter-1" },
      "proposedContent": "Updated decision replaces the older chapter.",
      "confidence": 0.91,
      "rationale": "Later evidence explicitly replaces the previous version."
    }
  ],
  "confidence": 0.9,
  "rationale": "One candidate is superseded by stronger evidence."
}
```

### 7.3 invalid_cross_workspace

```json
{
  "planId": "plan-invalid-cross-workspace",
  "workspaceId": "workspace-1",
  "source": { "workspaceId": "workspace-1", "sessionId": "session-1", "subconsciousJobId": "job-1" },
  "candidateReads": [{ "workspaceId": "workspace-1", "chapterId": "chapter-1" }],
  "operations": [
    {
      "operationId": "op-invalid",
      "action": "reuse_existing",
      "target": { "workspaceId": "workspace-2", "chapterId": "chapter-1" },
      "confidence": 0.9,
      "rationale": "This must be rejected because it crosses workspace boundary."
    }
  ],
  "confidence": 0.9
}
```

---

## 8. Verification

Regression tests:

- `MemoryMaintenancePlanValidatorTests.Validate_ShouldAcceptAppendAndSupersedeOperations_WhenReferencesAreAllowed`
- `MemoryMaintenancePlanValidatorTests.ValidateJson_ShouldRejectMalformedJson`
- `MemoryMaintenancePlanValidatorTests.Validate_ShouldRejectCrossWorkspaceReferences`
- `MemoryMaintenancePlanValidatorTests.Validate_ShouldRejectLowConfidenceOperations`
- `MemoryMaintenancePlanValidatorTests.Validate_ShouldRejectReferencesOutsideCandidateSet`
- `SubconsciousPlanGenerationServiceTests.GenerateDryRunAsync_ShouldReturnValidPlanAndRecordSucceededMetric`
- `SubconsciousPlanGenerationServiceTests.GenerateDryRunAsync_ShouldRejectInvalidJsonAndRecordFailedMetric`
- `SubconsciousPlanGenerationServiceTests.GenerateDryRunAsync_ShouldCreateAcceptedJobResultEnvelope`
- `SubconsciousPlanGenerationServiceTests.GenerateDryRunAsync_ShouldCreateRejectedJobResultEnvelope`
- `SubconsciousPlanGenerationServiceTests.GenerateDryRunAsync_ShouldCreateQuarantineEnvelopeForLowConfidence`
- `SubconsciousJobQueueTests.RecordResultAsync_ShouldPersistDryRunPlanEnvelope`
- `RuntimeServiceExtensionsTests.AddPuddingRuntime_RegistersSubconsciousPlanGenerationService`

Command:

```powershell
dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --filter "FullyQualifiedName~MemoryMaintenancePlanValidatorTests" --no-restore --verbosity minimal
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~SubconsciousPlanGenerationServiceTests" --no-restore --verbosity minimal
dotnet test Source\PuddingMemoryEngineTests\PuddingMemoryEngineTests.csproj --filter "FullyQualifiedName~SubconsciousJobQueueTests.RecordResultAsync_ShouldPersistDryRunPlanEnvelope" --no-restore --verbosity minimal
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~RuntimeServiceExtensionsTests" --no-restore --verbosity minimal
```
