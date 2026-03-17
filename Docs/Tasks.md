# Pudding Agent Network 任务与状态看板

最后更新：2026-03-15

## 2026-03-15 第一批平台任务（Pudding Agent Network V1）

目标：先打通首条真实垂直切片，并为知识、存储、治理和多客户端扩展预留稳定接口。

目标链路：`CLI  -> Controller API -> Workspace 路由 -> ServiceSession -> Runtime Agent -> 真实 LLM 回复`

第2目标链路
目标链路：`PuddingPlatform（含admin管理界面，PuddingPlatformAdmin）  -> Controller API -> Workspace 路由 -> ServiceSession -> Runtime Agent -> 真实 LLM 回复`


我们桌面端PuddingAvalonia（优先级最低）优先级最低。



总览文档：
- [Tasks/task24-platform-v1-first-slice.md](Tasks/task24-platform-v1-first-slice.md)
- 架构.md

细化任务文档：
- [Tasks/task26-runtime-foundation.md](Tasks/task26-runtime-foundation.md)
- [Tasks/task27-controller-routing-session.md](Tasks/task27-controller-routing-session.md)
- [Tasks/task28-platform-workspace-governance.md](Tasks/task28-platform-workspace-governance.md)
- [Tasks/task29-agent-template-and-audit.md](Tasks/task29-agent-template-and-audit.md)
- [Tasks/task30-knowledge-infrastructure.md](Tasks/task30-knowledge-infrastructure.md)
- [Tasks/task31-client-surfaces.md](Tasks/task31-client-surfaces.md)
- [Tasks/task32-observability-integration.md](Tasks/task32-observability-integration.md)
- [Tasks/task33-embedded-runtime-host.md](Tasks/task33-embedded-runtime-host.md)

补充约束：
- 平台内置支持 Email Channel。
- 一个 Workspace 可以挂接多个渠道，并为每个渠道声明默认 Agent 或允许 Agent 集合。
- 渠道接入机制本身必须插件化，便于后续扩展更多渠道。
- 知识库归属于 Workspace，由 Controller 持有服务端能力，Runtime 提供透明访问支持。
- 统一存储层由 Controller 持有，Runtime 提供挂载与访问支持，上层 Agent 无感。
- 知识图谱归属于 Workspace 共享资产，底层先用 PostgreSQL 实现，上层 Agent 无感。
- 语音批准属于系统控制链路，不属于业务 Agent。
- 每个 Workspace 至少拥有 1 个审计 Agent。
- 新增客户端层 `PuddingAvalonia（优先级最低）`，作为用户持有的桌面控制端。
- `PuddingRuntime` 后续支持嵌入其他 C# 桌面软件，使其成为可调度 Runtime 节点，并暴露受控原生能力。

### 路线总览

| 阶段 | 任务 | 目标 | 前置依赖 | 可并行 |
|---|---|---|---|---|
| Phase 0 | [task24-platform-v1-first-slice.md](Tasks/task24-platform-v1-first-slice.md) | 固化首条垂直切片的类/API 设计 | 架构分层已稳定 | 否 |
| Phase 1A | [task26-runtime-foundation.md](Tasks/task26-runtime-foundation.md) | 建立 `PuddingRuntime` 作为 Agent Runtime 宿主 | Phase 0 | 可与 Phase 1B 并行 |
| Phase 1B | [task27-controller-routing-session.md](Tasks/task27-controller-routing-session.md) | 建立 `PuddingController` 路由、会话与控制入口 | Phase 0 | 可与 Phase 1A 并行 |
| Phase 2A | [task28-platform-workspace-governance.md](Tasks/task28-platform-workspace-governance.md) | 建立 Platform 的 Workspace 业务层与治理策略 | Phase 1B | 可与 Phase 2B 并行 |
| Phase 2B | [task29-agent-template-and-audit.md](Tasks/task29-agent-template-and-audit.md) | 建立 AgentTemplate、审计模板和运行画像 | Phase 1A + Phase 1B | 可与 Phase 2A 并行 |
| Phase 3 | [task30-knowledge-infrastructure.md](Tasks/task30-knowledge-infrastructure.md) | 建立知识库、统一存储、知识图谱和 Runtime 透明访问 | Phase 1A + Phase 1B | 可与 Phase 2A/2B 后段局部并行 |
| Phase 4 | [task31-client-surfaces.md](Tasks/task31-client-surfaces.md) | 建立 CLI / Avalonia（优先级最低） 客户端控制面 | Phase 1A + Phase 1B，且 API 基本稳定 | CLI 与 Avalonia（优先级最低） 可并行 |
| Phase 5 | [task32-observability-integration.md](Tasks/task32-observability-integration.md) | 建立可观测性并完成阶段验收 | Phase 1-4 | 审计、指标、调试查询可并行 |
| Phase 6 | [task33-embedded-runtime-host.md](Tasks/task33-embedded-runtime-host.md) | 支持把其他 C# 桌面软件作为嵌入式 Runtime 节点调度 | Phase 1A + Phase 1B + Phase 2B | 可与 Phase 3 后段和 Phase 4 后段局部并行 |

