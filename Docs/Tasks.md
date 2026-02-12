# PuddingCode 任务索引

> 本文档跟踪 PuddingCode 项目所有设计与开发任务的状态。
>
> **状态说明：** 📋 规划中 · ✏️ 设计中 · 🚧 开发中 · ✅ 已完成 · ⏸️ 暂停
>
> **优先级说明：** 🔴 P0 关键路径 · 🟠 P1 重要 · 🟡 P2 增强 · 🟢 P3 远期

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

## 依赖拓扑图

```
                     Task 03 (V0.1 方案) ✅
                       │
            ┌──────────┼──────────┐
            ▼          ▼          ▼
          D01 ✅     Task 01    Task 02
         (Core)    (CLI 设计)  (桌面设计)
        ┌──┴──┐
        ▼     ▼
      D02 ✅  D03 ✅
     (工具)  (LLM 网关)
        └──┬──┘
           ▼
         D04 ✅ ──────────────────────────────────────────────┐
        (REPL)                                                │
     ┌────┼────────┐                                          │
     ▼    ▼        ▼                                          │
   D05  D06      D07 🚧 ◄── Task 01, 02                      │
  (LSP) (Git)   (桌面MVP)                                     │
     │    │        │                                          │
     │    │        ├──── Task 05 (Swarm 视图) ◄── Task 04     │
     │    │        └──── Task 06 (消息系统)                    │
     │    │              Task 07 (命名系统)                    │
     │    │                                                   │
     │    └────┐                                              │
     │         ▼                                              │
     │       D08 ◄── Task 04 (蜂群模式)                       │
     │      (契约串行编排)                                     │
     │         │                                              │
     │         ▼                                              │
     │       D09                                              │
     │      (并行 Worktree)                                   │
     │         │                                              │
     │         ▼                                              │
     │       D10                                              │
     │      (P2P 分布式)                                      │
     │         │                                              │
     │         ▼                                              │
     │       D11 ◄── Task 16 (服务商管理)                      │
     │      (多模型+费用+可视化)                                │
     │                                                        │
     │                                                        │
     │  ◄── 独立演进的"Agent 智能化"链 ──►                     │
     │                                                        │
     │    Task 04 (蜂群) ◄── Task 03                          │
     │       │                                                │
     │       ▼                                                │
     │    Task 09 (生命周期) ── Task 08 (记忆)                 │
     │       │                                                │
     │       ▼                                                │
     │    Task 10 (能力体系)                                   │
     │    ┌──┼──────────────────┐                              │
     │    ▼  ▼                  ▼                              │
     │  T11  T12 ──► T13     Task 16                          │
     │ (权限)(过滤)  (预热)  (服务商)                           │
     │    │    │       │                                       │
     │    ▼    │       ▼                                       │
     │  T14 ◄─┘    (预热)                                     │
     │ (插件化)                                                │
     │    │                                                    │
     │    ▼                                                    │
     │  T15                                                    │
     │ (MCP)                                                   │
     │                                                         │
```

---

## 任务列表

### 📐 设计任务

