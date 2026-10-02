# PuddingPlatform

> **2026-05-02**：Platform 不再是独立进程，而是 Pudding Agent 内的 Web UI + API 模块。

## 定位

- 内嵌 Web UI：管理界面（Workspace/Agent 配置）+ 对话界面
- REST API：供 Web UI 调用
- SQLite 数据库：持久化配置、会话、记忆

## 废弃

原 Platform 的独立 ASP.NET Core 服务 + PostgreSQL + MinIO + JWT 认证体系全部废弃。替换为单进程内 Kestrel + SQLite。