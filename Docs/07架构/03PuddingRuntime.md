# PuddingRuntime

## 定位

PuddingRuntime 是执行面与数据平面，是可独立部署的运行时宿主。它承载 Session、AgentInstance、技能运行、记忆处理和受限执行环境。

它在系统中的核心职责不是“做控制决策”，而是把 Controller 已经决定好的事情真实执行出来，并把执行过程中产生的上下文、状态、事件与结果稳定承载下来。

如果用更直接的话说：`PuddingRuntime` 本身就是平台里的 Agent Runtime，而不是在它里面再平行抽出一个独立的“Runtime of Runtime”。

`PuddingRuntime` 同时支持两种宿主形态：

- 独立宿主：作为独立进程运行的 Runtime 节点（本阶段的设计目标）。
- 嵌入宿主：嵌入到其他 C# 开发的桌面软件中，把该桌面软件视为一个可运行环境和可调度节点（不是本阶段的设计目标）。

## 子模块

- SessionRuntime：负责会话级状态、隔离和元数据。
- AgentExecutionContext：作为 `PuddingRuntime` 内部的单 Agent 执行视角，负责主 Agent 与其派生 sub_agent 的执行上下文。
- PuddingMemoryEngine：`PuddingRuntime` 内模块，负责召回、压缩、候选写回、污染筛查与记忆边界执行。
- SkillRuntime：负责基础 Skill 注入与执行上下文。
- McpRuntime：负责 MCP Server 连接、能力发现与调用路由。
- PluginRuntimeSupport：负责插件运行时上下文与宿主能力桥接。
- NativeHostBridge：负责把嵌入宿主的原生能力暴露为受控 Runtime 能力。
- SandboxExecutor：负责高风险动作的受限执行。

## 建议补齐的运行时内核抽象

如果 `PuddingRuntime` 要真正成为稳定的 Agent Runtime，仅靠“几个模块拼在一起”还不够，至少还需要一层更明确的运行时内核抽象。

建议最小内核对象：

- `RuntimeNodeHost`：Runtime 节点宿主，负责进程级生命周期、配置、依赖装配、健康状态与节点注册。
- `RuntimeDispatchInbox`：接收来自 Controller 的执行投递、事件命中、取消、冻结、恢复等控制指令。
- `SessionSupervisor`：管理 Session 级执行边界、并发控制、锁与恢复入口。
- `AgentExecutionCoordinator`：把一次 Agent 执行拆成若干有序步骤，负责调度模型调用、工具调用、记忆召回和事件发布。
- `WakeupDispatcher`：消费事件命中结果，将“订阅命中”转换为“恢复哪个 Agent / Session / Workflow Worker”。
- `CapabilityAssembler`：根据 AgentTemplate、Workspace 策略和 Controller 下发的授权结果装配 Skill、MCP、插件和宿主能力。
- `ExecutionJournal`：记录执行阶段、关键输入摘要、状态迁移、失败原因、恢复点。
- `RuntimeTelemetryReporter`：负责心跳、负载、能力、执行结果、异常与观测指标上报。

这里的关键思想是：**Runtime 不只是一个执行函数集合，而是一个有 inbox、有 supervisor、有 journal 的长期运行宿主。**

## PuddingRuntime 应具备的 Agent Runtime 能力

- Agent 实例生命周期管理：创建、加载、运行、忙碌、睡眠、休眠、归档、恢复、销毁。
- 主 Agent 与 sub_agent 承载：一个 `PuddingRuntime` 节点可承载多个 Session，每个 Session 内可承载多个 Agent，以及由主 Agent 派生出的临时 sub_agent。
- 单轮与多轮执行编排：组装输入、调用模型、处理工具与结果。
- 短期上下文窗口管理：裁剪、摘要、预算控制、上下文恢复。
- 调用 PuddingMemoryEngine 处理记忆召回、候选写回和边界校验。
- 装配模板声明的 Skill、MCP、插件运行能力与工具能力。
- 提供事件订阅与事件分发能力，让 Agent、sub_agent、Workflow Worker 可以订阅自己关心的事件类型，而不是持续轮询外部状态。
- 支持基于事件的直接唤醒：当订阅命中时，Runtime 可以恢复休眠中的 Agent、补齐上下文并触发后续执行。
- 当 Runtime 嵌入桌面软件时，能够把宿主软件的原生能力以受控方式暴露给 Agent，例如查询软件状态、驱动自动化测试、读取宿主对象模型或调用宿主命令。
- 落实执行期权限与沙箱约束，而不是决定治理策略本身。
- 输出运行状态、错误、工具事件、记忆候选事件，供 Controller 与审计链路消费。