| # | 任务 | 状态 | 优先级 | 依赖 | 文档 | 说明 |
| --- | --- | --- | --- | --- | --- | --- |
| 03 | V0.1 实现方案 | ✅ 已完成 | 🔴 P0 | — | [task03开始.md](tasks/task03开始.md) | Core 抽象层、本地工具、LLM 网关、CLI REPL 闭环 |
| 01 | CLI 交互界面设计 | ✏️ 设计中 | 🟠 P1 | Task 03 | [task01交互界面.md](tasks/task01交互界面.md) | 视觉语言、布局、斜杠指令、Diff 预览、架构支撑层 |
| 02 | 桌面端设计 | ✏️ 设计中 | 🟠 P1 | Task 03 | [task02桌面端设计.md](tasks/task02桌面端设计.md) | 共享内核架构、三段式布局、UI 交互协议、桌面特有功能 |
| 04 | 蜂群模式设计 | ✏️ 设计中 | 🔴 P0 | Task 03 | [task04蜂群.md](tasks/task04蜂群.md) | 契约驱动开发、作用域隔离、P2P 分布式协作、自治选举 |
| 09 | Agent 生命周期 | ✏️ 设计中 | 🔴 P0 | Task 04 | [task09agent的生命周期.md](tasks/task09agent的生命周期.md) | 模板管理、异步创建、休眠唤醒、销毁回收 |
| 10 | Agent 能力体系 | ✏️ 设计中 | 🔴 P0 | Task 04, 09 | [task10agent能力.md](tasks/task10agent能力.md) | 三层技能架构、SkillRegistry、权限模型、自适应 Prompt |
| 11 | 权限与安全沙盒 | ✏️ 设计中 | 🔴 P0 | Task 10, D02 | [task11权限.md](tasks/task11权限.md) | 路径沙盒、指令分级白名单、PermissionGuard、人工授权 UI |
| 12 | 感官过滤 | ✏️ 设计中 | 🔴 P0 | Task 10, 11, D02 | [task12感官过滤.md](tasks/task12感官过滤.md) | 输出蒸馏三层模型、断路器截断、异构模型压缩链、自适应规则 |
| 08 | 记忆系统 | ✏️ 设计中 | 🟠 P1 | Task 09 | [task08记忆系统.md](tasks/task08记忆系统.md) | Markdown 记忆、SQLite-VSS 检索、分层存储 |
| 06 | Agent 消息系统 | ✏️ 设计中 | 🟠 P1 | Task 04 | [task06agent消息.md](tasks/task06agent消息.md) | 气泡交互、私聊连线、消息总线 |
| 07 | Agent 命名系统 | ✏️ 设计中 | 🟡 P2 | Task 04 | [task07agent名字.md](tasks/task07agent名字.md) | 布丁风格昵称生成、身份标识 |
| 05 | Swarm 视图设计 | ✏️ 设计中 | 🟡 P2 | Task 04, D07 | [task05Swarm视图.md](tasks/task05Swarm视图.md) | 拓扑图、弹幕思维层、指挥官模式、编排可视化、CLI 极客风格 |
| 13 | 上下文预热 | ✏️ 设计中 | 🟡 P2 | Task 10, 12, D03 | [task13上下文预热.md](tasks/task13上下文预热.md) | 三级预热、并发启动、后台索引引擎、认知包、功耗控制 |
| 14 | SKILL 插件化 | ✏️ 设计中 | 🟡 P2 | Task 10, 11, 13 | [task14SKILL插件化.md](tasks/task14SKILL插件化.md) | 插件三层接口、动态 DLL 加载、探活机制、分级调用模型 |
| 16 | 服务商与模型管理 | ✏️ 设计中 | 🟡 P2 | Task 12, 13, D03 | [task16服务商界面.md](tasks/task16服务商界面.md) | 模型元数据、能力标签、成本预算熔断、抽样评分、动态路由 |
| 15 | MCP 服务器集成 | ✏️ 设计中 | 🟢 P3 | Task 11, 14 | [task15MCP服务器.md](tasks/task15MCP服务器.md) | JSON-RPC 2.0 对接、工具自动发现、权限桥接、懒启动 |
| 17 | Leader 动态路由 | ✏️ 设计中 | 🟠 P1 | Task 04, 09, 10, D08 | [task17Leader的动态路由.md](tasks/task17Leader的动态路由.md) | Plan-then-Execute、智能路由、能力匹配、熔断机制、结果汇聚 |

### 🔨 开发任务

| # | 任务 | 状态 | 优先级 | 依赖 | 说明 |
| --- | --- | --- | --- | --- | --- |
| D01 | Core 抽象层（接口 + 模型） | ✅ 已完成 | 🔴 P0 | Task 03 | `ITool`、`IToolRegistry`、`ILlmGateway`、`IAgentOrchestrator` |
| D02 | 本地工具实现 | ✅ 已完成 | 🔴 P0 | D01 | `FileTool`（CliWrap）、`ShellTool` |
| D03 | LLM 网关 + Agent 编排器 | ✅ 已完成 | 🔴 P0 | D01 | `OpenAiLlmGateway`、`AgentOrchestrator`、Tool Calling 闭环 |
| D04 | CLI REPL 原型 | ✅ 已完成 | 🔴 P0 | D02, D03 | Spectre.Console REPL、事件流渲染 |
| D05 | LSP 语义感知集成 | 📋 规划中 | 🟠 P1 | D01 | Roslyn LSP Client、`/map` 指令 |
| D06 | Git 快照与回滚 | 📋 规划中 | 🔴 P0 | D01 | `/undo` 指令、时光机基础（D08 前置） |
| D07 | 桌面端 MVP（Avalonia） | 🚧 开发中 | 🟠 P1 | D04, Task 01, 02 | 双视图 Editor/Swarm、流式 Chat、思维链、Swarm 拓扑图、Agent 右键菜单 |
| D08 | 蜂群契约 + 串行编排 | 📋 规划中 | 🔴 P0 | D04, D06, Task 04 | `IContractManager`、`ScopedFileTool`、`ISwarmOrchestrator`、作用域隔离 |
| D09 | 蜂群并行 + Worktree | 📋 规划中 | 🟠 P1 | D08 | Git Worktree 管理、Leader-Worker 并行、自动合并 |
| D10 | P2P 分布式蜂群 | 📋 规划中 | 🟢 P3 | D09 | `ISwarmTransport`(P2P)、`ILeaderElection`、NAT 穿越、选举 |
| D11 | 蜂群多模型 + 费用 + 可视化 | 📋 规划中 | 🟢 P3 | D10, Task 16 | Model Router、Token 预算、费用仪表盘、Swarm 可视化 |

