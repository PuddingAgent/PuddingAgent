# ADR-070 通用 Agent 编排图基础架构

> 状态：**phase-4d-graph-management-implemented；完整施工设计见 ADR-071**  
> 日期：2026-08-09  
> 范围：Agent 生成任务图、版本化 JSON 契约、组件注册表、强类型多模态端口、DAG 校验、节点执行绑定、触发器、独立画布布局、事件模型、MOA 模板适配  
> 前置：[11 工作流与任务图](11工作流与任务图.md)、[ADR-056 可靠事件流](57ADR-056聊天消息受理与可靠事件流架构ADR.md)、[ADR-060 子代理运行可观测性](61ADR-060子代理运行可观测性与会话事件投影ADR.md)、[ADR-069 MOA](80ADR-069MOA子代理设计委员会编排核心ADR.md)

> **设计分册说明**：本文继续作为 V2 已落地基础和当前事实的记录。完整目标架构、Head/Deployment 分离、运行时施工、蓝图编辑器、组件目录、多模态与验收门禁已拆分到
> [ADR-071](82ADR-071通用Agent编排平台完整设计方案ADR.md)、[83 后端施工图](83通用Agent编排后端执行内核与ControlPlane施工图.md)、
> [84 编辑器施工图](84通用Agent编排蓝图编辑器与组件系统施工图.md) 和 [85 验收图册](85通用Agent编排交付测试与运维验收图册.md)。这些目标项不得被误读为当前已经实现。

## 1. 决策摘要

Pudding 将 Agent Orchestration 提升为 Core 的基础能力。主代理可以根据用户请求产生声明式任务图，Pudding 负责校验、版本化、激活、调度、恢复和投影；Agent 不直接提交可执行 C#、JavaScript、Shell 或任意表达式。

MOA 不再拥有第二套长期编排内核。`DesignCouncilPlanCompiler` 仍负责设计委员会的专业规则，随后由适配器编译为通用编排图；后续调度、事件、持久化和 Admin UI 都复用通用能力。

当前已实现 Phase 1、Phase 1B、Phase 2A 与 Phase 4 的布局编辑切片：

- `pudding.agent-orchestration/v2` 强类型 JSON 契约；
- 版本化组件/触发器注册表与确定性 `contractHash` 冻结；
- 类型、MIME、基数和 delivery 四维端口契约；
- inline JSON 与 ArtifactRef 多模态值信封，媒体内容不以内联 Base64 进入任务图；
- Trigger 作为创建新 run 的图外入口，单次 run 继续保持 DAG；
- `GraphLayout` 独立于可执行定义，移动卡片不会改变运行修订或内容哈希；
- graph/revision、input、node、control/data edge、executor、gate、permission、failure policy；
- 纯 `AgentOrchestrationGraphCompiler`，负责规范化、引用校验、精确路由冻结和 DAG 拓扑校验；
- append-only run event 信封及稳定事件名；
- `DesignCouncilOrchestrationGraphAdapter`，把 MOA stage/work item 映射为通用 gate/subAgent 节点；
- JSON 往返、循环拒绝、路由冻结、写权限门禁、修订关系、MOA 可见性和控制边测试。
- SQLite graph/revision/run/node-run/event 独立事实表；
- 图修订 CAS、幂等 run 创建、显式激活和 root Ready 投影；
- 原子 claim、运行并发上限、lease renew、fencing token、执行 run/sub-session 身份回填；
- claim 过期后的跨 store 实例恢复、旧 fence 拒绝和 `afterSequence` 事件回放；
- 状态与事件同事务提交，提交完成后才发送进程内 wake-up signal。
- 只读 Control Plane API：组件目录、最新/指定修订、修订历史、run/node 快照和事件分页；
- `watch(afterSequence)` replay-to-live SSE，支持 `Last-Event-ID`，并对游标超前和持久化序列缺口显式报错。
- Admin `/orchestration` React Flow viewer/editor：DAG、节点检查器、运行事件时间线与 cursor 重连；节点拖拽和 viewport 只写独立布局，不修改 executable revision 或运行事实。
- Graph/Run 分页发现 API 与 Admin 选择器；无 Run 的 Graph 可直接预览最新 Revision。
- 独立 `orchestration_graph_layouts` 投影及 `expectedCurrentLayoutRevision` CAS；布局只引用已存在的 base revision/node，不改变 executable revision。
- Admin 布局保存提交完整节点坐标与 viewport，新建布局从 L1 开始，更新严格递增；运行状态刷新不覆盖未保存坐标，409 冲突保留本地编辑并要求显式重新加载。
- Admin 可新建 Graph：服务端生成经编译器校验的 Revision 1 和 `humanInput` 占位节点，不自动激活或运行；Graph ID 使用稳定的 URL-safe 约束。
- Admin 可删除尚无 Run 的 Graph：删除使用 Graph Head Revision CAS，并清理它的 Revision/Layout；只要存在任意 durable Run 就拒绝删除，运行历史不级联清理。

