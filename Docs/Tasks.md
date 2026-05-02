# Pudding Agent Network 任务与状态看板

最后更新：2026-03-18

## 2026-03-15 第一批平台任务（Pudding Agent Network V1）

目标：先打通首条真实垂直切片，并为知识、存储、治理和多客户端扩展预留稳定接口。

目标链路：`PuddingPlatform（含admin管理界面，PuddingPlatformAdmin）  -> Controller API -> Workspace 路由 -> ServiceSession -> Runtime Agent -> 真实 LLM 回复`

## 为什么要做这套系统

Pudding 当前主线不是继续堆高单 Agent 的聊天能力，而是解决企业级多智能体落地中的几个硬问题：

1. **多智能体与工作流能力不足**
	真实任务更像任务图（Task Graph），而不是自由推理链，因此 Workflow 必须是一等能力，系统决定流程，LLM 只做 reasoning node。

2. **多工作交叉导致上下文污染**
	一个 Workspace 不应只是简单分组，而应成为 Memory + Agent + Tool + Event 的完整命名空间，保证任务、记忆和协作边界隔离。

3. **现有智能体系统缺少事件队列机制**
	只有心跳、没有事件总线，会导致响应缓慢、浪费 token。Pudding 的方向是 event-driven AI runtime，让 Agent 被事件唤醒，而不是轮询世界。

4. **企业系统接入困难**
	MQTT、WebSocket、HTTP、Webhook、GitHub 触发器等都应成为一等输入，而不是额外特判。

5. **协同机制缺乏系统化设计**
	多智能体不只是“多开几个 Agent”，而是需要角色、委派、监督、审查和共享痕迹的稳定协作协议。

## MVP 三大核心模块

如果只看最小可落地版本，Pudding 必须先站稳三块：

1. **Agent Runtime**：承载 Session、Agent、sub_agent、技能、记忆与唤醒执行。
2. **Event Bus**：承载外部输入、内部状态变化、订阅、直接唤醒、审计、死信与重放。
3. **Workspace Memory**：把 Workspace 升级为 Memory + Agent + Tool + Event 的完整命名空间。

这三者是当前所有任务排序的总前提。


我们桌面端PuddingAvalonia（不需要开发）、CLI（暂时不需要开发）等优先级最低。

## 当前主线的模块分工

Controller、Runtime、Platform 不再作为独立进程，而是 Pudding Agent 单进程内的模块：

- **Controller 模块**：消息路由、鉴权、Session 管理、审计日志
- **Runtime 模块**：LLM 对话、工具执行、记忆引擎、Skill 装配
- **P2P 网络模块**：节点发现、直连通信、事件广播
- **Web UI 模块**：内嵌前端，管理界面 + 对话界面

## 部署方式

Pudding Agent 是一个独立可执行程序。用户选择以下任一方式运行：

- **裸进程**：下载可执行文件，双击运行。无需安装数据库或中间件。
- **Docker 单容器**：`docker run -p 8080:8080 pudding-agent`
- **Docker Compose 多 Agent**：`docker compose up --scale pudding-agent=3`

不再需要 Nginx、PostgreSQL、Redis、RabbitMQ、MinIO 等外部基础设施。

## V1 简化说明（2026-05-02）

单进程 P2P 模型，每个 Agent = 独立可执行程序，内嵌 Web UI + SQLite。

| 废弃 | 替代 |
|---|---|
| Controller/Runtime/Platform 分进程 | 同进程内模块 |
| Nginx 反向代理 | Kestrel 直接对外 |
| PostgreSQL | SQLite |
| Redis | 内存缓存 |
| RabbitMQ | P2P 直连广播 |
| MinIO | 本地文件系统 |
| Docker Compose 多容器 | 可选单容器或裸进程 |

## 总览

V1 目标：一个双击即用的 Pudding Agent 可执行程序。

- [架构.md](架构.md) — 单进程 P2P 模型总览
- [Tasks/task24-platform-v1-first-slice.md](Tasks/task24-platform-v1-first-slice.md) — 首条垂直切片（需更新）
- [Tasks/task31-client-surfaces.md](Tasks/task31-client-surfaces.md)
- [Tasks/task32-observability-integration.md](Tasks/task32-observability-integration.md)
- [Tasks/task33-embedded-runtime-host.md](Tasks/task33-embedded-runtime-host.md)
- [Tasks/task34-event-bus-and-subscription.md](Tasks/task34-event-bus-and-subscription.md)
- [Tasks/task35-workspace-cockpit-and-collaboration-tools.md](Tasks/task35-workspace-cockpit-and-collaboration-tools.md)

