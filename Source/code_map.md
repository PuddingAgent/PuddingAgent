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
| `Services/AgentExecutionService.cs` | 🔑 Agent 执行入口；所有入口先经过 session 单写者，工具调用轮次在 Assistant + 全部 Tool results 完整后原子写入历史 |
| `Services/SessionExecutionGate.cs` + `PuddingCore/Runtime/ISessionExecutionGate.cs` | Runtime 会话进程内单写者；统一串行化 Conversation Worker、MessageDelivery、Heartbeat 与直接 Runtime 调度对同一 session 的状态修改 |
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
| `Services/LlmInvocationService.cs` | LLM 调用服务（统一入口）；Provider 调用前校验/修复 tool-call 消息序列并记录诊断 |
| `Services/LlmProfileResolver.cs` | 解析 Agent 的 LLM 配置 |
| `Services/LlmOptions.cs` | LLM 请求选项（RecordProviderUsage 只更新诊断字段，不覆盖上下文快照） |
| `Services/ProviderRateLimiter.cs` | Provider 级限流器 |

### 上下文管理
| 文件 | 用途 |
|------|------|
| `Services/ContextWindowManager.cs` | 🔑 上下文窗口管理（token 驱动裁剪 + 自动压缩触发）；比较持久化快照前先修复内存中的不完整工具轮次 |
| `PuddingCore/Models/LlmMessageSequenceNormalizer.cs` | OpenAI-compatible 消息协议守卫；保留完整工具轮次、移除 orphan Tool、降级或丢弃不完整 Assistant tool-call |
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
| `Tools/BuiltIns/Agents/` | `SubAgentTool.cs` | 🔑 子代理派生入口；将 model/capability 一次解析为不可变 `LlmProfile + LlmConfig` 路由快照，并透传 `max_rounds + WorkingDirectory` 执行快照 |
| `Tools/BuiltIns/Agents/` | `AgentSleepTool.cs` | 心跳睡眠控制（max 86400s） |
| `Tools/BuiltIns/Search/` | `SmartSearchTool.cs` | 🔑 语义代码搜索 — 薄包装子代理，三层搜索协议，MainAgentOnly，Explorer 模型 |
| `Tools/BuiltIns/Search/` | `AnySearchSearchTool.cs` | 通用搜索（Web/文档） |
| `Tools/BuiltIns/Search/` | `GitHubSearchTool.cs` | GitHub REST API 搜索 |
| `Tools/BuiltIns/Sessions/` | `SmartQuerySessionLogsTool.cs` | 🔑 语义会话日志查询 — 薄包装子代理，MainAgentOnly，Explorer 模型 |
| `Tools/BuiltIns/Sessions/` | `QuerySessionLogsTool.cs` | 会话日志查询（支持 exclude_heartbeat） |
| `Tools/BuiltIns/Sessions/` | `QuerySessionsTool.cs` | 会话列表查询 |
| `Tools/BuiltIns/SmartWorkflow/` | `SmartWorkflowToolBase.cs` + `Smart*Tool.cs` | 🔑 7 个角色化 Smart 工作流工具；统一 `task` 主参数，通过 manifest.{Role}Model 选定模型，并冻结角色 timeout/maxRounds/真实目录 scope |
| `Tools/BuiltIns/Management/` | `LlmResourcePoolTool.cs` | LLM 资源池查询（Provider + Model + 能力标签），MainAgentOnly |
| `Tools/BuiltIns/Management/` | `AgentStateTool.cs` | Agent 私有状态自维护：检查、诊断、读取、原子更新白名单 Markdown；Low 风险且只使用当前 `AgentInstanceId` |
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
| `Services/WorkspaceAgentFileService.cs` | Agent 实例定义写入权威；创建/管理端更新同步维护 manifest、Markdown 与 `config/llm.json`，并实现 `IAgentSelfMaintenanceService` 的受控自维护写入 |
| `Services/SubAgentManager.cs` | 子代理管理 |
| `Services/ConversationAcceptanceStore.cs` | 原子受理：Message + Batch + Turn + Command + Event 单事务 |
| `Services/ExecutionCommandReader.cs` | Command 稳定执行引用的只读适配器；不拥有任何状态转换 |
| `Services/ConversationEventStore.cs` | Conversation Sequence 分配、事件追加和历史读取 |
| `Services/ConversationProjector.cs` | Event Store 到查询模型的 checkpoint 投影；除按稳定身份物化 ChatMessages 外，还将带不可变 Provider/Model 归因的 `usage.recorded` v2 必达写入 Token 明细账本 |
| `Services/ConversationProjectionWorker.cs` | 按持久 Conversation Head/Checkpoint 扫描投影积压；与具体事件写入者解耦并支持重启追平 |
| `Services/TokenUsageRecorder.cs` | Token 明细账本唯一增量写入器；计费用量事实使用 `RecordRequiredAsync` 并由拥有方等待完成，只有非权威遥测可使用 best-effort `RecordAsync` |
| `Services/TokenUsageRebuildService.cs` | 从 Conversation Event Store 的 `usage.recorded` v2 重建 `agent_llm` 明细，再从完整账本重建月度汇总；禁止猜测历史路由，仅在同一事务中替换可成功重建的 sourceId，未归因事实不得触发删除 |
| `Services/AgentChat/ChatExecutionWorker.cs` | Worker v5 — 通过 IExecutionLeaseStore 原子 CAS 领取，透传 Lease 到 Coordinator |
| `Services/AgentChat/ExecutionRunCoordinator.cs` | Execution Kernel 入口 — 接收 Lease，读取 Command 稳定引用，组装 Snapshot，执行 Runtime，向全部输出事件贯穿 assistant MessageId，提交 Journal；终态写入失败时执行 fenced 基础设施兜底 |
| `Services/AgentChat/TurnOutputChunker.cs` | Runtime delta 聚合边界；持久事件必须持有独立 JsonElement，非 delta 事件必须原样保留 Runtime SchemaVersion |
| `Services/AgentChat/AgentConversationProjectionService.cs` | Chat 历史与活动 Run 查询投影；以 `conversation_events` 为过程事实源，按稳定 `messageId/runId` 关联 `ChatMessages` |
| `Services/AgentChat/AgentRunProjectionService.cs` | Agent 联系人状态投影；状态与 cursor 均来自 canonical Conversation Event sequence |
| `Services/Execution/SqliteExecutionLeaseStore.cs` | 原子 CAS 领取与恢复：BEGIN IMMEDIATE + fencing；释放/过期时事务恢复 Run、Command、Turn |
| `Services/Execution/SqliteExecutionJournal.cs` | 统一 fenced 事件写入、原子终态和 Worker 基础设施失败兜底；终态从 Command 读取 assistant MessageId |
| `Services/Execution/SqliteControlInbox.cs` | 控制消息只读/确认端口；写入只允许经 ExecutionControlService |
| `Services/Execution/ExecutionControlService.cs` | Cancel/Control 的唯一事务写入权威 |
| `Services/PlatformReadinessProbe.cs` | Conversation 执行链 readiness：DB + Submit Handler + Coordinator |
| `Services/Snapshot/AgentExecutionSnapshotFactory.cs` | 只消费 AgentRuntimeProfile 的无密钥快照工厂；冻结 Provider/Profile/Model 与能力引用 |
| `Services/Conversation/SubmitTurnHandler.cs` | Submit Turn 应用处理器 |
| `Services/Conversation/SystemCommandHandler.cs` | 系统命令执行边界；`/yolo` 仅修改 RuntimeControl 并持久化 system transcript，禁止创建 Agent Turn/Command |
| `Services/Conversation/RequestTurnCancellationHandler.cs` | Cancel 处理器 — 写 turn.cancel.requested |
| `Services/Conversation/CreateSteeringHandler.cs` | Steering 应用 Handler；端点在 Runtime 消费器落地前保持关闭 |
| `Services/Conversation/RequestCompactionHandler.cs` | 手动压缩唯一应用入口；解析 Agent Profile、执行压缩、写生命周期事件并创建后继 Conversation |
| `Services/Conversation/CompactionSessionSuccessor.cs` | 压缩后继会话边界；集中创建 Session、持久化 Agent mainSessionId、注册旧→新重定向 |

