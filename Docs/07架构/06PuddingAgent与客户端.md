# PuddingAgent 与客户端

## PuddingAgent

PuddingAgent 用于定义 Agent 是谁，而不是 Agent 当前处于什么运行状态。

换句话说，`PuddingAgent` 负责的是“定义层”，不是“执行层”。

它更像是 **AgentTemplate / Agent Blueprint / Agent Profile** 的承载层，而不是一个长期运行中的 Agent 进程。

它回答的问题通常是：

- 这个 Agent 是什么角色？
- 默认应该具备哪些能力？
- 默认遵循什么系统提示词与策略画像？
- 默认如何参与协作、记忆和工具调用？

## PuddingAgent 与 PuddingRuntime 的关系

这两者的关系需要刻意说清，否则很容易在实现时把“定义”和“执行”缠在一起：

- `PuddingAgent` 负责定义 Agent 模板、画像、偏好与约束。
- `PuddingRuntime` 负责根据模板把 Agent **组装成可运行的 AgentInstance**。
- `PuddingController` 负责决定这个 AgentInstance **应该被放到哪个 Runtime**。

换句话说：

- `PuddingAgent` 回答“它是谁”。
- `PuddingController` 回答“它应该去哪跑”。
- `PuddingRuntime` 回答“它现在怎么跑”。

更具体地说，一次 Agent 组装至少来自四类输入：

1. `AgentTemplate`
	- 系统提示词
	- 角色画像
	- 默认行为策略

2. Controller 批准后的能力视图
	- 允许使用哪些 Skill
	- 允许使用哪些 MCP
	- 允许访问哪些宿主能力

3. Runtime 节点实际能力
	- 操作系统类型
	- CPU / 内存 / 磁盘 / GPU 等资源
	- 是否支持 Docker、WASM、gVisor、Firecracker 等沙箱实现

4. 当前 Workspace / Session 运行态
	- 当前会话上下文
	- 当前记忆视图
	- 当前挂起状态与事件订阅命中结果

因此，`PuddingAgent` 本身不直接“运行”；它是被 `PuddingRuntime` 依据模板快照、授权结果和节点能力组装出来的。

### 负责内容

- 角色、人设、系统提示词模板。
- 默认能力组合、工具白名单、策略画像。
- 心跳策略、默认记忆策略、运行画像声明。
- 协作角色声明，例如 planner / executor / reviewer / memory keeper。
- 委派偏好、审查偏好、默认事件订阅偏好等声明式画像。
- Runtime 偏好与约束声明，例如 Windows / Linux / MacOS、内存高、绘图、测试执行、宿主软件类型等标签。
- 默认隔离级别声明，例如 `none` / `workspace` / `dedicated`。
- 对 Skill、MCP、宿主能力的引用声明，而不是它们的真实执行实现。

### 不负责内容

- Skill 与 MCP 的真实装配执行。
- 全局 Skill Registry 与 MCP Registry 的托管。
- 插件的安装与治理。
- 会话热状态与执行期上下文。
- 事件命中后的直接唤醒与执行恢复。
- Workflow 控制权与审批权威。

## SKILL 在哪里

这是一个非常关键的问题：**Skill 不应散落在每个 AgentInstance 身上，也不应由每个 Runtime 各自发明一套真相。**

建议分三层看：

### 1. 全局注册层：在 Controller

`PuddingController` 维护一套全局 `Skill Registry` 与 `Mcp Registry`，负责：

- Skill / MCP 元数据登记
- 版本管理
- 风险等级与信任等级管理
- 适用 Runtime 标签与能力要求声明
- 启停、冻结、审计与可见性控制

第一阶段建议把这套注册信息保存在 **PostgreSQL** 中，由 Controller 作为权威读写入口。

### 2. 产品配置层：在 Platform

`PuddingPlatform` / `PuddingPlatformAdmin` 负责把全局注册表以产品方式暴露出来，支持：

- 浏览可用 Skill / MCP
- 给 AgentTemplate 绑定 Skill / MCP 引用
- 设置是否对某个 Workspace 可见
- 配置模板默认运行画像、隔离级别与 Runtime 偏好

### 3. 执行装配层：在 Runtime

`PuddingRuntime` 不持有全局定义权，但会：

- 拉取或接收已批准的 Skill / MCP 引用集合
- 在本地进行装配、缓存、激活与调用
- 根据当前 Runtime 节点能力判断能否满足模板要求

因此：

- **Skill 的定义权和注册权在 Controller**
- **Skill 的产品配置入口在 Platform**
- **Skill 的真实装配与执行在 Runtime**

AgentTemplate 只应该“引用 Skill”，不应该“拥有 Skill 的实现本体”。

## Agent 必须完成的功能

从系统设计角度，`PuddingAgent` 至少必须承担以下职责：

1. **角色定义**
	- 定义它是 planner、executor、reviewer 还是其他协作角色
	- 定义它在协作图中的默认职责边界

