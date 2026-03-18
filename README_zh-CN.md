# Pudding Agent Network

**一个以 Workspace 为治理边界、以事件驱动为骨架的 Agent OS 与受控协作平台。**

[English README](README.md)

![Version](https://img.shields.io/badge/version-v0.1.0-blue)
![Platform](https://img.shields.io/badge/platform-windows%20%7C%20linux%20%7C%20macos-lightgrey)
![License](https://img.shields.io/badge/license-Apache%202.0-green)

---

## 这是什么？

Pudding Agent Network 是当前仓库的产品与平台演进方向。

它不是一个单纯的 coding CLI，也不只是一个单 Agent 框架。它正在被设计成一个 **Agent OS** 与协作网络平台：在统一的控制与执行模型之下，负责多 Agent 的接入、路由、执行、审计、审批、治理与协作。

一句话概括：

> **Pudding 是一个以 Workspace 为中心、以事件驱动为基础的多智能体操作系统。**

---

## 为什么要做这个系统？

Pudding 面向的是一些普通助手式系统不太擅长解决的工程问题：

- **真实工作更像工作流，而不只是聊天。** Pudding 把 `Workflow` 与 `TaskMap` 设计成一等执行结构，而不是后期拼装的编排层。
- **多任务并行会造成上下文串味。** Pudding 用 `Workspace` 作为 Agent、Memory、Tool、Event、Workflow 的统一治理边界。
- **轮询不是好的协作方式。** Pudding 通过统一事件总线，让 Agent 被事件唤醒，而不是不断询问“有没有新任务”。
- **企业接入场景复杂。** 渠道与协议接入应当是插件化、可治理的，而不是把所有输入都绑死在单个客户端上。
- **治理能力不能事后补丁。** 审批、审计、记忆边界、冻结控制与监督能力，必须从架构第一天就成为主模型的一部分。

---

## 核心概念速览

- **Workspace** — Memory、Tool、Event、Workflow、Channel 与 Agent 配置的主要治理边界。
- **Controller** — 路由、鉴权、审批、审计、策略与治理的控制权威。
- **Runtime** — 会话热状态、Agent 实例、记忆、工具与沙箱执行的执行权威。
- **Event Bus** — 接入、唤醒、工作流推进、协作反馈与系统联动的主骨架。
- **Workflow / TaskMap** — 多 Agent 协作与受控执行的一等结构。
- **Governance** — 审批、审计 Agent、冻结控制、权限边界与监督能力都属于平台原生能力。

---

## 架构快照

```text
PuddingPlatform
	├─ PuddingController
	│   └─ PuddingGateway
	├─ PuddingRuntime
	├─ PuddingAgent
	└─ PuddingCLI / PuddingWeb / PuddingAvalonia / External Channels
```

当前架构基线：

- **Workspace 是主要治理边界**
- **Runtime 持有会话热状态权威**
- **Controller 持有路由、策略、审批、审计与治理权威**
- **Platform 承载上层产品语义与业务表面**
- **AgentTemplate 负责能力与策略声明，但不持有执行热状态**
- **Event Bus 是接入、唤醒、工作流推进与协作的主骨架**
- **Workflow / TaskMap 是一等执行模型**
- **Channels 应插件化，而不是写死分支逻辑**
- **公开或低信任输入默认不能直接污染长期记忆**
- **知识库、统一存储和图谱能力归属于 Workspace，并由 Controller 受控暴露**
- **审批属于系统控制链路，而不是业务 Agent 的自主决定**
- **每个 Workspace 至少需要一个审计 Agent**

---

## 当前状态

当前仓库正从更早期的 **PuddingCode** 编码助手原型，向更完整的 **Pudding Agent Network** 平台演进。

也就是说：架构主线已经明显走在部分实现前面。

| 领域 | 状态 | 说明 |
|---|---|---|
| 架构文档 | 活跃 | 当前最可靠的方向来源 |
| CLI 纵向切片 | 部分可用 | 仍然存在可运行入口，适合持续验证 |
| Controller / Runtime 分层 | 迁移中 | 责任边界已经清晰，实现正在追赶 |
| Workflow / 事件驱动模型 | 已设计 | 架构成立，实现仍在逐步补齐 |
| 平台治理与管理表面 | 规划中 + 部分展开 | 产品与管理面方向已经明确 |

因此，阅读这个仓库时，建议把它理解成：

> 一个正从 coding-agent 原型演进为 Agent OS 与协作网络平台的系统。

---

## V1 重点

第一条真正的落地切片目标是：

```text
CLI / Avalonia -> Controller API -> Workspace routing -> ServiceSession -> Runtime Agent -> real LLM reply
```

当前 V1 的约束与优先级包括：

- Email 作为内建核心渠道之一
- 一个 Workspace 可以绑定多个渠道
- 每个 ChannelBinding 可以声明默认 Agent 与允许 Agent 集合
- 渠道接入本身必须是插件化的
- 存储可以先从本地文件与 SQLite 起步，再向更大规模演进
- Workspace 的知识库、统一存储与知识图谱要通过 Controller 所拥有的服务暴露
- 语音审批属于系统控制能力，而不是业务 Agent 能力
- 每个 Workspace 应包含至少一个审计 Agent

---

## 快速开始

### 构建解决方案

```bash
dotnet build PuddingAgentNetwork.slnx
```

### 运行当前 CLI 入口

```bash
dotnet run --project Source/PuddingCLI
```

### 先读架构文档

平台正在围绕新的架构主线重塑，因此最准确的意图和近期范围，仍然以文档集为准：

- 从 `Docs/架构.md` 开始，先建立整体阅读地图
- 再读 `Docs/07架构/README.md`，进入模块级架构分册
- 查看 `Docs/Tasks.md`，理解当前路线与任务拆分

---

## 仓库导航

### 核心系统模块

- `Source/PuddingCore` — 共享抽象、协议与公共模型
- `Source/PuddingRuntime` — 执行面宿主
- `Source/PuddingController` — 控制面宿主（建设中）
- `Source/PuddingPlatform` — 平台层与治理宿主
- `Source/PuddingAgent` — Agent 模板与能力画像
- `Source/PuddingMemoryEngine` — Runtime 记忆子系统

### 客户端与操作表面

- `Source/PuddingCLI` — 通过 Controller / Gateway 接口工作的 CLI 表面
- `Source/PuddingWeb` — Web 前端
- `Source/PuddingAvalonia` — 规划中的桌面端操作 / 用户控制界面
- `Source/PuddingGateway` — 接入与协议适配边界模块

### 过渡与历史模块

- `Source/PuddingCode` — 更早期的 coding-agent 实现，正在被更大的平台方向吸收
- `Source/PuddingCodeCLI` — 迁移阶段保留的旧 CLI 入口

### 文档与规划

- `Docs/架构.md` — 总体架构总览与阅读地图
- `Docs/07架构/` — 模块级架构分册
- `Docs/Tasks.md` 与 `Docs/Tasks/` — 路线图、任务板与实现拆分

---

## 关键文档

- `Docs/架构.md` — 架构总览与阅读地图
- `Docs/07架构/README.md` — 模块级架构索引
- `Docs/Tasks.md` — 平台任务板与优先级
- `Docs/Tasks/task24-platform-v1-first-slice.md` — 第一条纵向切片的类与 API 拆解

---

## License

Apache License 2.0