### 工作空间 Agent 管理前端
| 文件 | 用途 |
|------|------|
| `PuddingPlatformAdmin/src/pages/workspace/[id]/index.tsx` | 工作空间 Agent 列表与实例编辑抽屉；Drawer body 是表单唯一滚动容器，避免嵌套滚动和底部留白 |
| `PuddingPlatformAdmin/src/pages/workspace/[id]/SmartRoleModelFields.tsx` | 7 个 Smart 子代理角色模型下拉；读取服务商模型目录并写入 Agent manifest 字段 |

### 记忆图书馆管理前端
| 文件 | 用途 |
|------|------|
| `PuddingPlatformAdmin/src/pages/memory-library/index.tsx` | 记忆图书馆工作台入口；组织 Workspace/Agent/Library 作用域、搜索与 Page/Book 操作 |
| `PuddingPlatformAdmin/src/pages/memory-library/components/MemoryPageTree.tsx` | Notebook/Page 树；长标题单行省略并保留原文提示 |
| `PuddingPlatformAdmin/src/pages/memory-library/components/MemoryPageEditor.tsx` | Page/Book 主内容区与未选择节点空状态 |
| `PuddingPlatformAdmin/src/pages/memory-library/components/MemoryInspector.tsx` | 节点信息、来源引用与链接检查器 |
| `PuddingPlatformAdmin/src/pages/memory-library/styles.less` | 三栏工作台响应式布局、面板滚动边界和工具栏收缩规则 |

