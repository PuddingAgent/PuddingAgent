# Goodput SLO — Step 3 Change Checkpoint（checkpoint-v1）

- goalRunId `tg-9ec9d27543747ec6849a52aae10d9f31`｜task `0b16740022f84b58a9532a87f1bc5509`｜stepNode `tn-ac79b4913b4ded0cdb1e1b72eae5240e`（**Change**, seq 3/5）
- step objective 逐字：*"Apply the bounded implementation changes within the declared conflict scope."*
- 上轮 verdict：`blocked / contract_coverage_insufficient`（`unmetCriteria` 空）；step1/2 已通过（`stepsPassed = 2/5`）

---

## 1. 变更清单（**纯新增，零生产行为改变**）
| 文件 | 类型 | 内容 |
|---|---|---|
`Source/PuddingPlatform/Services/GoodputAttributionService.cs` | 新增 | 归因解析（`UsageAttribution.Parse`）、价格状态判定（`PricingClassifier`）、只读聚合服务（`GoodputAttributionService : IGoodputAttributionService`）与报告契约 |
`Source/PuddingPlatformTests/Services/GoodputAttributionServiceTests.cs` | 新增 | **11 个测试**（4 个解析契约 + 4 个价格状态 + 2 个 DB 聚合 + 1 个回退） |

## 2. 逐字验证证据（**外部可核查**）
```
# 定向
已通过! - 失败:     0，通过:    11，已跳过:     0，总计:    11，持续时间: 1 s - PuddingPlatformTests.dll (net10.0)   exit_code=0
# 全工程回归
已通过! - 失败:     0，通过:  1335，已跳过:     0，总计:  1335，持续时间: 1 m 12 s - PuddingPlatformTests.dll (net10.0)   exit_code=0
```
⇒ 既有 1324 + 新增 11 = **1335**，完全吻合；**未破坏任何既有测试**。

## 3. 归因桥接：**一次自我否证 + 一次确认**（本轮最重要的事实）
- **H1（中段 = `run_id`）被证伪**：种群检验 300 条 `agent_llm` SourceId → 31 个不同中段 → 与 2795 个 `execution_runs.run_id` **匹配 0**；单点亦未命中 `execution_runs.run_id/snapshot_id/command_id`、`goal_iterations.run_id`、`chat_execution_commands.id`、`sub_agent_runs.id`。
- **H1′（中段 = `TraceId`）被确认**，代码逐字（两个生产写入点）：
  `AgentExecution/AgentExecutionService.Buffered.cs:846` 与 `...Streaming.cs:1170`：
  `sourceId: $"{request.SessionId}:{trace.TraceId}:{round + 1}"`
  单点：`trace_id=e01cde26…` → `execution_runs(run_id=c83c0bdf…, worker_id=chat-execution-worker, status=succeeded)` + `goal_iterations(goal_run_id=tg-9ec9d275…, iteration_no=2, stop_reason=completed)`。
  种群：31/31 命中 `execution_runs.trace_id`，**未匹配 = 0**。
- ⇒ **可行桥接**：`SourceId[1] = TraceId → execution_runs.trace_id / goal_iterations.trace_id → goal_run_id → task binding → task_nodes(WorkUnit)`。
- **旁证（仓库自述）**：既有 `Source/PuddingPlatform/code_map.md` 对 `ConversationProjector` 的描述**逐字**写着「usage 仅在 direct `session:trace:round` 行缺失时补记」—— 即仓库自身已把该形态记为 `session:trace:round`，与代码逐字相互印证。
  （上一轮 plan §7 预声明的分支"不成立 ⇒ 改经 snapshot_id/command_id 或 session+时间窗"已按此路线落定为 trace_id；**不是事后改标准**。）

## 4. 双源交叉验证（**桥接正确性的独立确认**）
按 TraceId 桥接聚合出的 prompt 与 `goal_iterations.input_tokens` **逐字一致**：

| goal(尾8) | iter | status/stop_reason | 用量行 | prompt（归因） | iter.input_tokens | 一致 |
|---|---|---|---|---|---|---|
…1407f55d | 1 | settled/completed | 5 | 1,131,341 | 1,131,341 | ✓ |
…1407f55d | 2 | **failed**/failed | 3 | 519,624 | 519,624 | ✓ |
…1407f55d | 3 | settled/completed | 2 | 297,605 | 297,605 | ✓ |
…1407f55d | 4 | settled/completed | 24 | 4,582,863 | 4,582,863 | ✓ |

