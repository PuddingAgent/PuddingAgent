# PuddingAgent CodeMAP

> 最后更新: 2026-07-18 | 维护原则: 仅收录核心常用类，不追求全覆盖

---

## 项目概览

PuddingAgent 是一个 AI Agent 运行时平台，支持多 Agent、多会话、工具调用、记忆系统和任务规划。

技术栈: .NET 10 / SQLite (EF Core) / React + TypeScript / Serilog

---

## 顶层目录结构

```
Source/
├── PuddingAgent/              # 入口项目 (Program.cs, 启动配置)
├── PuddingRuntime/            # 🔑 运行时核心 (Agent Loop, LLM 调用, 工具系统)
├── PuddingPlatform/           # 🔑 平台层 (Session 管理, API, 数据持久化)
├── PuddingMemoryEngine/       # 🔑 记忆引擎 (Library/Book/Chapter, FTS5)
├── PuddingCore/               # 🔑 核心抽象与契约 (接口、模型、解析器)
├── PuddingController/         # 代理控制层
├── PuddingGateway/            # LLM 网关适配
├── PuddingCodeIntelligence/   # 代码索引/分析
├── PuddingFullTextIndex/      # 全文索引引擎
```

---

## 🔑 PuddingRuntime — 运行时核心

### 入口 & 配置
| 文件 | 用途 |
|------|------|
| `DependencyInjection.cs` | Runtime 服务注册入口 |
| `Services/PuddingConfigLoader.cs` | 加载 JSON 配置文件 |
| `Services/PuddingJsonConfig.cs` | 配置模型定义 |
| `Services/RuntimeExecutionConfigService.cs` | 运行时执行配置 |

### Agent Loop (核心执行循环)
| 文件 | 用途 |
|------|------|
| `Services/AgentExecutionService.cs` | 🔑 Agent 执行入口，驱动 LLM 调用 → 工具调用 → 循环 |
| `Services/AgentLoop/CompletionPolicy.cs` | 判断 Agent 何时完成（stop reason 处理） |
| `Services/AgentLoop/ExecutionJournal.cs` | 执行日志记录 |
| `Services/AgentLoop/AgentExecutionGuardrails.cs` | 执行护栏（最大轮次等） |
| `Services/AgentLoop/ExecutionControlRegistry.cs` | 注册执行控制策略 |

### LLM 调用
| 文件 | 用途 |
|------|------|
| `Services/IRuntimeLlmClient.cs` | LLM 客户端接口 |
| `Services/DirectLlmClient.cs` | 直连 LLM 客户端 |
| `Services/ControllerRoutedLlmClient.cs` | 通过代理路由的 LLM 客户端 |
| `Services/LlmInvocationService.cs` | LLM 调用服务（统一入口） |
| `Services/LlmProfileResolver.cs` | 解析 Agent 的 LLM 配置 |
| `Services/LlmOptions.cs` | LLM 请求选项（RecordProviderUsage 只更新诊断字段，不覆盖上下文快照） |
| `Services/ProviderRateLimiter.cs` | Provider 级限流器 |

### 上下文管理
| 文件 | 用途 |
|------|------|
| `Services/ContextWindowManager.cs` | 🔑 上下文窗口管理（token 驱动裁剪 + 自动压缩触发） |
| `Services/ContextCompactionService.cs` | 上下文压缩执行（消息合并） |
| `Services/ContextHealthEvaluator.cs` | 🔑 上下文健康度评估 + 容量预测（PredictCapacity） |
| `Services/ContextAssemblyService.cs` | 上下文组装（System Prompt + 历史 + 记忆） |
| `Services/ContextPipeline.cs` | 上下文管线编排 |

### 工具系统
| 文件 | 用途 |
|------|------|
| `Tools/Platform/PuddingToolRegistry.cs` | 🔑 工具注册表（所有工具的注册中心） |
| `Tools/Platform/ToolInvocationService.cs` | 工具调用分发（解析工具名 → 执行） |
| `Tools/Platform/ToolPermissionPolicyService.cs` | 工具权限策略（安全区检查） |
| `Tools/Approval/InMemoryToolApprovalService.cs` | 高危工具审批服务 |

