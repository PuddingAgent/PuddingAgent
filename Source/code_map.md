# PuddingAgent CodeMAP

> 最后更新: 2026-07-23 | 维护原则: 仅收录核心常用类，不追求全覆盖 | +Subconscious 系统

---

## 项目概览

PuddingAgent 是一个 AI Agent 运行时平台，支持多 Agent、多会话、工具调用、记忆系统和任务规划。

技术栈: .NET 10 / SQLite (EF Core) / React + TypeScript / Serilog

### 开发启动与诊断

| 文件 | 用途 |
|------|------|
| `../dev-up.py` | 本地 Backend/Frontend/Proxy 启动器；前端短时间连续退出时熔断并指向 `tmp/dev/frontend.err.log`，避免编译错误触发无限重启；`--auto-yolo` 以后台健康等待执行，不受前台监督循环阻塞 |
| `../How-Debuge.md` | 可重复使用的启动、会话、SSE、子代理与工具诊断路径 |

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
| `Services/RuntimeExecutionConfigService.cs` | 运行时执行配置；统一归一化父 Turn 24h 硬上限、1h 无进展窗口、LLM 首块/流空闲窗口，以及子代理并发、timeout 与父 Turn 收尾预留 |

### Agent Loop (核心执行循环)
| 文件 | 用途 |
|------|------|
| `Services/AgentExecutionService.cs` | 🔑 Agent 执行入口；所有入口先经过 session 单写者，工具调用轮次在 Assistant + 全部 Tool results 完整后原子写入历史；把父 `ExecutionDeadlineUtc` 传入每次工具调用；以稳定 identity 报告 LLM/工具/子代理的 liveness 与带指纹 meaningful progress；子代理执行按 runId 发出 round/LLM/tool/terminal 审计事件，并以绝对 deadline 区分 timed_out/cancelled、从 journal 提交真实终态统计 |
| `PuddingCore/Runtime/RuntimeExecutionIdentity.cs` | 主 Agent、工具调用和子代理共用的稳定执行身份；贯穿 Conversation/Turn/Command/Run/Tool/Invocation |
| `PuddingCore/Runtime/ExecutionProgressRegistry.cs` | 主 Run 进程内进展注册表；按 Conversation 汇聚子执行信号，区分 liveness/meaningful，并拒绝相同 Run+阶段+指纹的重复续期 |
| `Services/SessionExecutionGate.cs` + `PuddingCore/Runtime/ISessionExecutionGate.cs` | Runtime 会话进程内单写者；统一串行化 Conversation Worker、MessageDelivery、Heartbeat 与直接 Runtime 调度对同一 session 的状态修改 |
| `Services/AgentLoop/CompletionPolicy.cs` | 判断 Agent 何时完成（stop reason 处理） |
| `Services/AgentLoop/ExecutionJournal.cs` | 执行日志记录 |
| `Services/AgentLoop/AgentExecutionGuardrails.cs` | 执行护栏（最大轮次等） |
| `Services/AgentLoop/ExecutionControlRegistry.cs` | 注册执行控制策略 |
| `Services/StreamWatchdog.cs` + `DirectLlmClient.cs` | LLM 流操作级滑动看门狗；首块默认 300 秒，首块后相邻流块默认 120 秒，Provider 配置只能收紧空闲窗口，不再施加固定流总时长；使用 Stopwatch 单调时钟 |

### LLM 调用
| 文件 | 用途 |
|------|------|
| `Services/IRuntimeLlmClient.cs` | LLM 客户端接口 |
| `Services/DirectLlmClient.cs` | 直连 LLM 客户端；统一区分 HTTP/网络瞬态错误，流式路径仅在首个 Delta 前按 Provider 策略重试，首块后禁止重试以避免重复输出/工具调用；仅当当前模型带 `vision` 能力标签时才把 workspace 授权视觉制品序列化为多模态内容，文本模型不再接收 `image_url` |
| `Services/ControllerRoutedLlmClient.cs` | 通过代理路由的 LLM 客户端 |
| `Services/LlmInvocationService.cs` | LLM 调用服务（统一入口）；Provider 调用前校验/修复 tool-call 消息序列并记录诊断；调用方取消必须重新抛出，禁止降级为普通 Provider 失败 |
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
| `Tools/Platform/PuddingToolRegistry.cs` | 🔑 工具注册表与执行硬边界；强制 MainAgentOnly/DelegatedSubAgent、AllowSubDelegation 和 DelegationDepth，模型无法用伪造工具名绕过 |
| `Tools/Platform/ToolInvocationService.cs` | 工具调用分发（解析工具名 → 透传配置所有者/委派深度 → 执行） |
| `Tools/Platform/ToolPermissionPolicyService.cs` | 工具权限策略（安全区检查） |
| `Tools/Approval/InMemoryToolApprovalService.cs` | 高危工具审批服务 |

