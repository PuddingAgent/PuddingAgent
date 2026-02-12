# task34 - 统一事件总线、订阅治理与直接唤醒

最后更新：2026-03-18

## 任务目标

建立 Pudding Agent Network 的统一事件总线基础能力，把 Gateway 的外部接入、Controller 的治理路由、Runtime 的执行唤醒和 Runtime 代表 Agent 落实的事件订阅串成一条真实可落地的事件驱动主链路。

对应架构：
- [../07架构/04PuddingController与Gateway.md](../07架构/04PuddingController与Gateway.md)
- [../07架构/03PuddingRuntime.md](../07架构/03PuddingRuntime.md)
- [../07架构/07协作网络与治理.md](../07架构/07协作网络与治理.md)
- [../07架构/08数据模型与配置.md](../07架构/08数据模型与配置.md)
- [../07架构/10事件系统与事件总线.md](../07架构/10事件系统与事件总线.md)

## 前置依赖

- [task26-runtime-foundation.md](task26-runtime-foundation.md) 已建立 Runtime 基础宿主。
- [task27-controller-routing-session.md](task27-controller-routing-session.md) 已建立 Controller 接入、路由和 Session 基础。
- [task29-agent-template-and-audit.md](task29-agent-template-and-audit.md) 已明确 AgentTemplate、审计模板和运行画像基础。

## 可并行关系

- 可与 [task30-knowledge-infrastructure.md](task30-knowledge-infrastructure.md) 后段局部并行。
- 可与 [task32-observability-integration.md](task32-observability-integration.md) 的指标/审计方案并行设计，但事件指标应以本任务契约为准。
- 嵌入式 Runtime 节点事件接入可与 [task33-embedded-runtime-host.md](task33-embedded-runtime-host.md) 后段局部并行。

## 顺序任务

1. 建立统一事件 Envelope 与命名规范
说明：定义事件最小字段、版本、命名规范、来源元数据、关联 ID 与信任等级。
输出：`EventEnvelope`、事件命名规则、版本策略。

2. 建立事件域模型
说明：明确 `global` 与 `workspace` 两级事件域，确定默认路由原则与跨域升级策略。
输出：事件域判定规则、Workspace 域隔离策略。
前置依赖：任务 1。

2A. 建立事件发布权限模型
说明：明确默认所有业务事件发布到 `workspace` 域；只有 Controller、用户动作或具备明确特权的 Agent / 系统模块，才允许发布 `global` 事件。
输出：事件发布权限规则、全局事件升级条件、拒绝原因与审计要求。
前置依赖：任务 2。

3. 建立事件总线抽象
说明：抽象统一事件总线接口，内部建议基于 RabbitMQ，但在代码边界上不把业务直接绑死在某个消息中间件实现上。
输出：`IEventBus`、发布/订阅接口、最小总线宿主。
前置依赖：任务 1、任务 2。

4. 建立 Gateway 入站事件转换链
说明：把 MQTT、Webhook、HTTP、嵌入式宿主事件等外部协议转换成统一事件 Envelope，并补齐来源识别、信任分级和审计入队。
输出：Gateway 入站事件转换链、Adapter 映射规则。
前置依赖：任务 3；接入依赖 [task27-controller-routing-session.md](task27-controller-routing-session.md)。

5. 建立 Controller 事件路由与订阅治理
说明：在 Controller 侧根据 Workspace、权限、模板策略和审批规则，判定谁可以订阅什么事件、事件进入哪个域、是否允许升级为直接唤醒。
输出：事件路由器、订阅治理器、拒绝原因与审计记录。
前置依赖：任务 2、任务 2A、任务 3、任务 4。

6. 建立 Runtime 订阅命中与直接唤醒链路
说明：让 Runtime 代表 Agent 落实事件订阅，在订阅命中后恢复休眠中的 Agent、补齐上下文并触发后续执行，避免依赖高频轮询。
输出：订阅命中处理器、Agent 唤醒入口、执行恢复链路。
前置依赖：任务 3、任务 5；执行依赖 [task26-runtime-foundation.md](task26-runtime-foundation.md)。