## Runtime 必须完成的功能

从产品落地角度看，`PuddingRuntime` 至少必须完成以下几类功能：

1. **执行承载**
	- 承载 Session
	- 承载主 Agent 与 sub_agent
	- 承载单轮与多轮执行

2. **上下文与记忆管理**
	- 维护短期上下文窗口
	- 调用记忆引擎完成召回与候选写回
	- 防止不同 Session / Workspace 执行态混杂

3. **能力装配与受限执行**
	- 装配 Skill、MCP、插件运行能力
	- 对高风险动作应用沙箱、限权与执行期约束

4. **事件驱动执行**
	- 接收 Controller 路由后的事件
	- 让 Agent 声明订阅关系
	- 在订阅命中时恢复 Agent 并继续执行，而不是依赖轮询

5. **状态上报**
	- 上报健康状态、负载、能力、执行结果和异常事件
	- 让控制面和审计链路可以看到运行态，而不是把 Runtime 变成黑盒

6. **恢复与续跑**
	- 在进程重启、节点漂移或执行中断后恢复 Session 与 Agent 的最小执行态
	- 至少能判断哪些执行已完成、哪些执行可重试、哪些执行必须人工介入

## Runtime 核心执行管线

为了避免后续实现时把所有逻辑堆进一个 `ExecuteAsync`，建议把 Runtime 的最小执行链明确定义为：

1. **接收投递**
	- `RuntimeDispatchInbox` 接收来自 Controller 的执行请求、事件唤醒请求或取消/冻结控制指令。

2. **命中运行边界**
	- `SessionSupervisor` 定位或创建 `SessionRuntime`
	- 定位目标 `AgentExecutionContext`
	- 校验当前 Session / Agent 是否处于允许执行状态

3. **恢复执行态**
	- 如果来自事件唤醒，则根据 `ExecutionJournal`、上下文快照、最近一次暂停点恢复最小执行态
	- 如果是新请求，则建立新一轮执行上下文

4. **装配能力与上下文**
	- 装配 AgentTemplate 对应的 Skill、MCP、插件与宿主能力
	- 调用 `PuddingMemoryEngine` 做最小召回、候选上下文补齐与边界校验
	- 生成本轮执行预算、权限视图与工具白名单

5. **执行单轮或多轮步骤**
	- 模型推理
	- 工具调用
	- sub_agent 派生与回收
	- Workflow worker 步骤推进

6. **沉淀执行结果**
	- 输出消息、结构化结果、工具结果、记忆候选、审计事件
	- 写入 `ExecutionJournal` 和必要的状态快照

7. **发布运行事件**
	- 发布 `agent.completed`、`agent.failed`、`tool.completed`、`memory.candidate.created` 等内部事件
	- 向 Controller 回报状态与结果摘要

8. **进入后续状态**
	- 结束、休眠、等待事件唤醒、等待审批、等待 sub_agent 回传，或进入失败恢复流程

## Runtime 的最小阶段目标

第一阶段不要求 `PuddingRuntime` 一开始就成为万能宿主，但至少要做到：

- 能承载真实 Session 与真实 Agent 回复
- 能处理最小记忆召回与候选写回
- 能装配最小工具/技能执行链
- 能接收至少 1 类事件并完成直接唤醒
- 能持续向 Controller 报告节点状态
- 能在最小范围内保留执行日志与恢复点，而不是进程一停就完全失忆

## Runtime 内部事件视角

在 `PuddingRuntime` 内部，万物皆事件不是一句口号，而是一种执行组织方式。至少包括：