### 核心工具
| 目录 | 工具 | 用途 |
|------|------|------|
| `Tools/BuiltIns/Files/` | `FileTools.cs` + `FileSearchTool.cs` | 文件读写、搜索、grep；`file_search` 在工具边界统一把任意 provider/fallback 结果规范化为绝对路径 |
| `Tools/BuiltIns/Memory/` | `MemoryTools.cs` | 记忆读写（save/manage/search/grep） |
| `Tools/BuiltIns/Agents/` | `SubAgentTool.cs` | 🔑 子代理派生入口；将 model/capability 一次解析为不可变 `LlmProfile + LlmConfig` 路由快照，并透传 `max_rounds + WorkingDirectory + ConfigurationAgentInstanceId + DelegationDepth + ParentExecutionDeadlineUtc` 执行快照；同步委派由 Manager 统一保留父级收尾时间 |
| `Tools/BuiltIns/Agents/` | `AgentSleepTool.cs` | 心跳睡眠控制（max 86400s） |
| `Tools/BuiltIns/Search/` | `SmartSearchTool.cs` | 🔑 语义代码搜索 — 薄包装子代理，三层搜索协议，MainAgentOnly，Explorer 模型 |
| `Tools/BuiltIns/Search/` | `AnySearchSearchTool.cs` | 通用搜索（Web/文档） |
| `Tools/BuiltIns/Search/` | `GitHubSearchTool.cs` | GitHub REST API 搜索 |
| `Tools/BuiltIns/Sessions/` | `SmartQuerySessionLogsTool.cs` | 🔑 语义会话日志查询 — 薄包装子代理，MainAgentOnly，Explorer 模型 |
| `Tools/BuiltIns/Sessions/` | `QuerySessionLogsTool.cs` | 会话日志查询（支持 exclude_heartbeat） |
| `Tools/BuiltIns/Sessions/` | `QuerySessionsTool.cs` | 会话列表查询 |
| `Tools/BuiltIns/SmartWorkflow/` | `SmartWorkflowToolBase.cs` + `Smart*Tool.cs` | 🔑 7 个角色化 Smart 工作流工具；统一 `task`、角色模型和父 deadline/120 秒收尾预留；单次调用默认上限 3600 秒，`smart_plan` 为 3600 秒/48 轮只读规划，`smart_explore` 为 1800 秒/32 轮只读探索，二者均禁止嵌套委派且使用显式只读工具白名单；共享五段顶层报告合同 |
| `Tools/BuiltIns/Management/` | `LlmResourcePoolTool.cs` | LLM 资源池查询（Provider + Model + 能力标签），MainAgentOnly |
| `Tools/BuiltIns/Management/` | `AgentStateTool.cs` | Agent 私有状态自维护：检查、诊断、读取、原子更新白名单 Markdown；Low 风险且只使用当前 `AgentInstanceId` |
| `Tools/BuiltIns/Http/` | `HttpFetchSkill.cs` | HTTP 请求 |
| `Tools/BuiltIns/Shell/` | Shell 工具 | 终端命令执行（支持 tail_lines）；执行边界强制应用不可绕过的宿主进程保护 |
| `Tools/BuiltIns/Terminal/` + `Services/TerminalSecurity.cs` | 后台终端与命令策略 | Normal/YOLO 共享宿主安全不变量；任意进程终止命令必须改用当前会话持有 job id 的 `terminal_cancel` |
| `Tools/BuiltIns/CodeIntelligence/` | `CodeQueryTools.cs` | 代码索引查询 |
| `Tools/BuiltIns/Documents/` | `ReadOfficeDocumentTool.cs` | Office 文档读取（NPOI 2.8.0） |
| `PuddingAgent/Tools/` | `ImageReaderTool.cs` | 文本主 Agent 的显式视觉回退：读取受控本地 PNG/JPEG/WebP 路径，以 `vision` 能力解析模型并返回文字观察；不会替换主 Agent 自身 Provider/Model |

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
| `PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.ts` | Chat 页面组合与跨域协调入口（1,314 行）；P0/P1 业务逻辑已委托专用 hook，并通过兼容导出维持现有调用方 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useWorkspaceAgentSelection.ts` | Workspace/Agent 选择域：路由解析、列表加载、默认 Agent 创建、选择项投影、`creatingSession` 与一次性主会话重建抑制 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useSessionCatalog.ts` | Session 目录与身份 ref 所有者：列表刷新、主/选中会话、重命名、删除、归档 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useSessionSelection.ts` | Session 切换事务：取消旧请求、加载历史、恢复 replay、同步 route 与 unread；通过分组端口协调其他域 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useSessionHistoryProjection.ts` | 持久消息到 `ChatTurn` 的投影与安全历史对账；完成后同步事件 cursor |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useSessionEventBuffers.ts` | delta/thinking 批处理缓冲与 timer 所有者 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useSessionEventConnection.ts` | Conversation SSE 连接、健康重连、在线恢复与 replay poll 生命周期 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useSessionEventReplay.ts` | 按 sequence/cursor 的缺口恢复、条件补偿与最新 Turn replay；分页最大 sequence 必须以有限哨兵归并并单调推进，不能以 `NaN` 为 reduce 初值；对仍 active 的子代理低频读取 canonical session 状态，校正有界 bootstrap 遗漏的历史终态 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useSessionEventProjection.ts` | 持久/实时事件到 Turn、SubAgent、usage、cache 与 working-agent 状态的统一投影 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useMessageSend.ts` | 发送事务：乐观 Turn、Outbox、202 acceptance 身份收敛、SSE/replay 衔接与失败回收 |
| `PuddingPlatformAdmin/src/pages/chat/components/IntentConsole.tsx` | Composer 图片暂存边界：多选、Ctrl+V/拖放、发送前预览与移除；提交时先上传全部图片，再用单次消息携带 `visionArtifactIds` |
| `PuddingPlatformAdmin/src/pages/chat/components/UserMessageBubble.tsx` + `types.ts` + `viewport/messageProjection.ts` | 用户多图气泡与历史/实时元数据投影；按 artifact id 渲染图片画廊并保留单图兼容字段 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useMessageInteractionQueue.ts` | Composer 输入、服务端命令队列、steering 队列、快捷键与定时刷新 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useCompaction.ts` | Compaction lifecycle、手工 compact、生命周期 Turn 与压缩后会话切换 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useMessageHistoryPagination.ts` | 历史分页状态、旧消息前插与 projector 绑定 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useWorkspaceNotifications.ts` | Workspace 通知 SSE、未读计数与通知流生命周期 |
| `PuddingPlatformAdmin/src/pages/chat/hooks/useChatModals.ts` / `useChatRuntimeEvents.ts` | Chat Modal 状态与有界 interaction runtime event 通道 |
| `PuddingPlatformAdmin/src/pages/chat/types/chatStateTypes.ts` | Chat 主 hook 的共享常量、跨模块状态类型与 `UseChatStateReturn` 接口 |
| `PuddingPlatformAdmin/src/pages/chat/utils/chatStateUtils.ts` | Chat 状态纯转换、格式化与 replay/cursor 判定的无 React 边界 |
| `PuddingPlatformAdmin/src/pages/chat/utils/chatDiagnostics.ts` | ChatDiag 有界序列化、错误终态识别与可检索 Markdown 格式化、控制台记录和 sessionStorage 持久化边界；诊断失败不得影响聊天流程 |
| `PuddingPlatformAdmin/src/pages/chat/utils/sessionEventReplay.ts` | 持久事件 wrapper 规范化与 replay page HTTP/404 边界 |
| `PuddingPlatformAdmin/src/pages/chat/client/chatClientStore.ts` | Agent conversation 查询缓存与轮询收敛；终态 cursor 暂时领先消息读模型、快照仍以 user 结尾时禁用条件 GET，避免相同 cursor 的 304 固化不完整投影 |
| `PuddingPlatformAdmin/src/pages/chat/viewport/useMessageViewportRuntime.ts` | 消息视口唯一滚动权威；按帧合并 scroll，按 message id 缓存行高，80/200 阈值自适应选择正常流/virtualizer，历史前插恢复 DOM 锚点，真实容器负责贴底 |
| `PuddingPlatformAdmin/src/pages/chat/components/MessageList.tsx` | 消息列表渲染与 viewport overlay；优先按 canonical `turnId` 合并用户/助手投影并替换本地运行壳，同时保留 canonical 用户消息 metadata 以在刷新后恢复图片/语音模态；React/virtualizer row key 使用真实 message id，避免同一 Turn 多消息复用 key；canonical conversation 落后时保留本地 SSE 终态回复，并抑制同一命令的陈旧 activeRun 占位；不直接拥有滚动策略 |
| `PuddingPlatformAdmin/src/pages/chat/components/AgentMessageBubble.tsx` | 主 Agent 消息呈现边界；正文、流式输出与首 Token 等待态共享同一气泡壳层，运行过程仅消费投影后的 timeline |
| `PuddingPlatformAdmin/src/pages/chat/components/ChatMain.tsx` | Chat 工作台布局壳层；`chatBody`/开发面板/历史搜索保持合法 JSX 嵌套，并展示 SSE 重连提示 |

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
| `Services/SessionStateManager.cs` | 遗留 Session 状态与 SSE/WS 推送；`session_sub_agents` 作为按 SubSessionId 唯一的当前状态投影，池化复用时用原子 UPSERT 重置终态并拒绝跨父会话重绑定 |
| `Services/SessionStateStore.cs` | 🔑 会话状态持久化 — 重启后恢复（data/sessions/{id}.json） |
| `Services/SessionCompactionEventEmitter.cs` | 自动压缩生命周期适配器；只写 canonical Conversation Event Store |
| `Services/SessionRedirectStore.cs` | 会话重定向（压缩后新旧 Session 映射） |
| `Services/PlatformApiClient.cs` | 平台 API 客户端（内部调用） |
| `Services/ChatHistoryService.cs` | 聊天历史查询 |
| `Services/AgentLLMConfigResolver.cs` | Agent 的 LLM 配置解析 |
| `Services/AgentRuntimeProfileResolver.cs` | Agent 执行配置唯一解析边界；从实例 manifest + `config/llm.json` 读取快照，并用 `llm.providers.json` 补齐连接配置 |
| `Services/WorkspaceAgentFileService.cs` | Agent 实例定义写入权威；创建/管理端更新同步维护 manifest、Markdown 与 `config/llm.json`，并实现 `IAgentSelfMaintenanceService` 的受控自维护写入 |
| `Services/VisionArtifactStorageService.cs` + `Services/VisualArtifactReference.cs` + `Services/VisualArtifactResolverBridge.cs` | 无状态 singleton 视觉制品存储/解析边界；同时提供 LLM 可消费引用与经过 workspace 根目录校验的受控本地路径，供文本主 Agent 调用 `image_reader` |
| `Services/SubAgentManager.cs` | 子代理统一调度边界；按父 deadline 归一化子 deadline，同步委派额外保留默认 120 秒父级收尾窗口并在不足时拒绝创建 run，把并发门等待计入预算；每次执行创建新 run，再投影可复用 SubSessionId 当前状态，投影失败时终结 run |
| `Services/SubAgentPool.cs` | 池化子代理生命周期；create/自动创建只原子预留稳定 SubSessionId，execute 才调用 `ExecuteSyncAsync`，避免隐藏异步 run 与首轮双执行 |
| `Services/FileSubAgentRunStore.cs` | 子代理运行审计与终态仲裁；`run.json/input.json/run.created` 持久化精确 `ExecutionDeadlineUtc`，终态提交前从 events.jsonl 合并真实轮次/工具/耗时/失败统计，先写自带 `run_id` 的事件再按持久游标投影 canonical Conversation Event |
| `Services/SubAgentConversationProjectionWorker.cs` | 启动时将上一进程遗留的非终态 run 仲裁为 `interrupted`，随后扫描 run archive 投影积压 |
| `Services/ConversationAcceptanceStore.cs` | 原子受理：Message + Batch + Turn + Command + Event 单事务 |
| `Services/ExecutionCommandReader.cs` | Command 稳定执行引用的只读适配器；不拥有任何状态转换 |
| `Services/ConversationEventStore.cs` | Conversation Sequence 分配、事件追加、历史读取和 `subagent.*` 类型前缀补读；事件分页读取 `limit + 1` 条并准确计算 `hasMore` |
| `Services/ConversationProjector.cs` | Event Store 到查询模型的 checkpoint 投影；除按稳定身份物化 ChatMessages 外，还将带不可变 Provider/Model 归因的 `usage.recorded` v2 必达写入 Token 明细账本 |
| `Services/ConversationProjectionWorker.cs` | 按持久 Conversation Head/Checkpoint 扫描投影积压；与具体事件写入者解耦并支持重启追平 |
| `Services/TokenUsageRecorder.cs` | Token 明细账本唯一增量写入器；计费用量事实使用 `RecordRequiredAsync` 并由拥有方等待完成，只有非权威遥测可使用 best-effort `RecordAsync` |
| `Services/TokenUsageSchemaBootstrapper.cs` | Platform SQLite 的 Token 用量 Schema 升级边界；启动时幂等补齐 `TokenUsageEvents.ParentSessionId` 与索引，DDL 失败直接阻止启动，避免 EF 模型与旧数据库静默失配 |
| `Services/ConversationCommandSchemaBootstrapper.cs` | Platform SQLite 的可靠命令 Schema 升级边界；启动时通过 `PRAGMA table_info` 幂等补齐 `chat_execution_commands.metadata_json`，避免已有数据库在 Turn 受理事务中因 EF 模型漂移返回 500 |
| `Services/TokenUsageRebuildService.cs` | 从 Conversation Event Store 的 `usage.recorded` v2 重建 `agent_llm` 明细，再从完整账本重建月度汇总；禁止猜测历史路由，仅在同一事务中替换可成功重建的 sourceId，未归因事实不得触发删除 |
| `Services/AgentChat/ChatExecutionWorker.cs` | Worker v5 — 通过 IExecutionLeaseStore 原子 CAS 领取，透传 Lease 到 Coordinator |
| `Services/AgentChat/ExecutionRunCoordinator.cs` + `ExecutionWatchdogPolicy.cs` | Execution Kernel 入口 — 接收 Lease，冻结 24h 硬上限并运行 1h 滑动无进展看门狗，读取 Command 稳定引用，组装 Snapshot，执行 Runtime，向全部输出事件贯穿 assistant MessageId，仲裁 `execution_timeout/execution_stalled/cancelled` 并提交 Journal；附图不会改写主模型路由，文本模型收到受控本地路径与 `image_reader` 提示，视觉模型继续由 DirectLlm 直接消费；终态写入失败时执行 fenced 基础设施兜底 |
| `Services/AgentChat/TurnOutputChunker.cs` | Runtime delta 聚合边界；持久事件必须持有独立 JsonElement，非 delta 事件必须原样保留 Runtime SchemaVersion |
| `Services/AgentChat/AgentConversationProjectionService.cs` | Chat 历史与活动 Run 查询投影；以 `conversation_events` 为过程事实源，按 `ChatMessages.turn_id` 或 command 的 user/assistant message 映射补齐 canonical `turnId`，再以稳定 `messageId/runId` 关联过程事实 |
| `Services/AgentChat/AgentRunProjectionService.cs` | Agent 联系人当前状态投影；状态与 cursor 均来自 canonical Conversation Event sequence，失败/取消/LeaseLost 终态结束后回到 idle，失败详情留在 Turn 事件 |
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
| `PuddingPlatformAdmin/src/pages/chat/reducer/subAgentReducer.ts` | 子代理 UI 唯一纯投影：按 eventId 幂等折叠 bootstrap/replay/live 的 created/round/LLM/tool/terminal，并允许 canonical session status 只把 active run 推进到终态；拒绝旧事件或快照把终态降级为 running |
| `PuddingPlatformAdmin/src/pages/chat/components/SubAgentActivityDock.tsx` | 子代理右上角悬浮运行坞与详情检查器；显示活动阶段、模型消息、脱敏工具输入输出、轮次、预算和有界事件时间线；成功/异常终态分别停留 12/30 秒后自动隐藏，完整结果仍按 Run ID 从归档 `output.md` 懒加载 |
| `PuddingPlatformAdmin/src/pages/chat/viewport/messageProjection.ts` | 纯消息虚拟项投影；只生成用户、主 Agent、系统消息和历史加载项，不投影子代理 run，避免多子代理调用污染文档流 |
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
| `Contracts/PuddingToolContracts.cs` | 🔑 工具契约（ToolAttribute；SubAgentExposure 的 MainAgentOnly/DelegatedSubAgent；配置所有者、委派开关和深度执行上下文） |

### 会话与运行时
| 文件 | 用途 |
|------|------|
| `Abstractions/ISessionStateManager.cs` | 会话状态管理接口（含 Restore 方法） |
| `Platform/SubAgentSessionId.cs` | 子代理会话 ID 唯一生成器；池预留和普通调度共用，不通过创建空 run 获取身份 |
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

### 4. Smart* 工具 — 子代理薄包装与有界委派模式
```
ExecutionRunCoordinator（Turn 启动时冻结 24h parent hard deadline，并注册 1h meaningful-progress 窗口）
  → Turn / Runtime / Tool contract 逐层透传（只能收紧）