2. **默认策略定义**
	- 定义系统提示词模板
	- 定义默认记忆策略、工具白名单、风险偏好和响应风格

3. **能力画像定义**
	- 定义它可以声明哪些工具、Skill、MCP、宿主能力需求
	- 定义它对事件、任务、审批与委派的默认偏好
	- 定义它偏好的 Runtime 标签、宿主类型、资源档位与默认隔离级别

4. **模板化复用**
	- 让同一类 Agent 能在不同 Workspace / Session 内被稳定复用
	- 让实例运行态与模板定义层分离，避免每次执行时重新发明角色

5. **环境偏好声明**
	- 支持声明指定 Runtime、必需标签、偏好标签和排斥标签
	- 支持声明隔离级别与宿主能力要求

## Agent 生命周期：定义层与执行层如何分工

Agent 本身也有生命周期，但必须区分：

- **定义层生命周期**：`AgentTemplate` 的创建、版本化、停用、归档
- **执行层生命周期**：`AgentInstance` 在 Runtime 中的创建、运行、忙碌、睡眠、休眠、归档与销毁

这里讨论的重点是后者，也就是：**一个真实运行中的 AgentInstance 在 Runtime 中如何变化。**

关键边界：

- `PuddingAgent` 定义“默认可以怎么活”
- `PuddingRuntime` 决定“现在处于哪种活法”
- `PuddingController` 与用户策略决定“哪些高权限状态切换被允许”

## AgentInstance 建议生命周期状态

建议至少支持以下状态：

- `Created`
- `Running`
- `Busy`
- `Sleeping`
- `Hibernated`
- `Frozen`
- `Archived`
- `Destroyed`

### `Created`

- Agent 已根据模板完成实例化
- 基础能力、记忆视图、权限视图和运行环境正在装配
- 尚未进入稳定可工作状态

### `Running`

- Agent 处于可工作、可接收新任务、可接收 Runtime 普通投递的状态
- 这不是“正在执行”，而是“在线可用”

### `Busy`

- Agent 已接收一个任务，正在处理模型推理、工具调用、Workflow 节点推进或协作步骤
- 这是典型的主执行占用状态

### `Sleeping`

- Agent 进入轻量睡眠态
- 保留最小热状态与恢复上下文
- Runtime 仍可根据已批准订阅、显式唤醒或系统调度把它恢复到工作态
- 适用于“暂时不做事，但还会随时被叫醒”的场景

### `Hibernated`

- Agent 进入深度休眠态
- 不再接收普通事件命中
- 必须通过显式唤醒才能恢复进入工作
- 为了节省资源，对应容器或运行资源可以被回收，但数据必须保留

### `Frozen`

- Agent 进入冻结 / stop 态
- 常用于潜在入侵、上下文污染、泄露风险或高危越权动作的紧急止损
- 冻结后不再接收普通任务、事件唤醒或自动恢复
- 对应 Docker 容器或 sandbox 应被 stop，而不是仅做逻辑暂停
- 只能由用户或具有权限的审计 Agent 发起，恢复也必须是显式动作

### `Archived`

- Agent 被冻结并归档
- 运行资源、沙箱、渠道绑定、临时运行关系都会被回收
- 数据会被保留、打包、压缩；未来可扩展上传到冷归档对象存储
- 恢复归档 Agent 的代价通常高于从休眠态恢复

### `Destroyed`

- 终态
- 只能从 `Archived` 进入
- 已归档数据被移除，不再保留可恢复副本
- 只能由用户发起，不能由 Agent 自行请求

## 典型状态迁移

建议最小迁移链如下：

- `Created -> Running`
- `Running -> Busy`
- `Busy -> Running`
- `Running -> Sleeping`
- `Sleeping -> Running`
- `Running -> Hibernated`
- `Busy -> Hibernated`（通常需要先安全挂起）
- `Running -> Frozen`
- `Busy -> Frozen`
- `Sleeping -> Frozen`
- `Hibernated -> Frozen`
- `Frozen -> Running`
- `Hibernated -> Running`
- `Running -> Archived`
- `Frozen -> Archived`
- `Hibernated -> Archived`
- `Archived -> Running`（恢复成本较高）
- `Archived -> Destroyed`

其中：

- `Busy -> Running` 表示任务完成、交付完成或显式释放忙碌态
- `Sleeping` 是轻量可恢复态
- `Hibernated` 是不接收普通事件的深度节能态
- `Frozen` 是安全优先的 stop 态，必须阻断事件与执行，并停止对应容器
- `Destroyed` 是不可逆终态

## Runtime 提供的内置生命周期工具

为了让 Agent 能以受控方式表达自己的生命周期意图，Runtime 应提供少量内置生命周期工具或等价运行时能力：

- `request_afk`
- `request_sleep`
- `request_hibernate`
- `request_wake`

冻结与恢复不应作为所有 Agent 默认自助工具开放，而应视为治理动作：

- `freeze_agent`
- `freeze_workspace`
- `resume_agent`
- `resume_workspace`

