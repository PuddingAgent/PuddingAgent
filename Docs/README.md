# PuddingCode 文档索引

最后更新：2026-02-20

## 目标
- 打造一个对标 OpenCode / Claude CLI 的编码代理工具。
- 保持 CLI First，同时为后续 Desktop/多模态入口预留同一套核心引擎。
- 以“可持续演进”为第一优先：文档状态必须与源码现状一致。

## 先看这些
- `Docs/Tasks.md`：统一任务看板、当前状态、下一阶段里程碑。
- `Docs/Tasks/task19-coding-agent-blueprint.md`：编码代理总体架构蓝图（生命周期、Prompt、Hook、SKILL/MCP/LSP）。
- `Docs/Tasks/task20-cli-ui-ux.md`：CLI/TUI 交互设计方案（三区布局、快捷键、信息密度策略）。
- `Docs/Tasks/task21-subconscious-dual-llm.md`：潜意识/显意识双模型设计（记忆、召回、摘要、监督）。

## 与现有文档关系
- 历史文档保留在 `Docs/Tasks/`、`Docs/Archive/` 作为设计演进记录。
- 以 `task19`、`task20`、`task21` 为新的主线设计稿。
- 后续状态更新只认 `Docs/Tasks.md`，避免多份状态冲突。

## 当前实现快照（基于源码审阅）
- 已有：单会话 Agent REPL、Slash 命令、Git snapshot、基础 Swarm、Role/Scope、Skill Registry（静态注册）。
- 部分：Swarm（存在模拟执行与 TODO，尚非稳定并行生产形态）。
- 未实现：Hook 系统、Prompt 外置模板体系、MCP 客户端、LSP 语义索引、三栏 TUI、多 Agent 快捷切换。