主 Agent 调用 smart_plan(task="...")
  → SmartPlanTool（上限 3600s / 48 rounds，父级预留 120s）
    → Planner 使用显式只读 capability whitelist
    → 不写计划文件、不执行 shell、不继续派生 Smart 子代理
主 Agent 或获授权子代理调用 smart_explore(task="...")
  → SmartExploreTool（上限 1800s / 32 rounds，DelegatedSubAgent）
    → Explorer 使用同类只读 whitelist，不能继续派生子代理
  → 其他 Smart 工具保持 MainAgentOnly
  → PuddingToolRegistry 在执行边界强制 exposure + allow + depth 三项检查

任意 Smart 工具 → spawn_sub_agent(sync, model 或 capability)
    → SubAgentTool.ResolveChildLlmRouteAsync()
      → ILlmResolver（唯一读取 data/config/llm.providers.json）
        → 唯一确定 Provider/Model + LlmConfig
      → ConfigurationAgentInstanceId 保持根 Agent 配置所有者；
        AgentInstanceId/ParentAgentId 表示当前临时执行身份
      → SubAgentTool 仅补充调用语义 ProfileId=subagent.conscious、Role=conscious
    → ISubAgentInvocationService（只映射，不重新解析）
      → SubAgentManager.ValidateLlmRoute()
      → 同步/异步统一以 parent deadline 收紧预算
      → 并发门等待计入同一个绝对 deadline
      → 非池化：生成 SubSessionId；池化：复用预留 SubSessionId
      → 每次执行创建全新 runId
      → SessionStateManager 原子 UPSERT SubSessionId 当前状态（首次创建/复用重置）
      → Runtime 派发
      → RuntimeExecutionIdentity 派生 child execution
      → AgentExecutionService 发出 round/LLM/tool 运行事实
      → FileSubAgentRunStore events.jsonl
      → SubAgentConversationProjectionWorker
      → canonical Conversation Event Store / resumable SSE
      → subAgentReducer / Chat 子代理运行面板
        → RuntimeDispatchRequest.LlmProfile + LlmConfig
    → 子代理执行角色协议并返回 canonical 详细报告
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
- 最大轮次、最大耗时、最大工具调用进入不可变执行快照；默认父 Turn 最大耗时为
  86400 秒最终安全上限，Runtime 以实例值和平台 `AgentExecutionGuardrails` 中较小者为
  有效硬上限；正常停滞由 3600 秒滑动 meaningful-progress 窗口终结