---

## 实现顺序

### 第一阶段：核心安全闭环 (V0.2) 🔴 关键路径

> **目标：** 在已完成的 V0.1 基础上，补全安全防护和输出过滤，让单 Agent 可安全地执行工具操作。
>
> **已有基础：** `ITool` / `IToolRegistry` / `ShellTool` / `FileTool` / `OpenAiLlmGateway` / `AgentOrchestrator` / CLI REPL

```
D06 Git 快照 ──────────────────────┐
                                   │  (并行)
Task 11 → PermissionGuard 实现 ───┤
                                   │  (并行)
Task 12 → OutputDistiller 实现 ───┘
                                   │
                                   ▼
              集成到 ShellTool / FileTool
                                   │
                                   ▼
                          V0.2 安全闭环 ✓
```

| 顺序 | 任务 | 产出 | 依赖前置 |
| --- | --- | --- | --- |
| ① | **D06** Git 快照与回滚 | `/undo` 指令 | D01 ✅ |
| ② | **Task 11** → 实现 `PermissionGuard` | 路径沙盒 + 命令白名单 | D02 ✅, Task 10 设计 |
| ③ | **Task 12** → 实现 `DefaultDistiller` | 输出截断 + 错误增强 | D02 ✅ |
| ④ | 集成到 `ShellTool` / `FileTool` | 工具操作前权限校验 + 操作后输出过滤 | ①②③ |

### 第二阶段：蜂群串行 (V0.3-0.8)

> **目标：** 实现 Leader-Worker 串行协作、契约驱动、作用域隔离。

```
Task 04 设计完成
    │
    ▼
Task 09 Agent 生命周期 ─→ Task 10 能力体系
    │                          │
    ▼                          ▼
  D08 蜂群契约串行      Task 06 消息系统
    │                   Task 08 记忆系统
    ▼
  V0.8 蜂群 Phase 1 ✓
```

| 顺序 | 任务 | 产出 | 依赖前置 |
| --- | --- | --- | --- |
| ⑤ | **Task 04** 蜂群设计定稿 | 契约/作用域/角色规范 | Task 03 ✅ |
| ⑥ | **Task 09** Agent 生命周期设计 | 模板管理、创建/销毁协议 | ⑤ |
| ⑦ | **Task 10** Agent 能力体系设计 | SkillRegistry、三层技能定义 | ⑤⑥ |
| ⑧ | **Task 06** 消息系统实现 | Agent 间异步信箱 | ⑤ |
| ⑨ | **Task 08** 记忆系统实现 | MEMORY.md + SQLite-VSS | ⑥ |
| ⑩ | **D08** 蜂群契约 + 串行编排 | `IContractManager`、`ScopedFileTool` | ⑤, D06 |

### 第三阶段：蜂群并行 + 智能增强 (V0.9)

> **目标：** 多 Worker 并行 + 上下文预热 + 插件化 + 桌面端可视化。

| 顺序 | 任务 | 产出 | 依赖前置 |
| --- | --- | --- | --- |
| ⑪ | **D09** 蜂群并行 + Worktree | Git Worktree 并行 | ⑩ |
| ⑫ | **D05** LSP 语义感知 | Roslyn 符号分析、`/map` | D01 ✅ |
| ⑬ | **Task 13** 上下文预热实现 | BackgroundIndexer、ContextManifest | ⑦, Task 12 |
| ⑭ | **Task 14** SKILL 插件化实现 | SkillLoader、PluginToolAdapter | ⑦, Task 11 |
| ⑮ | **D07** 桌面端 MVP 完成 | Editor + Swarm 双视图 | D04 ✅ |
| ⑯ | **Task 05** Swarm 视图实现 | 拓扑图、弹幕层 | ⑤, ⑮ |
| ⑰ | **Task 07** Agent 命名系统 | PuddingNameGenerator | ⑤ |

### 第四阶段：生态扩展 (V1.0+)

> **目标：** MCP 生态接入、多模型调度、分布式组网。

| 顺序 | 任务 | 产出 | 依赖前置 |
| --- | --- | --- | --- |
| ⑱ | **Task 15** MCP 服务器集成 | McpClient、McpHostService | ⑭, Task 11 |
| ⑲ | **Task 16** 服务商与模型管理 | ModelRegistry、UsageTracker | ⑬, Task 12 |
| ⑳ | **D10** P2P 分布式蜂群 | ISwarmTransport、Leader 选举 | ⑪ |
| ㉑ | **D11** 多模型 + 费用 + 可视化 | Model Router、费用仪表盘 | ⑳, ⑲ |

---

## 关键路径分析

