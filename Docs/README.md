# Pudding Agent Network 文档索引

最后更新：2026-05-02

## 文档定位

这里是 Pudding Agent 的设计入口。当前主线为 V1 单进程模型：一个可执行文件，内嵌 Web UI + Controller + Runtime + SQLite，双击启动，浏览器自动打开即用。支持 LLM 多轮对话（带工具调用）及 P2P 节点发现与直连通信（mDNS + HTTP/gRPC）。仓库正在从旧分布式架构（Platform/Controller/Runtime 多服务）向单进程 V1 模型迁移。

## 建议阅读顺序

1. `Docs/架构.md`
	 - 架构总览、分层边界与阅读地图。
2. `Docs/07架构/README.md`
	 - 模块级架构分册入口，包含 Runtime、Controller、Platform、治理、数据模型与 V1 落地说明。
	 - 其中 `10事件系统与事件总线.md` 负责解释统一事件模型、订阅、唤醒、重放与死信策略。
	 - `11工作流与任务图.md` 负责解释工作流节点类型、触发方式、任务图表达与 Agent 生命周期。

3. `Docs/Tasks.md`
	 - 全局任务入口与 V1 目标，任务状态通过 Todo API 实时查询，不依赖硬编码表格。

## 当前主线文档

- `Docs/架构.md`
	- Pudding Agent Network 的架构总览与阅读入口。
- `Docs/07架构/README.md`
	- 按模块拆分后的架构分册目录。
- `Docs/Tasks.md`
	- 全局任务入口，任务状态通过 Todo API 管理。

## 主题文档分组

### 1. 渠道、网关与接入

- `Docs/06智能体网关/`
- `Docs/Config/hooks.md`
- `Docs/Config/pudding-yaml.md`

### 2. 智能体、运行时与协作

- `Docs/02智能体与智能体运行时/`
- `Docs/03多智能体/`
- `Docs/04工具与技能/`
- `Docs/07架构/`

### 3. 历史任务与设计演进记录

- `Docs/Tasks/task04-swarm.md` 到 `Docs/Tasks/task18-positioning.md`
- `Docs/Tasks/task19-coding-agent-blueprint.md`
- `Docs/Tasks/task20-cli-ui-ux.md`
- `Docs/Tasks/task21-subconscious-dual-llm.md`
- `Docs/Tasks/task22-agent-roles-orchestration.md`
- `Docs/Tasks/task23-central-lock-coordination.md`

这些文档仍然有价值，但需要放在新的 Platform / Runtime / Workspace 治理主线下理解，不能再单独代表产品总方向。

## 当前架构基线

- V1 目标：单进程 Pudding Agent，内嵌 Web UI + Controller + Runtime + SQLite
- 双击启动，浏览器自动打开即用
- 支持 LLM 多轮对话（带工具调用）
- 支持 P2P 节点发现与直连通信（mDNS + HTTP/gRPC）
- 支持裸进程运行或 Docker 单容器部署
- 任务管理已迁移至 Todo API（`python .github/skills/todo-api/todo_api.py`）

## 当前实现状态说明

- 仓库正在从旧分布式架构（Platform/Controller/Runtime 多服务 + PuddingCode CLI）向单进程 V1 模型迁移。
- 当前源码中仍保留较多旧架构残留，阅读时请以本文档和 `Docs/Tasks.md` 作为 V1 目标状态参考。
- 任务状态通过 Todo API 管理，不在文档或代码中硬编码。