补充约束：
- Workflow 是一等能力，系统决定流程，LLM 只负责推理节点。
- Workspace 不只是资源分组，而是 Memory + Agent + Tool + Event 的完整命名空间。
- Agent 应优先由事件唤醒，而不是依赖心跳轮询。
- 平台需要同时支持 Leader、投票/联邦、基于事件痕迹的自发协同三类协作模式。
- 平台内置支持 Email Channel。
- 平台内置支持 Web Chat Channel（嵌入式 HTTP + WebSocket 网页聊天；渠道 ID 采用 `web-chat-{workspaceId}` 固定格式，由 `WebChatGatewayAdapter` 统一处理，每个 Workspace 在初始化时自动注册该绑定）。**Web Chat 是后台管理的核心用户入口，优先级 P0。**
- 平台规划支持飞书（Feishu）Channel，由 `FeishuGatewayAdapter` 实现；飞书及其他第三方渠道（Webhook、MQTT、钉钉等）优先级 P3，在所有关键链路任务完成后实现。
- 一个 Workspace 可以挂接多个渠道，并为每个渠道声明默认 Agent 或允许 Agent 集合。
- 渠道接入机制本身必须插件化，便于后续扩展更多渠道。
- 渠道优先级分层：P0 = Web Chat（内置，首个真实用户入口）；P1 = Email（内置）；P2 = CLI/Avalonia（控制面）；P3 = 飞书及其他第三方渠道。
- 知识库归属于 Workspace，由 Controller 持有服务端能力，Runtime 提供透明访问支持。
- 统一存储层由 Controller 持有，Runtime 提供挂载与访问支持，上层 Agent 无感。
- 知识图谱归属于 Workspace 共享资产，底层先用 PostgreSQL 实现，上层 Agent 无感。
- Workspace 内应提供以 TaskMap / DAG 为主视图的多 Agent 协作驾驶舱，默认展示聚合状态而不是原子事件洪流。
- Workspace 共享工具台至少应抽象任务板、表格、Wiki、对象存储和事件中心，Agent 通过 Runtime API / HTTP API 访问；HTTP API 需通过 `agent-token` 声明身份。
- 语音批准属于系统控制链路，不属于业务 Agent。
- 每个 Workspace 至少拥有 1 个审计 Agent。
- 新增客户端层 `PuddingAvalonia（优先级最低）`，作为用户持有的桌面控制端。
- `PuddingRuntime` 后续支持嵌入其他 C# 桌面软件，使其成为可调度 Runtime 节点，并暴露受控原生能力。

### 路线总览

| 阶段 | 任务 | 目标 | 前置依赖 | 可并行 | 状态（2026-03-18 审计） |
|---|---|---|---|---|---|
| Phase 0 | [task24-platform-v1-first-slice.md](Tasks/task24-platform-v1-first-slice.md) | 固化首条垂直切片的类/API 设计 | 架构分层已稳定 | 否 | partial |
| Phase 1A | [task26-runtime-foundation.md](Tasks/task26-runtime-foundation.md) | 建立 `PuddingRuntime` 作为 Agent Runtime 宿主 | Phase 0 | 可与 Phase 1B 并行 | partial |
| Phase 1B | [task27-controller-routing-session.md](Tasks/task27-controller-routing-session.md) | 建立 `PuddingController` 路由、会话与控制入口 | Phase 0 | 可与 Phase 1A 并行 | partial |
| Phase 2A | [task28-platform-workspace-governance.md](Tasks/task28-platform-workspace-governance.md) | 建立 Platform 的 Workspace 业务层与治理策略 | Phase 1B | 可与 Phase 2B 并行 | partial |
| Phase 2B | [task29-agent-template-and-audit.md](Tasks/task29-agent-template-and-audit.md) | 建立 AgentTemplate、审计模板和运行画像 | Phase 1A + Phase 1B | 可与 Phase 2A 并行 | partial |
| Phase 3 | [task30-knowledge-infrastructure.md](Tasks/task30-knowledge-infrastructure.md) | 建立知识库、统一存储、知识图谱和 Runtime 透明访问 | Phase 1A + Phase 1B | 可与 Phase 2A/2B 后段局部并行 | partial |
| Phase 4 | [task31-client-surfaces.md](Tasks/task31-client-surfaces.md) | 建立 CLI / Avalonia（优先级最低） 客户端控制面 | Phase 1A + Phase 1B，且 API 基本稳定 | CLI 与 Avalonia（优先级最低） 可并行 | partial |
| Phase 5 | [task34-event-bus-and-subscription.md](Tasks/task34-event-bus-and-subscription.md) | 建立统一事件总线、订阅治理与直接唤醒链路 | Phase 1A + Phase 1B + Phase 2B | 可与 Phase 3 后段局部并行 | partial |
| Phase 6 | [task35-workspace-cockpit-and-collaboration-tools.md](Tasks/task35-workspace-cockpit-and-collaboration-tools.md) | 建立 Workspace 内的多 Agent 协作驾驶舱与共享虚拟工作台 | Phase 2A + Phase 3 + Phase 5 | 可与 Phase 4 后段和 Phase 6 后段局部并行 | todo |
| Phase 7 | [task32-observability-integration.md](Tasks/task32-observability-integration.md) | 建立可观测性并完成阶段验收 | Phase 1-6 | 审计、指标、调试查询可并行 | partial（当前验收 blocked） |
| Phase 8 | [task33-embedded-runtime-host.md](Tasks/task33-embedded-runtime-host.md) | 支持把其他 C# 桌面软件作为嵌入式 Runtime 节点调度 | Phase 1A + Phase 1B + Phase 2B | 可与 Phase 3 后段和 Phase 5 后段局部并行 | todo |

