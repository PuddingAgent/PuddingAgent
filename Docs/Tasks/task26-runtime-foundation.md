# task26 - PuddingRuntime 基础宿主

最后更新：2026-03-18

## 任务目标

建立 `PuddingRuntime` 作为 Agent Runtime 宿主的最小可运行骨架，遵循 1 Runtime : 1 Agent : 1 Session 的简化模型，明确 Agent 执行、记忆处理、技能装配的内部边界。V1 阶段不引入沙箱/Docker 隔离，工具在进程内直接执行。

对应架构：
- [../07架构/03PuddingRuntime.md](../07架构/03PuddingRuntime.md)
- [../07架构/01总览与分层.md](../07架构/01总览与分层.md)

## 前置依赖

- 架构总览与 Runtime 分册已经稳定。
- `PuddingRuntime` 是 Agent Runtime 的结论已经确定。

## 可并行关系

- 可与 [task27-controller-routing-session.md](task27-controller-routing-session.md) 并行推进。
- 可与 [task29-agent-template-and-audit.md](task29-agent-template-and-audit.md) 的模板定义部分并行推进。
- 可与 [task34-event-bus-and-subscription.md](task34-event-bus-and-subscription.md) 的契约设计与最小唤醒语义并行，但事件执行接入需要在 Runtime 宿主稳定后收口。
- 不应早于 [task27-controller-routing-session.md](task27-controller-routing-session.md) 完成集成联调。

## 顺序任务

1. 建立 `PuddingRuntime` 宿主入口
说明：独立进程、生命周期、基础 DI、配置加载、健康探针。
输出：最小可启动 Runtime host。

1A. 预留宿主适配接口
说明：在不阻塞独立宿主主链路的前提下，预留 `EmbeddedRuntimeHost` 或等价宿主适配抽象，使后续可把 Runtime 嵌入其他 C# 桌面软件。
输出：最小宿主适配接口。
前置依赖：任务 1。

2. 建立 `SessionRuntime` 最小模型
说明：1 Runtime : 1 Session，维护基础会话热状态、元数据和状态上报。
输出：`SessionRuntimeRecord`、基础状态存取接口。
前置依赖：任务 1。

3. 建立单 Agent 执行视角
说明：在 `PuddingRuntime` 内建立单 Agent 执行上下文 `AgentExecutionContext`。1 Runtime : 1 Agent。
输出：Agent 实例创建、加载、销毁、状态查询接口。
前置依赖：任务 2。

3A. 建立最小执行状态机
说明：明确 `Created / Running / Busy / WaitingEvent / WaitingApproval / Sleeping / Hibernated / Archived / Completed / Failed / Frozen / Destroyed` 等状态与状态迁移规则。
输出：`AgentExecutionState`、状态迁移约束、基础恢复判断逻辑。
前置依赖：任务 3。

4. 接入最小真实执行链路
说明：基于模板创建 AgentInstance，完成单轮真实 LLM 回复。
输出：`AgentExecutionService`、执行请求/结果对象。
前置依赖：任务 3。

4A. 建立执行协调器与最小执行日志
说明：将执行链分为投递、恢复、能力装配、执行、结果沉淀、事件发布等阶段，并记录最小 `ExecutionJournal`。
输出：`AgentExecutionCoordinator`、`ExecutionJournal` 或等价内部模型。
前置依赖：任务 3A、任务 4。

5. 接入 `PuddingMemoryEngine`
说明：提供记忆召回、候选写回、边界校验和污染筛查的最小链路。
输出：`PuddingMemoryEngine`、`MemoryBoundaryService`、最小 recall/write candidate 流程。
前置依赖：任务 3。

6. 接入 `SkillRuntime`
说明：装配 Skill，工具在 Runtime 进程内直接执行。V1 不引入沙箱隔离。
输出：最小技能装配与执行骨架。
前置依赖：任务 4。

7. 预留事件订阅与直接唤醒接入口
说明：为 Agent 提供最小事件订阅注册、命中恢复与执行入口，但详细事件契约与治理规则由 [task34-event-bus-and-subscription.md](task34-event-bus-and-subscription.md) 收口。
输出：事件订阅接入口、唤醒回调或等价内部抽象。
前置依赖：任务 2、任务 3、任务 4。

7A. 建立 WakeupDispatcher 与本地唤醒绑定模型
说明：把治理通过的订阅落实为 Runtime 本地 `WakeupBinding`，支持去重、排队、恢复执行与拒绝原因记录。
输出：`WakeupDispatcher`、`WakeupBinding`、最小唤醒决策抽象。
前置依赖：任务 3A、任务 4A、任务 7。

8. 建立 Runtime 与 Controller 的最小节点协议
说明：定义节点注册、心跳、执行状态回报、结果回报、冻结/恢复/取消等交互对象。
输出：`RuntimeNodeRegistration`、`RuntimeHeartbeat`、`DispatchExecutionRequest`、`DispatchWakeupRequest` 等最小契约。
前置依赖：任务 1、任务 2、任务 4A。

