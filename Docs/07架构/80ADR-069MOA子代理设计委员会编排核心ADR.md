# ADR-069 MOA 子代理设计委员会编排核心

> 状态：**phase-1-implemented**  
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

因此引入 MOA（Mixture of Agents）设计委员会核心。Phase 1 只编译计划，不启动子代理。

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

## 4. 后续阶段

### Phase 2：执行状态机

- 增加 plan activation、stage transition、pause/resume、cancel；
- 提交上下文审计结果时识别 critical gap，进入 `AwaitingUserInput`；
- 按 `MaxConcurrency` 派发 ready work item；
- 成员失败时重新计算法定人数，不做模型替换；
- 只有满足阶段门禁时才开放下一阶段。

### Phase 3：运行时适配

- 将 work item 映射到 `ISubAgentInvocationService`；
- 复用现有 sub-agent run archive、deadline、权限和父会话投影；
- 为所有调用写入 plan/stage/work item/member/route 关联；
- 不新增第二套子代理执行引擎。

### Phase 4：持久化、工具与 UI

- 文件作为专家组配置权威；
- SQLite/运行归档作为运行索引与审计事实；
- 新增 `smart_design_council` 工具；
- Admin 展示设计请求、阶段、成员、提案、批判、综合和终审；
- Skill 只负责识别使用时机和构造请求，不再硬编码模型或直接编排 spawn。

## 5. 非目标

Phase 1 不包含：

- 新增前端或后端专家配置页面；
- 修改 Agent manifest；
- 实际调用 Kimi K3、GLM-5.2 或其他模型；
- 自动搜索互联网；
- 自动修改代码；
- 数据库迁移；
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