### 核心工具
| 目录 | 工具 | 用途 |
|------|------|------|
| `Tools/BuiltIns/Files/` | `FileTools.cs` | 文件读写、搜索、grep |
| `Tools/BuiltIns/Memory/` | `MemoryTools.cs` | 记忆读写（save/manage/search/grep） |
| `Tools/BuiltIns/Agents/` | `SubAgentTool.cs` | 🔑 子代理派生（支持 model + capability_requirements 参数） |
| `Tools/BuiltIns/Agents/` | `AgentSleepTool.cs` | 心跳睡眠控制（max 86400s） |
| `Tools/BuiltIns/Search/` | `SmartSearchTool.cs` | 🔑 语义代码搜索 — 薄包装子代理，三层搜索协议，MainAgentOnly |
| `Tools/BuiltIns/Search/` | `AnySearchSearchTool.cs` | 通用搜索（Web/文档） |
| `Tools/BuiltIns/Search/` | `GitHubSearchTool.cs` | GitHub REST API 搜索 |
| `Tools/BuiltIns/Sessions/` | `SmartQuerySessionLogsTool.cs` | 🔑 语义会话日志查询 — 薄包装子代理，MainAgentOnly |
| `Tools/BuiltIns/Sessions/` | `QuerySessionLogsTool.cs` | 会话日志查询（支持 exclude_heartbeat） |
| `Tools/BuiltIns/Sessions/` | `QuerySessionsTool.cs` | 会话列表查询 |
| `Tools/BuiltIns/Management/` | `LlmResourcePoolTool.cs` | LLM 资源池查询（Provider + Model + 能力标签），MainAgentOnly |
| `Tools/BuiltIns/Http/` | `HttpFetchSkill.cs` | HTTP 请求 |
| `Tools/BuiltIns/Shell/` | Shell 工具 | 终端命令执行（支持 tail_lines） |
| `Tools/BuiltIns/Terminal/` | `TerminalTools.cs` | 后台终端执行 |
| `Tools/BuiltIns/CodeIntelligence/` | `CodeQueryTools.cs` | 代码索引查询 |
| `Tools/BuiltIns/Documents/` | `ReadOfficeDocumentTool.cs` | Office 文档读取（NPOI 2.8.0） |

### 事件系统
| 文件 | 用途 |
|------|------|
| `Services/Events/InternalEventBus.cs` | 内部事件总线（进程内） |
| `Services/Events/EventDispatcher.cs` | 事件分发器 |
| `Services/Events/EventPreprocessor.cs` | 事件预处理（上下文注入） |
| `Services/Events/PriorityEventQueue.cs` | 优先级事件队列 |

### 其他服务
| 文件 | 用途 |
|------|------|
| `Services/HeartbeatService.cs` | Agent 心跳服务（定时唤醒） |
| `Services/AgentSessionManager.cs` | Agent 会话管理 |
| `Services/SseEventForwarder.cs` | SSE 事件转发到前端 |

### Chat 前端 Viewport
| 文件 | 用途 |
|------|------|
| `PuddingPlatformAdmin/src/pages/chat/viewport/useMessageViewportRuntime.ts` | 消息视口唯一滚动权威；virtualizer 负责测量/锚点，真实容器负责贴底，并在 pinned 模式下对延迟布局增长持续收敛 |
| `PuddingPlatformAdmin/src/pages/chat/components/MessageList.tsx` | 消息虚拟列表与 viewport overlay；不直接拥有滚动策略 |

---

## 🔑 PuddingPlatform — 平台层

### 数据层
| 文件 | 用途 |
|------|------|
| `Data/PlatformDbContext.cs` | 🔑 EF Core 主 DbContext |
| `Data/Entities/*.cs` | 实体定义（40+ 实体） |