8A. 建立单 Agent 冻结与 Workspace 全局冻结协议
说明：定义 `FreezeAgentRequest`、`ResumeAgentRequest`、`FreezeWorkspaceRequest`、`ResumeWorkspaceRequest` 及其返回结果。冻结即停止 Agent 执行，不涉及容器操作。
输出：冻结 / 恢复控制协议与权限检查入口。
前置依赖：任务 8。

9. 建立最小恢复与续跑能力
说明：支持 Runtime 在进程重启或执行中断后恢复 `WaitingEvent` / `WaitingApproval` / `Sleeping` / `Hibernated` 等挂起态，并避免重复执行已产生外部副作用的步骤。
输出：最小恢复快照、恢复判断逻辑、幂等保护约束。
前置依赖：任务 3A、任务 4A、任务 7A、任务 8。

---

## 源码审计快照（2026-03-19，已按简化架构重新评估）

### ✅ done（已在源码中可用）

| 子任务 | 源码位置 |
|---|---|
| 1 宿主入口 | `PuddingRuntime/Program.cs` — DI、配置、健康探针 `/health` |
| 1A 宿主适配接口 | `INativeHostBridge` + `NativeCapabilityExecutor` + `DemoDesktopHostBridge`（Demo 实现） |
| 2 SessionRuntime 最小模型 | `Models/SessionRuntimeRecord.cs` + `Services/InMemoryRuntimeSessionStore.cs` |
| 3 单 Agent 执行视角 | `Services/AgentSessionManager.cs` + `AgentInstanceRecord`（定义于 PuddingCore） |
| 4 最小真实执行链路 | `Services/AgentExecutionService.cs` → `ControllerRoutedLlmClient` → LLM |
| 5 PuddingMemoryEngine 接入 | `MemoryEngine` + `SessionMemoryStore` + `WorkspaceMemoryStore` + `MemoryBoundaryService` 全部注入，执行链路已写回/召回 |
| 8 Runtime↔Controller 节点协议 | `Services/RuntimeSelfRegistrationService.cs` — 启动注册、30s 心跳续约、ActiveSessionCount 上报；嵌入式节点原生能力列表一并上报 |

### ⚠️ partial（已有骨架，未达目标能力）

| 子任务 | 差距 |
|---|---|
| 3A 执行状态机 | `AgentInstanceStatus` 已有 `Creating/Running/Idle/Suspended/Terminated/Failed`，缺 `Busy/WaitingEvent/WaitingApproval/Sleeping/Hibernated/Archived/Frozen/Destroyed`；`AgentSessionManager` 只实现 Running→Terminated，无完整状态迁移逻辑 |
| 6 SkillRuntime | 仅有 `SandboxExecutor` 门控检查（可简化为进程内工具执行），无完整 Skill 加载与执行管道 |

### ❌ todo（尚未实现）

| 子任务 | 差距 |
|---|---|
| 4A 执行协调器 + ExecutionJournal | 无 `AgentExecutionCoordinator`；执行阶段全混在 `AgentExecutionService` 单方法内；无 `ExecutionJournal` |
| 7 / 7A 事件订阅 + WakeupDispatcher | 无事件订阅接入口；无 `WakeupDispatcher` / `WakeupBinding`；Agent 只能被 HTTP 调用触发 |
| 8A 冻结/解冻协议 | 无 `FreezeAgentRequest` / `ResumeAgentRequest` / `FreezeWorkspaceRequest` |
| 9 进程恢复与续跑 | 全部状态在内存，进程重启即丢失；无快照、无幂等保护 |

### 当前主要风险（按优先级）

1. **7 事件唤醒缺失** — Agent 被动等待 HTTP，无法由事件主动唤醒（task34 依赖此点）
2. **9 无进程恢复** — 进程重启后所有热状态清零，生产环境不可用
3. **3A 状态机不完整** — `WaitingApproval` / `Hibernated` 等挂起态缺失，人工审批链路无法支撑
4. **4A 执行日志缺失** — 无法追踪执行阶段和失败原因

### 已移除的 V1 任务

以下任务因架构简化（取消沙箱/Docker + 1:1 Runtime-Agent 模型）被移除：

- ~~6A ISandboxProvider + DockerSandboxProvider~~ — V2 扩展能力
- ~~6B/6C Agent→Sandbox/Docker 绑定台账~~ — V2 扩展能力
- ~~11 sandbox 故障上报与重建~~ — V2 扩展能力
- ~~2A Session 执行邮箱~~ — 1:1 模型下无并发多消息问题
- ~~7 sub_agent 承载~~ — 多 Agent 协作通过独立 Runtime 节点 + 事件总线实现

## 验收标准

- Runtime 可以独立启动并上报健康状态。
- Runtime 能承载 1 Agent : 1 Session。
- Runtime 能根据模板创建 AgentInstance 并返回真实回复。
- Runtime 能执行最小记忆召回和候选写回。
- Runtime 至少预留 1 条事件命中后恢复 Agent 执行的接入点。
- Runtime 至少具备 1 套清晰可查询的执行状态机。
- Runtime 至少能保留最小执行日志，并能恢复 1 类挂起态或深度休眠态。
- Runtime 至少支持按 Agent 和按 Workspace 的 stop / recover。
