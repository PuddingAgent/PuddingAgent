# PuddingCode 任务索引

> 本文档跟踪 PuddingCode 项目所有设计与开发任务的状态。
>
> **状态说明：** 📋 规划中 · ✏️ 设计中 · 🚧 开发中 · ✅ 已完成 · ⏸️ 暂停

---

## 项目概览

| 项 | 内容 |
| --- | --- |
| **项目名称** | PuddingCode — Agentic Self-Programming CLI |
| **技术栈** | C# / .NET 10 · Spectre.Console · Avalonia UI · Semantic Kernel |
| **代码仓库** | [github.com/hyfree/PuddingCode](https://github.com/hyfree/PuddingCode) |
| **分支** | `master` |
| **架构文档** | [Docs/Map.md](Map.md) |
| **设计方案** | [Docs/讨论.md](讨论.md) — 行业瓶颈分析 + 6 层架构 + 5 个突破方向 |

---

## 任务列表

### 📐 设计任务

| # | 任务 | 状态 | 文档 | 说明 |
| --- | --- | --- | --- | --- |
| 01 | CLI 交互界面设计 | ✏️ 设计中 | [task01交互界面.md](tasks/task01交互界面.md) | 视觉语言、布局、斜杠指令、Diff 预览、架构支撑层 |
| 02 | 桌面端设计 | ✏️ 设计中 | [task02桌面端设计.md](tasks/task02桌面端设计.md) | 共享内核架构、三段式布局、UI 交互协议、桌面特有功能 |
| 03 | V0.1 实现方案 | ✅ 已完成 | [task03开始.md](tasks/task03开始.md) | Core 抽象层、本地工具、LLM 网关、CLI REPL 闭环 |
| 04 | 蜂群模式设计 | ✏️ 设计中 | [task04蜂群.md](tasks/task04蜂群.md) | 契约驱动开发、作用域隔离、P2P 分布式协作、自治选举 |
| 05 | Swarm 视图设计 | ✏️ 设计中 | [task05Swarm视图.md](tasks/task05Swarm视图.md) | 拓扑图、弹幕思维层、指挥官模式、编排可视化、CLI 极客风格 |
| 06 | Agent 消息系统 | ✏️ 设计中 | [task06agent消息.md](tasks/task06agent消息.md) | 气泡交互、私聊连线、消息总线 |
| 07 | Agent 命名系统 | ✏️ 设计中 | [task07agent名字.md](tasks/task07agent名字.md) | 布丁风格昵称生成、身份标识 |
| 08 | 记忆系统 | ✏️ 设计中 | [task08记忆系统.md](tasks/task08记忆系统.md) | Markdown 记忆、SQLite-VSS 检索、分层存储 |
| 09 | Agent 生命周期 | ✏️ 设计中 | [task09agent的生命周期.md](tasks/task09agent的生命周期.md) | 模板管理、异步创建、休眠唤醒、销毁回收 |
| 10 | Agent 能力体系 | ✏️ 设计中 | [task10agent能力.md](tasks/task10agent能力.md) | 三层技能架构、SkillRegistry、权限模型、自适应 Prompt |
| 11 | 权限与安全沙盒 | ✏️ 设计中 | [task11权限.md](tasks/task11权限.md) | 路径沙盒、指令分级白名单、PermissionGuard、人工授权 UI |
| 12 | 感官过滤 | ✏️ 设计中 | [task12感官过滤.md](tasks/task12感官过滤.md) | 输出蒸馏三层模型、断路器截断、异构模型压缩链、自适应规则 |
| 13 | 上下文预热 | ✏️ 设计中 | [task13上下文预热.md](tasks/task13上下文预热.md) | 三级预热、并发启动、后台索引引擎、认知包、功耗控制 |
| 14 | SKILL 插件化 | ✏️ 设计中 | [task14SKILL插件化.md](tasks/task14SKILL插件化.md) | 插件三层接口、动态 DLL 加载、探活机制、分级调用模型 |
| 15 | MCP 服务器集成 | ✏️ 设计中 | [task15MCP服务器.md](tasks/task15MCP服务器.md) | JSON-RPC 2.0 对接、工具自动发现、权限桥接、懒启动 |
| 16 | 服务商与模型管理 | ✏️ 设计中 | [task16服务商界面.md](tasks/task16服务商界面.md) | 模型元数据、能力标签、成本预算熔断、抽样评分、动态路由 |

### 🔨 开发任务

| # | 任务 | 状态 | 依赖 | 说明 |
| --- | --- | --- | --- | --- |
| D01 | Core 抽象层（接口 + 模型） | ✅ 已完成 | Task 03 | `ITool`、`IToolRegistry`、`ILlmGateway`、`IAgentOrchestrator` |
| D02 | 本地工具实现 | ✅ 已完成 | D01 | `FileTool`（CliWrap）、`ShellTool` |
| D03 | LLM 网关 + Agent 编排器 | ✅ 已完成 | D01 | `OpenAiLlmGateway`、`AgentOrchestrator`、Tool Calling 闭环 |
| D04 | CLI REPL 原型 | ✅ 已完成 | D02, D03 | Spectre.Console REPL、事件流渲染 |
| D05 | LSP 语义感知集成 | 📋 规划中 | D01 | Roslyn LSP Client、`/map` 指令 |
| D06 | Git 快照与回滚 | 📋 规划中 | D01 | `/undo` 指令、时光机基础 |
| D07 | 桌面端 MVP（Avalonia） | 🚧 开发中 | D04 | 双视图 Editor/Swarm、流式 Chat、思维链、Swarm 拓扑图 |
| D08 | 蜂群契约 + 串行编排 | 📋 规划中 | D04, D06, Task 04 | `IContractManager`、`ScopedFileTool`、`ISwarmOrchestrator`、作用域隔离 |
| D09 | 蜂群并行 + Worktree | 📋 规划中 | D08 | Git Worktree 管理、Leader-Worker 并行、自动合并 |
| D10 | P2P 分布式蜂群 | 📋 规划中 | D09 | `ISwarmTransport`(P2P)、`ILeaderElection`、NAT 穿越、选举 |
| D11 | 蜂群多模型 + 费用 + 可视化 | 📋 规划中 | D10 | Model Router、Token 预算、费用仪表盘、Swarm 可视化 |

---

## 里程碑

| 版本 | 目标 | 预期状态 |
| --- | --- | --- |
| **V0.1** | Core 抽象层 + 本地工具 + LLM 网关 + CLI REPL 闭环（Task 03） | ✅ 已完成 |
| **V0.3** | 自我调试闭环基础：编译验证 + 错误回传 + 递归修复 | 📋 规划中 |
| **V0.5** | 语义代码引擎：LSP 集成 + 符号级操作 + `/map` 指令 | 📋 规划中 |
| **V0.8** | 蜂群 Phase 1：契约驱动 + 作用域隔离 + Leader-Worker 串行 | 📋 规划中 |
| **V0.9** | 蜂群 Phase 2：多 Worker 并行 + Git Worktree + Leader 并行监控 | 📋 规划中 |
| **V1.0** | 蜂群 Phase 3：P2P 分布式组网 + NAT 穿越 + Leader 选举 | 📋 规划中 |
| **V1.x** | 蜂群 Phase 4：多模型调度 + 费用控制 + 桌面端 Swarm 可视化 | 📋 规划中 |
