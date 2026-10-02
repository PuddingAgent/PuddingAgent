# 阶段感知模型路由 Explore — Step 1 Checkpoint (checkpoint-v1)

- goalRunId `tg-0992b9b323e3c4e4a38f7fa51407f55d`｜iteration 1｜objectiveVersion 1
- taskId `cec285c367324e4a90ef62dbef1b0404`｜assignmentId `f1dfa79d62fc44808a074b77d4e254e1`｜**已认领**（v14 → v15，InProgress）
- planId `tp-cdaa80e3a25c3d50b47e27c2c27cd886`｜stepNodeId `tn-3c9c822ccfd5b6b223b1cc2561ce2142`（Explore 1/5）
- `lastVerdict` = **null**（首次迭代）；`acceptanceContract` = null

---

## 1. **对卡片前提的两处修正（本轮最重要结论）**

### 前提 A（卡片）：「manifest 虽有 explorer/planner/developer/reviewer 路由，但**没有 Scheduler 基于 taskType/phase/complexity/risk 做确定性选择」**
**部分不成立**：**基于 taskType 的确定性路由已存在，并已在 4 个 Scheduler 入口被消费**。

`Source/PuddingPlatform/Services/Scheduling/TaskAutoDispatchEvaluator.cs`
- `:80` `public Dictionary<string, TaskTypeRouteOptions> TaskTypeRoutes { get; set; }`（**配置项**）
- `:133-140` 启动校验：key 长度 1–64、capability/role 不得为空
- `:154` `public sealed class TaskTypeRouteOptions`
- `:319` `_options.TaskTypeRoutes.TryGetValue(task.TaskType, out var typeRoute);`

同源消费点（**四处**）：`TaskBacklogRefinementEvaluator.cs:63`、`TaskBacklogRefinementStore.cs:59`、
`TaskExecutionPlanCompiler.cs:22/:28`、`TaskGoalDispatchTransactionStore.cs:88`；
`TaskSchedulerControlService.cs:355` 将其纳入 policy 快照。

`Source/PuddingPlatform/Services/Scheduling/TaskAgentRouteMatcher.cs`（**确定性匹配器**）逐字自述：
> **Deterministic structured route matcher. Task titles and descriptions are deliberately excluded:
> only persisted routing metadata and canonical Agent template capabilities/provider/model participate in selection.**

返回 `TaskAgentRouteMatch(Compatible, Code, Fingerprint)`，判定码逐字：
`agent_disabled`、`agent_frozen`、`role_mismatch`、`preferred_agent_exclusive`、`provider_mismatch`、
`model_mismatch`、`capability_missing:{x}`，成功码 `preferred_agent` / `compatible_agent`。
`Fingerprint(...)` 对**规范化后的路由元数据**做拼接（TaskType、required capabilities、required provider/model、
allowed roles、preferred agent、flags、agent 的 provider/model/capabilities/状态）
⇒ **「同一 snapshot 输入 ⇒ 确定性相同结果」在 Agent 路由层已成立**。

**但维度缺口确实存在**：现路由维度只有 `TaskType → {AllowedRoles, RequiredProviderId, RequiredModelId, capabilities}`，
**不含 phase、complexity、risk**；也**不含 health/TTFT/cost/cache**。

### 前提 B（卡片）：「新增 … **per-WorkUnit route snapshot**」
**already exists（以 Run/Graph 为粒度）**：
- `Source/PuddingCore/Platform/ExecutionRunContracts.cs:126` `public sealed record LlmRouteSnapshot(`
- `Source/PuddingCore/Orchestration/AgentOrchestrationModels.cs:230` `public bool RequireFrozenRoutes { get; init; } = true;`（**默认要求冻结**）
- `Source/PuddingCore/Orchestration/AgentOrchestrationGraphCompiler.cs:404` `if (options.RequireFrozenRoutes)`
- `LlmRouteSnapshot` 随调用链传递：`PuddingToolContracts.cs:201` / `ITurnExecutor.cs:53` /
  `ToolInvocationContracts.cs:36` / `MessageContracts.cs:217` 均带 `CallerLlmSnapshot`