### LLM 资源池前端
| 文件 | 用途 |
|------|------|
| `PuddingPlatformAdmin/src/pages/llm-resource-pool/providerTemplates.ts` | 服务商预设目录；包含 DeepSeek、Moonshot Kimi K3、小米 MiMo、DashScope、OpenAI、BigModel 等 Provider/Model 初始配置 |
| `PuddingPlatformAdmin/src/pages/llm-resource-pool/index.tsx` | LLM 服务商与模型管理；服务商配置支持并发数、TPM、RPM 并写入 `llm.providers.json` |

### 消息系统
| 文件 | 用途 |
|------|------|
| `PuddingPlatformAdmin/src/pages/chat/types.ts` + `components/MessageList.tsx` | ChatTurn→虚拟消息→MessageStream 投影；必须保留 `sourceId/sourceType`，系统命令不得退化为 Agent 身份 |
| `Services/MessageFabric/MessageSystem.cs` | 消息系统核心 |
| `Services/MessageFabric/MessageRouter.cs` | 消息路由（Topic → Channel → Room） |
| `Services/MessageFabric/MessageFabricStore.cs` | 消息持久化与 Inbox 原子 claim/ack/retry；从 `queued/retrying` 投递发现待处理 Agent 目标 |
| `Services/MessageFabric/MessageQueueProjectionService.cs` | Agent 交互队列读模型；默认排除 `visibility=system`，诊断模式可显式包含并把 Pudding envelope 投影为正文 |
| `PuddingRuntime/Services/Messaging/MessageDeliveryDispatcher.cs` | Runtime 消息投递唯一消费者；Hosted Service 订阅唤醒事件，并周期从持久化 Inbox 发现目标、恢复 lease、原子领取；执行进入共享 session 单写者后再 ack/retry/dead-letter |
| `Controllers/Api/MessageQueueController.cs` | Agent 交互队列 API；`includeSystem=false` 为默认用户界面边界 |

