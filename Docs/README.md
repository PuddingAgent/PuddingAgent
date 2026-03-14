# Pudding Agent Network 文档索引

最后更新：2026-03-14

## 文档定位

这里是 Pudding Agent Network 的设计入口，不再仅服务于旧的 PuddingCode CLI 原型。

当前主线已经切换为一个以 Workspace 为边界、以 Platform + Runtime 为核心、支持多渠道、多智能体、审批审计与记忆治理的 Agent 平台。

## 建议阅读顺序

1. `Docs/架构.md`
	 - 总体蓝图、分层边界、治理模型、租户与会话模型、渠道与插件化方向。
2. `Docs/Tasks.md`
	 - 当前任务看板、第一批平台任务、实现优先级与阶段目标。
3. `Docs/Tasks/task24-platform-v1-first-slice.md`
	 - Platform V1 首条垂直切片，已细化到类与 API 级别。

## 当前主线文档

- `Docs/架构.md`
	- Pudding Agent Network 的核心架构图纸。
- `Docs/Tasks.md`
	- 全局任务看板与当前阶段的实现路线。
- `Docs/Tasks/task24-platform-v1-first-slice.md`
	- 第一条真实链路：`CLI -> Platform API -> Workspace 路由 -> ServiceSession -> Runtime Agent -> 真实 LLM 回复`。

## 主题文档分组

### 1. 渠道、网关与接入

- `Docs/06智能体网关/`
- `Docs/Config/hooks.md`
- `Docs/Config/pudding-yaml.md`

### 2. 智能体、运行时与协作

- `Docs/02智能体与智能体运行时/`
- `Docs/03多智能体/`
- `Docs/04工具与技能/`

### 3. 历史任务与设计演进记录

- `Docs/Tasks/task04-swarm.md` 到 `Docs/Tasks/task18-positioning.md`
- `Docs/Tasks/task19-coding-agent-blueprint.md`
- `Docs/Tasks/task20-cli-ui-ux.md`
- `Docs/Tasks/task21-subconscious-dual-llm.md`
- `Docs/Tasks/task22-agent-roles-orchestration.md`
- `Docs/Tasks/task23-central-lock-coordination.md`

这些文档仍然有价值，但需要放在新的 Platform / Runtime / Workspace 治理主线下理解，不能再单独代表产品总方向。

## 当前架构基线

- 产品目标已从“编码代理 CLI”升级为“Agent 平台与网络”。
- `PuddingPlatform` 是控制面，负责渠道接入、路由、权限、审批、审计、工作流与治理。
- `PuddingRuntime` 是执行面，负责 Session、Agent、Memory、Skill、Sandbox 与运行态承载。
- 一个 Workspace 可绑定多个 Channel，并容纳多个 Agent。
- 平台内置支持 Email Channel，但渠道接入机制本身必须插件化。
- 公开或低信任输入默认不能直接污染长期记忆。

## 当前实现状态说明

- 当前仓库仍保留较多旧的 CLI / PuddingCode 原型实现。
- 文档设计已经先行推进到平台化形态。
- 因此阅读源码时，应优先以 `Docs/架构.md` 和 `Docs/Tasks.md` 作为目标状态参考，而不是把当前实现误认为最终分层。
