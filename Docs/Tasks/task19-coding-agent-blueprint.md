# Task 19 - Coding Agent 总体架构蓝图

状态：`design`  
优先级：`P0`  
最后更新：2026-02-20

## 1. 目标
设计一个与 OpenCode / Claude CLI 同级别的编码代理系统，要求：
- 工程可控：权限、安全、审计可落地。
- 交互高效：低摩擦命令流 + 快捷键驱动。
- 架构可扩展：支持 Hook、SKILL、MCP、LSP、多 Agent。

## 2. 当前现状（源码事实）
- 已有：
  - `AgentOrchestrator` 工具调用闭环。
  - `FileTool`/`ShellTool` + `PermissionGuard` + `DefaultDistiller`。
  - `SkillRegistry`（反射注册 + role 过滤）。
  - `GitSnapshotService`。
  - `SwarmOrchestrator` + `WorkerManager`（部分实现）。
- 缺口：
  - Prompt 硬编码在 `BuildSystemPrompt`。
  - 无 Hook 机制。
  - 无 MCP/LSP runtime 集成。
  - Swarm 有模拟执行分支。



## 3. 建议的模块抽象
```text
PuddingCodeCLI (UI Shell)
  -> InteractionController
  -> SessionRuntime

SessionRuntime (Application Layer)
  -> AgentKernel
  -> ContextBudgetManager
  -> HookBus
  -> Telemetry

AgentKernel (Domain Layer)
  -> PromptRegistry
  -> Planner (Leader)
  -> Executor (Worker/Spirit)
  -> ToolRouter (Tool/Skill/MCP/LSP)

Infrastructure Layer
  -> Tool implementations
  -> Skill loader/runtime
  -> MCP client(s)
  -> LSP index/query
  -> Git snapshot/worktree
```

## 4. Pudding 设计与生命周期
这里的 “一个 Pudding” 定义为“一个可执行会话实体（Session Agent Instance）”。

### 生命周期状态机
`Created -> Warmup -> Active -> Paused -> Completed -> Archived`

### 状态说明
- `Created`：会话上下文初始化，加载配置。
- `Warmup`：加载 prompt/skill/tool/memory 索引。
- `Active`：处理用户输入与工具调用。
- `Paused`：等待用户决策、权限确认、外部依赖恢复。
- `Completed`：任务完成并生成总结。
- `Archived`：会话压缩与归档，释放资源。

### 必须落盘的数据
- 会话元数据：id、模型、成本、token、开始/结束时间。
- 上下文摘要：目标、关键决策、未完成项。
- 操作日志：工具调用、结果、错误、权限事件。
- 关联资产：快照 id、worktree、产出文件索引。

## 5. Prompt 体系：硬编码 vs 外置
采用“外置模板为主 + 代码兜底”。


### 推荐目录
```text
.pudding/prompts/
  system.base.md
  role.leader.md
  role.worker.md
  role.spirit.md
  policy.tools.md
  policy.security.md
```

### 合并规则
`system.base` + `role.*` + `policy.*` + `project overrides`

当外置文件缺失时回退到内置默认模板。

## 6. Hook 设计（v1）
定义统一 Hook 总线：
- `pre_user_input`
- `pre_plan`
- `pre_tool_call`
- `post_tool_call`
- `pre_reply`
- `post_reply`
- `on_error`

FIX：我们还需要一些比如HOOK，比如执行完成，编程代理需要实现连续的对话，比如我输入执行任务，LLM执行需要连续多个，也就是循环。

FIX：我们会需要HOOK，从而循环这个过程，直到LLM告诉程序任务完成。


### Hook contract
- 输入：`session context` + `event payload`
- 输出：`allow/deny/modify` + `annotations`
- 行为：可同步或异步，支持超时保护和失败隔离。

## 7. SKILL / MCP / LSP 一体化策略
统一抽象成 `Capability Provider`：
- `ToolProvider`（本地工具）
- `SkillProvider`（本地/外部技能）
- `McpProvider`（远端工具协议）
- `LspProvider`（语义查询能力）

统一注册到 `ToolRouter`，由 Planner 决策调用。

## 8. Swarm 架构建议
- Leader 只做：拆解、分派、验收、合并策略。
- Worker 只做：作用域内实现与自测。
- 明确“禁止直改主工作区”，所有 Worker 默认 worktree 执行。
- 验收分三级：
  - 语法/编译通过
  - 目标测试通过
  - 合约检查通过

## 9. 下一步落地任务
1. 实现 `PromptRegistry`（支持 `.md` 模板加载与回退）。  
2. 实现 `HookBus` 与最小 4 个 hook 点。  
3. 抽象 `CapabilityProvider`，把现有 Tool/Skill 接入同一路由。  
4. Swarm 去模拟化：清理 TODO 分支，建立真实执行路径。  