- Agent 生命周期事件：创建、唤醒、休眠、销毁、迁移。
- 执行事件：任务开始、步骤完成、工具完成、子任务完成、失败重试。
- 协作事件：主 Agent 指派 sub_agent、sub_agent 回传结果、Swarm 协调完成。
- 节点事件：心跳、负载变化、能力变化、降级与恢复。
- 数据事件：记忆候选写回、知识命中、上下文摘要完成、审计记录提交。

这些事件可以同时被 Runtime 内部组件、同 Workspace 的 Agent，以及控制面的审计/治理链路消费。

这里不再把 `AgentRuntime` 视为一个与 `PuddingRuntime` 平级的独立模块；如果后续还保留这个词，它更适合表示 `PuddingRuntime` 内部的单 Agent 执行视角，而不是新的宿主层。

## Runtime 状态机建议

如果没有清晰状态机，Runtime 很容易在“收到事件时能不能再次执行”“等待审批算不算运行中”“sub_agent 回来了主 Agent 要不要恢复”这些问题上陷入混乱。

建议至少区分以下状态：

### SessionRuntime 状态

- `Created`
- `Active`
- `Idle`
- `Suspended`
- `Closing`
- `Closed`
- `Failed`

### AgentExecutionContext 状态

- `Created`：实例已创建，正在装配能力与最小状态。
- `Running`：在线可用，可接收新任务或普通投递。
- `Busy`：正在执行模型、工具、工作流步骤或协作任务。
- `WaitingEvent`：等待事件命中。
- `WaitingApproval`：等待审批或人工确认。
- `WaitingSubAgent`：等待子 Agent 结果。
- `Sleeping`：进入轻量可恢复睡眠态，仍可接受已批准事件或显式唤醒。
- `Hibernated`：进入深度休眠态，不接受普通事件，运行资源可回收。
- `Frozen`：进入冻结 / stop 态，不接受普通执行、事件唤醒和自动恢复；对应容器应被停止。
- `Archived`：进入归档态，运行资源与渠道绑定被释放，数据被打包保留。
- `Completed`：本轮执行结束。
- `Failed`：本轮失败，等待重试或人工处理。
- `Destroyed`：终态，仅能由归档态进入。

建议原则：

- 同一 `AgentExecutionContext` 在任一时刻只应有一个主执行片段处于 `Busy`。
- `WaitingEvent`、`WaitingApproval`、`WaitingSubAgent` 都属于“未结束但不可继续空转”的挂起态。
- `Running` 表示在线可用，不等于“正在忙”。
- `Busy -> Running` 应由 Runtime 在任务完成、交付完成或显式 `request_afk` 后完成。
- `Sleeping` 是可恢复态，不等于销毁。
- `Hibernated` 不接受普通事件，恢复成本高于 `Sleeping`。
- `Frozen` 是安全优先的 stop 态，可由用户或审计 Agent 触发，恢复必须显式执行。
- `Archived` 会冻结 Agent 并回收运行资源，但保留打包后的数据。
- `Frozen` 必须由控制面显式解除，而不是 Runtime 自己偷偷恢复。
- `Destroyed` 只能从 `Archived` 进入，且只能由用户发起。

## 单 Agent 冻结与 Workspace 全局冻结

当用户或具有权限的审计 Agent 觉察到潜在入侵、上下文污染、泄露风险或高危越权动作时，Runtime 必须支持两类 stop 语义：

1. **单 Agent 冻结**
	- 将指定 AgentExecutionContext 迁移为 `Frozen`
	- 停止该 Agent 对应的 Docker 容器或 sandbox 实例
	- 阻断其事件投递、自动唤醒和后续执行

2. **Workspace 全局冻结**
	- 对指定 Workspace 内全部 Agent 执行统一 stop
	- 批量停止其对应 Docker 容器或 sandbox
	- 将 Workspace 标记为紧急冻结态，直到显式恢复

这类动作本质上属于 **紧急安全治理链路**，优先级高于普通任务调度、事件订阅和自动恢复策略。

在实现方式上，Runtime 不应假设 Controller 会逐个点对点调用自己。更推荐的实现原则是：