### 核心实体（最常用）
| 实体 | 用途 |
|------|------|
| `WorkspaceEntity.cs` | 工作区 |
| `WorkspaceAgentEntity.cs` | Agent 实例 |
| `WorkspaceAgentTemplateEntity.cs` | Agent 模板 |
| `ChatMessageEntity.cs` | 聊天消息 |
| `ChatExecutionCommandEntity.cs` | Conversation Turn 的可靠执行命令 |
| `AcceptanceBatchEntity.cs` | 用户提交批次与 `clientRequestId` 幂等事实 |
| `ConversationTurnEntity.cs` | Conversation Turn 独立实体（ADR-059 Execution Kernel） |
| `ExecutionRunEntity.cs` | 每次执行尝试独立记录（ADR-059 Execution Kernel） |
| `ControlMessageEntity.cs` | 统一控制消息收件箱（ADR-059 Cancel/Steering/Approval） |
| `ConversationEventEntity.cs` | Conversation Event Store 事件 Envelope |
| `ConversationHeadEntity.cs` | Conversation 内已提交事件 Head Sequence |
| `ConversationProjectionCheckpointEntity.cs` | 物化视图投影进度 |
| `LlmProviderEntity.cs` / `LlmModelEntity.cs` | LLM 提供者/模型 |
| `SessionEventLogEntity.cs` | 会话事件日志 |

### 核心服务
| 文件 | 用途 |
|------|------|
| `Services/SessionStateManager.cs` | 遗留 Session 状态与 SSE/WS 推送；Conversation Event Store 迁移完成后退出聊天事实链路 |
| `Services/SessionStateStore.cs` | 🔑 会话状态持久化 — 重启后恢复（data/sessions/{id}.json） |
| `Services/SessionCompactionEventEmitter.cs` | 自动压缩生命周期适配器；只写 canonical Conversation Event Store |
| `Services/SessionRedirectStore.cs` | 会话重定向（压缩后新旧 Session 映射） |
| `Services/PlatformApiClient.cs` | 平台 API 客户端（内部调用） |
| `Services/ChatHistoryService.cs` | 聊天历史查询 |
| `Services/AgentLLMConfigResolver.cs` | Agent 的 LLM 配置解析 |
| `Services/AgentRuntimeProfileResolver.cs` | Agent 执行配置唯一解析边界；从实例 manifest + `config/llm.json` 读取快照，并用 `llm.providers.json` 补齐连接配置 |
| `Services/WorkspaceAgentFileService.cs` | Agent 实例定义写入权威；创建/更新同步维护 manifest、Markdown 与 `config/llm.json` |
| `Services/SubAgentManager.cs` | 子代理管理 |
| `Services/ConversationAcceptanceStore.cs` | 原子受理：Message + Batch + Turn + Command + Event 单事务 |
| `Services/ExecutionCommandReader.cs` | Command 稳定执行引用的只读适配器；不拥有任何状态转换 |
| `Services/ConversationEventStore.cs` | Conversation Sequence 分配、事件追加和历史读取 |
| `Services/ConversationProjector.cs` | Event Store 到 ChatMessages/查询模型的 checkpoint 投影；保留事件的 Message/Turn/Command 稳定身份 |
| `Services/ConversationProjectionWorker.cs` | 按持久 Conversation Head/Checkpoint 扫描投影积压；与具体事件写入者解耦并支持重启追平 |
| `Services/AgentChat/ChatExecutionWorker.cs` | Worker v5 — 通过 IExecutionLeaseStore 原子 CAS 领取，透传 Lease 到 Coordinator |
| `Services/AgentChat/ExecutionRunCoordinator.cs` | Execution Kernel 入口 — 接收 Lease，读取 Command 稳定引用，组装 Snapshot，执行 Runtime，向全部输出事件贯穿 assistant MessageId，提交 Journal；终态写入失败时执行 fenced 基础设施兜底 |
| `Services/AgentChat/TurnOutputChunker.cs` | Runtime delta 聚合边界；持久事件必须持有独立 JsonElement |
| `Services/AgentChat/AgentConversationProjectionService.cs` | Chat 历史与活动 Run 查询投影；以 `conversation_events` 为过程事实源，按稳定 `messageId/runId` 关联 `ChatMessages` |
| `Services/AgentChat/AgentRunProjectionService.cs` | Agent 联系人状态投影；状态与 cursor 均来自 canonical Conversation Event sequence |
| `Services/Execution/SqliteExecutionLeaseStore.cs` | 原子 CAS 领取与恢复：BEGIN IMMEDIATE + fencing；释放/过期时事务恢复 Run、Command、Turn |
| `Services/Execution/SqliteExecutionJournal.cs` | 统一 fenced 事件写入、原子终态和 Worker 基础设施失败兜底；终态从 Command 读取 assistant MessageId |
| `Services/Execution/SqliteControlInbox.cs` | 控制消息只读/确认端口；写入只允许经 ExecutionControlService |
| `Services/Execution/ExecutionControlService.cs` | Cancel/Control 的唯一事务写入权威 |
| `Services/PlatformReadinessProbe.cs` | Conversation 执行链 readiness：DB + Submit Handler + Coordinator |
| `Services/Snapshot/AgentExecutionSnapshotFactory.cs` | 只消费 AgentRuntimeProfile 的无密钥快照工厂；冻结 Provider/Profile/Model 与能力引用 |
| `Services/Conversation/SubmitTurnHandler.cs` | Submit Turn 应用处理器 |
| `Services/Conversation/RequestTurnCancellationHandler.cs` | Cancel 处理器 — 写 turn.cancel.requested |
| `Services/Conversation/CreateSteeringHandler.cs` | Steering 应用 Handler；端点在 Runtime 消费器落地前保持关闭 |
| `Services/Conversation/RequestCompactionHandler.cs` | 手动压缩唯一应用入口；解析 Agent Profile、执行压缩、写生命周期事件并创建后继 Conversation |
| `Services/Conversation/CompactionSessionSuccessor.cs` | 压缩后继会话边界；集中创建 Session、持久化 Agent mainSessionId、注册旧→新重定向 |

