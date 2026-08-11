# ADR-069 MOA 子代理设计委员会编排核心

> 状态：**phase-3-implemented; generic-graph-adapter-implemented**
> 日期：2026-08-09  
> 范围：设计请求、专家组、模型多样性、阶段门禁、独立提案、交叉批判、综合与终审  
> 前置：[11 工作流与任务图](11工作流与任务图.md)、[21 子代理工作空间与运行归档](21子代理工作空间与运行归档ADR.md)、[24 核心架构组件边界与执行引擎拆分](24核心架构组件边界与执行引擎拆分ADR.md)

## 1. 背景

Pudding 已有 `spawn_sub_agent`、批量子代理调用、任务规划上下文、运行归档和会话投影，但这些基础设施只解决“怎样执行一个子代理”。它们没有定义多个不同模型怎样围绕同一个设计问题独立工作、互相批判，并形成可审计结论。

原 `smart-committee-workflow` 属于顺序式 Skill 流程。它可以指导主代理依次调用角色，但不能硬性保证：

- 所有成员收到同一份不可变请求；
- 提案阶段彼此隔离；
- 专家组具有真实的模型多样性；
- 批判不能由提案作者自评；
- 关键上下文缺口会暂停流程并回到用户；
- 主席综合和独立终审由不同成员完成；
- 编译计划不会被误认为已经授权执行。

因此引入 MOA（Mixture of Agents）设计委员会核心。Phase 1 编译计划，Phase 2 用纯状态机执行门禁，Phase 3 通过运行时适配器复用现有子代理执行链路。后续通用化决策见 [ADR-070](81ADR-070通用Agent编排图基础架构ADR.md)：MOA 保留专业规则，但不长期维护第二套调度内核。

## 2. 决策

### ADR-069-A：先形成规范化设计请求

主代理必须先产生 `DesignRequest`，最少包含：

- 用户意图和问题陈述；
- 用户意图的证据；
- 已知上下文、约束和非目标；
- 疑似上下文缺口；
- 调研问题；
- 验收标准和交付物。

编译器不接受缺少用户意图、问题陈述、验收标准或交付物的请求。主代理不能先选择一个答案，再让专家组为该答案背书。

### ADR-069-B：专家组使用精确模型路由

`ExpertGroupMemberDefinition.RouteKey` 是调用前已经解析的精确 `provider/model` 路由。MOA 配置必须满足：

- `AllowFallback=false`；
- 提案成员数达到法定人数；
- 提案成员的不同路由数达到模型多样性门槛；
- 主席具有综合能力；
- 终审成员不是主席、不是提案作者，并使用不同于主席的路由。

原因是隐式 fallback 会让“多个模型达成的结论”退化成同一个模型的多个会话，破坏法定人数和多样性语义。成员失败只能记录为缺席或失败，不能静默换模型。

### ADR-069-C：计划是带门禁的 DAG

标准阶段固定为：

```text
上下文审计
  -> 市场与案例调研
    -> 独立提案
      -> 交叉批判
        -> 主席综合
          -> 独立终审
```

每个阶段声明自己的依赖和门禁：

| 阶段 | 门禁 | 核心规则 |
|------|------|----------|
| 上下文审计 | `ContextResolved` | 发现关键缺口时暂停，等待用户输入 |
| 调研 | `EvidenceAvailable` | 事实、案例、来源与推断必须分离 |
| 独立提案 | `ProposalQuorum` | 达到成功提案数和不同模型路由数 |
| 交叉批判 | `CritiqueCoverage` | 每份提案获得规定数量的非自评批判 |
| 主席综合 | `ChairSynthesis` | 记录采纳、拒绝、分歧和原因，不使用简单多数投票 |
| 独立终审 | `IndependentFinalReview` | 由独立模型做对抗性终审 |

独立提案只能看到规范化请求和调研证据，不能看到其他提案。批判任务只能看到目标提案及证据。主席和终审可以看到全部前序输出。

### ADR-069-D：编译不等于执行授权

