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
| **技术栈** | C# / .NET 10 · Spectre.Console · Semantic Kernel |
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
          D01 �?    Task 01
         (Core)    (CLI 设计)
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
   D05  D06
  (LSP) (Git)
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
| 04 | 蜂群模式设计 | ✏️ 设计�?| 🔴 P0 | Task 03 | [task04蜂群.md](tasks/task04蜂群.md) | 契约驱动开发、作用域隔离、P2P 分布式协作、自治选举 |
| 09 | Agent 生命周期 | ✏️ 设计�?| 🔴 P0 | Task 04 | [task09agent的生命周�?md](tasks/task09agent的生命周�?md) | 模板管理、异步创建、休眠唤醒、销毁回�?|
| 10 | Agent 能力体系 | ✏️ 设计�?| 🔴 P0 | Task 04, 09 | [task10agent能力.md](tasks/task10agent能力.md) | 三层技能架构、SkillRegistry、权限模型、自适应 Prompt |
| 11 | 权限与安全沙�?| ✏️ 设计�?| 🔴 P0 | Task 10, D02 | [task11权限.md](tasks/task11权限.md) | 路径沙盒、指令分级白名单、PermissionGuard、人工授�?UI |
| 12 | 感官过滤 | ✏️ 设计�?| 🔴 P0 | Task 10, 11, D02 | [task12感官过滤.md](tasks/task12感官过滤.md) | 输出蒸馏三层模型、断路器截断、异构模型压缩链、自适应规则 |
| 08 | 记忆系统 | ✏️ 设计�?| 🟠 P1 | Task 09 | [task08记忆系统.md](tasks/task08记忆系统.md) | Markdown 记忆、SQLite-VSS 检索、分层存�?|
| 06 | Agent 消息系统 | ✏️ 设计�?| 🟠 P1 | Task 04 | [task06agent消息.md](tasks/task06agent消息.md) | 气泡交互、私聊连线、消息总线 |
| 07 | Agent 命名系统 | ✏️ 设计�?| 🟡 P2 | Task 04 | [task07agent名字.md](tasks/task07agent名字.md) | 布丁风格昵称生成、身份标�?|
| 05 | Swarm 视图设计 | ✏️ 设计�?| 🟡 P2 | Task 04 | [task05Swarm视图.md](tasks/task05Swarm视图.md) | 拓扑图、弹幕思维层、指挥官模式、编排可视化、CLI 极客风格 |
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

> **目标�?* �?Worker 并行 + 上下文预�?+ 插件�?+ 可视化�?

| 顺序 | 任务 | 产出 | 依赖前置 |
| --- | --- | --- | --- |
| �?| **D09** 蜂群并行 + Worktree | Git Worktree 并行 | �?|
| �?| **D05** LSP 语义感知 | Roslyn 符号分析、`/map` | D01 �?|
| �?| **Task 13** 上下文预热实�?| BackgroundIndexer、ContextManifest | �? Task 12 |
| �?| **Task 14** SKILL 插件化实�?| SkillLoader、PluginToolAdapter | �? Task 11 |
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
| **V0.6** | 后端 | 核心后端 | LocalActionProvider + MemoryHub + Leader 系统指令 | 📋 规划�?|
| **V0.9** | 蜂群 | 蜂群编排 | 契约驱动 + 作用域隔�?+ Leader-Worker 串行/并行 | 📋 规划�?|
| **V1.0** | 插件 | 插件�?| IPuddingPlugin + 动态载�?+ 沙盒隔离 + Pro Mode | 📋 规划�?|
| **V1.x** | 生�?| 生态扩�?| MCP 生�?+ 多模型调�?+ P2P 分布�?+ 费用控制 | 📋 规划�?|

---

---

## 代码审阅报告（2026-02-20）

### 状态变更汇总

| 条目 | 原状态 | 新状态 | 依据 |
|------|--------|--------|------|
| Task 10 Agent 能力体系 | ✏️ 设计中 | 🚧 开发中 | 、、、 已实现 |
| Task 11 权限与安全沙箱 | ✏️ 设计中 | ✅ 已完成 |  路径沙盒 + 指令白名单已实现并集成到 / |
| Task 12 感官过滤 | ✏️ 设计中 | ✅ 已完成 |  三层蒸馏（截断/压缩/摘要）已实现并集成到  |
| D08 蜂群契约 + 串行编排 | 📋 规划中 | 🚧 开发中 | 、、、、、 已实现 |
| D09 蜂群并行 + Worktree | 📋 规划中 | 🚧 开发中 |  多 Worker 并发框架已存在，Git Worktree 隔离尚未完成 |