- Controller 发布全局治理事件，例如 `workspace.freeze.requested`
- Runtime 订阅该类全局事件
- Runtime 根据事件中的 `workspaceId` / `agentId` 检查自己当前承载的 Agent
- 若命中，则冻结这些 Agent 并 stop 对应容器
- 若未命中，则忽略该事件

也就是说，**Runtime 是治理事件的订阅执行者，而不是被动等待逐节点 RPC 的黑盒。**

## 冻结与恢复控制指令

建议 Controller -> Runtime 至少支持以下控制指令：

- `FreezeAgentRequest`
- `ResumeAgentRequest`
- `FreezeWorkspaceRequest`
- `ResumeWorkspaceRequest`

若采用事件广播实现，这些对象也可以作为全局治理事件的 payload，而不一定要求 Controller 与每个 Runtime 建立独立点对点调用关系。

恢复不应被视为简单状态翻转，还应包括：

- 校验当前 Workspace 是否仍处于冻结态
- 校验恢复发起者权限
- 根据绑定表重新启动 Docker 容器或重建 sandbox
- 恢复 AgentExecutionContext 与渠道绑定

## Runtime 内置生命周期工具

除了控制面指令之外，Runtime 还应提供少量内置生命周期工具或等价运行时能力，让 Agent 能对自己的生命周期提出请求：

- `request_afk`
- `request_sleep`
- `request_hibernate`
- `request_wake`

### `request_afk`

- 用于任务完成、交付完成或需要释放忙碌占用时，请求从 `Busy` 回到 `Running`
- 可附带交付结果、任务完成摘要与恢复建议

### `request_sleep`

- 用于主动进入 `Sleeping`
- Runtime 保存最小热状态、上下文窗口与恢复锚点

### `request_hibernate`

- 用于进入 `Hibernated`
- Runtime 保存耐久状态后可回收容器、沙箱或运行资源
- 进入后不再接收普通事件，只能显式唤醒

### `request_wake`

- 用于从 `Sleeping` 或 `Hibernated` 恢复到 `Running`
- 对 `Hibernated` 的恢复通常需要重建运行资源

这些能力不意味着 Agent 拥有绝对决定权，而是意味着 Runtime 至少提供一套受控接口，允许 Agent 表达自己的运行意图。最终是否执行，仍受当前权限、Workspace 策略和控制面治理约束。

## 归档与销毁在 Runtime 的资源语义

### 归档

- 停止并冻结 AgentExecutionContext
- 回收运行容器、沙箱和临时资源
- 释放渠道绑定表等运行关系
- 保留数据并打包压缩
- 若存在冷归档对象存储，可上传归档数据节省 Runtime 空间

### 销毁

- 只能对 `Archived` Agent 执行
- 由用户发起，Runtime 不应接受 Agent 自行销毁自己
- 移除归档数据，但应保留最小审计与治理痕迹

## 事件订阅在 Runtime 内部如何落地

Agent 声明了订阅偏好，并不等于 Runtime 已经能稳定消费。中间至少还需要一层运行时落实模型。

这里要再强调一个边界：

> **事件订阅由 Runtime 完成，Agent 对订阅执行过程整体无感。**

Agent 只会在某轮输入中看到 Runtime 已经插入好的事件消息、恢复提示和上下文补丁，而不会自己去管理订阅连接、消费游标或消息优先级队列。

建议拆成三层：

1. **模板声明层**
	- `AgentTemplate` 声明默认订阅偏好、过滤条件、是否允许直接唤醒。

2. **治理准入层**
	- Controller 根据 Workspace 策略、事件域权限、审批与风险等级生成实际可用订阅。

3. **运行时落实层**
	- Runtime 把“已批准订阅”编译为本地 `WakeupBinding`
	- `WakeupDispatcher` 在事件命中时定位目标 `SessionRuntime` / `AgentExecutionContext`
	- 根据当前状态决定直接恢复、排队、去重、合并或拒绝

建议最小内部对象：

- `ApprovedEventSubscription`
- `WakeupBinding`
- `WakeupDecision`
- `WakeupExecutionRequest`
- `InterruptDecision`
- `PendingMessageQueue`
- `ResumeAnchor`
- `InterruptionRecord`

