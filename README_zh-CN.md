# Pudding Agent

<p align="center">
  <img src="me.png" alt="Pudding" width="200"/>
</p>

<p align="center">
  <strong>本地优先、Windows First 的桌面 AI 助手与 Agent IDE。</strong><br/>
  <sub><a href="README.md">English README</a></sub>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/version-v0.1.0-blue" alt="Version"/>
  <img src="https://img.shields.io/badge/platform-Windows%20First-0078D4" alt="Windows First"/>
  <img src="https://img.shields.io/badge/runtime-.NET%2010-512BD4" alt=".NET 10"/>
  <img src="https://img.shields.io/badge/license-Apache%202.0-green" alt="License"/>
</p>

Pudding 是一个个人 Agent 系统，把对话、工具、子代理委派、持久编排、本地记忆、后台学习和桌面集成放进同一个产品。最终产品入口是 `PuddingDesktop.exe`：WPF Shell 监督独立的 Core 服务，两者只通过带认证的 Loopback HTTP 与 WebSocket Bridge 通信。

项目仍在持续开发。下面描述的是明确的目标架构和演进方向，不代表每个扩展点今天都已完整落地。

## Pudding 的独特性

- **Windows First、本地优先**——桌面生命周期、本地数据、托盘运行、故障恢复以及浏览器/IDE 工作流都是产品本身，而不是聊天网页外面的一层壳。
- **持久工作，而不只是对话**——任务、子运行、编排 Run、审批、Artifact、重试和恢复都是显式持久事实。
- **六层记忆与学习闭环**——上下文、Memory Books、Skills、Goal、经验提取、Auto-Dream 和受控技能进化共同构成长生命周期学习系统。
- **委派是一等操作**——Agent 可以调用专门的子 Agent，并显式携带身份、路由、能力、预算和可追踪结果。
- **可视化控制平面**——对话解释叙事，编排图解释因果，检查器解释细节，时间线提供证据。

## 架构设计原则

我们的目标可以概括为三个需要严格限定边界的判断：

1. **一切业务能力皆由插件贡献。** 插件可以贡献工具、Agent 函数、Hook、事件消费者、Projection、调度器、策略、UI 呈现和配置 Schema；但这不意味着每个 DTO 或内部辅助类都必须成为独立部署的插件。
2. **一切关键操作皆有 Typed Hook。** Hook 是操作内部确定性的拦截面，不是所有通知的同义词；提交后的事实属于 Event，而不是 Hook。
3. **一切已提交状态变化皆产生 Event。** Event 让状态变化可观察、可回放、可审计；查询和直接能力调用仍然是强类型函数，不强迫绕行异步 EventBus。

五类合同让这些口号保持精确：

| 合同 | 含义 | 典型用途 |
|:---|:---|:---|
| **Command** | 请求改变状态 | 启动 Run、批准步骤、取消任务 |
| **Function / Capability** | 有输入输出的强类型调用 | 调用 Agent、工具、图、模型或 Artifact 转换 |
| **Hook / Interceptor** | 操作内部有边界的扩展点 | Guard、Transform、Around 或 Observer |
| **Event** | 状态变化后不可变的事实 | Run 已完成、任务已阻塞、工具结果已提交 |
| **Projection** | 可从事件重建的读模型 | 聊天状态、Admin 列表、图覆盖层、审计时间线 |

默认规则是：

> Command 表达意图；Function 执行工作；Hook 治理工作；Event 记录事实；Projection 解释事实。

## Agent 是有限状态与副作用循环

Agent 不是递归的聊天处理器，而是一个有限状态转移循环；模型调用、工具调用、子 Agent 调用、等待和消息发送都是显式 Effect：

```text
Transition(State, Event, ContextSnapshot)
    -> NewState + Effects + DomainEvents

EffectHost(Effect)
    -> EffectSucceeded | EffectFailed | EffectDeferred
```

状态转移核心应当确定、无副作用。Effect Host 执行外部工作，再把结果作为事件送回循环。持久 Inbox、幂等键、Lease、Fencing Token、预算和终态单调性，使 Loop 可以暂停、恢复、重试、settle 和跨重启继续，而不依赖模型自觉或心跳时机。

