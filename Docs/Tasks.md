# Pudding Agent Network 任务与状态看板

最后更新：2026-03-15

## 2026-03-15 第一批平台任务（Pudding Agent Network V1）

目标：先打通首条真实垂直切片，并为知识、存储、治理和多客户端扩展预留稳定接口。

目标链路：`CLI / Avalonia -> Controller API -> Workspace 路由 -> ServiceSession -> Runtime Agent -> 真实 LLM 回复`

细化任务文档：
- `Docs/Tasks/task24-platform-v1-first-slice.md`

补充约束：
- 平台内置支持 Email Channel。
- 一个 Workspace 可以挂接多个渠道，并为每个渠道声明默认 Agent 或允许 Agent 集合。
- 渠道接入机制本身必须插件化，便于后续扩展更多渠道。
- 知识库归属于 Workspace，由 Controller 持有服务端能力，Runtime 提供透明访问支持。
- 统一存储层由 Controller 持有，Runtime 提供挂载与访问支持，上层 Agent 无感。
- 知识图谱归属于 Workspace 共享资产，底层先用 PostgreSQL 实现，上层 Agent 无感。
- 语音批准属于系统控制链路，不属于业务 Agent。
- 每个 Workspace 至少拥有 1 个审计 Agent。
- 新增客户端层 `PuddingAvalonia`，作为用户持有的桌面控制端。

### A. PuddingController

1. 实现 `ChannelManager` 最小模型与渠道身份映射
验收标准：能够接收来自 CLI through Controller API 的消息请求，并附带渠道、用户、Workspace 识别信息进入平台；内置支持 Email Channel 基础模型。

2. 实现 `ChannelPluginHost` 与 `IChannelProvider` 注册机制
验收标准：Controller 可注册内置渠道 Provider，如 CLI 和 Email；后续新增渠道不需要改核心路由接口定义。

3. 实现 `SessionRouter` 的 Workspace 命中与 AgentTemplate 路由
验收标准：Controller 能根据渠道绑定、用户身份/角色、消息类型和基础意图分类，命中正确 Workspace 与 AgentTemplate；支持一个 Workspace 下多个渠道绑定到默认/允许 Agent；路由决策可查询。

4. 实现 `ServiceSession` 自动创建或复用逻辑
验收标准：收到消息时 Controller 能自动新建或复用 ServiceSession，并可查询 Session 状态、所属 Workspace、所属 Runtime。

5. 实现 `AuthorizationService` 最小权限校验链
验收标准：Controller 能在消息进入执行前完成用户、WorkspaceRole、AgentTemplate 三者交集校验；拒绝原因可查询。

6. 实现 `ApprovalService` 最小审批记录链路
验收标准：高风险动作能生成 ApprovalRecord，支持确认码、过期时间、审批状态查询；CLI 与 HTTP API 可完成批准。

7. 扩展 `ApprovalService` 支持语音批准入口
验收标准：平台可接收来自客户端的语音批准请求；语音批准与用户身份、审批记录、时间窗口绑定；批准链路由系统控制，不由 Agent 自行放行。

8. 实现 `AuditStore` 与首批审计事件落盘
验收标准：至少能记录并查询“渠道消息进入、Session 创建/复用、路由决策、审批请求与结果、工具执行、Workflow Step 开始/完成、记忆写入/提升、Workspace 冻结动作”。

9. 实现 `WorkflowEngine` 最小两到三步执行骨架
验收标准：Controller 可执行一个 2 到 3 步 Workflow，支持步骤开始/完成、失败中止和状态查询。

10. 实现 `KnowledgeBaseService` 与 `KnowledgeIndexService`
验收标准：Workspace 可挂接知识库；支持目录文件导入、RAG 检索、向量召回与 Agent 生产知识的入库/提升；上层 Agent 不直接感知底层数据库。

11. 实现 `UnifiedStorageService`
验收标准：Controller 可统一管理 NFS 和对象存储访问；支持跨网络或跨物理机 Runtime 访问统一存储；对象存储底层可采用 MinIO Docker。

12. 实现 `KnowledgeGraphService`
验收标准：Workspace 可共享结构化知识图谱；底层先基于 PostgreSQL 实现实体、关系和查询；Agent 通过统一知识能力间接访问。

13. 实现 `WorkspaceAgentControlService`
验收标准：可冻结某个 Workspace 内全部 Agent；冻结请求可由审计 Agent 或系统控制入口触发；冻结状态可查询和审计。

### B. PuddingPlatform

1. 实现 `WorkspaceBusinessService`
验收标准：Platform 能承载 Workspace 级业务逻辑，统一组织知识库、知识图谱、统一存储、Agent 暴露策略与业务流程编排。