⇒ 两个**互相独立**的写入路径（用量账本 vs 迭代结算）在归因后数值相同 ⇒ 桥接结论成立。

## 5. P1 生效的报读口径（实测样例，扫描窗口 50000 行）
- 窗口内 **1838** 个不同 trace；**未归因行 3553 / 50000 = 7.1%**（SourceId 段数 < 3，如 `mem:*`、`context_compaction`）⇒ 这是「未知归因」的可量化基线。
- 迭代级可读出：`prompt / completion / cache_hit / cache_miss / priced_cost / zero_cost_with_tokens / savings_claimable`。
  例（iter4）：prompt 4,582,863、cache_hit 4,388,096、**cache_miss 194,767**、cost 0.799498、`savings_claimable = true`。
- **`SavingsClaimable` 的语义**：只要窗口内存在"**非零 token 却零成本**"的行，即返回 false —— 这正是卡片「0usage / 缺价 / 未知归因**不被当节省**」的机器可判定形态。
  另：`PricingClassifier` 明确区分 `priced / free / unpriced / unknown_provider`，且**只有 `priced` 计入节省**（真实免费档也不算节省）。

## 6. 更正我此前的一处表述（诚实义务）
`TokenUsageEventEntity.TotalCost` 是**非可空 `decimal`**（`InputCost/OutputCost/CacheHitCost/TotalCost` 同类）。
⇒ 早前探针标签 `zero_or_null_cost = 7725` 是"**零或 NULL 的并集**"条件；既然列非可空，**这 7725 条应理解为零值**（并非 NULL）。相关结论（93% 来自未注册 provider）不受影响，但表述已更正。

## 7. **接线状态（明确声明，不隐藏死代码）**
本切片交付的是**契约与只读聚合实现**，**尚未接线**：
- 未注册 DI；未暴露到工具/端点的动作面。
- 已查明的天然消费点：`PuddingRuntime/Tools/BuiltIns/Diagnostics/AgentDiagnosticsTool.cs:213/:668` 以
  `scope.ServiceProvider.GetService<ICacheDiagnosticsService>()` 消费同类只读诊断服务（即 `agent_diagnostics` 工具）。
- **下一原子切片（P1b）清单**：①在 Runtime 注册 `IGoodputAttributionService`；②在 `AgentDiagnosticsTool` 增加动作（如 `goodput_attribution`）；③**重启后核验**；④按卡片要求把"源码交付 / 已加载构建 / 产品验收"分开登记。
- 若下一迭代仍未接线，按工程纪律**应撤回**而不是长期悬挂（与我在路由卡 Review 中的自审标准一致）。

## 8. 卡内验收项现状（**本切片后**）
| 卡片标准 | 现状 |
|---|---|
0usage / 缺价 / 未知归因不被当节省 | **有机器可判定实现 + 11 测试 + 实测口径**（未接线 ⇒ 尚未在生产报告面生效） |
任务与真实 worker/assignment/Run 可对账 | **桥接已证实（trace_id）**，且双源交叉验证一致；「→模型」边由用量账本 provider/model 补齐 |
全部心跳可解释 / 等待无忙轮询 | 未动（属 P3，`work_unit_await_handles` 仍 0 行） |
三项 SLO（−50% / P95 / −30%） | 未动（属 P4/P5） |

## 9. 下一步与风险
- **下一步**：P1b 接线（DI + 工具动作 + 重启核验）→ P2 缺价标记（只新增列、不改成本语义）→ P3 心跳投影（复用 HeartbeatOutcome）→ P4 时间账派生 → P5 三项 SLO 口径。
- **风险**：R1 接线需重启 Core（会终止运行中的 Goal/子代理）⇒ 必须在无活跃 Goal 的窗口内做；R2 `work_unit_await_handles` 仍 0 行；R3 本卡 plan_version 须保持稳定（否则触发缺陷 `5413ce1b`）。

## 10. 诚实限定
- 本轮**新增 2 文件、无修改既有生产文件**；数据库未写入（探针只读）。
- 所有测试与探针证据均为**本机单库单次**运行结果。
- 「未归因 7.1%」是**扫描窗口内**比例（最近 50000 行），非全库比例。
- 归因中段的身份结论来自 **代码逐字 + 单点命中 + 种群 31/31 + 双源数值一致** 四重证据；**不含**生产长期运行验证。