### 消息系统
| 文件 | 用途 |
|------|------|
| `Services/MessageFabric/MessageSystem.cs` | 消息系统核心 |
| `Services/MessageFabric/MessageRouter.cs` | 消息路由（Topic → Channel → Room） |
| `Services/MessageFabric/MessageFabricStore.cs` | 消息持久化 |

### API Controllers（核心）
| Controller | 用途 |
|------------|------|
| `Api/SessionEventsController.cs` | 🔑 Conversation Event live SSE、forward replay 与 `/compact` HTTP 映射；不编排压缩业务 |
| `Api/SessionApiController.cs` | Session CRUD |
| `Api/AgentChatApiController.cs` | Agent 聊天 API |
| `Api/ConversationTurnsController.cs` | ADR-059 Conversation Turn 唯一命令入口：Submit / Cancel；Steering 明确返回 501 |
| `Api/MessageApiController.cs` | ChatMessages 查询 API；返回 Message/Turn/Command 稳定身份供前端历史收敛 |
| `Api/AuthApiController.cs` | 认证（JWT） |
| `Api/WorkspaceApiController.cs` | 工作区管理 |
| `Api/ToolCatalogApiController.cs` | 工具目录 |
| `Api/MemoryLibraryAdminController.cs` | 记忆图书馆管理 |

---

## 🔑 PuddingMemoryEngine — 记忆引擎

### 核心类
| 文件 | 用途 |
|------|------|
| `Data/MemoryLibrary.cs` | 🔑 记忆图书馆实现（Book/Chapter CRUD, FTS5 搜索） |
| `Data/IMemoryLibrary.cs` | 记忆图书馆接口 |
| `Data/MemoryDbContext.cs` | 核心会话、消息、记忆与事件队列 DbContext |
| `Data/MemoryDbInitializer.cs` | 从 `Schema/init_memory.sql` 显式初始化核心 Schema；与图书馆共享数据库时不使用 `EnsureCreated` |
| `Data/MemoryLibraryDbContext.cs` | 记忆图书馆 DbContext（与核心记忆共享同一 SQLite 文件） |
| `Data/MemoryLibraryDbInitializer.cs` | 核心 Schema 完成后显式初始化图书馆 Schema |
| `Data/BookRegistry.cs` | 标准 Book 注册表（14 本预定义书） |
| `Data/LibraryEntities.cs` | 实体定义（Library, Book, Chapter, ChapterRelation, Pointer） |

### 结构化事实库
| 文件 | 用途 |
|------|------|
| `FactMemoryService.cs` | 结构化事实 CRUD（Fact + Evidence + Context + Freshness） |
| `MemoryEngine.cs` | 记忆引擎（融合召回：Library + Facts + Prefs） |

### 工具类
| 文件 | 用途 |
|------|------|
| `MemoryBoundaryService.cs` | 记忆边界控制 |
| `SessionMemoryStore.cs` | Session 级记忆存储 |
| `WorkspaceMemoryStore.cs` | Workspace 级记忆存储 |
| `MemoryEntry.cs` | 记忆条目模型 |