---

## 🧠 Subconscious — 潜意识自改进系统

> 后台异步自循环，5 条专业化管道 + 完整作业队列 + 运行时控制 + 多层可观测性。

### 触发入口（3 条路径）

| 组件 | 文件 | 用途 |
|------|------|------|
| **SubconsciousConsolidationHook** | `PuddingRuntime/Services/Background/SubconsciousConsolidationHook.cs` | AgentLoop Hook：每轮对话结束 → `Channel<ConsolidationJob>` 入队 |
| **SubconsciousJobScheduler** | `PuddingRuntime/Services/Background/SubconsciousJobScheduler.cs` | 定时调度：9 种跳过条件（空闲冷却/并发限制/预算耗尽/DryRun...） → `TryLeaseNextAsync` |
| **SubconsciousTriggerTool** | `PuddingRuntime/Tools/BuiltIns/Management/SubconsciousTriggerTool.cs` | 手动触发：`auto_dream` / `extract_patterns` / `improve_skills` / `consolidate` / `all` |
| **SubconsciousWorkerService** | `PuddingRuntime/Services/Background/SubconsciousWorkerService.cs` | ⚠️ HOSTED-DISABLED — 定时后台服务（已注释） |

### 作业队列

| 组件 | 文件 | 用途 |
|------|------|------|
| **SubconsciousJobQueue** | `PuddingMemoryEngine/Services/SubconsciousJobQueue.cs` | 持久队列：Enqueue(幂等键) → Lease(超时) → Complete/Fail/DeadLetter；遥测指标 |
| **SubconsciousJobEntity** | `PuddingMemoryEngine/Entities/SubconsciousEntities.cs` | EF 实体：JobId/Type/IdempotencyKey/Status/RetryCount/LeaseUntil |
| **SubconsciousJobLogEntity** | 同上 | 作业日志：SessionId/Status/FactsExtracted/FactsMerged/ElapsedMs/ErrorMessage |

