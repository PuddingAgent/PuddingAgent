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

- Agent 实例生命周期管理：创建、加载、休眠、恢复、销毁。
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

- `Ready`：已加载，可接收执行。
- `Running`：正在执行模型、工具或子任务。
- `WaitingEvent`：等待事件命中。
- `WaitingApproval`：等待审批或人工确认。
- `WaitingSubAgent`：等待子 Agent 结果。
- `Sleeping`：进入可恢复休眠态。
- `Completed`：本轮执行结束。
- `Failed`：本轮失败，等待重试或人工处理。
- `Frozen`：被控制面冻结，不可继续执行。

建议原则：

- 同一 `AgentExecutionContext` 在任一时刻只应有一个主执行片段处于 `Running`。
- `WaitingEvent`、`WaitingApproval`、`WaitingSubAgent` 都属于“未结束但不可继续空转”的挂起态。
- `Sleeping` 是可恢复态，不等于销毁。
- `Frozen` 必须由控制面显式解除，而不是 Runtime 自己偷偷恢复。

## 事件订阅在 Runtime 内部如何落地

Agent 声明了订阅偏好，并不等于 Runtime 已经能稳定消费。中间至少还需要一层运行时落实模型。

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

这样可以避免把“订阅声明”“治理结果”“运行时命中结果”混成同一个对象，最后每个人都自称自己是真相。那样的系统一般也确实会产生很多“真相”。

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