`DesignCouncilPlanCompiler` 是纯函数组件：

- 不依赖数据库、文件系统、DI 或网络；
- 不调用 `ISubAgentInvocationService`；
- 不写任务计划和运行归档；
- 返回 `Draft` 状态计划；
- `RequiresExplicitActivation=true`。

后续执行器必须经过显式激活，才能把计划中的 work item 映射到现有 `ISubAgentInvocationService`。这样 UI、主代理或策略服务可以先展示和检查计划，不会“一生成就开跑”。

### ADR-069-E：设计和评审阶段默认只读

所有 Phase 1 work item 都带 `IsReadOnly=true`。专家成员可以读取请求、代码和调研材料，但不能在设计委员会阶段修改文件。只有综合设计被用户或策略明确批准后，后续实施工作流才能产生可写任务。

## 3. Phase 1 实现

代码位置：

- `Source/PuddingCore/Orchestration/SubAgentOrchestrationModels.cs`
- `Source/PuddingCore/Orchestration/DesignCouncilPlanCompiler.cs`
- `Source/PuddingCoreTests/Orchestration/DesignCouncilPlanCompilerTests.cs`

当前编译器实现：

- 请求与成员配置的机器可读校验问题；
- 精确路由、禁用 fallback 和提案模型多样性校验；
- 六阶段 DAG；
- Double Review 和 All-to-All 两种批判拓扑；
- 自评、同路由评审和主席预评审隔离；
- 主席综合与独立终审分离；
- 只读 work item 和显式激活门禁；
- 稳定、可检查的 stage/work item ID。

## 4. 运行时阶段

### Phase 2：执行状态机（已完成）

实现位置：

- `Source/PuddingCore/Orchestration/SubAgentOrchestrationRuntimeModels.cs`
- `Source/PuddingCore/Orchestration/DesignCouncilRunStateMachine.cs`
- `Source/PuddingCoreTests/Orchestration/DesignCouncilRunStateMachineTests.cs`

已实现：

- `Draft` 计划只能通过显式 `Activate` 开始；
- 只有当前阶段的 ready work item 可以领取；
- `MaxConcurrency` 是整个 MOA run 的硬上限；
- 每次领取产生 claim ID，完成结果必须匹配当前 claim，拒绝迟到或外部结果；
- 关键上下文缺口进入 `AwaitingUserInput`，暂停新领取，但仍接受已经运行成员的回报；
- 用户回答以 `ContextResolution` 进入运行快照，`Resume` 后重新计算当前门禁；
- 成员失败后计算剩余成功数和不同模型路由数，法定人数不可达时立即失败；
- 达到提案法定人数时，失败提案对应的批判任务自动 `Skipped`；
- 每个成功提案仍必须达到批判覆盖数；
- 支持成功完成和显式取消终态；
- 运行快照带单调递增 `Version`，为后续持久化乐观并发提供基础。

状态机保持纯函数边界：不访问数据库、不调用模型、不生成子代理 run，也不静默替换失败成员。

### Phase 3：运行时适配（已完成）

实现位置：

- `Source/PuddingCore/Orchestration/SubAgentOrchestrationRuntimeContracts.cs`
- `Source/PuddingRuntime/Services/InMemorySubAgentOrchestrationRunStore.cs`
- `Source/PuddingRuntime/Services/DesignCouncilRuntimeService.cs`
- `Source/PuddingRuntimeTests/Services/DesignCouncilRuntimeServiceTests.cs`

运行时遵循以下边界：

