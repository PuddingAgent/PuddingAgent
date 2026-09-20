# 阶段感知模型路由 — Step 2 Plan Freeze（checkpoint-v1）

- goalRunId `tg-0992b9b323e3c4e4a38f7fa51407f55d`｜iteration 3｜stepNode `tn-a89688d2b34b57c73043e71252b74695`（Plan, seq 2/5）
- **上轮 verdict**：`outcome=blocked`、`blockerCode=work_unit_budget_exhausted`、`unmetCriteria=[]`
  ⇒ **非判据失败**，是 **step2 的 WorkUnit 预算（maxCost 0.75 / maxToolCalls 30）被大文件读取打爆**
  （一次读 27KB + 一次读 70KB + 一次 890 文件宽 grep）。**教训已入纪律（见 §7）。**
- 本轮目标：**冻结实现路径、所有权边界与验收门**（step2 的 objective 原文）。

---

## 1. 现状台账（全部为上一轮只读取证的结论）

### 1.1 已存在（可直接复用，勿重造）
| 能力 | 证据 |
|---|---|
冻结路由快照 | `PuddingCore/Platform/ExecutionRunContracts.cs:126` `LlmRouteSnapshot(ProviderId, ModelId, Protocol, CapabilityTags, VisionPolicy?)`，注释逐字「冻结的 LLM 路由能力快照（ADR-077 §4.3）。Coordinator、Image Reader 与调用链消费同一份快照，**不再各自读取可热变的模型目录**」 |
快照不可变+重试复用 | 同文件 `AgentExecutionSnapshot` 注释逐字「Run 启动时由 SnapshotFactory 一次性生产。**快照不可变；同一 Turn 的重试复用第一次生成的快照**。快照不保存 API Key」 |
冻结为默认要求 | `Orchestration/AgentOrchestrationModels.cs:230 RequireFrozenRoutes = true`；`AgentOrchestrationGraphCompiler.cs:404` |
快照随链路传递 | `PuddingToolContracts.cs:201` / `Runtime/ITurnExecutor.cs:53` / `ToolInvocationContracts.cs:36` / `Platform/MessageContracts.cs:217` 均带 `CallerLlmSnapshot` |
确定性 Agent 路由 | `Scheduling/TaskAgentRouteMatcher.cs`：逐字「Deterministic structured route matcher. Task titles and descriptions are deliberately excluded」；返回 `(Compatible, Code, Fingerprint)`；7 判定码 |
taskType 路由配置 | `TaskAutoDispatchEvaluator.cs:80 TaskTypeRoutes`（`:133-140` 校验）+ `:154 TaskTypeRouteOptions`；四处消费（`TaskBacklogRefinementEvaluator:63`、`TaskBacklogRefinementStore:59`、`TaskExecutionPlanCompiler:22/:28`、`TaskGoalDispatchTransactionStore:88`）；`TaskSchedulerControlService:355` 入 policy 快照 |
能力标签路由（确定性排序） | `Services/FileLlmResolver.cs`：`CapabilityTags` 全含 ⇒ `OrderBy(SortOrder).ThenBy(ProviderId).ThenBy(ModelId)` 取首个；裸 modelId 多 provider 命中即报错 |
并发槽位（provider/model 级） | `Runtime/DependencyInjection.cs:137 ProviderRateLimiter` 单例；`DirectLlmClient` `AcquireAsync` lease，注释「流式请求在整个流生命周期内持有槽位」 |
**容量等待与 TTFT 观测量** | `DirectLlmClient` `RecordLlmMetricAsync(operation: "rate_limit.wait", durationMs: WaitMs, metadata: {rate_limit_waited, rate_limit_wait_ms})`；终态 `chat_stream` metric 带 `stream_first_chunk_wait_ms`（`:1460`）；`ObserveFirstChunkWait(_firstChunkWaitMs, received)`（`:1415`）；测试断言见 `LlmStreamObservabilityTests:434/:497-498` |
provider 级熔断（日志面） | `DirectLlmClient.cs:155` `[DirectLlm] CIRCUIT_OPEN provider={Provider} recovery={RecoveryTime}` |
熔断的既有模式参照 | `GoalSettlementStore.cs:1517 NoProgressCircuitOpenBlockerCode="no_progress_circuit_open"` + `:1303/:1702 GoalEventTypes.CircuitOpened` |