6A. 建立消息事件优先级与中断恢复链路
说明：为消息型事件建立 `P0 / P1 / P2` 三档优先级，明确 Runtime 何时立即打断、何时当前轮插入、何时空闲后投递，并建立 `ResumeAnchor` 与最小恢复能力。
输出：优先级消息策略、打断记录、恢复锚点与运行时内置恢复能力。
前置依赖：任务 6。

7. 建立内部事件生产者
说明：把任务完成、Agent 完成任务、心跳、审批结果、记忆候选写回、Workflow Step 完成等内部状态变化纳入统一事件流。
输出：首批内部事件类型与发布点。
前置依赖：任务 3、任务 5、任务 6。

7A. 建立冻结 / 恢复全局治理事件链路
说明：把 `Stop Workspace`、`Recover Workspace`、`Freeze Agent`、`Resume Agent` 建模为 Controller 发布的全局治理事件，由 Runtime 订阅后按 `workspaceId` / `agentId` 本地命中执行。
输出：`workspace.freeze.requested`、`workspace.resume.requested`、`agent.freeze.requested`、`agent.resume.requested` 及其 applied 结果事件。
前置依赖：任务 5、任务 6、任务 7。

8. 建立幂等、去重与防抖策略
说明：处理重复事件、Webhook 重试、设备抖动、短时间重复唤醒、优先级消息重复插入等问题。
输出：事件幂等键、去重窗口、防抖策略。
前置依赖：任务 4、任务 6、任务 6A。

9. 建立死信与失败隔离机制
说明：为无法路由、无法消费、反复失败、权限不合法或版本不兼容的事件建立隔离与查询机制。
输出：`DeadLetterEvent` 或等价死信对象、失败分类、人工修复入口。
前置依赖：任务 5、任务 8。

10. 建立重放与补偿基础能力
说明：支持按 Workspace、事件类型、时间窗口进行有限重放，并保证重放受权限和审计约束。
输出：事件重放接口、重放标记、游标模型。
前置依赖：任务 7、任务 9。

10A. 建立事件日志沉淀与查询接口
说明：把统一事件总线中的关键事件沉淀为 `EventLogRecord` 或等价查询对象，支持按 Workspace、事件类型、Agent、任务和时间范围过滤。
输出：事件日志查询接口。
前置依赖：任务 7、任务 9；关联 [task32-observability-integration.md](task32-observability-integration.md)。

11. 完成首条外部事件驱动链路验收
说明：至少跑通 1 条 MQTT 或 Webhook → Gateway → Workspace 域 → Runtime/Agent 订阅 → 直接唤醒的真实链路。
输出：端到端联调记录与验收用例。
前置依赖：任务 6、任务 8、任务 9、任务 10A。

## 验收标准

- 平台具备统一事件 Envelope 与稳定命名规范。
- 系统支持 `global` / `workspace` 两级事件域。
- 默认所有业务事件都在 `workspace` 域内传播，`global` 事件发布受到显式权限控制。
- Gateway 能把至少 1 种外部协议转换成统一事件。
- Controller 能治理订阅权限、事件域路由和唤醒资格。
- Runtime 能代表 Agent 落实事件订阅，并在订阅命中后直接唤醒 Agent，而不是依赖轮询。
- 平台至少定义 `P0 / P1 / P2` 三档消息事件优先级，以及对应的打断、插入和延迟投递策略。
- Runtime 至少能为 `P1` 消息建立最小 `ResumeAnchor`，并支持 Agent 处理完成后恢复原任务流。
- 至少 1 类内部状态变化已进入统一事件流。
- 冻结 / 恢复链路已支持通过全局治理事件广播到 Runtime，并由 Runtime 只处理自己承载的目标实例。
- 死信隔离、重放基础能力与审计记录可查询。
- 关键事件日志至少可按 Workspace / Agent / 事件类型 / 时间范围查询。
- 至少 1 条外部事件驱动链路被真实验收。
