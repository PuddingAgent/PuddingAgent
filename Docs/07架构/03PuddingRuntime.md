# PuddingRuntime

## 定位

PuddingRuntime 是执行面与数据平面，是可独立部署的运行时宿主。它承载 Session、AgentInstance、技能运行、记忆处理和受限执行环境。

如果用更直接的话说：`PuddingRuntime` 本身就是平台里的 Agent Runtime，而不是在它里面再平行抽出一个独立的“Runtime of Runtime”。

## 子模块

- SessionRuntime：负责会话级状态、隔离和元数据。
- AgentExecutionContext：作为 `PuddingRuntime` 内部的单 Agent 执行视角，负责主 Agent 与其派生 sub_agent 的执行上下文。
- PuddingMemoryEngine：`PuddingRuntime` 内模块，负责召回、压缩、候选写回、污染筛查与记忆边界执行。
- SkillRuntime：负责基础 Skill 注入与执行上下文。
- McpRuntime：负责 MCP Server 连接、能力发现与调用路由。
- PluginRuntimeSupport：负责插件运行时上下文与宿主能力桥接。
- SandboxExecutor：负责高风险动作的受限执行。

## PuddingRuntime 应具备的 Agent Runtime 能力

- Agent 实例生命周期管理：创建、加载、休眠、恢复、销毁。
- 主 Agent 与 sub_agent 承载：一个 `PuddingRuntime` 节点可承载多个 Session，每个 Session 内可承载多个 Agent，以及由主 Agent 派生出的临时 sub_agent。
- 单轮与多轮执行编排：组装输入、调用模型、处理工具与结果。
- 短期上下文窗口管理：裁剪、摘要、预算控制、上下文恢复。
- 调用 PuddingMemoryEngine 处理记忆召回、候选写回和边界校验。
- 装配模板声明的 Skill、MCP、插件运行能力与工具能力。
- 落实执行期权限与沙箱约束，而不是决定治理策略本身。
- 输出运行状态、错误、工具事件、记忆候选事件，供 Controller 与审计链路消费。

这里不再把 `AgentRuntime` 视为一个与 `PuddingRuntime` 平级的独立模块；如果后续还保留这个词，它更适合表示 `PuddingRuntime` 内部的单 Agent 执行视角，而不是新的宿主层。

## Runtime 负责的事

- 作为 Session 权威持有会话热状态。
- 承载实际 Agent、Worker、Swarm 执行。
- 承载主 Agent 派生出的 sub_agent，并负责其创建、回收、结果回传与生命周期约束。
- 管理 Agent 的上下文、自我管理、心跳、休眠与唤醒。
- 对外报告节点健康、负载、能力与状态。

## Runtime 不负责的事

- 多 Runtime 全局调度。
- 渠道接入与边界认证。
- 租户级治理、审批权威和平台级业务规则。
- 插件是否允许进入某个 Workspace。

## 当前迁移方向

当前位于 CLI 中的 SubconsciousEngine、MemoryIndexer、TodoListStore、ReviewQueueStore 应迁移到 Runtime。其中 MemoryIndexer 应收敛为 PuddingMemoryEngine 的子组件，而不是继续作为 CLI 资产存在。

当前结论：`PuddingRuntime` 就是 Agent Runtime。后续如果需要单独描述某个 Agent 的执行容器，应优先使用“AgentExecutionContext”或“单 Agent 执行视角”这类表述，而不是再引入一个与 `PuddingRuntime` 并列的宿主概念。