---

## 🔑 PuddingCore — 核心抽象与契约

### LLM 配置与解析
| 文件 | 用途 |
|------|------|
| `Abstractions/ILlmResolver.cs` | 🔑 LLM 解析器接口（含 ResolveByCapabilityAsync 按能力标签匹配） |
| `Abstractions/ILlmConfigService.cs` | LLM 配置服务接口（LlmModelInfo 含 CapabilityTags） |
| `Services/FileLlmResolver.cs` | 基于文件配置的 LLM 解析器实现 |
| `Services/FileLlmConfigService.cs` | 基于文件的 LLM 配置服务 |
| `Contracts/LlmContracts.cs` | LLM 相关契约模型 |

### 工具契约
| 文件 | 用途 |
|------|------|
| `Contracts/PuddingToolContracts.cs` | 🔑 工具契约（ToolAttribute, SubAgentExposure 枚举 — MainAgentOnly） |

### 会话与运行时
| 文件 | 用途 |
|------|------|
| `Abstractions/ISessionStateManager.cs` | 会话状态管理接口（含 Restore 方法） |
| `Models/SwarmSessionState.cs` | 会话状态枚举 |
| `Services/RuntimeActivity.cs` | 运行时活动记录（Enrich 方法处理 "unknown" 合法阶段） |
| `Configuration/PuddingDataPaths.cs` | 数据路径配置 |
| `Agents/AgentProfileProvider.cs` | 加载自包含 Agent 实例定义：manifest、`config/llm.json`、Markdown 与 permissions；运行时不跨目录读取模板 |

### Conversation 受理与可靠事件流
| 文件 | 用途 |
|------|------|
| `Platform/ConversationHandlers.cs` | 4 个应用 Handler 接口：Submit/Cancel/Steering/Compaction |
| `Platform/ConversationTurnContracts.cs` | SubmitTurn、Recipient、ContentPart、AcceptanceResult |
| `Platform/ConversationEventContracts.cs` | Event Envelope、AppendResult、Cursor 与写入条件 |
| `Platform/ExecutionRunContracts.cs` | 冻结契约：ExecutionLease、TurnTerminal、AgentExecutionSnapshot |
| `Platform/IExecutionRunCoordinator.cs` | Execution Kernel 入口契约 |
| `Platform/IExecutionJournal.cs` | 统一 fenced 事件写入 + 原子终态契约 |
| `Platform/IExecutionLeaseStore.cs` | 原子 CAS 领取、续租、释放契约 |
| `Platform/IControlInbox.cs` | 统一控制消息收件箱契约 |
| `Platform/IAgentExecutionSnapshotFactory.cs` | Agent 执行快照工厂契约 |
| `Platform/IConversationAcceptanceStore.cs` | Turn 批次幂等受理事务边界契约 |
| `Platform/IConversationEventStore.cs` | Conversation Event 追加、重放、Head/Sequence 契约 |
| `Platform/IExecutionCommandReader.cs` | Command 只读契约；写入分别归 Acceptance/Lease/Journal |
| `Platform/ConversationContracts.cs` | 状态枚举（CommandStatus/RunStatus/TurnStatus/TurnTerminalKind）和事件类型常量 |
| `Runtime/ITurnExecutor.cs` | Runtime 执行端口；不依赖 HTTP/SSE/Platform DTO |

### Token 预算
| 文件 | 用途 |
|------|------|
| `Contracts/ContextCompactionContracts.cs` | 上下文压缩契约（含 CapacityPrediction 模型） |
| `Contracts/PrefixCacheContracts.cs` | Prefix Cache 契约（Churn 归因） |

---

## 关键流程（调用链路）