### API Controllers（核心）
| Controller | 用途 |
|------------|------|
| `Controllers/Api/SystemCommandsController.cs` | `POST /api/v1/conversations/{id}/system-commands`；系统命令专用端口，不进入 Agent 执行链 |
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
| `Data/PlatformDbContextFactory.cs` | Platform 数据库统一工厂；singleton options + singleton factory 供后台服务使用，并由同一工厂创建 scoped HTTP/application DbContext |
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
| `Abstractions/ILlmResolver.cs` | 🔑 LLM 路由解析边界；`ResolveRouteAsync` 原子返回 Provider/Model 身份与 `LlmConfig` 快照 |
| `Abstractions/ILlmConfigService.cs` | LLM 唯一配置源接口；`GetDefaultProfile` 保留默认 Provider/Profile/Model 身份 |
| `Services/FileLlmResolver.cs` | 基于文件配置的 LLM 路由实现；负责显式路由、唯一纯模型、能力标签和默认 Profile 选择 |
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
| `Agents/IAgentSelfMaintenanceService.cs` | Agent 自维护端口；只暴露当前实例白名单文档的 inspect/read/update，不暴露任意路径或其他 Agent ID |

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
        → TurnExecutorAdapter                     // usage 帧封装为 v2：usage + 不可变 Provider/Profile/Model/Role
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
  → ConversationProjector
    → 按事件 MessageId 幂等物化 ChatMessages
    → 按 EventId 幂等物化 agent_llm TokenUsageEvents（失败不推进 checkpoint）
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
    → SubAgentTool.ResolveChildLlmRouteAsync()
      → ILlmResolver（唯一读取 data/config/llm.providers.json）
        → 唯一确定 Provider/Model + LlmConfig
      → SubAgentTool 仅补充调用语义 ProfileId=subagent.conscious、Role=conscious
    → ISubAgentInvocationService（只映射，不重新解析）
      → SubAgentManager.ValidateLlmRoute()
        → RuntimeDispatchRequest.LlmProfile + LlmConfig
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

## Smart 工具角色模型配置

### manifest.json 字段（Agent 实例级）
| 字段 | 用途 |
|------|------|
| `explorerModel` | Explorer 子代理模型（smart_explore/smart_search/smart_query_session_log） |
| `researcherModel` | Researcher 子代理模型（smart_research） |
| `plannerModel` | Planner 子代理模型（smart_plan） |
| `reviewerModel` | Reviewer 子代理模型（smart_review） |
| `developerModel` | Developer 子代理模型（smart_develop） |
| `deployerModel` | Deployer 子代理模型（smart_deploy） |
| `testerModel` | Tester 子代理模型（smart_test） |

值格式: `"{providerId}/{modelId}"`，如 `"deepseek/deepseek-v4-pro"`。
不配置时 Smart 工具不传 `model`，由 `spawn_sub_agent` 的默认模型策略解析。

### C# 数据模型
- `AgentInstanceManifest`（`PuddingConfigModels.cs`）：7 个 `string?` 属性
- `WorkspaceAgentDto` / `CreateWorkspaceAgentRequest` / `UpdateWorkspaceAgentRequest`：管理 API 的七字段契约
- `WorkspaceAgentFileService`：创建、列表、详情、更新均以 Agent manifest 为唯一配置源；PUT 支持清空角色模型
- `SmartWorkflowToolBase.ResolveRoleModelAsync()`：从 manifest 解析角色模型
- `ILlmResolver.ResolveRouteAsync()`：消费入场 `providerId/modelId` 或能力标签，从
  `ILlmConfigService` 原子解析 `ProviderId + ModelId + LlmConfig`；默认路由使用
  `GetDefaultProfile()`，不得从 endpoint/key/model 反推 Provider
- `SubAgentTool.ResolveChildLlmRouteAsync()`：只为上述路由补充
  `ProfileId=subagent.conscious` 与 `Role=conscious`