`completed` 与 `settled` 必须分开：Agent 可以已经生成完答复，但 Hook、Projection、监督 Job 和消息投递尚未进入稳定终态。

## Agent 与编排图都是可组合函数

Pudding 的编排方向，是让所有可调用单元共享一种函数描述合同：

```text
AgentFunction<Input, Output>
ToolFunction<Input, Output>
GraphFunction<Input, Output>
GateFunction<Input, Output>
HumanInputFunction<Input, Output>
```

每个 Descriptor 声明身份与版本、输入输出 Schema、所需 Capability、副作用等级、幂等/重试语义、超时/成本策略和呈现元数据。图节点引用冻结的 Descriptor 与合同 Hash；Typed Edge 把上游函数输出映射为下游函数输入。

Agent 可以生成编排图、把另一个 Agent 当作函数调用，也可以把子图作为函数调用。但生成图必须先经过编译、策略、能力、预算和审批校验，并冻结为不可变 Revision，不能直接执行 Prompt 临时生成的任意图。隐藏式递归调用不是工作流原语；循环必须表现为显式有界 Loop、子编排或 Child Run，并受深度、成本、Lease 和 Fence 约束。

## 插件与 Hook 模型

完整插件不只是 manifest 和一个程序集，它还应包含：

- 有版本的 Package 和依赖声明；
- 注册到不可变作用域快照中的 Typed Contribution；
- 明确的 Owner、Lifetime、Effect、Permission 和 Disposal；
- 能在 Admin 完整往返的 Schema 与配置贡献；
- 后端 Contribution 与声明式 UI Presentation Contribution；
- 健康、诊断、兼容、Drain、升级和回滚行为。

Hook Pipeline 借鉴 Middleware/Interceptor 的形态，但保留更强的语义：

```text
Guard -> Transform -> Around/Execute -> Post-Transform -> Commit -> Event
```

- **Guard** 决策单调：后续扩展不能悄悄重新开放已经拒绝的操作。
- **Transform** 生成新的强类型值，不修改共享对象。
- **Around** 明确超时、取消和失败策略。
- **Observer** 不能改变已经提交的结果。
- Pipeline 顺序确定且可检查；插件加载顺序不能意外成为安全策略。

## 事件模型

Pudding 区分三个平面：

- **Durable Domain Event**——已提交的状态事实，具备 Outbox、Schema Version、Consumer Checkpoint、Replay、Dead Letter 和脱敏策略。
- **Live Stream Event**——模型 Delta 等低延迟进度，服务于 UI，但不会默认永久写入全局事件日志。
- **Capability-local Event**——只在插件或执行作用域内传播的有界信号。

状态与 Outbox Event 必须在同一事务提交。每个 Consumer Group 独立维护 Checkpoint 和重试状态。长时间的 LLM 或工具工作从持久意图调度，不能在 Event Dispatcher 事务中直接执行。

## 产品与前端思想

Pudding 向 Pi 学习小而可组合的 Agent Core 和扩展体验，向 DeepSeek Harness 学习 Capability Seam、Typed Lifecycle、自动生成的事件地图和克制的控制平面 UI。它们是对齐参考，而不是要复制的皮肤。

Pudding 自己的身份是安静的 Windows 伙伴和持久的本地工作台：

- **对话是叙事，图是因果，检查器是细节，时间线是证据。**
- 使用语义化 Design Token、克制动效、清晰层级和渐进披露，避免堆砌 Dashboard 装饰。
- 不只显示一个状态颜色，还要说明 Agent **为什么**在等待、阻塞、推迟、休眠、质询或申请审批。
- Chat、Admin、Desktop 和自动化界面消费同一组 Projection，不各自猜测状态。
- 插件 UI 优先使用声明式、可沙箱化的呈现；可信代码模块是例外，必须签名、受 Capability 限制并可卸载。
- 无障碍、键盘操作、Reduced Motion 和大图性能属于架构要求。