### 5 条专业化管道

| 管道 | Orchestrator 方法 | 描述 | 报告 |
|------|------|------|------|
| **事实提取** | `ConsolidateAsync` | LLM → 事实/偏好 → Jaccard≥0.8 去重合并 → MemoryFacts/Preferences → Library | `SubconsciousJobLogEntity` |
| **记忆整理** | `AutoDreamAsync` | Flash 分析 Library 快照 → merge/archive/delete（≤5 op, 30d 过期） | `AutoDreamReport` |
| **经验→SKILL** | `ExtractPatternsAsync` | 扫描会话 → 检测黄金路径 → 3 条件过滤（passing/named-failure/ruled-out） → SKILL.md | `PatternExtractionReport` |
| **Skill 改进** | `ImproveSkillsAsync` | 列出 auto-generated 技能 → Flash 逐条评估 → 修补 + Bump 版本 | `SkillImprovementReport` |
| **增强召回** | `RecallAugmentedAsync` | LLM 直接阅读全量 MemoryFacts + Preferences，自主判断相关性（不做 LIKE/FTS5） | `RecallDiagnostics` |

```text
ConsolidateAsync:  消息 → LLM抽取 → ExtractionPayload(JSON) → 去重(Jaccard) → Facts/Prefs → Library
AutoDreamAsync:    MemorySnapshot → LLM规划(AutoDreamPlan) → merge/archive/delete
ExtractPatternsAsync: 会话消息 → LLM检测(PatternCandidate[]) → 3条件过滤 → promote(SKILL) / demote(笔记) / skip
ImproveSkillsAsync: auto-generated技能 → LLM评估(SkillEvaluation) → 修补 → Bump版本 → 写回Library
RecallAugmentedAsync: 用户消息 + 全量Facts → LLM编译 → 截断(maxTokens*4 chars)
```