### 路线审计快照（2026-03-18）

- 已按 `Docs/Tasks/task24~task34` 文档目标与当前源码状态进行对照审计。
- 当前主链路处于“可运行但未全部收口”阶段：`task24~task32` 以 `partial` 为主，`task33` 仍为 `todo`。
- `task32` 最新验收脚本结果：`blocked`（Controller API 当前不可达，报告：`TestResults/task32-acceptance-20260318-210259.json`）。
- 具体已完成能力已在下方「能力状态总览（以源码为准）」中标记为 `done`（如 PlatformAdmin 登录/工作台、业务页面、JSON API、端到端冒烟链路、Docker Compose 基础设施等）。

### 按控制层 / Runtime / Agent 观察当前主线

为了避免只从“阶段”看任务而忽略职责边界，也可以从三层视角理解当前路线：

#### 1. 控制层主线

- `task27`：建立 `PuddingController` 最小控制入口
- `task34`：建立统一事件总线、订阅治理与直接唤醒链路
- `task32`：建立控制面的观测、审计与调试查询

控制层的目标是：**先拿回治理权、路由权和事件控制权**。

#### 2. Runtime 主线

- `task26`：建立 `PuddingRuntime` 基础宿主
- `task30`：接入知识、统一存储与 Runtime 透明访问
- `task33`：扩展到嵌入式 Runtime 节点

Runtime 的目标是：**先成为稳定执行宿主，再扩展事件唤醒、知识访问与宿主能力桥接**。

#### 3. Agent 定义层主线

- `task29`：建立 AgentTemplate、审计模板和运行画像
- `task34`：补充事件订阅、唤醒与协作相关声明能力
- 后续应继续补角色模型、委派图、协作契约等设计

Agent 层的目标是：**先稳定定义“Agent 是谁”，再稳定它如何参与协作**。

### 推荐执行顺序

1. 先完成 task24，把首条切片的设计接口固定下来。
2. 同时推进 task26 和 task27，分别稳定执行面与控制面基础。
3. 在基础宿主稳定后，并行推进 task28 和 task29，稳定 Workspace 业务规则与模板体系。
4. 接着推进 task30，补齐知识、存储、图谱三类基础设施。
5. 在 Controller 与 Runtime 主链路稳定后，推进 task34，建立统一事件总线、订阅与直接唤醒链路。
6. 当 API 与治理链路稳定后，推进 task31，打通 CLI 
7. 在 TaskMap、知识与事件链路稳定后，推进 task35，建立 Workspace 协作驾驶舱和共享工具台。
8. 最后执行 task32，把观测面和集成验收收口。
9. 在主链路稳定后，推进 task33，把嵌入式桌面宿主纳入 Runtime 节点体系。

一个更直接的理解是：

- 先稳住 **Controller + Runtime** 两条骨架线
- 再稳住 **Workspace + AgentTemplate** 两条定义线
- 再补 **Event Bus**，把系统从轮询世界切换到事件世界
- 最后做观测面与嵌入式扩展能力收口

### 并行建议