我们的目标不是再造一个 DeepSeek Harness。Pudding 要把插件架构与本地记忆、任务/Goal 监督、峰谷栅栏、编排图、Windows 生命周期和伙伴式交互结合起来。

## 当前基础与关键缺口

| 领域 | 已有基础 | 仍需演进 |
|:---|:---|:---|
| 工具 | Registry-first 发现、校验、权限过滤、工作区来源 | 统一 Owner、生命周期、诊断和插件贡献元数据 |
| 插件 | Package 校验与 Manifest 发现 | 真正的激活 Host、依赖图、Scope、Grant、Drain/Unload、回滚 |
| Hook | Agent Loop 生命周期回调与内部通知 | 有明确失败策略的 Guard/Transform/Around/Observer Pipeline |
| Event | Conversation Store、内部 Bus、持久优先队列、编排事件 | 统一 Envelope、事务 Outbox、每消费者 Checkpoint、Schema/Replay 工具 |
| Agent Loop | 持久会话、工具、流式输出、子代理、Goal 和后台路径 | 显式 Reducer/Effect FSM、Inbox 顺序、Completion/Settlement 合同 |
| 编排 | 不可变 Revision、Typed Port、Run、Lease/Fence、真实 Executor | 通用 Function Registry、有界循环/子图、Agent 创作工具、策略化部署 |
| 前端 | Chat、Admin、Run 视图、图编辑器、组件 UI Registry | 共享 Projection、插件呈现目录、Pipeline/Event Inspector、语义 Token 收敛 |

近期优先级是：

1. 冻结 Command/Function/Hook/Event/Projection 的统一术语和 Envelope；
2. 在不推翻现有 Tool Registry 的前提下引入真正的插件 Contribution Host；
3. 提取 Agent 状态转移 Reducer 与 Effect 边界；
4. 让 Function 成为 Agent、工具和编排图的共同调用合同；
5. 增加 Durable Outbox/Checkpoint 和自动生成的 Event/Hook Map；
6. 在 Admin 展示组合关系、策略决策、事件流、成本与阻塞原因；
7. 逐步迁移现有能力，并为每个 Seam 建立一致性和 Replay 测试。

## 产品拓扑

```text
PuddingDesktop.exe
  WPF Shell · WebView2 Workbench · Tray · Runtime Center
        |
        | authenticated loopback HTTP / WebSocket bridge
        v
core/PuddingAgent.exe --desktop-child
  Core API · Agent Runtime · Connectors · Orchestration · Memory · SQLite
        |
        +-- Plugin Contributions
        +-- Function Registry
        +-- Hook Pipelines
        +-- Durable Events / Projections
```

即使 Core 无法启动，Desktop 仍应允许用户进入设置和运行中心修复、启动、停止或重启服务。业务逻辑不能迁入 WPF。`dev-up.py` 只负责源码开发环境，不属于最终产品的进程生命周期。

## 源码开发

需要 Windows、PowerShell、.NET 10 SDK、Node.js 和 Python。

```powershell
python .\dev-up.py --status
python .\dev-up.py --restart
python .\dev-up.py --frontend-only
python .\dev-up.py --down
```

定向构建：

```powershell
dotnet build PuddingRuntime --no-restore
dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --no-restore --nologo
```

运行时用户数据位于配置的 DataRoot，不能作为构建或测试输出目录。

## 设计文档

- [插件、Hook、Event、Agent Loop 与函数图总架构](Docs/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md)
- [通用 Agent 编排 ADR](Docs/07架构/82ADR-071通用Agent编排平台完整设计方案ADR.md)
- [编排后端执行内核施工图](Docs/07架构/83通用Agent编排后端执行内核与ControlPlane施工图.md)
- [编排编辑器与组件 UI 施工图](Docs/07架构/84通用Agent编排蓝图编辑器与组件系统施工图.md)
- [工作区 TODO、峰谷自动化、质询器与 Goal 模式](Docs/Features/工作区TODO与峰谷节能任务编排设计方案.md)

## License

Apache License 2.0

<p align="center">
  <em>角落里安静的伙伴：阅读、思考、学习，并为每一步留下证据。</em><br/>
  <sub>「……交给我吧。」</sub>
</p>