### 增强召回管道（Track 1）

| 组件 | 文件 | 用途 |
|------|------|------|
| **SubconsciousRecallPipeline** | `PuddingRuntime/Services/SubconsciousRecallPipeline.cs` | 关键词提取(纯算法) → 混合搜索(记忆库→日摘要→日志) → Flash 判断排名(单次调用, Temp=0/Seed=42) → 截断注入(≤5条, ~2K tokens)。Session 级状态：话题转换检测 + 连续不召回兜底（每5轮强制）、30s 内存缓存 |

### 运行时控制与可观测性

| 组件 | 文件 | 用途 |
|------|------|------|
| **SubconsciousRuntimeControlService** | `PuddingRuntime/Services/Background/SubconsciousRuntimeControlService.cs` | Pause/Resume + GetSnapshot（队列状态+调度配置+诊断） |
| **SubconsciousDiagnosticLog** | `PuddingRuntime/Services/Background/SubconsciousDiagnosticLog.cs` | JSONL 诊断日志：按日分片、1MB 滚动、200 文件保留 |
| **SubconsciousPlanGenerationService** | `PuddingRuntime/Services/SubconsciousPlanGenerationService.cs` | Dry-run 计划生成 → MemoryMaintenancePlan → 校验 → 遥测（Activity + Metric） |
| `/health/subconscious` | `PuddingAgent/Program.cs` | HTTP 健康端点：DB 查询最近 JobLog |
| **SubconsciousRuntimeControlSnapshot** | `PuddingCore/Platform/SubconsciousDtos.cs` | 一站式快照：State/IsPaused/QueueStats/Scheduling/Diagnostics |
| **SubconsciousJobQueueStats** | 同上 | 队列统计：Pending/Retrying/Processing/Completed/DeadLetter + per-workspace/per-session |
| **SchedulingSkipReasons（9种）** | 同上 | Disabled/DryRun/Cooldown/WorkspaceLimit/GlobalLimit/SessionLimit/BudgetExhausted/BackoffNotElapsed/NoEligibleJob |
| 遥测指标 | — | `subconscious_job.enqueue` / `lease` / `complete` / `schedule_skip` |
| 流事件 | — | `StreamingEventBus`：SubconsciousLoad / SubconsciousThink / SubconsciousDone |