### 1.2 部分存在（**真实缺口在"维度/内容"，不在机制**）
- `TaskTypeRouteOptions` 只有 **4 个维度**：`RequiredCapabilityIds[]`、`AllowedRoles[]`、`RequiredProviderId`、`RequiredModelId`
  ⇒ **无 phase / complexity / risk / quality floor / cost / health**。
- **`TaskTypeRoutes` 默认是空字典**（`= new(StringComparer.OrdinalIgnoreCase)`）⇒ 机制在、**实际未配置** ⇒
  现状下具体模型仍由 agent 的 `PreferredProviderId/PreferredModelId` 与主模型传参决定
  ⇒ 卡片原判断「没有 Scheduler 基于 taskType 的确定性选择」**在"实际生效"意义上仍成立**。
- `LlmRouteSnapshot` 只有 **5 个字段** ⇒ **无 reason / 无 quality floor / 无 health snapshot / 无 taskType / 无 phase**。

### 1.3 缺失（未命中字面，附检索范围限定）
`RoutePolicy`、`ModelCapabilityProfile`、provider 级 `Ewma`/`ProviderHealth`；
**401/403/model-unavailable 的差异化策略**（grep 命中的 `Unauthorized` 全为文件系统/终端权限异常；
LLM 侧只有 provider 级 `CIRCUIT_OPEN` 日志与限流等待，**未见按错误类分类**）。

## 2. 实现路径（分阶段，每阶段一个可独立验收的原子面）

### P1-1 记录面（criterion 1）—— 最小侵入
- **落点选择**：不新建状态机。在**既有 route snapshot 链路**上扩展：
  (a) 为 `TaskTypeRouteOptions` 增加 `Phase`/`Complexity`/`Risk`/`QualityFloor` 维度（配置层）；
  (b) 新增 `RouteDecision(ProviderId, ModelId, Reason, QualityFloor, HealthSnapshot, TaskType, Phase, Fingerprint)`
      并**随现有快照持久化位落库**（已存在 `goal_runs.route_snapshot_json`、`goal_verifications.route_snapshot_json`；
      WorkUnit 级需确认 `task_nodes` 是否可承载 → **待查，见 §6**）。
- **验收**：任一 WorkUnit 可回溯出 taskType/phase/route/reason/quality floor/health snapshot 七元组。

### P1-2 确定性选择（criterion 2）
- 复用 `TaskAgentRouteMatcher` 的**同一范式**：纯函数 + 规范化 `Fingerprint` + 判定码枚举；
  **明确禁止**把标题/描述等自由文本纳入（沿用其逐字原则）。
- **验收**：同 snapshot 输入 ⇒ 逐字相同输出（含 Fingerprint 相同）；自由文本不可影响结果。

### P1-3 差异化故障策略（criterion 3）
- 按错误类分派：`401/403` ⇒ **不可重试 + 立即 circuit-open**；`model unavailable` ⇒ **compatible 路由 failover 至多一次**；
  `rate limit` ⇒ **有界等待**（复用 `ProviderRateLimiter`）。
- 沿用 `no_progress_circuit_open` + `CircuitOpened` 的**既有事件模式**；failover 需**冻结 causation**（记录来源 route）。
- **验收**：三类错误产生三种可区分的策略与事件码；同单元 failover ≤1。

### P1-4 联合并发约束（criterion 4）
- 在既有 `ProviderRateLimiter`（provider/model 槽位）之外，接入 Agent slot（`agent_execution_reservations`）与
  **workspace/provider/model token budget**；批匹配时把 Agent slot 一并纳入（沿用 `MaxStartsPerScan=2` 的既有准入语义）。
- **验收**：无本地排队风暴（排队与等待均可在观测面区分）。

### P1-5 shadow A/B（criterion 5）
- route 决策带 `shadow` 标记：shadow 只**记录不生效**；按 taskType 比较 `success / Verifier pass / cycle time / cost`。
- **验收**：≥50 个 WorkUnit 后按类型成表；质量不得因提速回退。