后继 Ready 计算、gate/human-input、通用组件执行适配器、Agent 工具、Admin 连线/修订编辑和运行控制命令尚未接入。

## 2. 为什么不复用现有 TaskPlan 或 manage_tasks

现有 `TaskPlanRun/TaskNode` 是父子任务树，适合记录目标拆分、责任人和结果，但不能表达多前驱 DAG、门禁、数据映射、节点 claim 和可重放事件。`manage_tasks` 是会话内待办工具，也不具备跨重启执行语义。

因此边界为：

- `AgentOrchestrationGraphDefinition` 是可执行 DAG 的权威定义；
- `TaskPlan` 后续可以作为编排图的树形摘要或兼容投影，但不是调度真相；
- `manage_tasks` 继续服务轻量个人待办，不承担子代理调度；
- `ISubAgentInvocationService` 继续是单个子代理执行入口，不再另建模型调用引擎。

## 3. 图契约

### 3.1 图与修订

每份定义必须包含：

- `schemaVersion`：当前固定为 `pudding.agent-orchestration/v2`；
- `graphId`：跨修订稳定的图身份；
- `revisionId`、`revision`、`parentRevisionId`：不可变修订链；
- `workspaceId`、`rootSessionId`、`createdByAgentId`：所有权与发起来源；
- `objective`、`inputs`、`nodes`、`edges`、`maxConcurrency`；
- `requiresExplicitActivation`：生成或编译不等于授权执行。

运行实例必须冻结 `graphId + revisionId`。对活动图的编辑产生新修订，不原地修改运行中或已完成节点。后续工具使用 `expectedRevision` 做 compare-and-swap，避免主代理、用户和后台服务相互覆盖。

### 3.2 节点

V2 当前保留四类底层调度/执行节点：

| 类型 | 用途 | 执行归属 |
|------|------|----------|
| `subAgent` | 专家、执行者、综合者 | `ISubAgentInvocationService` |
| `tool` | 一个受治理的工具调用 | Runtime Tool 层 |
| `humanInput` | 明确暂停并等待用户补充 | Runtime / Channel |
| `gate` | 法定人数、审批、上下文完整性等门禁 | Runtime Gate Evaluator |

节点通过 `componentType + version` 引用服务端受信组件注册表；编译器把注册表契约哈希冻结进修订。媒体处理、网络请求、聚合和判断等能力不扩展为任意代码，而是注册为具有固定 executor、端口、配置 Schema、副作用和能力声明的组件。底层 `NodeKind` 只决定执行适配器边界，不承担组件目录枚举。

V2 不支持任意脚本、循环和动态代码执行。需要分支时使用门禁、节点状态和后续新修订表达。

每个 `subAgent` 节点在可激活前必须冻结 `provider/model` 格式的 `routeKey`。`role` 和 `templateId` 用于提示词与审计，不能在运行时静默变成 fallback。每个节点还声明期望输出、尝试次数、超时、失败策略与权限模式；写节点默认被编译器拒绝，直到激活端接入审批策略。

### 3.3 边

边显式拆成两种语义：

- `control`：决定节点何时可进入 Ready，可按成功或终态触发；
- `data`：决定下游能看到哪些上游输出，并通过 binding 映射到命名输入。

两类边都参与环检测。这样“执行顺序”和“上下文可见性”不会混为一谈，独立方案可以共享研究证据而不互相观察，批判节点也只能获得指定目标方案。

### 3.4 强类型端口与多模态值

每个组件输入/输出端口同时声明：

- `dataType`：`pudding.text/json/content/artifact/event/...` 等可扩展语义类型；
- `mediaTypes`：精确 MIME 或 `image/*`、`audio/*` 等通配范围；
- `cardinality`：`one` 或 `many`；
- `deliveries`：`inline`、`artifact`、`stream`、`event`。

编译器同时校验 graph input -> node port 和 node output -> node input。单值端口不能接收多个 binding，多值输出不能接入单值端口，媒体类型和 delivery 必须相交。文字或小型结构可以使用 inline JSON；图片、音频、视频和文件必须使用带 `artifactId/contentType/size/sha256/metadata` 的引用。