- `Source/PuddingRuntime/Services/FrozenVisionContextAccessor.cs` 注释逐字：
  「Agent 执行入口把 **Run 启动时冻结的 `LlmRouteSnapshot`**（由 AgentExecutionSnapshot…）」并 `Push(...)`
⇒ **冻结路由快照机制已实现**；缺的是**快照内是否含 quality floor / reason / health snapshot**（待 step2 读字段）。

## 2. 已具备的其它相关能力

### 2.1 能力标签路由：**确定性排序**
`Source/PuddingPlatform/Services/FileLlmResolver.cs`（281 行）文档逐字：
> 文件配置 LLM 路由解析器。模型选择和 Provider 身份只来自 `ILlmConfigService`，不访问数据库，
> 也不从 endpoint、密钥或 model 字符串反推 Provider。

`ResolveRouteAsync(modelRoute, requiredCapabilityTags)`：
- 显式 `providerId/modelId` ⇒ `ResolveRequired`；
- 裸 `modelId` ⇒ 多 provider 命中即**报错**（「exists under multiple providers」）；
- **仅给能力标签** ⇒ 过滤 `CapabilityTags` 全含后 **`OrderBy(SortOrder).ThenBy(ProviderId).ThenBy(ModelId)` 取首个**
  ⇒ **确定性的能力路由已存在**（criterion 2 的模型层一半）。

### 2.2 provider/model 级限流：**已实现且有观测量**
- `Source/PuddingRuntime/DependencyInjection.cs:137` `services.AddSingleton<ProviderRateLimiter>();`
- `Source/PuddingRuntime/Services/DirectLlmClient.cs`：`ExecuteAsync(config.ProviderId, config.Model, …)`（等待式）
  与 `AcquireAsync(...)`（lease 式，`ProviderRateLimitLease`），并把 **`WaitMs`** 写入事件 metadata
  （`BuildRateLimitMetadata`，`:408-464`）
⇒ **criterion 6 的「调度容量等待 P95 <30s」已有现成数据源（`rate limit WaitMs`）**，待 step2 定位其落库位置。

### 2.3 熔断先例（Goal 层）
`GoalSettlementStore.cs:1517` `private const string NoProgressCircuitOpenBlockerCode = "no_progress_circuit_open";`
`:1303`/`:1702` `events.Add(new(GoalEventTypes.CircuitOpened, …))`
⇒ 可作为 criterion 3（401/403/model-unavailable/rate-limit 差异化熔断）的**既有模式参照**。

## 3. **未命中**（字面检索为零，标注为「未以该命名实现」）
| 目标 | 检索结果 |
|---|---|
`RoutePolicy` | PuddingPlatform 未命中（卡片的拟新增项） |
`ModelCapabilityProfile` | PuddingRuntime 未命中（卡片的拟新增项） |
`Ewma` / `EWMA` / `ProviderHealth` / `HealthSnapshot`（provider 级） | 未命中；Runtime 命中的均为 **ContextHealth**（上下文健康），非 provider 健康 |
`CircuitOpen`（provider/model 级） | 仅命中 Goal 层 `no_progress_circuit_open` |

**限定**：上述检索在 `PuddingRuntime`/`PuddingPlatform` 两工程内进行；`search_grep` 有**2000 文件/次上限**，
部分调用返回 `coverage=partial`，故**零命中不等于功能不存在**（可能换名或落在其它工程）。

## 4. 终端判定
- **Step 1（Explore）= COMPLETE**：给出「已存在（位置+逐字证据）/ 部分存在（维度缺口）/ 未命中」三类结论，
  并**修正卡片的两处前提**（taskType 确定性路由、route snapshot 冻结）。
- **Task = NOT COMPLETE**：step 2–5 未开始。**自述判定，终局由服务端 verifier 裁决。**

## 5. 诚实限定
- 全部为**只读**（源码检索 + 阅读）；**未运行测试、未改任何代码**。
- 「已存在」的结论均附**文件:行**；「未命中」均附**检索范围**与覆盖率限定。
- 本轮**未取证运行时数据**（真实 tok/s、route 落库、WaitMs 分布）—— 留给 step2/step3。