- `task26` 与 `task27` 适合双线并行，是当前最重要的基础阶段。
- `task28` 与 `task29` 可并行，但都依赖基础宿主和控制入口已经稳定。
- `task30` 的知识库、统一存储、知识图谱底层服务可以拆成三个并行子流，但 Runtime 透明访问桥接必须最后收口。
- `task34` 中事件命名/Envelope、订阅治理、唤醒执行、死信与重放可分成多条子流并行，但最终契约必须统一收口。
- `task35` 中 TaskMap 主视图、Agent 侧边栏、关键事件流、共享工具台可以分成多条前后端子流并行，但聚合查询模型必须统一收口。
- `task31` 中 CLI（暂不开发CLI） /web 可以并行推进，但都依赖 Controller API 不再频繁变更。
- `task32` 中审计事件、指标、调试查询三条线可以并行，最后统一验收。
- `task33` 可以在主链路稳定后作为扩展能力推进；宿主抽象、节点注册、原生能力桥接可部分并行，但权限与审批接入必须后收口。

### 关键依赖关系

1. `task24` -> `task26`、`task27`
2. `task26` + `task27` -> `task29`
3. `task27` -> `task28`
4. `task26` + `task27` -> `task30`
5. `task26` + `task27` + `task29` -> `task34`
6. `task26` + `task27` -> `task31`
7. `task28` + `task30` + `task34` -> `task35`
8. `task26` + `task27` + `task28` + `task29` + `task30` + `task31` + `task34` + `task35` -> `task32`
9. `task26` + `task27` + `task29` + `task34` -> `task33`

### 当前任务分工原则

- `Tasks.md` 只保留路线总览、顺序、依赖和并行关系。
- `Tasks/` 目录下的任务文档承载各任务的详细说明、分步目标、前置依赖和验收标准。
- 如果某个能力既说不清归 Controller，又说不清归 Runtime，也说不清归 Agent，那么说明它的职责边界还没有定义干净，不应急着编码。

## 状态定义
- `done`：已完成并在源码中可用
- `partial`：已有实现，但未达到目标能力
- `todo`：尚未开始