### 3.5 组件、触发器与画布布局

`IAgentOrchestrationComponentRegistry` 是 Runtime 和 Admin 组件面板的共同发现边界。注册表只接受完整、无重复端口的版本化 descriptor；相同 `componentType@version` 的契约不可漂移。首批内建 descriptor 覆盖 sub-agent、tool、gate、human-input，后续媒体、网络、数据、事件、存储组件使用同一注册机制。

Trigger 不是长期占用 worker 的 DAG 节点。手动、聊天、定时、Webhook、Connector 和 orchestration event trigger 接收事件后创建一个新 run，并把事件字段绑定到 graph input。事件循环通过新 run、去重键和关联 ID 表达，不允许在单次 DAG 内形成环。

`AgentOrchestrationGraphLayout` 是独立编辑器投影，只保存 viewport、节点坐标/大小、父组与折叠状态。布局 revision 可以独立变化，不进入 executable graph 的内容哈希。

当前 SQLite 使用 `(graph_id, base_revision_id)` 作为布局主键，只保存该 executable revision 的当前布局投影。首次写入必须为 `layoutRevision=1 / expected=0`；后续写入必须严格递增并匹配 `expectedCurrentLayoutRevision`。布局允许只覆盖部分节点，未保存坐标的节点由 Admin 自动 DAG 布局；未知节点、未知父节点、父组环、非有限坐标、非法尺寸和越界 zoom 会被拒绝。

Base Revision 和节点集合是不可变事实，因此 Layout 写入先通过只读连接完成 Revision/Graph/Node 校验，再进入短生命周期的 serializable CAS 写事务。这样不存在的 Revision 或非法节点不会仅为返回确定性 4xx 就等待无关 SQLite writer；只有读取当前 layout revision 与 INSERT/UPDATE 属于写事务。

## 4. Agent 操作面

后续向 Agent 暴露的能力保持窄接口，不允许直接写数据库：

```text
orchestration.create
orchestration.validate
orchestration.revise(expectedRevision, definition)
orchestration.activate(revisionId)
orchestration.inspect(runId)
orchestration.watch(runId, afterSequence)
orchestration.provide_input(runId, nodeId, input)
orchestration.retry_node(runId, nodeId)
orchestration.cancel(runId)
```

主代理可以观察进度、消费输出事件并创建新修订，但不能篡改既有运行事实、完成中的节点或历史事件。运行控制命令必须幂等，并校验当前 run/node version。

## 5. 运行时与事件一致性

持久化阶段采用独立的 orchestration definition/revision/run/node-run/event 表或等价存储。图可能跨会话、跨 Core 重启存在，所以 session event log 不是编排事实源；编排事件会投影到 root session，供聊天和 Admin UI 感知。

约束如下：

- 每个 run 的事件使用单调递增 `sequence`；
- 状态与事件先在同一事务提交，再发布到进程内总线或 SSE；
- `watch(afterSequence)` 先回放已提交事件，再订阅 live，并使用 high-water 去重；
- `subSessionId` 是可复用的子代理会话身份；
- 每次节点尝试都产生新的不可变 `executionRunId`，不能和 `subSessionId` 混用；
- 大输出写 artifact/run archive，事件只携带摘要和引用。

Phase 2A 已由 `SqliteAgentOrchestrationStore` 实现独立事件事实源。`orchestration_runs.head_sequence` 是每个 run 的持久化高水位，
`GetEventsAfterAsync(runId, afterSequence, limit)` 负责 cursor 回放；`IAgentOrchestrationCommittedEventSignal` 只做提交后的唤醒，不能替代事件读取。

`AgentOrchestrationEventFollower` 固化 replay-to-live 交接：先读取 run 的持久化 `headSequence`，连续回放到该水位，再以当前 cursor 等待 retained signal。提交若发生在查询与等待之间，signal 会立即返回；发现缺失 sequence 时抛出明确的 `AgentOrchestrationEventGapException`，不静默跳过。

### 5.1 只读 Control Plane API