### 配置

| 组件 | 文件 | 用途 |
|------|------|------|
| **SubconsciousOptions** | `PuddingCore/Configuration/SubconsciousOptions.cs` | 开关：EnableLegacyConsolidationHook / DebugApiEnabled |
| **SubconsciousSchedulingOptions** | 同上 | 调度：IdleCooldown(60s) / MaxGlobalConcurrent(1) / MaxRetryAttempts(3) / BudgetWindow(60min) / MaxJobsPerWorkspacePerHour(20) |

### 数据流水线

```text
触发:
  AgentLoopHook → Channel<ConsolidationJob>
  SubconsciousJobScheduler → ISubconsciousJobQueue.LeaseNextAsync() → Worker → Orchestrator
  SubconsciousTriggerTool → Orchestrator（手动调试）

Orchestrator:
  PuddingMemoryEngine/Services/SubconsciousOrchestrator.cs（1614 行）
  依赖: IMemoryLibrary, IMemoryEngine, IMemoryLlmClient, IEmbeddingService,
        IMemoryDbContextFactory, IMemoryLibrarian, IStreamingEventBus

可观测性栈:
  ILogger → 结构化日志（Debug~Error, 含 SessionId/WorkspaceId）
  SubconsciousDiagnosticLog → JSONL 按日归档
  SubconsciousRuntimeControlSnapshot → 队列+调度+诊断 一站式
  /health/subconscious → HTTP 健康检查
  TelemetryMetricSink → enqueue/lease/complete/schedule_skip 指标
  RuntimeActivitySink → memory_maintenance_plan.validate 活动
  RecallDiagnostics (AsyncLocal) → Rounds/Queries/FoundItems/Latency
```

---

## 注意事项

1. **双轨工具系统**: 正在从 `IAgentSkill`（Legacy）迁移到 `IPuddingTool`（新），两套接口并存
2. **双轨记忆系统**: 传统图书馆（Book/Chapter）+ 结构化事实库（Fact）并存，未来融合
3. **Smart* 工具有界委派模式**: 七个工具统一 `task` 合同；单次共享上限 3600 秒，`smart_plan=3600 秒/48 轮`，`smart_explore=1800 秒/32 轮`；除 `smart_explore=DelegatedSubAgent` 外保持 `MainAgentOnly`，唯一嵌套边为 `smart_plan → smart_explore`，并由 capability whitelist、委派开关和深度硬门共同防循环
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