## 一、能力状态总览（以源码为准）
| 领域 | 状态 | 说明 |
|---|---|---|
| CLI REPL 基础交互 | done | `Program.cs` 已支持消息流、工具调用渲染、slash 命令。 |
| Slash 命令选择器 | done | `SlashCommandPicker` 已支持 `/` 触发与筛选。 |
| Git 快照/回滚 | done | `GitSnapshotService` + `/undo` `/snapshot` `/history`。 |
| 权限与输出蒸馏 | done | `PermissionGuard`、`DefaultDistiller` 已接入工具层。 |
| Skill 注册与角色过滤 | done | `SkillRegistry` + `AgentRole` 过滤能力已接入。 |
| Swarm 编排 | partial | 已接入 v1 任务状态机（Created/Assigned/InProgress/PendingReview/Completed/Blocked/Failed）、continue executor（`/swarm continue`）与 reviewer gate 阻断；主链路已去模拟化，已接入真实 `git merge` 与最终 `dotnet test` 执行，并支持 merge 失败分类（冲突/分支缺失/仓库异常）；后续需补自动冲突修复策略与多仓库测试策略。 |
| Worker 生命周期 | partial | `WorkerManager` 已有 worktree 逻辑，并补齐 `/swarm cancel` 到会话状态机（任务标记 `Abandoned`）与执行重试；整体调度策略与恢复策略仍需增强。 |
| Prompt 模板体系 | partial | 已支持项目级 `.pudding/prompts/system.md` 与角色模板 `leader.md/worker.md/spirit.md` 外置；仍缺版本管理与灰度策略。 |
| Hook 体系 | partial | 已有 `IAgentHook`、Hook 注册中心（`metrics`/`audit_file`/`external`）、`/hook status|enable|disable` 管理与状态可视化；外部进程 Hook 已支持，仍缺标准协议。 |
| 中心锁与协同通知 | done | `CentralLockManager` 已完整接入事件总线（`ICoordinationEventBus`）；支持 LockAcquired/Denied/Released/ForceReleased/Expired/UnlockRequested 六类事件；`/locks request-unlock`、`/locks events` 命令就绪；右侧面板实时显示最新锁事件。 |
| 双模型潜意识体系 | partial | 已有双模型配置解析、文本检索召回、`[S]` 心流与记忆落盘；上下文分层压缩（`IHistoryCompressor` + `SubconsciousHistoryCompressor`）已实现，`config.ContextBudget.UseCompression=true` 可启用；向量召回与预算治理待实现。 |
| MCP 集成 | todo | 源码尚无 MCP client/host 实现。 |
| LSP 语义索引 | todo | 暂无 Roslyn/LSP runtime 集成。 |
| 三栏 TUI | partial | UI v1 已实现；UI v2 最小版已实现会话/运行状态双面板、`/status` 与 `/todo`；UI v3 已实现三栏；UI v4 已实现中栏多视图、worker 焦点切换与任务状态汇总；UI v5 最小版已实现 review 面板、diff 审批命令、持久化审批队列，approve 已接入 Git 快照并支持 apply hooks，reject 支持可确认 discard 且提供丢弃前文件预览。 |
| 计费模型抽象 | partial | Provider 已支持 `per_token/per_request/per_session/monthly_flat/local_free` 配置与会话估算显示；仍缺供应商模板与账单对账。 |
| YAML 配置扫描 | partial | 已支持 `pudding.yaml` + `providers/*.yaml` 启动扫描并合并服务商/模型，并支持 `swarm_final_test_command` 覆盖 Swarm 最终测试命令；仍缺 schema 校验与热重载。 |
| 冷启动引导与配置诊断 | partial | 已有首次引导（本地/Ollama、云服务商、YAML 脚手架）、配置健康检查（`/config check`）与安全自动修复（`/config fix`）；仍缺 YAML 模式一键修复与 schema 级修复。 |
| PuddingPlatformAdmin 登录+工作台 | done | Ant Design Pro 6 + UmiJS 4，登录页（mock 凭证 admin/pudding.dev），工作台 Dashboard（统计卡/近期事件/快捷入口/服务健康），运行于 http://localhost:8004。 |
| PuddingPlatformAdmin 业务页面 | done | Workspace 管理 / AgentTemplate 管理 / Session 历史三个页面已完成，含 ProTable、冻结/解冻/删除操作、详情 Drawer；mock 数据已在 `mock/platform.mock.ts` 注册，无需后端即可预览。 |
| PuddingPlatform JSON API | done | `AuthApiController`、`WorkspaceApiController`、`SessionApiController`、`AgentTemplateApiController` 四个 REST API 控制器已就绪；CORS Policy "AdminSpa" + Session 中间件已配置；dotnet build 0 errors。 |
| PuddingController 骨架 | partial | 13 个控制器 + 14 个 InMemory 服务均已就绪；`MessageIngressController → SessionRouter → RuntimeDispatcher → Runtime` 链路完整；待持久化层（PostgreSQL）替换 InMemory 实现。 |
| PuddingRuntime 骨架 | partial | `AgentExecutionService` 已集成 LLM 调用（OpenAI）、MemoryEngine；`RuntimeSelfRegistrationService` 自动向 Controller 注册。 |
| 端到端冒烟链路 | done | Controller(5000)→SessionRouter→RuntimeDispatcher→Runtime(5100)→OpenAI 链路已验证通畅；HTTP 200 返回正确的 `messageId`+`sessionId`；LLM 报 401/NoKey 属预期（未配置密钥）。 |
| Docker Compose 基础设施 | done | `docker-compose.yml` + 4 个 Dockerfile（多阶段 SDK 构建）+ `deploy/nginx/nginx.conf`（路由 `/api/`→Platform, `/ingress/`→Controller）+ `.env.example`；`docker compose config` 校验通过；启动顺序：Runtime → Controller → Platform → nginx；`Pudding__ControllerEndpoint` / `SelfEndpoint` / `LlmEndpoint`（含 /v1）已通过环境变量注入。 |

## 二、近期目标（M1）


1. Prompt 外置化（避免硬编码）：system prompt、角色 prompt、命令策略 prompt 可配置。  
2. Hook v1：pre_tool_call / post_tool_call / pre_reply / post_reply。  
3. TUI v1：三栏布局、状态面板、可见快捷键帮助。  
4. Swarm v1：去模拟化，明确 Leader/Worker 调度与真实执行链路。  
5. 可观测性：token、时延、工具成功率、上下文占用率可视化。  
6. 双模型潜意识 v1：双配置、记忆写入/召回、低干扰监督输出。  

## 三、中期目标（M2）
1. SKILL 热加载与版本管理。  
2. MCP 接入（stdio 优先）。  
3. LSP/Roslyn 语义检索。  
4. 多模型路由（按任务类型、预算、时延动态选择）。  