2. 实现 `ServiceExposurePolicy`
验收标准：Platform 能定义某个 Workspace 对哪些渠道暴露哪些 Agent、知识能力和服务形态。

3. 实现 `AuditGovernancePolicy`
验收标准：Platform 能确保每个 Workspace 至少拥有 1 个审计 Agent，并管理审计 Agent 的冻结、批准和监督策略。

### C. PuddingRuntime

1. 实现 `SessionRuntime` 最小会话承载能力
验收标准：Runtime 能承载 ServiceSession，持有基础会话状态，并向 Controller 回报 Session 存活与状态。

2. 实现 `AgentRuntime` 最小执行链路
验收标准：Runtime 能根据 AgentTemplate 创建 AgentInstance，并处理单轮真实 LLM 回复。

3. 实现 `SkillRuntime` 最小内置技能装配
验收标准：至少支持基础低风险技能注入；Agent 执行时可获得模板声明的最小能力集。

4. 实现 `MemoryRuntime` 最小记忆读写边界
验收标准：至少区分 Session Memory 与 Workspace Memory；公开群/受限输入默认不能直接写入长期记忆。

5. 实现 `KnowledgeAccessRuntime`
验收标准：Runtime 可透明访问 Workspace 知识库、知识图谱和统一存储；上层 Agent 不需要感知底层 KV、向量库、PostgreSQL、NFS 或对象存储。

6. 实现 `SandboxExecutor` 最小受限执行环境
验收标准：文件写入、网络访问、Shell/进程执行等高风险操作能够进入受限执行环境，并能被平台审批链控制。

### D. PuddingCLI

1. 改为通过 Controller API 发起消息与会话
验收标准：CLI 不再直接驱动本地运行时主链路；从 CLI 发消息可获得平台返回的真实 Agent 回复。

2. 增加 Session / Approval / Workflow 查询命令
验收标准：CLI 可查询 Session 状态、审批待办、Workflow 执行状态。

3. 增加批准命令与确认码提交流程
验收标准：CLI 可对高风险动作进行批准，且批准结果可在平台查询。

4. 增加路由与审计调试查询
验收标准：CLI 可查看“某条消息命中了哪个 Agent”“某个动作为何被拒绝”“某个 Workflow 卡在哪一步”。

5. 增加 Workspace 冻结与审计控制命令
验收标准：CLI 可触发或查询 Workspace 冻结状态，并查看对应审计记录。

### E. PuddingAvalonia

1. 实现桌面客户端基础会话面板
验收标准：Avalonia 客户端可登录、查看 Workspace、发起消息、查看会话与 Agent 状态。

2. 实现语音批准入口
验收标准：客户端可采集语音并提交系统批准请求；批准结果与 ApprovalRecord 绑定可查询。

3. 实现 Workspace 控制面板
验收标准：客户端可触发审计相关控制，例如请求审计 Agent 冻结 Workspace 内全部 Agent。

### F. PuddingAgent

1. 提供少量内置 `AgentTemplate`
验收标准：至少提供一个对外服务 Agent 模板、一个低风险任务 Agent 模板；可被 Controller 路由命中并由 Runtime 创建实例。

2. 提供 `AuditAgentTemplate`
验收标准：每个 Workspace 至少可挂接 1 个审计 Agent 模板；审计 Agent 不接触外部原始信息与业务上下文，只处理结构化、脱敏、受限视图。

3. 为模板补齐权限画像与运行画像
验收标准：模板可声明默认能力、受限运行环境、心跳策略与默认记忆策略，并被 Controller/Runtime 共同读取。

### G. 集成验收

1. 打通首条垂直切片
验收标准：从 CLI 或 Avalonia 发消息，系统完成 Workspace/AgentTemplate 路由、自动创建或复用 ServiceSession、投递到 Runtime，并返回真实 LLM 回复。

2. 打通首条高风险审批链
验收标准：高风险动作不会直接执行，而是生成 ApprovalRecord；用户可通过 CLI、HTTP API 或客户端语音提交批准，批准后再执行。

3. 打通最小知识访问链路
验收标准：Workspace 级知识库、知识图谱与统一存储可被 Runtime 透明访问，并为 Agent 提供无感知识支持。

4. 打通最小审计冻结链路
验收标准：用户可通过系统控制入口请求审计 Agent 冻结 Workspace 内全部 Agent；冻结结果、审批与审计状态可查询。

5. 打通最小 Workflow 链路
验收标准：一个 2 到 3 步 Workflow 可以执行完成，且每一步状态、失败和审计事件可查询。

6. 打通最小可观测性链路
验收标准：至少能查询 Session 状态、Agent 状态、路由决策、审批待办、Workflow 状态、知识访问链路与审计事件。

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