### P1-6 生产目标（criterion 6）
- `P95(rate_limit_wait_ms) < 30s`；模型故障不导致连续重复派发。
- **数据源已确认存在**（§1.1 的 metric 面）⇒ **只差定位其落库表**（§6 待查）。

## 3. 所有权边界（**冻结**）
1. **不创建第二套 Task/Goal 状态机**（沿用审计 `:273` 的落点约束）。
2. **Scheduler 拥有选择权**；Runtime 只**消费冻结快照**，不自行读取可热变模型目录（沿用 ADR-077 §4.3 逐字语义）。
3. **不改 `LlmRouteSnapshot` 的既有语义**（只增字段，不改变现有消费者的行为）。
4. 本卡**不含 UI**（criterion 5 的展示面属其它卡）；**不触碰计费/模型目录定义**。

## 4. 验收门 ↔ 6 条标准映射
| 标准 | 现状 | 本轮冻结的门 |
|---|---|---|
1 记录七元组 | 部分（快照 5 字段、无 reason/floor/health） | 七元组可回溯 + **禁止无解释选择**（无 reason 即失败） |
2 确定性 | **机制已存在**（Agent 层） | 扩展到模型层；Fingerprint 逐字可比 |
3 差异化策略 | **缺失** | 三类错误三种策略 + failover ≤1 + causation 冻结 |
4 联合并发 | 部分（仅 provider/model 槽位） | Agent/workspace/provider/model/token 四约束齐备 |
5 shadow A/B | **无** | ≥50 WorkUnit + 按类型成表 + 质量不回退 |
6 P95<30s | **数据源已存在**，落库位待查 | P95 可计算 + 无连续重复派发 |

## 5. 前置与硬阻塞（**必须在实现前解决**）
1. ⚠️ **本卡 plan `tp-cdaa80e3…` 的 `plan_version` 必须保持 = 2**；任何 recompile 使其 ≥3 即坠入缺陷 `5413ce1b` 的永久死锁。
2. ⚠️ **`rate_limit_wait_ms` / `stream_first_chunk_wait_ms` 的落库表未定位** ⇒ 标准 6 的 P95 与卡片的 TTFT 软分**可能同样不可离线度量**（与 Tracker 卡标准 6 同一类风险）。
3. ⚠️ **WorkUnit 级 route 记录位未确认**（`task_nodes` 是否可承载七元组）⇒ 决定 criterion 1 的实现形态。
4. 平台缺陷（我侧无写入通道）：`5413ce1b`、`14c02e8b`、`5c81f660`、`709bbf5e`。

## 6. 待查（step3/4 的入口，按依赖排序）
① `rate_limit_wait_ms` 与 `stream_first_chunk_wait_ms` 的落库表/列（**标准 6 唯一数据源**）；
② WorkUnit 级 route 记录位（`task_nodes` 列清单）；
③ `TaskTypeRoutes` 在生产配置文件中**是否被赋值**（默认空字典 ⇒ 需查 config）；
④ `LlmRouteSnapshot` 现有消费者的兼容性影响面（增字段的 blast radius）。

## 7. 本轮纪律（从上轮预算耗尽中学到）
- **禁止**一次性读 >10KB 文件：改用 `offset_lines`+`limit_lines` 窗口。
- **禁止**对 `Source` 全量 grep（2000 文件上限 → 既慢又成为 `coverage=partial` 的伪证据）。
- step2 的 WorkUnit 预算仅 `maxToolCalls=30 / maxCost=0.75` ⇒ **一轮内不超 4 个工具调用**。

## 8. 诚实限定
- 本文全部基于**只读检索 + 阅读**；**未改产品代码、未运行测试**。
- §1.3 的「缺失」以**字面检索**为据且**部分调用 `coverage=partial`** ⇒ 表述为「未命中该命名」，**不等于功能不存在**。
- 本文是 **step 2 的计划冻结**，**不是实现、不是产品验收**；终局由服务端 verifier 裁决。
