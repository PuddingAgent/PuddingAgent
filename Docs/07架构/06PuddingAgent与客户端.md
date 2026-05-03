# Agent 模板与客户端

> **2026-05-03**：客户端 = 内嵌 Web UI。Agent 模板分为全局模板和 Workspace 模板两级，旧版统一模板页面已移除。

## Agent 模板

Agent 模板分为两个层级，存储在本地 SQLite：

### 全局 Agent 模板（GlobalAgentTemplate）

定义系统内置 Agent 的角色、系统提示词、默认能力和偏好，对所有 Workspace 可见：

- 角色类型：Service / Task / Audit / Custom
- 系统提示词
- 首选模型与提供商
- 能力（Capability）引用
- Skill Package 引用
- 记忆策略与 Token 限制

前端页面：`/global-agent-template`

### Workspace Agent 模板（WorkspaceAgentTemplate）

定义特定 Workspace 内的 Agent 模板，覆盖或扩展全局模板：

- 继承全局模板的基础配置
- 可覆盖系统提示词、模型选择
- 绑定到特定 Workspace

前端页面：`/workspace-agent-template`

> **2026-05-03 变更**：旧版 `/agent-template` 统一模板页面已移除，其 API（`listAgentTemplates`、`getAgentTemplate`、`AgentTemplateType`、`AgentTemplateDefinition`）同步清理。详见 [QA-2026-05-03-RemoveOldAgentTemplate](../QA/QA-2026-05-03-RemoveOldAgentTemplate.md)。

## 内嵌 Web UI

- 前端使用 React/TypeScript 开发
- 构建产物嵌入 ASP.NET Core 的 wwwroot
- 一个进程同时提供 API 和 UI
- 用户双击启动后浏览器直接打开

## 客户端

不再有独立的 CLI/Web 客户端项目。Web UI 就是唯一的客户端。