这些动作通常只应由用户或具有权限的审计 Agent 通过治理链路发起，而不是由普通执行 Agent 直接调用。

### `request_afk`

建议语义：

- Agent 在完成当前任务交付后，向 Runtime 请求释放 `Busy` 状态
- Runtime 将 Agent 从 `Busy` 切回 `Running`
- 可同时附带任务完成摘要、下次恢复建议或交付结果引用

这里的 `AFK` 更接近“我这轮忙完了，先退回待命态”，而不是进入真正睡眠。

### `request_sleep`

- Agent 请求进入 `Sleeping`
- Runtime 保存最小热状态与恢复上下文
- 后续仍可被已批准订阅或显式唤醒恢复

### `request_hibernate`

- Agent 或用户可请求进入 `Hibernated`
- 该动作通常需要权限或策略批准
- 进入后不再接受普通事件
- 运行资源、容器或沙箱可回收，但数据必须保留

### `request_wake`

- 用于从 `Sleeping` 或 `Hibernated` 显式恢复到 `Running`
- 对 `Hibernated` 的唤醒通常比 `Sleeping` 更重，因为需要重建运行资源

## 用户与 Agent 的生命周期权限边界

建议明确以下权限边界：

- Agent 可请求：`request_afk`、`request_sleep`
- Agent 在具备权限时可请求：`request_hibernate`、请求自己或指定 Agent 进入深度休眠
- 用户可请求：唤醒、休眠、冻结、Workspace 全局冻结、归档、恢复、销毁
- 审计 Agent 在具备权限时可请求：冻结单 Agent、冻结整个 Workspace、发起恢复申请
- `Destroyed` 只能由用户发起，而且只能作用于 `Archived` Agent

建议原则：

- Agent 可以表达生命周期意图，但 Runtime / Controller 仍需根据权限、Workspace 策略和当前状态决定是否执行
- 冻结、全局冻结、归档与销毁属于治理动作，不应作为普通自助工具对 Agent 全开放

## 归档与销毁的资源语义

### 归档

- 停止并冻结 Agent
- 回收容器、沙箱和运行资源
- 回收渠道绑定表等运行关系
- 保留数据，并执行压缩 / 打包
- 若存在冷归档对象存储，归档数据可上传到归档存储以节省 Runtime 空间

### 销毁

- 只能对已归档 Agent 执行
- 移除已归档数据
- 不再保留恢复副本
- 应写入审计记录，并建议保留最小治理痕迹（例如 destroy record）

## Agent 的最小阶段目标

第一阶段至少应做到：

- 能定义角色、人设、系统提示词与默认能力组合
- 能声明最小协作角色与执行偏好
- 能为 Runtime 提供稳定可复用的模板输入
- 不与 Runtime 的执行态和 Controller 的治理权威混淆

## AgentTemplate 建议最小字段

建议 `AgentTemplate` 至少具备：

- `templateId`
- `workspaceId`
- `name`
- `roleProfile`
- `systemPrompt`
- `memoryPolicy`
- `eventSubscriptionPreferences`
- `requiredRuntimeTags`
- `preferredRuntimeTags`
- `excludedRuntimeTags`
- `runtimeAffinityPolicy`
- `defaultIsolationLevel`
- `skillRefs`
- `mcpRefs`
- `hostCapabilityRefs`
- `riskProfile`
- `approvalProfile`

## 客户端层

客户端层包括 PuddingCLI、PuddingWeb、PuddingAvalonia。

需要额外区分：桌面软件不一定永远只是客户端。如果某个 C# 桌面软件嵌入了 `PuddingRuntime`，那么它在平台里应被视为嵌入式 Runtime 节点，而不是单纯的 UI 入口。

### PuddingCLI

- 管理与调试入口。
- 通过 Controller API 发起消息、查看状态、执行批准。
- 不长期承载 Runtime 状态。

### PuddingWeb

- Web 控制台与可视化面板。
- 展示渠道、会话、Agent、路由、审计与治理状态。
- 通过 Controller 暴露的接口工作。

### PuddingAvalonia

- 用户持有的桌面控制端。
- 承载审批、会话观察、Workspace 控制、语音批准等能力。
- 不直接承载 Agent 运行态。

## 嵌入式桌面宿主

其他 C# 开发的桌面软件可以嵌入 `PuddingRuntime`，此时它们的角色不同于 `PuddingAvalonia`：

- `PuddingAvalonia` 仍然是控制端。
- 被嵌入 Runtime 的桌面软件则是可调度的运行节点。

这种宿主可以把自己的原生功能暴露给 Runtime，例如：

- 查询软件内部状态。
- 调用软件原生命令。
- 驱动测试软件执行测试并读取结果。
- 在宿主对象模型上执行受控自动化操作。

边界：

- 它不是新的控制面。
- 它只是承载 Runtime 的特殊宿主类型。
- 它暴露的原生能力必须通过 Controller 审批、权限与审计链约束。
