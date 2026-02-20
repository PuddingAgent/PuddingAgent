# PuddingCode 任务索引

> 本文档跟�?PuddingCode 项目所有设计与开发任务的状态�?
>
> **状态说明：** 📋 规划�?· ✏️ 设计�?· 🚧 开发中 · �?已完�?· ⏸️ 暂停
>
> **优先级说明：** 🔴 P0 关键路径 · 🟠 P1 重要 · 🟡 P2 增强 · 🟢 P3 远期

---

## 项目概览

| �?| 内容 |
| --- | --- |
| **项目名称** | PuddingCode �?数字化布丁管�?|
| **定位** | 面向大众用户的智能助手，编程能力（PuddingCode）作�?Pro Mode 插件保留 |
| **技术栈** | C# / .NET 10 · Avalonia UI · Spectre.Console · Semantic Kernel · SkiaSharp |
| **代码仓库** | [github.com/hyfree/PuddingCode](https://github.com/hyfree/PuddingCode) |
| **分支** | `master` |
| **架构文档** | [Docs/Map.md](Map.md) |
| **产品设计** | [task18定位.md](Tasks/task18定位.md) �?双态架�?+ Swarm 编排 + 记忆系统 + 插件�?+ 视觉设计 |
| **行业分析** | [Docs/讨论.md](讨论.md) �?行业瓶颈分析 + 6 层架�?+ 5 个突破方向（历史参考） |

---

## 依赖拓扑�?

```
                     Task 03 (V0.1 方案) �?
                       �?
            ┌──────────┼──────────�?
            �?         �?         �?
          D01 �?    Task 01    Task 02
         (Core)    (CLI 设计)  (桌面设计)
        ┌──┴──�?
        �?    �?
      D02 �? D03 �?
     (工具)  (LLM 网关)
        └──┬──�?
           �?
         D04 �?──────────────────────────────────────────────�?
        (REPL)                                                �?
     ┌────┼────────�?                                         �?
     �?   �?       �?                                         �?
   D05  D06      D07 🚧 ◄── Task 01, 02                      �?
  (LSP) (Git)   (桌面MVP)                                     �?
     �?   �?       �?                                         �?
     �?   �?       ├──── Task 05 (Swarm 视图) ◄── Task 04     �?
     �?   �?       └──── Task 06 (消息系统)                    �?
     �?   �?             Task 07 (命名系统)                    �?
     �?   �?                                                  �?
     �?   └────�?                                             �?
     �?        �?                                             �?
     �?      D08 ◄── Task 04 (蜂群模式)                       �?
     �?     (契约串行编排)                                     �?
     �?        �?                                             �?
     �?        �?                                             �?
     �?      D09                                              �?
     �?     (并行 Worktree)                                   �?
     �?        �?                                             �?
     �?        �?                                             �?
     �?      D10                                              �?
     �?     (P2P 分布�?                                      �?
     �?        �?                                             �?
     �?        �?                                             �?
     �?      D11 ◄── Task 16 (服务商管�?                      �?
     �?     (多模�?费用+可视�?                                �?
     �?                                                       �?
     �?                                                       �?
     �? ◄── 独立演进�?Agent 智能�?�?──�?                    �?
     �?                                                       �?
     �?   Task 04 (蜂群) ◄── Task 03                          �?
     �?      �?                                               �?
     �?      �?                                               �?
     �?   Task 09 (生命周期) ── Task 08 (记忆)                 �?
     �?      �?                                               �?
     �?      �?                                               �?
     �?   Task 10 (能力体系)                                   �?
     �?   ┌──┼──────────────────�?                             �?
     �?   �? �?                 �?                             �?
     �? T11  T12 ──�?T13     Task 16                          �?
     �?(权限)(过滤)  (预热)  (服务�?                           �?
     �?   �?   �?      �?                                      �?
     �?   �?   �?      �?                                      �?
     �? T14 ◄─�?   (预热)                                     �?
     �?(插件�?                                                �?
     �?   �?                                                   �?
     �?   �?                                                   �?
     �? T15                                                    �?
     �?(MCP)                                                   �?
     �?                                                        �?
```

---

## 任务列表

### 📐 设计任务

| # | 任务 | 状�?| 优先�?| 依赖 | 文档 | 说明 |
| --- | --- | --- | --- | --- | --- | --- |
| 03 | V0.1 实现方案 | �?已完�?| 🔴 P0 | �?| [task03开�?md](tasks/task03开�?md) | Core 抽象层、本地工具、LLM 网关、CLI REPL 闭环 |
| 01 | CLI 交互界面设计 | ✏️ 设计�?| 🟠 P1 | Task 03 | [task01交互界面.md](tasks/task01交互界面.md) | 视觉语言、布局、斜杠指令、Diff 预览、架构支撑层 |
| 02 | 桌面端设�?| ✏️ 设计�?| 🟠 P1 | Task 03 | [task02桌面端设�?md](tasks/task02桌面端设�?md) | 共享内核架构、三段式布局、UI 交互协议、桌面特有功�?|
| 04 | 蜂群模式设计 | ✏️ 设计�?| 🔴 P0 | Task 03 | [task04蜂群.md](tasks/task04蜂群.md) | 契约驱动开发、作用域隔离、P2P 分布式协作、自治选举 |
| 09 | Agent 生命周期 | ✏️ 设计�?| 🔴 P0 | Task 04 | [task09agent的生命周�?md](tasks/task09agent的生命周�?md) | 模板管理、异步创建、休眠唤醒、销毁回�?|
| 10 | Agent 能力体系 | ✏️ 设计�?| 🔴 P0 | Task 04, 09 | [task10agent能力.md](tasks/task10agent能力.md) | 三层技能架构、SkillRegistry、权限模型、自适应 Prompt |
| 11 | 权限与安全沙�?| ✏️ 设计�?| 🔴 P0 | Task 10, D02 | [task11权限.md](tasks/task11权限.md) | 路径沙盒、指令分级白名单、PermissionGuard、人工授�?UI |
| 12 | 感官过滤 | ✏️ 设计�?| 🔴 P0 | Task 10, 11, D02 | [task12感官过滤.md](tasks/task12感官过滤.md) | 输出蒸馏三层模型、断路器截断、异构模型压缩链、自适应规则 |
| 08 | 记忆系统 | ✏️ 设计�?| 🟠 P1 | Task 09 | [task08记忆系统.md](tasks/task08记忆系统.md) | Markdown 记忆、SQLite-VSS 检索、分层存�?|
| 06 | Agent 消息系统 | ✏️ 设计�?| 🟠 P1 | Task 04 | [task06agent消息.md](tasks/task06agent消息.md) | 气泡交互、私聊连线、消息总线 |
| 07 | Agent 命名系统 | ✏️ 设计�?| 🟡 P2 | Task 04 | [task07agent名字.md](tasks/task07agent名字.md) | 布丁风格昵称生成、身份标�?|
| 05 | Swarm 视图设计 | ✏️ 设计�?| 🟡 P2 | Task 04, D07 | [task05Swarm视图.md](tasks/task05Swarm视图.md) | 拓扑图、弹幕思维层、指挥官模式、编排可视化、CLI 极客风格 |
| 13 | 上下文预�?| ✏️ 设计�?| 🟡 P2 | Task 10, 12, D03 | [task13上下文预�?md](tasks/task13上下文预�?md) | 三级预热、并发启动、后台索引引擎、认知包、功耗控�?|
| 14 | SKILL 插件�?| ✏️ 设计�?| 🟡 P2 | Task 10, 11, 13 | [task14SKILL插件�?md](tasks/task14SKILL插件�?md) | 插件三层接口、动�?DLL 加载、探活机制、分级调用模�?|
| 16 | 服务商与模型管理 | ✏️ 设计�?| 🟡 P2 | Task 12, 13, D03 | [task16服务商界�?md](tasks/task16服务商界�?md) | 模型元数据、能力标签、成本预算熔断、抽样评分、动态路�?|
| 15 | MCP 服务器集�?| ✏️ 设计�?| 🟢 P3 | Task 11, 14 | [task15MCP服务�?md](tasks/task15MCP服务�?md) | JSON-RPC 2.0 对接、工具自动发现、权限桥接、懒启动 |
| 17 | Leader 动态路�?| ✏️ 设计�?| 🟠 P1 | Task 04, 09, 10, D08 | [task17Leader的动态路�?md](tasks/task17Leader的动态路�?md) | Plan-then-Execute、智能路由、能力匹配、熔断机制、结果汇�?|
| 18 | 产品定位与设�?| �?已完�?| 🔴 P0 | �?| [task18定位.md](Tasks/task18定位.md) | 双态架构、Swarm 编排、记忆系统、插件化、布丁美学、MVP 路径 |

### 🔨 开发任�?

| # | 任务 | 状�?| 优先�?| 依赖 | 说明 |
| --- | --- | --- | --- | --- | --- |
| D01 | Core 抽象层（接口 + 模型�?| �?已完�?| 🔴 P0 | Task 03 | `ITool`、`IToolRegistry`、`ILlmGateway`、`IAgentOrchestrator` |
| D02 | 本地工具实现 | �?已完�?| 🔴 P0 | D01 | `FileTool`（CliWrap）、`ShellTool` |
| D03 | LLM 网关 + Agent 编排�?| �?已完�?| 🔴 P0 | D01 | `OpenAiLlmGateway`、`AgentOrchestrator`、Tool Calling 闭环 |
| D04 | CLI REPL 原型 | �?已完�?| 🔴 P0 | D02, D03 | Spectre.Console REPL、事件流渲染 |
| D05 | LSP 语义感知集成 | 📋 规划�?| 🟠 P1 | D01 | Roslyn LSP Client、`/map` 指令 |
| D06 | Git 快照与回�?| �?已完�?| 🔴 P0 | D01 | `IGitSnapshot`、`GitSnapshotService`、`/undo` `/snapshot` `/history` 指令、AgentOrchestrator 自动快照 |
| D07 | 桌面�?MVP（Avalonia�?| 🚧 开发中 | 🟠 P1 | D04, Task 01, 02 | 双视�?Editor/Swarm、流�?Chat、思维链、Swarm 拓扑图、Agent 右键菜单 |
| D08 | 蜂群契约 + 串行编排 | 📋 规划�?| 🔴 P0 | D04, D06, Task 04 | `IContractManager`、`ScopedFileTool`、`ISwarmOrchestrator`、作用域隔离 |
| D09 | 蜂群并行 + Worktree | 📋 规划�?| 🟠 P1 | D08 | Git Worktree 管理、Leader-Worker 并行、自动合�?|
| D10 | P2P 分布式蜂�?| 📋 规划�?| 🟢 P3 | D09 | `ISwarmTransport`(P2P)、`ILeaderElection`、NAT 穿越、选举 |
| D11 | 蜂群多模�?+ 费用 + 可视�?| 📋 规划�?| 🟢 P3 | D10, Task 16 | Model Router、Token 预算、费用仪表盘、Swarm 可视�?|

---

## 实现顺序

### 第一阶段：核心安全闭�?(V0.2) 🔴 关键路径

> **目标�?* 在已完成�?V0.1 基础上，补全安全防护和输出过滤，让单 Agent 可安全地执行工具操作�?
>
> **已有基础�?* `ITool` / `IToolRegistry` / `ShellTool` / `FileTool` / `OpenAiLlmGateway` / `AgentOrchestrator` / CLI REPL

```
D06 Git 快照 ──────────────────────�?
                                   �? (并行)
Task 11 �?PermissionGuard 实现 ───�?
                                   �? (并行)
Task 12 �?OutputDistiller 实现 ───�?
                                   �?
                                   �?
              集成�?ShellTool / FileTool
                                   �?
                                   �?
                          V0.2 安全闭环 �?
```

| 顺序 | 任务 | 产出 | 依赖前置 |
| --- | --- | --- | --- |
| �?| **D06** Git 快照与回�?| `/undo` 指令 | D01 �?| �?已完�?|
| �?| **Task 11** �?实现 `PermissionGuard` | 路径沙盒 + 命令白名�?| D02 �? Task 10 设计 | �?已完�?|
| �?| **Task 12** �?实现 `DefaultDistiller` | 输出截断 + 错误增强 | D02 �?| �?已完�?|
| �?| 集成�?`ShellTool` / `FileTool` | 工具操作前权限校�?+ 操作后输出过�?| ①②�?| �?已完�?|

### 第二阶段：蜂群串�?(V0.3-0.8)

> **目标�?* 实现 Leader-Worker 串行协作、契约驱动、作用域隔离�?

```
Task 04 设计完成
    �?
    �?
Task 09 Agent 生命周期 ─�?Task 10 能力体系
    �?                         �?
    �?                         �?
  D08 蜂群契约串行      Task 06 消息系统
    �?                  Task 08 记忆系统
    �?
  V0.8 蜂群 Phase 1 �?
```

| 顺序 | 任务 | 产出 | 依赖前置 |
| --- | --- | --- | --- |
| �?| **Task 04** 蜂群设计定稿 | 契约/作用�?角色规范 | Task 03 �?|
| �?| **Task 09** Agent 生命周期设计 | 模板管理、创�?销毁协�?| �?|
| �?| **Task 10** Agent 能力体系设计 | SkillRegistry、三层技能定�?| ⑤⑥ |
| �?| **Task 06** 消息系统实现 | Agent 间异步信�?| �?|
| �?| **Task 08** 记忆系统实现 | MEMORY.md + SQLite-VSS | �?|
| �?| **D08** 蜂群契约 + 串行编排 | `IContractManager`、`ScopedFileTool` | �? D06 |

### 第三阶段：蜂群并�?+ 智能增强 (V0.9)

> **目标�?* �?Worker 并行 + 上下文预�?+ 插件�?+ 桌面端可视化�?

| 顺序 | 任务 | 产出 | 依赖前置 |
| --- | --- | --- | --- |
| �?| **D09** 蜂群并行 + Worktree | Git Worktree 并行 | �?|
| �?| **D05** LSP 语义感知 | Roslyn 符号分析、`/map` | D01 �?|
| �?| **Task 13** 上下文预热实�?| BackgroundIndexer、ContextManifest | �? Task 12 |
| �?| **Task 14** SKILL 插件化实�?| SkillLoader、PluginToolAdapter | �? Task 11 |
| �?| **D07** 桌面�?MVP 完成 | Editor + Swarm 双视�?| D04 �?|
| �?| **Task 05** Swarm 视图实现 | 拓扑图、弹幕层 | �? �?|
| �?| **Task 07** Agent 命名系统 | PuddingNameGenerator | �?|

### 第四阶段：生态扩�?(V1.0+)

> **目标�?* MCP 生态接入、多模型调度、分布式组网�?

| 顺序 | 任务 | 产出 | 依赖前置 |
| --- | --- | --- | --- |
| �?| **Task 15** MCP 服务器集�?| McpClient、McpHostService | �? Task 11 |
| �?| **Task 16** 服务商与模型管理 | ModelRegistry、UsageTracker | �? Task 12 |
| �?| **D10** P2P 分布式蜂�?| ISwarmTransport、Leader 选举 | �?|
| �?| **D11** 多模�?+ 费用 + 可视�?| Model Router、费用仪表盘 | �? �?|

---

## 关键路径分析

**最短可用路径（�?Agent 安全可用）：**

```
D01 �?�?D02 �?�?D03 �?�?D04 �?�?D06 + Task 11 + Task 12 �?V0.2
```

**蜂群最短路径（Leader-Worker 协作可用）：**

```
V0.2 �?Task 04 �?D08 �?V0.8
```

**当前阻塞点：**

| 阻塞�?| 阻塞了什�?| 建议 |
| --- | --- | --- |
| **D06** (Git 快照) �?已完�?| D08 蜂群契约需要快�?回滚能力 | �?已解�?|
| **Task 04** 设计未定�?| D08 及整�?Agent 智能化链 (09�?0�?1�?2) | 🔴 优先定稿 |
| **Task 11** �?已实�?| ShellTool/FileTool 已集�?PermissionGuard | �?已解�?|

---

## 里程�?

| 版本 | 阶段 | 目标 | 关键交付 | 预期状�?|
| --- | --- | --- | --- | --- |
| **V0.1** | 基础 | Core 闭环 | ITool + LLM Gateway + CLI REPL | �?已完�?|
| **V0.2** | 基础 | 安全闭环 | PermissionGuard + OutputDistiller + Git 快照 | �?**已完�?* |
| **V0.3** | 精灵 | 透明精灵�?| Avalonia 异形窗口 + 静态布�?+ 鼠标拖动 | 📋 规划�?|
| **V0.4** | 精灵 | 动作与状态机 | Lottie 动画 + 5 种状态切换（Idle/Thinking/Success/Error/Sleeping�?| 📋 规划�?|
| **V0.5** | 交互 | 意图与气�?| Chat Bubble + 种子插件（意图输入框�? 环形菜单 | 📋 规划�?|
| **V0.6** | 后端 | 核心后端 | LocalActionProvider + MemoryHub + Leader 系统指令 | 📋 规划�?|
| **V0.8** | 窗口 | 管理窗口 | Swarm 视图 + Editor + 成果�?+ 记忆抽屉 | 📋 规划�?|
| **V0.9** | 蜂群 | 蜂群编排 | 契约驱动 + 作用域隔�?+ Leader-Worker 串行/并行 | 📋 规划�?|
| **V1.0** | 插件 | 插件�?| IPuddingPlugin + 动态载�?+ 沙盒隔离 + Pro Mode | 📋 规划�?|
| **V1.x** | 生�?| 生态扩�?| MCP 生�?+ 多模型调�?+ P2P 分布�?+ 费用控制 | 📋 规划�?|

---

## D07 Swarm 右键菜单开发记�?

> **状态：** 🚧 UI 骨架已完成，后端逻辑待前置任�?

### 已实现（UI + 模拟逻辑�?

| 右键菜单�?| 功能 | 实现状�?|
| --- | --- | --- |
| 📋 Model Info | 查看 Agent 模型参数（model / role / status / tokens / 消息�?/ 思维链步数） | �?覆盖面板 |
| 💬 Chat with Agent | 独立聊天覆盖面板，支持发送消�?+ 查看完整对话历史 | �?覆盖面板 + 模拟回复 |
| 📝 Assign Task | 选中 Agent 并聚焦到右侧任务分配面板 | �?联动 |
| 🧠 Thinking Chain | 查看 Agent 思维链（分步骤、带时间戳） | �?覆盖面板 |
| 📜 Agent Log | 查看 Agent 运行日志 | �?覆盖面板 |
| �?Resume | 恢复 Agent（从 Idle / Sleeping / Completed �?Idle�?| �?状态变�?|
| �?Stop | 停止 Agent 当前工作 | �?状态变�?|
| 💤 Sleep | 休眠 Agent | �?状态变更（新增 `Sleeping` 状态） |
| 🔄 Restart | 重启 Agent（清�?Token、清任务、模拟延迟恢复） | �?模拟 |
| 🏗 Rebuild | 重建 Agent（清空所有状�?+ 消息 + 思维�?+ 日志�?| �?模拟 |
| 🗑 Destroy | 销�?Agent 并从 Swarm 移除 | �?已实�?|

### 待前置依赖的 TODO

| TODO 位置 | 所需前置 | 说明 |
| --- | --- | --- |
| `ContextRestart` | D08 蜂群编排�?| 需调用 `ISwarmOrchestrator.RestartAgentAsync` 真正重建 Agent 实例 |
| `ContextStop` | D08 蜂群编排�?| 需取消 Agent 当前执行�?`CancellationToken` |
| `ContextResume` | Task 09 生命周期 | 从休眠恢复需重新加载记忆快照 |
| `ContextSleep` | Task 09 生命周期 | 休眠需持久化对话状态和记忆到磁�?|
| `ContextDestroy` | Task 09 生命周期 | 销毁需清理记忆文件、Git Worktree 等资�?|
| `ContextRebuild` | D08 蜂群编排�?| 重建 = 销�?+ 用同模板重新创建 |
| `SendOverlayChat` | D03/D08 | 需调用 `AgentOrchestrator.ProcessAsync` 将消息发�?Agent �?LLM |