这样可以避免把“订阅声明”“治理结果”“运行时命中结果”混成同一个对象，最后每个人都自称自己是真相。那样的系统一般也确实会产生很多“真相”。

## 消息事件优先级与打断模型

Runtime 不应把所有事件都当成同一种“新输入”。对于消息型事件，建议至少支持三档优先级：

- `P0`：紧急打断级
- `P1`：当前轮插入级
- `P2`：空闲后投递级

### P0：紧急打断级

- Runtime 可以立即把消息插入当前上下文
- 若当前 LLM 仍处于活跃会话状态，可直接打断当前主执行片段
- 打断前必须写入 `InterruptionRecord` 与 `ResumeAnchor`
- 默认仅允许高权限用户、系统控制链路或特殊授权 Agent 发布

### P1：当前轮插入级

- Runtime 不直接粗暴终止当前会话
- Runtime 在下一次输入组装时，把消息拼入当前轮上下文
- Runtime 同时记录最小恢复锚点，帮助 Agent 处理完插入消息后恢复原工作

### P2：空闲后投递级

- Runtime 若判断当前 Agent 忙碌，则先进入 `PendingMessageQueue`
- 等到会话结束、进入空闲或挂起态后再投递
- 适用于不要求立刻响应的一般消息

关键点：

- 是否允许打断，由 Runtime 根据当前执行态决定
- 是否有资格发布高优先级消息，由 Controller / Policy 决定
- Agent 只接收“Runtime 已决定要让它看的消息”，而不是自己决定是否订阅总线原始流

## 恢复锚点与快速回忆

只要允许消息插入与打断，Runtime 就必须帮助 Agent 恢复原任务链，而不是让 Agent 自己从一堆上下文碎片里考古。

建议 Runtime 维护：

- `ResumeAnchor`：记录被打断前做到哪里
- `ContextCheckpoint`：记录最小上下文摘要或指针
- `PendingMessageQueue`：记录延迟投递消息
- `InterruptionRecord`：记录打断原因、来源事件、优先级与恢复状态

建议 `ResumeAnchor` 至少包含：

- `anchorId`
- `sessionId`
- `agentId`
- `workflowRunId?`
- `interruptedByEventId`
- `interruptedAt`
- `memoryPointer`
- `summary`
- `resumePriority`

其中 `memoryPointer` 指向记忆引擎中的特定位置，帮助 Agent 在处理完插入消息后快速回忆“我上次做到哪里了”。这就像人类被打断时先在 note 上记一句“做到第几步”，回来时不至于重新发明自己。

建议 Runtime 内置恢复能力：

- `recall_interrupted_work`
- `list_resume_anchors`
- `resume_previous_flow`

它们未必都表现为公开工具，但在运行时语义上必须存在，才能让 P1 插入与 P0 打断成为“可恢复的中断”，而不是“优雅一点的事故”。

## 并发、隔离与邮箱模型

`PuddingRuntime` 不应以“谁先抢到线程谁先执行”的方式组织并发。建议采用更接近 Actor / Mailbox 的组织方式：

- **节点级**：`RuntimeNodeHost` 承载多个 Session。
- **Session 级**：每个 `SessionRuntime` 拥有自己的执行邮箱和恢复边界。
- **Agent 级**：`AgentExecutionContext` 在 Session 内运行，但其主执行片段应受串行化约束。
- **sub_agent 级**：允许并发存在，但必须挂靠到主 Agent 的因果链路与预算控制下。

建议基本并发规则：

- 同一 Session 内的主执行链默认串行，避免上下文热态互相覆盖。
- 同一主 Agent 派生的多个 sub_agent 可以并行，但需要共享预算上限与回收时限。
- 事件命中若落到已在 `Running` 的 Agent，可进入去重、排队或合并策略，而不是直接重入。
- 高风险宿主能力调用必须经过单独执行隔离，避免把宿主线程模型污染到主执行链。

## 与 Controller 的交互契约

`PuddingRuntime` 虽然不是控制面，但它也不是一个被动黑盒。它与 Controller 之间至少需要稳定的节点协议。

