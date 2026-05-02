# Agent 模板与客户端

> **2026-05-02**：客户端 = 内嵌 Web UI。Agent 模板定义 Agent 的角色和能力。

## Agent 模板

AgentTemplate 定义 Agent 的角色、系统提示词、默认能力和偏好：

- 角色类型：Service / Task / Custom
- 系统提示词
- 首选模型
- Skill/MCP 引用
- 记忆策略

模板存储在本地 SQLite。

## 内嵌 Web UI

- 前端使用 React/TypeScript 开发
- 构建产物嵌入 ASP.NET Core 的 wwwroot
- 一个进程同时提供 API 和 UI
- 用户双击启动后浏览器直接打开

## 客户端

不再有独立的 CLI/Avalonia/Web 客户端项目。Web UI 就是唯一的客户端。