- `SubAgentInvocationRequest` / `SubAgentSpawnRequest`：不再持有冗余 `ModelId`，
  只透传上述不可变快照；同时透传 `MaxRounds` 与 `WorkingDirectory`，后者是文件
  工具根目录而不是 WorkspaceId 的路径映射；`SubAgentManager` 在产生状态/事件前
  校验两个模型 ID 一致
- `SmartWorkflowArgs`：七个 Smart 工具统一以 `task` 作为必填主指令；
  `ScopedSmartWorkflowArgs.scope` 只有在指向真实文件/目录时才冻结为 WorkingDirectory
- `AgentExecutionService.ExecuteAsync()`：同步边界禁止返回 `Running`；function-call 在最后
  一轮耗尽时统一返回 `Failed + MaxRoundsReached`

### 前端 UI
- `workspace/[id]/SmartRoleModelFields.tsx`：加载启用的 LLM 服务商/模型并生成 7 个角色模型下拉
- `workspace/[id]/WorkspaceAgentSettingsDrawer.tsx`：Workspace Agent 自包含配置编辑器；
  复用全局模板的分组组件，但字段绑定到 Agent DTO，不回查模板
- `workspace/[id]/index.tsx`：加载 Agent 详情、模板创建快照、Provider/Model、Capability
  和 Skill 选项，并负责完整创建/更新请求
- 下拉选项格式：`{服务商名} / {模型名} (上下文大小)`

### Workspace Agent 配置闭环

```text
WorkspaceAgentSettingsDrawer
  → Create/UpdateWorkspaceAgentRequest
  → WorkspaceAgentFileService
  → data/agents/{agentId}/manifest.json + Markdown + config/llm.json
  → AgentProfileProvider
  ├→ AgentExecutionSnapshotFactory
  │   → ExecutionRunCoordinator → TurnExecutionContext → RuntimeDispatchRequest
  └→ SmartWorkflowToolBase
```

- Agent 编辑器覆盖角色、Prompt/Markdown、能力、Skill、主模型、潜意识模型、
  Embedding、Smart 子代理模型和执行护栏
- `sourceTemplateId` 创建后只作为来源审计信息，运行时不得据此读取模板
- `maxContextTokens` 不进入 Agent 表单或 Agent 配置，容量只由 Provider Model 解析
- 最大轮次、最大耗时、最大工具调用进入不可变执行快照；Runtime 以实例值和平台
  `AgentExecutionGuardrails` 中较小者为有效上限

---

## 注意事项

1. **双轨工具系统**: 正在从 `IAgentSkill`（Legacy）迁移到 `IPuddingTool`（新），两套接口并存
2. **双轨记忆系统**: 传统图书馆（Book/Chapter）+ 结构化事实库（Fact）并存，未来融合
3. **Smart* 工具薄包装模式**: 语义化工具 = `SubAgentExposure.MainAgentOnly` + 统一 `task` 合同 + 角色执行预算 + 三层搜索协议。支持 `model` 和 `capability_requirements` 两种模型选择方式
4. **能力标签系统 (P2)**: `ILlmResolver.ResolveRouteAsync(requiredCapabilityTags)` 按标签选择唯一配置源中的模型；显式 model 路由优先
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
17. **Agent 不复制模型容量**: `maxContextTokens` 只从 `llm.providers.json` 的 Provider Model 解析；Agent manifest、Agent DTO 和 `config/llm.json` binding 不保存该字段
18. **前端终态游标**: `turn.accepted` 负责尽早迁移 optimistic Turn 身份；终态按 Turn 清除全部关联 messageId，事件只有成功归并后才能推进 cursor
19. **Agent 执行护栏生效链**: Agent manifest → RuntimeProfile → ExecutionSnapshot → TurnExecutionContext → RuntimeDispatchRequest；实例上限不得超过平台 Guardrails
