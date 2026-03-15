# PuddingRuntime

## 定位

PuddingRuntime 是执行面与数据平面，是可独立部署的运行时宿主。它承载 Session、AgentInstance、技能运行、记忆处理和受限执行环境。

如果用更直接的话说：`PuddingRuntime` 本身就是平台里的 Agent Runtime，而不是在它里面再平行抽出一个独立的“Runtime of Runtime”。

`PuddingRuntime` 同时支持两种宿主形态：

- 独立宿主：作为独立进程运行的 Runtime 节点。
- 嵌入宿主：嵌入到其他 C# 开发的桌面软件中，把该桌面软件视为一个可运行环境和可调度节点。

## 子模块

- SessionRuntime：负责会话级状态、隔离和元数据。
- AgentExecutionContext：作为 `PuddingRuntime` 内部的单 Agent 执行视角，负责主 Agent 与其派生 sub_agent 的执行上下文。
- PuddingMemoryEngine：`PuddingRuntime` 内模块，负责召回、压缩、候选写回、污染筛查与记忆边界执行。
- SkillRuntime：负责基础 Skill 注入与执行上下文。
- McpRuntime：负责 MCP Server 连接、能力发现与调用路由。
- PluginRuntimeSupport：负责插件运行时上下文与宿主能力桥接。
- NativeHostBridge：负责把嵌入宿主的原生能力暴露为受控 Runtime 能力。
- SandboxExecutor：负责高风险动作的受限执行。

## PuddingRuntime 应具备的 Agent Runtime 能力

- Agent 实例生命周期管理：创建、加载、休眠、恢复、销毁。
- 主 Agent 与 sub_agent 承载：一个 `PuddingRuntime` 节点可承载多个 Session，每个 Session 内可承载多个 Agent，以及由主 Agent 派生出的临时 sub_agent。
- 单轮与多轮执行编排：组装输入、调用模型、处理工具与结果。
- 短期上下文窗口管理：裁剪、摘要、预算控制、上下文恢复。
- 调用 PuddingMemoryEngine 处理记忆召回、候选写回和边界校验。
- 装配模板声明的 Skill、MCP、插件运行能力与工具能力。
- 当 Runtime 嵌入桌面软件时，能够把宿主软件的原生能力以受控方式暴露给 Agent，例如查询软件状态、驱动自动化测试、读取宿主对象模型或调用宿主命令。
- 落实执行期权限与沙箱约束，而不是决定治理策略本身。
- 输出运行状态、错误、工具事件、记忆候选事件，供 Controller 与审计链路消费。

这里不再把 `AgentRuntime` 视为一个与 `PuddingRuntime` 平级的独立模块；如果后续还保留这个词，它更适合表示 `PuddingRuntime` 内部的单 Agent 执行视角，而不是新的宿主层。

## Runtime 负责的事

- 作为 Session 权威持有会话热状态。
- 承载实际 Agent、Worker、Swarm 执行。
- 承载主 Agent 派生出的 sub_agent，并负责其创建、回收、结果回传与生命周期约束。
- 管理 Agent 的上下文、自我管理、心跳、休眠与唤醒。
- 在嵌入模式下，把宿主桌面软件注册为一个可调度节点，并向 Controller 报告其原生能力、健康状态和可执行范围。
- 对外报告节点健康、负载、能力与状态。

## Runtime 不负责的事

- 多 Runtime 全局调度。
- 渠道接入与边界认证。
- 租户级治理、审批权威和平台级业务规则。
- 插件是否允许进入某个 Workspace。

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