### 推荐执行顺序

1. 先完成 task24，把首条切片的设计接口固定下来。
2. 同时推进 task26 和 task27，分别稳定执行面与控制面基础。
3. 在基础宿主稳定后，并行推进 task28 和 task29，稳定 Workspace 业务规则与模板体系。
4. 接着推进 task30，补齐知识、存储、图谱三类基础设施。
5. 当 API 与治理链路稳定后，推进 task31，打通 CLI 
6. 最后执行 task32，把观测面和集成验收收口。
7. 在主链路稳定后，推进 task33，把嵌入式桌面宿主纳入 Runtime 节点体系。

### 并行建议

- `task26` 与 `task27` 适合双线并行，是当前最重要的基础阶段。
- `task28` 与 `task29` 可并行，但都依赖基础宿主和控制入口已经稳定。
- `task30` 的知识库、统一存储、知识图谱底层服务可以拆成三个并行子流，但 Runtime 透明访问桥接必须最后收口。
- `task31` 中 CLI /web 可以并行推进，但都依赖 Controller API 不再频繁变更。
- `task32` 中审计事件、指标、调试查询三条线可以并行，最后统一验收。
- `task33` 可以在主链路稳定后作为扩展能力推进；宿主抽象、节点注册、原生能力桥接可部分并行，但权限与审批接入必须后收口。

### 关键依赖关系

1. `task24` -> `task26`、`task27`
2. `task26` + `task27` -> `task29`
3. `task27` -> `task28`
4. `task26` + `task27` -> `task30`
5. `task26` + `task27` -> `task31`
6. `task26` + `task27` + `task28` + `task29` + `task30` + `task31` -> `task32`
7. `task26` + `task27` + `task29` -> `task33`

### 当前任务分工原则

- `Tasks.md` 只保留路线总览、顺序、依赖和并行关系。
- `Tasks/` 目录下的任务文档承载各任务的详细说明、分步目标、前置依赖和验收标准。

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
| Hook 体系 | partial | 已有 `IAgentHook`、Hook 注册中心（`metrics`/`audit_file`/`external`）、`/hook status|enable|disable` 管理与状态可视化；外部进程 Hook 已支持，仍缺标准协议与隔离沙箱。 |
| 中心锁与协同通知 | todo | 设计已完成（Task 23）；待实现中心锁、自动加解锁、Leader 强制控制与通知总线。 |
| 双模型潜意识体系 | partial | 已有双模型配置解析、文本检索召回、`[S]` 心流与记忆落盘；向量召回与预算治理待实现。 |
| MCP 集成 | todo | 源码尚无 MCP client/host 实现。 |
| LSP 语义索引 | todo | 暂无 Roslyn/LSP runtime 集成。 |
| 三栏 TUI | partial | UI v1 已实现；UI v2 最小版已实现会话/运行状态双面板、`/status` 与 `/todo`；UI v3 已实现三栏；UI v4 已实现中栏多视图、worker 焦点切换与任务状态汇总；UI v5 最小版已实现 review 面板、diff 审批命令、持久化审批队列，approve 已接入 Git 快照并支持 apply hooks，reject 支持可确认 discard 且提供丢弃前文件预览。 |
| 计费模型抽象 | partial | Provider 已支持 `per_token/per_request/per_session/monthly_flat/local_free` 配置与会话估算显示；仍缺供应商模板与账单对账。 |
| YAML 配置扫描 | partial | 已支持 `pudding.yaml` + `providers/*.yaml` 启动扫描并合并服务商/模型，并支持 `swarm_final_test_command` 覆盖 Swarm 最终测试命令；仍缺 schema 校验与热重载。 |
| 冷启动引导与配置诊断 | partial | 已有首次引导（本地/Ollama、云服务商、YAML 脚手架）、配置健康检查（`/config check`）与安全自动修复（`/config fix`）；仍缺 YAML 模式一键修复与 schema 级修复。 |

## 二、近期目标（M1）
目标：把 PuddingCode 从“可用原型”推进到“稳定编码代理 CLI”。

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