| API | 用途 |
|------|------|
| `GET /api/orchestrations/catalog` | 组件/触发器 descriptor、版本和 `contractHash`，供 Admin palette 使用 |
| `GET /api/orchestrations/graphs` | 按 workspace 分页发现 Graph head，并返回 run/active-run 计数 |
| `GET /api/orchestrations/runs` | 按 workspace/graph/status 分页发现轻量 Run 投影，不加载 node 明细 |
| `GET /api/orchestrations/graphs/{graphId}/latest` | 读取图的当前不可变修订 |
| `GET /api/orchestrations/graphs/{graphId}/revisions` | 按 revision 倒序读取修订元数据 |
| `GET /api/orchestrations/revisions/{revisionId}` | 读取指定 executable definition；支持 revisionId 含 `/` |
| `GET /api/orchestrations/runs/{runId}` | 读取 run 与全部 node-run 的当前持久化投影 |
| `GET /api/orchestrations/runs/{runId}/events` | `afterSequence` 分页补齐，返回 `nextSequence/headSequence/hasMore` |
| `GET /api/orchestrations/runs/{runId}/watch` | 先 replay 后 live 的 SSE；query cursor 优先，`Last-Event-ID` 兜底 |
| `GET /api/orchestrations/graphs/{graphId}/layout?baseRevisionId=...` | 读取指定 executable revision 的独立编辑器布局 |
| `PUT /api/orchestrations/graphs/{graphId}/layout` | Admin-only 布局 CAS；只写 layout 表，不写 graph revision/run/event |
| `POST /api/orchestrations/graphs` | Admin-only 新建 Graph；服务端构造并编译 Revision 1，不接受前端绕过编译器 |
| `DELETE /api/orchestrations/graphs/{graphId}?expectedCurrentRevision=...` | Admin-only Graph Head CAS 删除；只允许无 Run 的 Graph，清理 Revision/Layout |

这些端点全部要求登录。只读 Controller 只注入 `IAgentOrchestrationQueryStore`，在类型层无法调用修订、激活或 claim 写命令；布局和 Graph 生命周期分别由 Admin-only Controller 注入窄写接口。Graph 管理面仍不暴露激活、run 创建或 claim。Watch 每 15 秒发送无 `id` 的 SSE heartbeat；API DTO 使用与图文件相同的 Web JSON 与字符串枚举规则，避免 Admin 和 Agent 看到另一套契约。

Admin viewer 使用认证 `fetch` 消费 Watch，因为原生 `EventSource` 不能携带 Bearer Header。页面先分页读取已提交事件，再从 `nextSequence` 订阅 SSE；重连同时携带 query cursor 与 `Last-Event-ID`，按 sequence 去重，并校验 SSE `id` 与事件信封 sequence 一致。Revision ID 按路径段转义，保留后端 catch-all 路由所需的 `/`。

## 6. MOA 作为模板实例

`DesignCouncilOrchestrationGraphAdapter` 的映射规则：

- MOA stage -> 通用 `gate` 节点；
- MOA work item -> 通用只读 `subAgent` 节点，并保留精确 route；
- 前一阶段 gate -> 当前 work item 为成功触发的 control edge；
- work item -> 当前 gate 为终态触发的 control edge，使 gate 能计算失败、法定人数和覆盖率；
- research -> proposal 为 data edge；
- research + target proposal -> critique 为 data edge；
- 所有前序成功 work item -> synthesis/final review 为 data edge。

MOA 原有纯状态机和运行时适配暂时保留，作为行为基线和迁移来源。通用持久化调度器达到相同门禁、claim、暂停恢复和精确路由语义后，再删除 MOA 专用运行分支。

## 7. 分阶段实施

### Phase 1：Core 契约与模板适配（已完成）

- 强类型、版本化 JSON 图；
- 纯编译器和 DAG 校验；
- 事件信封；
- MOA -> 通用图；
- Core 单元测试。

### Phase 1B：V2 组件与多模态契约（已完成）

- V1 开发态契约直接升级为 V2，不保留双协议兼容层；
- 组件/触发器 descriptor、注册表、确定性 contract hash；
- graph input、component port、data binding 的强类型连接；
- inline JSON 与多模态 artifact value envelope；
- trigger 与 graph input mapping；
- executable definition 与 editor layout 分离；
- 组件解析、端口兼容、MIME、Artifact 和 JSON round-trip 测试。

### Phase 2A：持久化事实与 claim 内核（已完成）

- definition/revision/run/node-run/event store；
- revision/run/version compare-and-swap 与幂等创建；
- Draft 激活与无前驱 root node Ready；
- claim lease、fencing token、并发上限、renew、started/terminal commit；
- 过期 claim 恢复与已耗尽尝试的失败投影；
- committed-before-publish 和 afterSequence 回放。
- 修订历史查询和只读 Control Plane API；
- retained high-water signal、replay-to-live follower 与 SSE watch。

实现位置：