### 1. 用户消息 → Agent 响应（Conversation 可靠事件流, ADR-059 Execution Kernel）
```
前端 POST /api/v1/conversations/{conversationId}/turns
  → ConversationTurnsController                 // HTTP 协议层
  → ISubmitTurnHandler                          // 应用处理器
  → IConversationAcceptanceStore                // 原子受理
    → Message + AcceptanceBatch + ConversationTurn + Command + turn.accepted Event + Head
  → ChatExecutionWorker v5                      // 后台 Worker
    → IExecutionLeaseStore.TryAcquireAsync       // 原子 CAS 领取（BEGIN IMMEDIATE + 每 Conv 互斥）
    → 透传 ExecutionLease 给 IExecutionRunCoordinator
      → IAgentRuntimeProfileResolver               // 唯一配置解析边界
      → IAgentExecutionSnapshotFactory             // 无密钥执行快照
      → LlmInvocationProfile                       // Provider/Profile/Model 类型化路由身份
      → ITurnExecutor                             // Agent Loop Runtime
      → TurnOutputChunker                         // delta 聚合
      → IExecutionJournal.AppendOutputAsync       // fenced 输出
      → IExecutionJournal.CommitTerminalAsync     // 原子终态（验证 runId/workerId/fence/lease）
        → Turn + Run + Command + Event + Head 同事务更新
  → ICommittedEventSignal
  → SSE / forward replay（同一 canonical envelope）
  → 前端确认服务端 Turn ID，原子替换 optimistic Turn ID
    → `turn.accepted` 可先于 POST continuation 完成身份迁移
  → 前端 Reducer 按 sequence 幂等提交
  → ConversationProjectionWorker 发现 Head > Checkpoint
  → ConversationProjector 按事件 MessageId 幂等物化 ChatMessages
  → 前端历史对账只允许单调收敛，不得用滞后物化结果降级 SSE 终态

旧 POST /api/workspaces/{workspaceId}/chat/message 已删除，不保留兼容翻译层
```

### 2. Token 预算与自动压缩
```
ContextWindowManager.EnsureCapacity()
  → ContextHealthEvaluator.Evaluate(usedTokens, maxBudget)
    ├── < 60% → 不裁剪
    ├── 60-80% → TrimHistory（token 驱动，修剪到 70%）  // 动态计算 maxMessages = budget/2500
    └── >= 80% → TryAutoCompactAsync()  // LLM 压缩
      → ContextCompactionService.CompactAsync()
      → CompactionEventEmitter.EmitAsync()
      → conversation_events → resumable SSE → 前端
  → CapacityPrediction: 剩余 tokens + 预计几轮后触发各阈值
```

### 3. 手动 `/compact` 与新会话切换

```text
Frontend /compact
  → POST /api/sessions/{conversationId}/compact
  → IRequestCompactionHandler
      → IAgentRuntimeProfileResolver
      → context.compaction.started
      → IContextCompactionService
      → ICompactionSessionSuccessor
          → create successor Session
          → Controller SessionRepository.RebindMainAsync
          → persist Agent mainSessionId
          → register old → new redirect
      → source context.compaction.completed
      → successor context.compaction.completed
  → 前端按 compactionId 更新独立状态 Turn
  → 清零新 Conversation 的 SSE cursor 并切换
  → Bootstrap.lifecycleEvents 恢复持久压缩状态
  → 前端维护独立 lifecycle Turn 索引
  → Hook 输出边界统一合并 ChatMessages 与 lifecycle Turn
```

Compact HTTP 命令只携带 Conversation/Workspace/Agent 身份、压缩级别、原因和
`compactionId`，不得携带 `llmConfig`。压缩事件没有 `turnId`，前端不得把它
归并到最近的 Agent 回复。`snapshotCursor` 覆盖压缩事件时，Bootstrap 必须同时
返回对应 `lifecycleEvents`，前端应用这些事件后才允许推进 SSE cursor。
Controller SessionRepository 是 Main Session 归属的事实源；Agent manifest 只是
运行时镜像，内存 redirect 只负责进程内低延迟跳转，二者都不能替代持久 rebind。

### 4. Smart* 工具 — 子代理薄包装模式
```
Agent 调用 smart_search(what="...", capability_requirements="fast,search")
  → SmartSearchTool → spawn_sub_agent(sync, model 或 capability)
    → SubAgentTool.BuildChildLlmConfigAsync()
      → ILlmResolver.ResolveByCapabilityAsync(["fast","search"])
        → 从 LLM 资源池匹配 deepseek-v4-flash
    → 子代理执行三层搜索协议（Phase 1 广度 → Phase 2 深度 → Phase 3 退路）
    → 返回结构化结果（FOUND/FILES/SUMMARY/MISSING）
  → 子代理标记 MainAgentOnly（不暴露给孙代理，防循环）
```