建议最小交互语义：

### Runtime -> Controller

- `RuntimeNodeRegistration`：节点注册、能力声明、宿主类型声明。
- `RuntimeHeartbeat`：健康状态、负载、容量、版本、降级状态。
- `ExecutionStatusReport`：执行开始、步骤推进、等待态、完成、失败。
- `ExecutionResultEnvelope`：结构化结果、消息结果、错误摘要、审计元数据。
- `RuntimeFaultReport`：崩溃、宿主桥接异常、能力不可用、沙箱失败。

### Controller -> Runtime

- `DispatchExecutionRequest`：普通执行投递。
- `DispatchWakeupRequest`：事件命中后的恢复执行投递。
- `CancelExecutionRequest`：取消执行。
- `FreezeExecutionRequest`：冻结 Session / Agent / Node。
- `ResumeExecutionRequest`：解除冻结或恢复执行。
- `CapabilityRefreshRequest`：刷新策略、模板或授权视图。

`RuntimeNodeRegistration` / `RuntimeHeartbeat` 建议至少包含：

- `runtimeId`
- `hostType`
- `osType`
- `architecture`
- `cpuCores`
- `memoryMb`
- `diskGb`
- `gpuProfile`
- `purposeTags`
- `capabilityTags`
- `sandboxProviders`
- `activeAgentCount`
- `activeWorkspaceCount`
- `loadScore`
- `pressureLevel`
- `supportsDockerSocket`

其中：

- `purposeTags` 用来描述用途，例如 `coding`、`drawing`、`test-runner`
- `capabilityTags` 用来描述环境能力，例如 `windows`、`linux`、`high-memory`、`gpu`、`browser`
- `sandboxProviders` 用来描述可用沙箱实现，例如 `docker`、`wasm`、`gvisor`

建议边界：

- Controller 决定“能不能做”。
- Runtime 决定“现在怎么做最稳定”。
- Runtime 不应绕过 Controller 私自扩大权限或订阅范围。

## Agent 在 Runtime 中如何被组装

`PuddingRuntime` 并不是直接“new 一个 Agent 然后开跑”，而是需要做一次受控组装：

1. 接收 Controller 下发的 `AgentTemplateSnapshot`
2. 接收已批准的 `SkillRef` / `McpRef` / `HostCapabilityRef`
3. 比对当前 Runtime 节点能力与模板偏好
4. 选择或创建对应的 sandbox / 宿主执行环境
5. 构建 `AgentExecutionContext`
6. 进入可运行或可挂起状态

建议最小对象：

- `AgentTemplateSnapshot`
- `AgentAssemblyPlan`
- `AgentCapabilityView`
- `AgentExecutionContext`

这里特别重要的一点是：

- `PuddingAgent` 提供的是定义
- `PuddingRuntime` 生成的是实例
- `PuddingController` 决定的是放置与授权

三者都重要，但不要让其中任何一个假装自己能代替全部。

## 沙箱抽象与隔离级别

虽然第一阶段最现实的沙箱实现是 Docker，但 Runtime 不应把“沙箱 = Docker 容器”写死在架构里。

更稳定的抽象应是：

- `ISandboxProvider`
- `SandboxInstance`
- `SandboxBinding`
- `SandboxExecutionChannel`

首批可实现提供者：

- `DockerSandboxProvider`

未来可以替换或补充为：

- Firecracker VM
- WASM sandbox
- gVisor

### 隔离级别

建议模板或创建参数支持三档隔离级别：

1. `none`
	- Agent 直接运行在 Runtime 宿主环境
	- 适用于低风险、强性能要求或受信内网环境

2. `workspace`
	- 同一 Workspace 的 Agent 尽量复用一个 sandbox
	- 但若模板偏好、OS 要求、资源要求差异明显，则允许拆分

3. `dedicated`
	- 单 Agent 独享一个 sandbox
	- 建议作为默认创建策略，除非用户显式降低隔离级别

### Docker 作为首批实现

如果采用 Docker 作为首批实现，运行形态更像：

