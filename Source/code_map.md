# PuddingAgent CodeMAP

> 最后更新: 2026-07-11 | 维护原则: 仅收录核心常用类，不追求全覆盖

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
| `LlmProviderEntity.cs` / `LlmModelEntity.cs` | LLM 提供者/模型 |
| `SessionEventLogEntity.cs` | 会话事件日志 |

### 核心服务
| 文件 | 用途 |
|------|------|
| `Services/SessionStateManager.cs` | 🔑 会话状态管理（SSM，SSE/WS 推送） |
| `Services/SessionStateStore.cs` | 🔑 会话状态持久化 — 重启后恢复（data/sessions/{id}.json） |
| `Services/SessionCompactionEventEmitter.cs` | 压缩事件发射器 |
| `Services/SessionRedirectStore.cs` | 会话重定向（压缩后新旧 Session 映射） |
| `Services/PlatformApiClient.cs` | 平台 API 客户端（内部调用） |
| `Services/ChatHistoryService.cs` | 聊天历史查询 |
| `Services/AgentLLMConfigResolver.cs` | Agent 的 LLM 配置解析 |
| `Services/SubAgentManager.cs` | 子代理管理 |

### 消息系统
| 文件 | 用途 |
|------|------|
| `Services/MessageFabric/MessageSystem.cs` | 消息系统核心 |
| `Services/MessageFabric/MessageRouter.cs` | 消息路由（Topic → Channel → Room） |
| `Services/MessageFabric/MessageFabricStore.cs` | 消息持久化 |

### API Controllers（核心）
| Controller | 用途 |
|------------|------|
| `Api/SessionEventsController.cs` | 🔑 Session SSE/WS 事件流（前端连接点） |
| `Api/SessionApiController.cs` | Session CRUD + `/compact` |
| `Api/AgentChatApiController.cs` | Agent 聊天 API |
| `Api/MessageApiController.cs` | 消息 API |
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
| `Data/MemoryLibraryDbContext.cs` | 记忆数据库 DbContext |
| `Data/MemoryLibraryDbInitializer.cs` | 数据库初始化 + Schema 迁移 |
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

### Token 预算
| 文件 | 用途 |
|------|------|
| `Contracts/ContextCompactionContracts.cs` | 上下文压缩契约（含 CapacityPrediction 模型） |
| `Contracts/PrefixCacheContracts.cs` | Prefix Cache 契约（Churn 归因） |

---

## 关键流程（调用链路）

### 1. 用户消息 → Agent 响应
```
前端 HTTP POST → AgentChatApiController
  → AgentExecutionService.ExecuteAsync()
    → ContextPipeline.BuildContextAsync()  // 组装上下文
      → ContextAssemblyService            // System Prompt + 历史 + 记忆
      → ContextWindowManager.EnsureCapacity()  // token 驱动裁剪/压缩
    → LlmInvocationService.InvokeAsync()  // 调用 LLM
      → IRuntimeLlmClient.CompleteAsync() // HTTP → LLM API
    → ToolInvocationService.InvokeAsync() // 如果有工具调用
    → CompletionPolicy.ShouldContinue()   // 判断是否继续循环
  → SSM.AppendAsync() → SSE/WS → 前端
```

### 2. Token 预算与自动压缩
```
ContextWindowManager.EnsureCapacity()
  → ContextHealthEvaluator.Evaluate(usedTokens, maxBudget)
    ├── < 60% → 不裁剪
    ├── 60-80% → TrimHistory（token 驱动，修剪到 70%）  // 动态计算 maxMessages = budget/2500
    └── >= 80% → TryAutoCompactAsync()  // LLM 压缩
      → ContextCompactionService.CompactAsync()
      → CompactionEventEmitter.EmitAsync() → SSE → 前端
  → CapacityPrediction: 剩余 tokens + 预计几轮后触发各阈值
```

### 3. Smart* 工具 — 子代理薄包装模式
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
8. **SSE 推送**: `SessionStateManager` → `SessionEventsController.EventsStream()` → 前端，按 sessionId 隔离
9. **工具权限**: `ToolPermissionPolicyService` 检查安全区，高危工具需 `InMemoryToolApprovalService` 审批