**最短可用路径（单 Agent 安全可用）：**

```
D01 ✅ → D02 ✅ → D03 ✅ → D04 ✅ → D06 + Task 11 + Task 12 → V0.2
```

**蜂群最短路径（Leader-Worker 协作可用）：**

```
V0.2 → Task 04 → D08 → V0.8
```

**当前阻塞点：**

| 阻塞项 | 阻塞了什么 | 建议 |
| --- | --- | --- |
| **D06** (Git 快照) 未开始 | D08 蜂群契约需要快照/回滚能力 | 🔴 立即开始 |
| **Task 04** 设计未定稿 | D08 及整个 Agent 智能化链 (09→10→11→12) | 🔴 优先定稿 |
| **Task 11** 未实现 | ShellTool/FileTool 无安全防护，不敢放开使用 | 🔴 与 D06 并行 |

---

## 里程碑

| 版本 | 阶段 | 目标 | 关键交付 | 预期状态 |
| --- | --- | --- | --- | --- |
| **V0.1** | 一 | Core 闭环 | ITool + LLM Gateway + CLI REPL | ✅ 已完成 |
| **V0.2** | 一 | 安全闭环 | PermissionGuard + OutputDistiller + Git 快照 | 📋 **← 下一步** |
| **V0.3** | 二 | 自我调试 | 编译验证 + 错误回传 + 递归修复 | 📋 规划中 |
| **V0.5** | 三 | 语义引擎 | LSP 集成 + 符号级操作 + `/map` 指令 | 📋 规划中 |
| **V0.8** | 二 | 蜂群 Phase 1 | 契约驱动 + 作用域隔离 + Leader-Worker 串行 | 📋 规划中 |
| **V0.9** | 三 | 蜂群 Phase 2 | 并行 Worktree + 插件化 + 上下文预热 | 📋 规划中 |
| **V1.0** | 四 | 蜂群 Phase 3 | P2P 分布式组网 + MCP 生态 + Leader 选举 | 📋 规划中 |
| **V1.x** | 四 | 蜂群 Phase 4 | 多模型调度 + 费用控制 + Swarm 可视化 | 📋 规划中 |

---

## D07 Swarm 右键菜单开发记录

> **状态：** 🚧 UI 骨架已完成，后端逻辑待前置任务

### 已实现（UI + 模拟逻辑）

| 右键菜单项 | 功能 | 实现状态 |
| --- | --- | --- |
| 📋 Model Info | 查看 Agent 模型参数（model / role / status / tokens / 消息数 / 思维链步数） | ✅ 覆盖面板 |
| 💬 Chat with Agent | 独立聊天覆盖面板，支持发送消息 + 查看完整对话历史 | ✅ 覆盖面板 + 模拟回复 |
| 📝 Assign Task | 选中 Agent 并聚焦到右侧任务分配面板 | ✅ 联动 |
| 🧠 Thinking Chain | 查看 Agent 思维链（分步骤、带时间戳） | ✅ 覆盖面板 |
| 📜 Agent Log | 查看 Agent 运行日志 | ✅ 覆盖面板 |
| ▶ Resume | 恢复 Agent（从 Idle / Sleeping / Completed → Idle） | ✅ 状态变更 |
| ⏹ Stop | 停止 Agent 当前工作 | ✅ 状态变更 |
| 💤 Sleep | 休眠 Agent | ✅ 状态变更（新增 `Sleeping` 状态） |
| 🔄 Restart | 重启 Agent（清零 Token、清任务、模拟延迟恢复） | ✅ 模拟 |
| 🏗 Rebuild | 重建 Agent（清空所有状态 + 消息 + 思维链 + 日志） | ✅ 模拟 |
| 🗑 Destroy | 销毁 Agent 并从 Swarm 移除 | ✅ 已实现 |

### 待前置依赖的 TODO

| TODO 位置 | 所需前置 | 说明 |
| --- | --- | --- |
| `ContextRestart` | D08 蜂群编排器 | 需调用 `ISwarmOrchestrator.RestartAgentAsync` 真正重建 Agent 实例 |
| `ContextStop` | D08 蜂群编排器 | 需取消 Agent 当前执行的 `CancellationToken` |
| `ContextResume` | Task 09 生命周期 | 从休眠恢复需重新加载记忆快照 |
| `ContextSleep` | Task 09 生命周期 | 休眠需持久化对话状态和记忆到磁盘 |
| `ContextDestroy` | Task 09 生命周期 | 销毁需清理记忆文件、Git Worktree 等资源 |
| `ContextRebuild` | D08 蜂群编排器 | 重建 = 销毁 + 用同模板重新创建 |
| `SendOverlayChat` | D03/D08 | 需调用 `AgentOrchestrator.ProcessAsync` 将消息发给 Agent 的 LLM |