- Runtime 节点负责 create / exec / kill sandbox
- Agent 的 shell / bash / 流式输入输出被绑定到 sandbox 内执行通道
- 容器只是 sandbox 的一种实现，而不是架构唯一真相

当 Runtime 自身也运行在 Docker 中时，可以在受控前提下采用 Docker-from-Docker，即挂载宿主 `docker.sock`。但这属于实现策略，不应上升为架构唯一依赖。

## Runtime 必须维护的绑定表

只要 Runtime 开始使用 sandbox，就不能只靠“容器名字差不多像就行”来管理执行环境。

建议 Runtime 至少维护两张运行时绑定表：

### 1. Agent -> Sandbox 绑定表

用于回答：

- 某个 Agent 当前绑定到哪个 sandbox
- shell / bash / stdio / stream 请求应被路由到哪里
- sandbox 是 `workspace` 共享还是 `dedicated` 独占

建议对象：

- `AgentSandboxBinding`

建议字段至少包含：

- `agentId`
- `workspaceId`
- `sandboxId`
- `sandboxProvider`
- `bindingMode`
- `stdinRoute`
- `stdoutRoute`
- `stderrRoute`
- `attachedAt`
- `lastHealthAt`

### 1A. Agent -> DockerContainer 绑定表

当首批实现使用 Docker 作为默认 sandbox 时，Runtime 应维护更直接的 Agent 与容器绑定台账，便于冻结、恢复、stop 与重建：

- `agentId`
- `workspaceId`
- `runtimeId`
- `containerId`
- `containerName`
- `containerStatus`
- `bindingMode`
- `startedAt`
- `stoppedAt`
- `lastHealthAt`

建议用途：

- 冻结指定 Agent 时，Runtime 能快速找到并 stop 对应容器
- 恢复 Agent 时，Runtime 能重建或重新启动对应容器
- 全局 Workspace stop 时，Runtime 能按 `workspaceId` 批量定位全部容器

### 2. Sandbox 实例台账

用于回答：

- 当前 Runtime 启动过哪些 sandbox
- 哪些还活着
- 哪些失败过
- 哪些已重建或待回收

建议对象：

- `SandboxInstanceRecord`

建议字段至少包含：

- `sandboxId`
- `provider`
- `runtimeId`
- `containerName`
- `status`
- `workspaceId`
- `ownerAgentId`
- `createdAt`
- `lastSeenAt`
- `rebuildCount`
- `failureReason`

若首批实现采用 Docker，可把该台账具体化为 `DockerContainerRecord`，并补充：

- `workspaceId`
- `ownerAgentId`
- `status`：`created / running / stopped / faulted / rebuilding / removed`
- `freezeReason`
- `resumeToken`

这样 Runtime 才能在发生安全冻结时，不只是“记得有个容器”，而是真正知道“哪一个 Agent、哪一个 Workspace、哪个容器现在被 stop 了，是否允许恢复”。

命名建议：

- 独占容器可采用 `agent-{agentId}`
- Workspace 级共享容器可采用 `workspace-{workspaceId}`

## Sandbox 故障与重建模型

Agent 可能因为误操作破坏容器环境，导致 sandbox 不再可用。这类故障不应被当成“普通工具失败”。

建议最小处理链：

1. Runtime 检测 sandbox 健康失败
2. Runtime 记录 `SandboxFault`
3. Runtime 上报 Controller
4. Controller / Platform 向用户或编排 Agent 暴露“重建 sandbox”选项
5. Runtime 创建新 sandbox，并更新 `AgentSandboxBinding`
6. 旧 sandbox 进入隔离或销毁流程

关键原则：

- **Agent 身份不应等于 sandbox 身份**
- sandbox 可以被重建，Agent 的逻辑身份和历史链路应保持稳定
- 是否允许自动重建，应受模板策略、审批和风险等级约束

## 持久化、恢复与最小耐久性

Runtime 是执行宿主，不等于所有状态都必须永久存数据库；但它至少要具备“可恢复”的最小耐久性。

建议至少区分三类状态：

1. **热状态**
	- 当前上下文窗口
	- 当前执行片段
	- 等待态原因

2. **恢复状态**
	- 最近一次可恢复快照
	- 最近一次成功步骤
	- 最近一次事件命中与唤醒原因