### 4. 记忆写入
```
Agent 调用 save_memory / manage_memory
  → MemoryTools.ExecuteAsync()
    → MemoryLibrary.CreateBookAsync()       // 创建 Book（检查重复）
    → MemoryLibrary.AddChapterAsync()       // 添加 Chapter
    → BookRegistry.GetBookIdByAlias()       // 标准名 → BookId 路由
```

### 5. 记忆召回
```
Agent 调用 search_memory / grep_memory
  → MemoryTools / MemoryLibraryTool
    → MemoryLibrary.SearchChaptersFtsAsync()  // FTS5 全文检索
    → MemoryLibrary.SearchBooksFtsAsync()     // Book 级检索
```

### 6. 会话状态持久化恢复
```
重启
  → SessionStateStore.LoadFromDisk()           // 扫描 data/sessions/*.json
  → 遍历持久化状态
    ├── Streaming/Running/Waiting → 恢复为 Stopped（被中断）
    ├── Completed/Closed → 保持原状态
    └── 无持久化记录 → 标记 Stopped（兜底）
  → ISessionStateManager.Restore() → 恢复 _sessionStates
```

---

## 注意事项

1. **双轨工具系统**: 正在从 `IAgentSkill`（Legacy）迁移到 `IPuddingTool`（新），两套接口并存
2. **双轨记忆系统**: 传统图书馆（Book/Chapter）+ 结构化事实库（Fact）并存，未来融合
3. **Smart* 工具薄包装模式**: 语义化工具 = `SubAgentExposure.MainAgentOnly` + 自然语言接口 + 三层搜索协议。支持 `model` 和 `capability_requirements` 两种模型选择方式
4. **能力标签系统 (P2)**: `ILlmResolver.ResolveByCapabilityAsync` 按标签匹配模型。`search` 标签仅 `deepseek-v4-flash`
5. **Token 预算准确**: `RecordProviderUsage` 不再覆盖上下文快照；`TrimHistory` 改为 token 驱动（`maxTokenBudget/2500`）
6. **会话持久化**: `SessionStateStore` 在状态变更时异步写入 `data/sessions/{id}.json`，重启后恢复
7. **EF Core Migration**: Platform 用 Code-First Migration，MemoryEngine 用 DbInitializer 手动建表
8. **SSE 双轨迁移**: 新聊天链路以 `ConversationEventStore` 为事实源并按 sequence 重放；`SessionStateManager` 仅保留遗留 Session 流
9. **工具权限**: `ToolPermissionPolicyService` 检查安全区，高危工具需 `InMemoryToolApprovalService` 审批
10. **执行配置边界**: Command 只保存稳定引用；LLM/Tool/Skill 配置由 Worker 执行时通过 SnapshotFactory 快照化
11. **ADR-059 Execution Kernel 已建成**: Worker 原子 CAS 领取；Journal 负责 fenced 输出、原子终态与基础设施失败兜底；SnapshotFactory 负责执行配置；ControlService 负责 Cancel/Control 写入
12. **唯一命令入口**: 前端只调用 `POST /api/v1/conversations/{id}/turns`；旧 ChatApiController 与旧前端发送函数已删除
13. **Command 单一写入权威**: `IChatCommandStore` 已删除；读取使用 `IExecutionCommandReader`，受理/租约/终态分别由 AcceptanceStore/LeaseStore/Journal 写入
14. **Control 安全边界**: Inbox 只读后确认；Cancel 在终态成功后确认；Steering 在 Runtime 消费器完成前返回 501
15. **启动与健康门禁**: 所有环境启用 DI Build/Scope 校验；`/health/live` 与 `/health/ready` 分离
16. **Agent LLM 快照**: `data/agents/{agentId}/config/llm.json` 是执行期 LLM Binding 真相源；manifest 中同名字段仅为管理视图镜像，写入服务必须同步维护，Resolver 不得回查模板或系统默认模型
17. **前端终态游标**: `turn.accepted` 负责尽早迁移 optimistic Turn 身份；终态按 Turn 清除全部关联 messageId，事件只有成功归并后才能推进 cursor