- `IDesignCouncilRuntimeService` 负责 create、activate、dispatch、resume 和 cancel；
- `ISubAgentOrchestrationRunStore` 使用快照 `Version` 做乐观并发，claim 必须先成功持久化，才能启动子代理；
- 当前 `InMemorySubAgentOrchestrationRunStore` 只保证单进程并发一致性，不声称支持 Core 重启恢复；
- `DesignCouncilRuntimeService` 从 work item 的 `RouteKey` 拆出精确 `provider/model`，直接通过 `ILlmConfigService.Resolve(provider, model)` 获取模型配置，不经过 profile 默认值、能力选模或 fallback；
- 实际执行只调用现有 `ISubAgentInvocationService`，继续复用 sub-session、run archive、deadline、调度额度和父会话投影；
- child 的 `runId`、`subSessionId`、完整输出和归档引用回填到 MOA run 快照；
- 每个成员使用同步调用返回结果，但同一批 claim 并行等待；整体并发仍受计划 `MaxConcurrency` 和现有子代理调度额度双重约束；
- 独立提案只注入规范化请求和调研输出；批判只注入调研证据及指定目标提案；主席和终审只读取前序成功输出；
- 设计成员获得显式只读工具 allowlist，不包含 shell、文件写入和再次派生子代理；
- 结构化 `pudding-moa-member-result` 结果可报告 `contextGaps`、`requiresUserInput` 和 `blockingQuestions`，并驱动既有暂停/恢复状态机；
- 未配置的精确路由、调用失败、超时或无输出都记录为该成员失败，不静默换模型。

组合根在 `PuddingRuntime/DependencyInjection.cs` 和 `PuddingHost/Extensions/PuddingServiceCollectionExtensions.Platform.cs` 注册同一套运行时服务，不新增平行执行引擎。

### Phase 3B：通用编排图适配（已完成）

实现位置：

- `Source/PuddingCore/Orchestration/AgentOrchestrationModels.cs`
- `Source/PuddingCore/Orchestration/AgentOrchestrationGraphCompiler.cs`
- `Source/PuddingCore/Orchestration/DesignCouncilOrchestrationGraphAdapter.cs`

MOA 的 stage 被映射为通用 gate 节点，work item 被映射为冻结精确路由的只读 subAgent 节点；控制依赖和输出可见性分别使用 control/data edge 表达。提案之间没有 data edge，批判只连接研究输出和目标提案。当前 MOA 专用状态机仍作为行为基线，待通用持久化运行内核具备同等语义后迁移。

### Phase 4：持久化、工具与 UI

- 文件作为专家组配置权威；
- SQLite/运行归档作为运行索引与审计事实；
- 复用通用 `orchestration.*` 工具，MOA 只提供设计请求到图的模板编译入口；
- Admin 展示设计请求、阶段、成员、提案、批判、综合和终审；
- Skill 只负责识别使用时机和构造请求，不再硬编码模型或直接编排 spawn。

## 5. 非目标

Phase 1–3 不包含：

- 新增前端或后端专家配置页面；
- 修改 Agent manifest；
- 实际调用 Kimi K3、GLM-5.2 或其他模型；
- 自动搜索互联网；
- 自动修改代码；
- 数据库迁移；
- Core 重启后的 MOA run 恢复；
- 自动回收进程崩溃前遗留的 Running claim；
- 新增外部工作流引擎。

## 6. 验收基线

- 编译结果保持 `Draft`，必须显式激活；
- 上下文审计是第一道硬门禁；
- 三个提案可以来自三个不同精确路由；
- 提案之间没有可见性依赖；
- Double Review 为每个提案分配两个非作者、不同路由的批判者；
- 主席不参与预批判；
- 终审模型独立于主席且不是提案作者；
- fallback、多样性不足和终审不独立时编译失败且不产生半成品计划。
- 未激活的 run 不可领取任务；
- 暂停状态不可领取新任务，用户补充上下文后可恢复；
- claim 不匹配的完成结果不会改变快照；
- 并发任务数不超过计划上限；
- 提案法定人数或不同模型路由数不可达时进入 `Failed`；
- 所有阶段通过后进入 `Completed`。
- claim 快照写入冲突时不启动子代理；
- 相同 ready work item 面对并发 dispatcher 只会被调用一次；
- work item 的精确 `provider/model` 配置不存在时直接失败，不允许 fallback；
- child `runId`、`subSessionId` 和完整输出可从运行快照追溯；
- 结构化关键上下文缺口会进入 `AwaitingUserInput`，补充信息持久化后继续下一门禁。