3. **审计状态**
	- 关键动作摘要
	- 权限与审批引用
	- 失败与重试痕迹

最小恢复原则：

- 进程重启后，至少能恢复 `WaitingEvent`、`WaitingApproval`、`Sleeping` 这类挂起态。
- 对已完成外部副作用的步骤，恢复时必须有幂等防护。
- 对未确认完成的执行片段，优先标记为 `Recovering` / `NeedsReview` 等待上层决策，而不是盲目重放。

## Runtime 先不要做成什么

为了防止架构膨胀，第一阶段不建议把 `PuddingRuntime` 做成：

- 一个自带全局调度权的“微型 Controller”
- 一个把所有事件治理逻辑都吞进去的“万能事件中心”
- 一个默认永久保存所有细粒度推理痕迹的“超重数据库代理”
- 一个每个 Agent 都自由开线程、自由重入、自由抢资源的“并发游乐场”

第一阶段更现实的目标是：**先把 Session、Agent、事件唤醒、能力装配和恢复边界做稳。**

## Runtime 负责的事

- 作为 Session 权威持有会话热状态。
- 承载实际 Agent、Worker、Swarm 执行。
- 承载主 Agent 派生出的 sub_agent，并负责其创建、回收、结果回传与生命周期约束。
- 管理 Agent 的上下文、自我管理、心跳、休眠与唤醒。
- 将事件订阅关系、唤醒条件和恢复策略落实到运行时执行链中。
- 根据 Controller 已批准的模板快照、Skill/MCP 引用和节点能力，把 AgentTemplate 组装为 AgentInstance。
- 维护本节点的 OS、硬件、用途、性能档位、沙箱能力等节点画像，并通过心跳定时上报给 Controller。
- 在嵌入模式下，把宿主桌面软件注册为一个可调度节点，并向 Controller 报告其原生能力、健康状态和可执行范围。
- 对外报告节点健康、负载、能力与状态。

## Runtime 不负责的事

- 多 Runtime 全局调度。
- 渠道接入与边界认证。
- 租户级治理、审批权威和平台级业务规则。
- 插件是否允许进入某个 Workspace。
- Agent 模板、角色画像和默认策略的定义权威。

如果某个能力需要回答“这个动作在本 Workspace 里是否允许发生”，它通常不应由 Runtime 独自决定。

## 嵌入式 Runtime 节点

当 `PuddingRuntime` 被嵌入到其他 C# 桌面软件时，该桌面软件不再只是一个客户端，而是一个运行环境：

- 平台可以把它当作一个 Runtime 节点调度。
- Controller 可以感知它的节点身份、健康状态、能力集合和宿主类型。
- Agent 可以在审批和权限约束下调用该宿主暴露的原生能力。

建议最小抽象：

- `EmbeddedRuntimeHost`
- `NativeHostBridge`
- `NativeCapabilityDescriptor`
- `RuntimeNodeRegistration`

典型能力：

- 查询桌面软件当前状态、打开文档、窗口、项目或测试上下文。
- 调用软件原生命令或自动化接口。
- 驱动测试软件执行测试、收集结果、读取日志。
- 在不离开宿主环境的情况下完成查询、操作和反馈。

边界：

- 宿主原生能力不是默认全开放能力，必须通过模板权限、Workspace 策略和审批链共同约束。
- 被嵌入的桌面软件虽然是 Runtime 节点，但不自动获得 Controller 权限。
- 对宿主原生能力的调用应可审计、可限制、可冻结。

## 当前迁移方向

当前位于 CLI 中的 SubconsciousEngine、MemoryIndexer、TodoListStore、ReviewQueueStore 应迁移到 Runtime。其中 MemoryIndexer 应收敛为 PuddingMemoryEngine 的子组件，而不是继续作为 CLI 资产存在。

当前结论：`PuddingRuntime` 就是 Agent Runtime。后续如果需要单独描述某个 Agent 的执行容器，应优先使用“AgentExecutionContext”或“单 Agent 执行视角”这类表述，而不是再引入一个与 `PuddingRuntime` 并列的宿主概念。
