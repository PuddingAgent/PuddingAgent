# task09 - Agent 生命周期与运行态管理

最后更新：2026-03-19

## 任务目标

为 Pudding Agent Network 建立清晰的 AgentInstance 生命周期模型，明确创建、运行、忙碌、睡眠、休眠、归档、销毁等状态的语义、权限边界、资源回收策略与恢复方式。

对应架构：
- [../07架构/03PuddingRuntime.md](../07架构/03PuddingRuntime.md)
- [../07架构/06PuddingAgent与客户端.md](../07架构/06PuddingAgent与客户端.md)
- [../07架构/08数据模型与配置.md](../07架构/08数据模型与配置.md)
- [../07架构/10事件系统与事件总线.md](../07架构/10事件系统与事件总线.md)

## 前置依赖

- [task26-runtime-foundation.md](task26-runtime-foundation.md) 已建立 Runtime 宿主与基础执行边界。
- [task29-agent-template-and-audit.md](task29-agent-template-and-audit.md) 已明确 AgentTemplate 与模板快照。
- [task34-event-bus-and-subscription.md](task34-event-bus-and-subscription.md) 已建立事件订阅与直接唤醒链路。

## 可并行关系

- 可与 [task32-observability-integration.md](task32-observability-integration.md) 并行设计生命周期日志与观测指标。
- 可与 [task30-knowledge-infrastructure.md](task30-knowledge-infrastructure.md) 后段并行设计归档数据落盘与冷存储策略。

## 生命周期状态

建议至少支持以下状态：

- `Created`
- `Running`
- `Busy`
- `Sleeping`
- `Hibernated`
- `Frozen`
- `Archived`
- `Destroyed`

### 状态语义

- `Created`：已实例化，正在装配能力与最小状态。
- `Running`：在线可用，可接收新任务。
- `Busy`：正在处理任务、工具调用或工作流步骤。
- `Sleeping`：轻量睡眠态，保留最小热状态，可被事件或显式唤醒恢复。
- `Hibernated`：深度休眠态，不接收普通事件，运行资源可回收，但数据保留。
- `Frozen`：冻结 / stop 态，不接收普通任务、事件唤醒和自动恢复，对应容器应被 stop。
- `Archived`：归档态，运行资源和渠道绑定被回收，数据打包压缩保留。
- `Destroyed`：终态，只能从归档态进入，归档数据被移除。

## 顺序任务

1. 建立 AgentInstance 生命周期状态机
说明：明确状态集合、允许迁移、禁止迁移和异常迁移回退策略。
输出：生命周期状态图、状态迁移表。

2. 建立 Runtime 生命周期执行器
说明：让 Runtime 能落实创建、任务接收、忙碌结束、睡眠、休眠、归档、恢复与销毁等动作。
输出：生命周期执行器、最小状态持久化入口。
前置依赖：任务 1。

3. 建立内置生命周期工具
说明：在 Runtime 中提供 `request_afk`、`request_sleep`、`request_hibernate`、`request_wake` 等内置工具或等价能力。
输出：内置生命周期工具接口与权限检查入口。
前置依赖：任务 2。

4. 建立 Busy -> Running 的交付回退语义
说明：任务完成后，Agent 可通过 `request_afk` 或等价方式释放忙碌态并回到 `Running`。
输出：任务完成到 Running 的最小回退链。
前置依赖：任务 3。

5. 建立 Sleeping 与 Hibernated 的差异化策略
说明：明确 Sleeping 仍可被事件或显式唤醒，Hibernated 不接收普通事件且允许运行资源回收。
输出：睡眠 / 休眠差异表、唤醒策略。
前置依赖：任务 2、任务 3。

6. 建立归档链路
说明：支持停止 Agent、冻结状态、回收运行资源、释放渠道绑定、保留并压缩数据，必要时上传冷归档存储。
输出：归档执行链、归档台账对象。
前置依赖：任务 2、任务 5。

6A. 建立冻结与恢复链路
说明：支持用户或审计 Agent 冻结单 Agent，或冻结整个 Workspace，并 stop 对应 Docker 容器；同时提供显式恢复链路。
输出：冻结 / 恢复执行链、容器 stop / restart 语义、治理审计记录。
前置依赖：任务 2、任务 3、任务 5。

7. 建立销毁链路
说明：销毁只能对已归档 Agent 执行，且只能由用户发起。销毁后移除归档数据，但保留最小审计痕迹。
输出：销毁执行链、用户权限检查、审计记录。
前置依赖：任务 6。

8. 建立生命周期日志与审计记录
说明：记录谁在什么时候把哪个 Agent 从什么状态迁移到什么状态，以及原因与结果。
输出：`AgentLifecycleRecord` 或等价审计对象。
前置依赖：任务 1、任务 2、任务 6、任务 7。

9. 建立恢复链路
说明：支持从 Sleeping / Hibernated / Archived 恢复到 Running，并明确恢复时间与恢复成本差异。
输出：恢复执行链与恢复前检查。
前置依赖：任务 5、任务 6。

## 验收标准

- 平台具备清晰的 AgentInstance 生命周期状态机。
- Runtime 能落实创建、运行、忙碌、睡眠、休眠、归档、恢复与销毁语义。
- `request_afk` 能让 Agent 从 `Busy` 返回 `Running`。
- `Sleeping` 与 `Hibernated` 的事件接收与资源回收策略明确不同。
- `Frozen` 会阻断普通执行与事件唤醒，并停止对应容器。
- `Archived` 会释放运行资源与渠道绑定，但保留可恢复数据。
- `Destroyed` 只能从 `Archived` 进入，且只能由用户发起。
- 支持用户或审计 Agent 对单 Agent 和整个 Workspace 发起冻结与恢复。
- 生命周期迁移可审计、可查询、可回溯。