## 四、设计主线文档
- `Docs/Tasks/task19-coding-agent-blueprint.md`：总体架构与生命周期。
- `Docs/Tasks/task20-cli-ui-ux.md`：CLI/TUI 交互与快捷键规范。
- `Docs/Tasks/task21-subconscious-dual-llm.md`：潜意识/显意识双模型机制设计。
- `Docs/Tasks/task22-agent-roles-orchestration.md`：角色命名、职责边界、编排模式与 DoD。
- `Docs/Tasks/task23-central-lock-coordination.md`：中心锁、消息通知、冲突治理与恢复策略。
- `Docs/Tasks/task24-platform-v1-first-slice.md`：Platform V1 首条垂直切片的类/API 级开发子任务。

## 五、关键差距（必须补齐）
1. Prompt 仍为硬编码字符串，无法版本化、灰度、项目级定制。  
2. 缺少统一 Hook 总线，无法做审计、策略扩展、第三方生态接入。  
3. 上下文预算管理器已接入最小版（历史裁剪）且支持配置化（max tokens/history/tail preserve）；仍缺精确 token 预算与分层压缩策略。  
4. Swarm 主链路已去模拟化并可执行真实 merge/test，但冲突恢复策略与测试矩阵仍需增强。  
5. 尚缺“中心锁 + 协同通知”控制层，无法系统性防止多 Agent 并发编辑冲突。  
6. CLI 缺少“可发现”的高效键位系统与固定状态面板。  

## 六、执行顺序、优先级与依赖（2026-02-20）

### 6.1 优先级分层
1. `P0`（必须先完成）
- Prompt 模板体系（系统提示词/角色提示词/策略提示词外置）
- Hook 体系（pre/post tool、pre/post reply）
- Context/Token 预算治理（窗口占用、截断、压缩策略）
- 双模型潜意识 v2（在预算治理之上闭环）

2. `P1`（核心能力）
- Swarm 编排去模拟化
- Worker 生命周期稳定化（重试/取消/恢复）
- 中心锁与协同通知（冲突治理与任务边界隔离）
- 三栏 TUI 真布局 + 多 Agent 面板

3. `P2`（生态扩展）
- Skill 热加载与版本管理
- MCP 集成（stdio 优先）
- LSP/Roslyn 语义索引

### 6.2 依赖关系（DAG）
1. Prompt 模板体系 -> Hook 体系
2. Prompt 模板体系 + Hook 体系 -> Context/Token 预算治理
3. Context/Token 预算治理 -> 双模型潜意识 v2
4. Prompt + Hook + 预算治理 -> Swarm 编排与 Worker 生命周期稳定化
5. Swarm 稳定化 -> 中心锁与协同通知
6. 中心锁与协同通知 -> 三栏 TUI 多 Agent 面板
7. Hook 体系 -> MCP 集成
8. MCP + Prompt/Skill 体系 -> LSP/Roslyn 语义索引
9. MCP + LSP + Swarm -> 高阶 UI（diff 审批、拓扑视图）

### 6.3 推荐实现顺序（迭代）
1. 迭代 A（P0）：Prompt 外置化 + Hook v1 + Context/Token 预算治理
2. 迭代 B（P0）：双模型潜意识 v2（记忆写入/召回/维护与预算联动）
3. 迭代 C（P1）：Swarm/Worker 去模拟化与状态机稳定化
4. 迭代 D（P1）：中心锁与协同通知（自动加解锁、冲突提示、Leader 强制控制）
5. 迭代 E（P1）：三栏 TUI 真布局、右侧状态区、多 Agent 切换
6. 迭代 F（P2）：MCP v1 + Skill 热加载/版本管理
7. 迭代 G（P2）：LSP/Roslyn 语义索引 + 高阶交互（diff 审批）

### 6.4 阶段验收标准（DoD）
1. Prompt/Hook/预算治理
- System Prompt 不再硬编码，可按项目覆盖
- Hook 可注册并看到 pre/post 事件日志
- 超上下文窗口时有可观测的裁剪/压缩行为

2. 双模型潜意识
- 潜意识预算可配置且生效
- 记忆召回命中率有统计输出（最近窗口）
- 维护动作（compact/rebuild）可手动触发且有状态显示

3. Swarm/Worker
- 关键链路无模拟分支
- 任务状态机可恢复、可取消、可追踪
- 失败路径有重试与退避策略

4. 三栏 TUI
- 左交互流/右状态/底输入栏固定布局
- PgUp/PgDn、鼠标滚轮、Tab 切换一致可用
- 多 Agent 的当前上下文/任务状态可见

5. MCP/LSP
- 至少 1 个 MCP server 成功接入并可调用
- LSP 索引可用于上下文增强（符号/引用检索）