- `Source/PuddingCore/Orchestration/AgentOrchestrationPersistenceContracts.cs`
- `Source/PuddingPlatform/Services/Orchestration/AgentOrchestrationSchemaBootstrapper.cs`
- `Source/PuddingPlatform/Services/Orchestration/SqliteAgentOrchestrationStore.cs`
- `Source/PuddingPlatform/Services/Orchestration/AgentOrchestrationCommittedEventSignal.cs`
- `Source/PuddingPlatform/Services/Orchestration/AgentOrchestrationEventFollower.cs`
- `Source/PuddingPlatform/Controllers/Api/AgentOrchestrationApiController.cs`
- `Source/PuddingPlatform/Controllers/Api/AgentOrchestrationLayoutApiController.cs`
- `Source/PuddingPlatformAdmin/src/pages/orchestration/`

Phase 2A 是持久化事实层，不声称已形成完整调度器。节点 terminal commit 目前不会自行释放后继节点，也不会自动决定整个 run 的终态。

### Phase 2B：调度与执行内核

- 根据 control/data edge 条件原子计算后继 Ready/Skipped；
- run 完成、失败、取消和 AwaitingInput 状态机；
- human-input 与版本化 gate evaluator；
- subAgent/tool executor adapter、重试策略和 deadline；
- root session 事件投影；当前通用 run watch 已直接从编排事实表提供。

### Phase 3：Agent 工具与 MOA 切换

- create/validate/revise/activate/inspect/watch/control 工具；
- 把 MOA 调度切到通用 runtime；
- 保留设计委员会的请求编译器和 gate evaluator，删除 MOA 专用 store/dispatcher；
- 把运行事件投影到 root session。

### Phase 4：Admin UI

- 采用 React Flow 作为 React 画布层，Pudding Core/API 继续是 schema、校验和运行事实权威；
- ✅ 首个切片已交付只读 run viewer；
- ✅ 图画布、运行事件时间线、节点检查器、输出摘要和 Artifact 引用；
- ✅ Graph/Run 浏览列表、无 Run 定义预览与独立 GraphLayout CAS API；
- ✅ Viewer 读取保存布局并对缺失节点回退自动 DAG 布局；
- ✅ 节点拖拽、viewport 保存、未保存提示、独立 layout revision CAS 与 409 冲突重载；
- ✅ Graph 新建、无 Run Graph 的 CAS 删除、删除确认与历史保护；
- 待增加 revision 历史切换、连线和 executable revision 编辑；
- 失败/等待输入/重试状态的运行控制；
- 用户可审批修订、激活、提供输入、取消和重试；
- UI 只通过 API/事件操作，不成为状态权威。

## 8. 验收基线

- 未知 schema、重复 ID、悬空引用、自环和任意环都不能产生可激活定义；
- 激活候选的子代理节点必须冻结精确 route；
- 第一修订不能引用父修订，后续修订必须引用父修订；
- 未接审批策略前，显式写节点不能通过默认编译；
- control/data edge 语义独立且都参与拓扑检查；
- JSON enum 使用 camelCase，可往返反序列化；
- MOA 映射后提案之间没有 data edge；
- 每个批判只接收研究输出和指定目标提案；
- 编译和适配都不执行工具、不启动子代理、不写运行状态。
- revision CAS 不允许并发编辑覆盖 graph head；
- run 创建、激活、claim、started、terminal 的状态与事件必须原子提交；
- 每个 run 的事件 sequence 连续递增，支持 `afterSequence` 回放；
- 并发 worker 不能突破 `MaxConcurrency`；
- claim 过期后新 worker 获得更高 fencing token，旧 worker 的 terminal commit 被拒绝；
- `executionRunId` 与 `subSessionId` 分开存储并进入运行事件。
- 图中组件必须解析到精确 `componentType + version`，编译结果冻结注册表 `contractHash`；
- graph input/data edge 只能连接兼容的 data type、MIME、cardinality 和 delivery；
- 多模态 payload 使用 ArtifactRef，不允许内联 Base64；
- Trigger 只能创建新 run，不能成为单次 DAG 的循环入口；
- `GraphLayout` 不进入 executable graph JSON 和内容哈希。
- Layout 必须绑定现有 Graph + base Revision，只能引用该 Revision 中的节点；并发保存使用独立 layout revision CAS。
- Layout 的不可变 Revision/Node 校验不得预先占用 SQLite write transaction；无关 writer 存在时，缺失 Revision 仍须快速返回 NotFound。
- Admin Watch 从分页 `nextSequence` 继续，SSE 重连不得重复或跳过已提交事件；画布点击与缩放不得产生运行写入。
