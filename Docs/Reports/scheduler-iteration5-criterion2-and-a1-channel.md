# Scheduler 迭代 5 证据：验收标准 2 实测 + A1 契约提案通道不可达（2026-09-20）

- GoalRun `tg-2efcdf7d329d8387632a67bcbfbdbe2b`｜task `3bd2a4b0ef5f4bff8f175fb7655927ad`｜iteration 5
- 数据源：`D:\data\databases\pudding_platform.db`（只读 `mode=ro`）｜探针 `temp/db-probe-criterion2.py`

---

## 1. 验收标准 2：事件 → 决策延迟实测（通过，且余量很大）

`task_scheduler_intents` 全量 543 条：

```
intents_total = 543      status_dist = {"done": 543}      未处理 = 0
source_dist   = {"task_events": 520, "conversation_events": 23}

latency(created_at_utc → processed_at_utc)
  p50 = 0.800 s
  p95 = 1.980 s
  max = 4.691 s
  超过 30 s 的样本数 = 0
```

⇒ **标准 2 的「P95 30s 内完成候选决策」实测通过，余量约 15×**；且两类事件源（task_events / conversation_events）均在真实驱动。

**口径更正（诚实标注）**：探针输出里的 `retried=543` 是我的**标签错误** —— `attempt_count` 为 1-based（首轮即 1），所以该字段不能解释为「全部重试过」。真实含义是「543 条全部有至少一次尝试记录」，**未证明也未证伪重试行为**。

## 2. 验收标准 2：重复 Goal/Assignment/Reservation（部分证据）

`task_goal_bindings` 共 17 条；其中 3 个 task 有多条 binding：

| task_id | bindings | distinct goal_run_id |
|---|---|---|
| `3bd2a4b0…`（本任务） | 7 | **7** |
| `4ed930e7…` | 3 | 3 |
| `77883a50…` | 3 | 3 |

`agent_execution_reservations` 呈现同样三个 task（7/3/3）。
⇒ 这些是**顺序重试**产生的多条记录（`goal_run_id` 互不相同），**不是重复启动**。
**真正的幂等判据是「同一 intent 至多启动一个 goal_run」**（`task_scheduler_intent_outcomes.intent_id` × `started_goal_run_id`），
**本轮未做该核对，标为待验**，不作通过结论。

## 3. A1 契约提案通道：对合规 Agent 不可达（新缺陷）

### 3.1 机制（代码级）
- `PuddingRuntime/Services/TurnExecutorAdapter.cs:193-207`
  ```csharp
  var trimmed = reply.TrimStart();
  if (!trimmed.StartsWith('{')) return null;          // 回复必须是纯 JSON 信封
  return AgentLoopResponse.Parse(trimmed).Meta?.GoalContractProposal;
  ```
- 信封形态（`AgentLoopResponseTests.cs:74-102` 的 Legal 用例）：
  `{"status":"DONE","message":"…","meta":{"goal_contract_proposal":{…}}}`
- 提案结构：`schemaVersion / kind=refine_acceptance_contract / expectedContractVersion / criteria[]`，
  criterion 为 `{requirement, requirementRefs[], verification:{kind, definitionRef, inputRefs[], expectedText}}`
  —— 注意是 **`requirement`**（非 `statement`），`verification` 是**单个对象**（非数组）。

### 3.2 校验规则（`GoalContractProposalValidator.DeriveContractItems`）
- `verification.kind` 只允许 `text-assertion`；`definitionRef` 必须是已登记的 `checks/text-assertion.md#equals`；
- `expectedText` **必填**（参与 definition hash）；
- `inputRefs` **必须为空**（注释：「text-assertion 的证据是 canonical Turn 终态最终 assistant 输出」）；
- `requirementRefs` 覆盖率门：每条 ref 至少含一个 objective 词元，且**全部 ref 的词元并集必须覆盖 objective 的全部词元**
  （ordinal、不折叠大小写）⇒ 实际上必须**逐字引用完整 objective**。

### 3.3 两个硬阻塞（本轮未发提案的原因）
1. **规范冲突**：运行时提示词明确要求「Do not output JSON control structures such as status/tool/meta」，
   而该通道要求回复**以 `{` 开头**的 JSON 信封。合规 Agent **无法同时满足**两者 ⇒ 通道实际不可达。
   证据：本 Goal 三次迭代均附提案（markdown 围栏），`goal_acceptance_contracts.contract_version` **始终为 1**。
2. **语义不可用**：`ExpectedEvidence` 写明「canonical turn 的最终 assistant 输出与期望文本 **ordinal 精确相等**」，
   即只能断言「最终输出恰好等于某固定串」，无法承载本任务的真实验收（build/test/行为级判据被显式排除：
   「A1 是目标级覆盖通道，不附加工程门禁，更不允许 Agent 自设工程门」）。
   ⇒ 发出提案若断言不成立，只会**新增失败判据**，使阻塞更严重。

### 3.4 内部不一致（附带发现）
`AgentLoopResponseTests` 的 Legal 示例使用 `inputRefs:["reply"]`，但 `GoalContractProposalValidator`
对 `inputRefs.Count > 0` **一律拒绝**（"inputRefs must be empty"）。测试只覆盖 `Parse`，未覆盖校验器。

## 4. 诚实限定
- 全部为只读快照；§2 的 per-intent 唯一性未核对，已标待验。
- §3 结论基于源码逐行阅读，未运行该通道的端到端实验（不运行的原因见 §3.3）。
