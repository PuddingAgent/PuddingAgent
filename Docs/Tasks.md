# Pudding Agent — 任务看板

最后更新：2026-05-03（Task-UI-02 全栈流式改造完成）

## 项目定位

Pudding Agent 是一个单进程、零外部依赖、支持 P2P 组网的 AI 代理程序。用户下载一个可执行文件，双击运行，浏览器自动打开即可使用。

## V1 目标

一个可运行的 Pudding Agent：
- 单进程，内嵌 Web UI + Controller + Runtime + SQLite
- 双击启动，浏览器打开即用
- 能进行 LLM 对话（多轮，带工具调用）
- 两个 Agent 进程可通过 P2P 互相发现与通信
- 支持裸进程运行或 Docker 单容器部署

## V1 任务管理

所有 V1 任务的详细状态、进度、依赖关系通过 Todo API 管理，不在本文档中硬编码。

查看任务：
```bash
python .github/skills/todo-api/todo_api.py kanban --group-by stage --project Pudding
python .github/skills/todo-api/todo_api.py list-tasks --project Pudding
```

### V1 能力分层任务一览

> 详见 [架构.md §Agent 能力分层模型](架构.md#agent-能力分层模型)

| 优先级 | 层级 | 任务卡 ID | 标题 |
|--------|------|-----------|------|
| **P0** | L0 基础 | task-20260502-008 | 实现多轮会话与工具调用框架 |
| **P0** | L0 基础 | task-20260502-007 | 实现 SQLite 数据持久化层 |
| **P0** | L0 基础 | task-20260502-012 | AI 服务商管理功能迁移 |
| **P0** | L1 记忆 | task-20260502-023 | 实现长期记忆引擎 |
| **P0** | L1 记忆 | task-20260503-039 | 会话持久化设计（JSONL + parent-UUID 链） |
| **P0** | L2 工具 | task-20260502-024 | 实现工具与Skill系统 |
| **P1** | L1 记忆 | task-20260503-040 | 上下文压缩设计（3层压缩 + Token 预算） |
| **P1** | — | task-20260502-005 | 实现 P2P 节点发现与直连通信 |
| **P1** | L3 子代理 | task-20260502-025 | 实现子代理与Orchestrator-Workers |
| **P1** | L4 连接器 | task-20260502-026 | 实现连接器框架与基础连接器（Webhook + CLI） |
| **P1.5** | L2 工具 | task-20260503-041 | Hook 系统设计（PreToolUse/PostToolUse） |
| **P2** | L5 自动化 | task-20260502-027 | 实现 Cron 定时任务调度 |
| **P2** | L5 工作流 | task-20260502-028 | 实现工作流管道（Prompt Chaining + Routing） |
| **P3** | 远期 | task-20260502-029 | 实现浏览器自动化（P3 远期） |

### UI 任务（PuddingPlatformAdmin）

| 优先级 | 任务卡 ID | 标题 | 状态 | QA |
|--------|-----------|------|------|-----|
| **P1** | task-ui-01-chat-core | Chat 核心体验 — 时间戳/重试/骨架屏/欢迎引导 | ✅ done | [PASS_WITH_NOTES](QA/QA-2026-05-03-ChatCoreUpgrade.md) |
| **P1** | task-ui-02-chat-interaction | Chat 交互增强 — 全栈 SSE 流式改造 + 消息操作 | ✅ done | [PASS_WITH_NOTES](QA/QA-2026-05-03-ChatStreamingUpgrade.md) |
| **P2** | task-ui-03-visual-system | 视觉系统 — 主题/响应式/动效/无障碍 | ✅ done | [PASS_WITH_NOTES](QA/QA-2026-05-03-VisualSystemUpgrade.md) |
| **P2** | task-ui-04-management-details | 管理详情页 — 仪表盘/场景/Agent 详情 | 🔲 ready | — |

## 技术选型

| 组件 | 技术 |
|---|---|
| 运行时 | .NET (ASP.NET Core, 单文件发布) |
| 数据库 | SQLite (EF Core) |
| 前端 | React + TypeScript, 内嵌 |
| LLM | 直接调用 OpenAI 兼容 API |
| P2P | mDNS + HTTP/gRPC |

## 不再需要的旧任务

以下旧任务因架构简化为单进程模型而废弃：

- ~~task24-platform-v1-first-slice~~ — 多服务分布式架构设计
- ~~task26-runtime-foundation~~ — 独立 Runtime 进程
- ~~task27-controller-routing-session~~ — 独立 Controller 进程
- ~~task28-platform-workspace-governance~~ — Platform 治理层
- ~~task29-agent-template-and-audit~~ — 审计 Agent 模板
- ~~task30-knowledge-infrastructure~~ — 知识库/图谱/存储
- ~~task31-client-surfaces~~ — 多客户端（CLI/Avalonia）
- ~~task32-observability-integration~~ — 多服务可观测性
- ~~task33-embedded-runtime-host~~ — 嵌入式 Runtime
- ~~task34-event-bus-and-subscription~~ — RabbitMQ 事件总线
- ~~task35-workspace-cockpit~~ — 协作驾驶舱