---

### 各模块实现质量审阅

#### ✅ D01 Core 抽象层 — 完成度高
- 所有核心接口均已定义：、、、、、、、、、、
- **改进建议**： 接口存在但无任何实现类，D10 依赖此接口，需在 D09 完成后补充 （单机 fallback）

#### ✅ D02 本地工具 — 完成度高
-  支持 Read/Write/Edit/Glob/Grep， 支持超时 + 工作目录
- **改进建议 1**： 缺少对危险命令（、）的二次确认机制， 白名单是静态的，建议支持运行时动态授权
- **改进建议 2**： 的  错误直接抛异常，建议返回结构化错误让 LLM 自动重试

#### ✅ D03 LLM 网关 + Agent 编排 — 完成度高
-  支持流式/非流式、DeepSeek 、Tool Calling
-  实现 Channel-based 事件流、Tool 闭环、角色过滤
- **改进建议 1**：Tool Calling 循环无最大迭代次数限制，存在无限循环风险，建议加 （默认 20）
- **改进建议 2**： 无  传播到 LLM 调用层， 指令无法中断正在进行的 LLM 请求
- **改进建议 3**：异常处理粒度粗，LLM 网络超时与 Tool 执行错误混用同一 catch，建议分层处理

#### ✅ D04 CLI REPL — 完成度高
- Spectre.Console 流式渲染、完整命令路由、、、
- **改进建议**： 仅显示快照列表，缺少  功能（查看两个快照之间的文件变更）

#### ✅ D06 Git 快照 — 完成度高
-  完整实现自动快照、命名快照、回滚
- **改进建议**： 使用  会污染工作区，建议改用 Saved working directory and index state WIP on master: c5e90d4 提交 + Your branch is ahead of 'origin/master' by 19 commits.
  (use "git push" to publish your local commits) 组合，或在回滚前自动创建保护快照

#### 🚧 D08 蜂群契约 + 串行编排 — 框架已建，核心逻辑待完善
-  契约 CRUD 已实现， 串行编排框架存在
-  路径作用域隔离已实现
- **改进建议 1**： 验证逻辑过于简单（仅检查字段非空），缺少对  越界、 合理性的语义验证
- **改进建议 2**： 基于文件系统的消息传递无锁机制，多 Worker 并发写入同一目录存在竞态条件，建议加文件锁或改用 Channel
- **改进建议 3**： 的 Worker 生命周期管理缺少健康检查和自动重启，Worker 崩溃后 Swarm 无感知

#### 🚧 D09 蜂群并行 + Worktree — 框架存在，Worktree 隔离缺失
-  多 Worker 并发框架已存在
- **改进建议 1**：Git Worktree 创建/销毁逻辑完全缺失，当前多 Worker 共享同一工作目录，并行写文件会产生冲突
- **改进建议 2**：缺少自动合并策略，Worker 完成后如何将各自 Worktree 的变更合并回主分支未实现
- **改进建议 3**：缺少 Worker 间任务分配算法，当前为轮询，建议支持基于文件作用域的亲和性调度

#### 🚧 Task 10 Agent 能力体系 — 基础已建，高级特性待实现
- 、、 已实现
- **改进建议 1**： 仅支持静态注册（ 特性），缺少运行时动态加载（从文件/URL 加载 Skill）
- **改进建议 2**： 的  为纯字符串，缺少模板变量支持（如 、）
- **改进建议 3**：缺少 Skill 版本管理和冲突检测，同名 Skill 注册时行为未定义

#### ✅ Task 11 权限与安全沙箱 — 已完成，建议加固
- **改进建议 1**： 路径检查使用字符串前缀匹配，存在路径穿越漏洞（ 未规范化），建议使用  规范化后再比较
- **改进建议 2**：Shell 命令白名单为硬编码列表，无法针对不同 Agent 角色配置不同权限级别

#### ✅ Task 12 感官过滤 — 已完成，建议优化
- **改进建议 1**： 的截断阈值（token 数）为硬编码常量，建议通过配置注入
- **改进建议 2**：压缩策略仅有截断，缺少基于重要性的选择性保留（如保留最近 N 轮 + 摘要历史）