---

## 🆕 新增模块 (2026-07-22)

### 多模型协作与 Fallback
| 文件 | 用途 |
|------|------|
| `Tools/BuiltIns/SmartWorkflow/SmartWorkflowToolBase.cs` | 7 个 Smart 工具的 Fallback 链：Qwen → DeepSeek Pro → Flash；每个工具可覆写 `FallbackModelIds`；`IsTransientSmartFailure` 判定瞬态故障触发降级 |
| `PuddingCore/Abstractions/ILlmConfigService.cs` (`ProviderCompatConfig`) | 6 个 Provider 兼容性开关；K3 Gateway 适配：`maxTokensField→max_tokens`、`requiresStringContent`、`useReasoningEffort`、`supportsUsageInStreaming→false`、`requiresReasoningContentInToolMessages` |
| `PuddingGateway/Services/OpenAiLlmGateway.cs` | `BuildRequestBody` 中消费 6 个 compat 字段 |

### 大文件支持
| 文件 | 用途 |
|------|------|
| `PuddingPlatform/Services/FileChunkService.cs` | 大文件分块读取基础 — 支持滑动窗口流式读取 >100KB 文件；为后续大文件工具操作提供基础 |

### Chat 前端 — 交互体验优化 (Phase 1+2+3)
| 文件 | 优化 | 说明 |
|------|------|------|
| `hooks/useSessionEventConnection.ts` | SSE 断流状态条 | `reconnectCountRef` → ChatMain Alert banner |
| `components/AgentMessageBubble.tsx` | TTFB + 停滞检测 + 语音气泡 | 3s/10s 阈值；15s 琥珀脉冲；`modality='voice'` 波形 |
| `components/MessageItem.tsx` | 代码懒高亮 + Settle FLIP | 流式跳过 Prism；200ms transform 平滑切换 |
| `hooks/useTypewriterStreaming.ts` | 增量扫描 + 自适应打字机 | O(n)→O(delta)；48-200 chars 动态缓冲 |
| `viewport/useMessageViewportRuntime.ts` | 高度缓存 + 滚动锚定 | Map 缓存；500ms 挂起；rAF×2 重试 |
| `components/MessageList.tsx` | 未读 badge + 诊断导出 + 骨架屏 | 红点计数；Alert 诊断复制；Skeleton |
| `components/MessageGroup.tsx` | 发送失败保护 | 红色边框 + 复制内容 + 重试发送 |
| `styles/animations.styles.ts` | 动画复活 | 5 keyframes：messageIn/stepIn/blockCondense/glowSettle/charFadeIn |
| `components/ChatMain.tsx` | React.lazy 懒加载 | DevPanel/SubAgentDock/HistorySearchModal 延迟加载 |
| `components/IntentConsole.tsx` + `VoiceConversationPanel.tsx` | 语音面板集成 | 麦克风 → 530 行语音面板 (ASR+TTS) |

### Chat 前端 — 多模态图片支持
| 文件 | 用途 |
|------|------|
| `components/UserMessageBubble.tsx` | 用户多图气泡：`visionArtifactIds` → GET API → `<img>` 画廊；加载失败回退 |
| `components/IntentConsole.tsx` | 图片暂存：多选/粘贴/拖放 → `onSendWithMetadata(visionArtifactId)` |
| `hooks/useMessageSend.ts` | `submitConversationTurn` 携带 `metadata: { visionArtifactId }` |
| `hooks/useSessionHistoryProjection.ts` | `toTurnsFromHistory` 映射 `item.metadata` → 历史图片渲染 |
| `client/api.ts` | `ChatMessageDto.metadata` + `SubmitConversationTurnRequest.metadata` |
| `PuddingPlatform/Services/ConversationCommandSchemaBootstrapper.cs` | SQLite Schema 升级：`PRAGMA table_info` 幂等补齐 `metadata_json` 列 |

### 死代码审计 (2026-07-22)
| 文件 | 状态 | 说明 |
|------|:--:|------|
| `PuddingMemoryEngine/Class1.cs` | 🗑️ 待删除 | 空白占位类，零引用 |
| `PuddingCore/Swarm/` (10 文件) | 🗑️ 待归档 | Swarm 原型，DI 中零引用 |
| `PuddingCoreTests/Test1.cs` | 🗑️ 待删除 | 占位测试 |
| `PuddingWebApiTests/Test1.cs` | 🗑️ 待删除 | 占位测试 |
| `ILLMConfigResolver.cs:13-32` | `[Obsolete]` | 旧版 ResolveAsync 方法 |
| `AgentTemplateProvider.cs` | `[Obsolete]` | 已迁移到 manifest |
