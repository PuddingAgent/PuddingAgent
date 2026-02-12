# 29 ADR-028 记忆图书馆基础设施重构方案

> 状态：**partially-implemented**（基础结构已部分落地；workspace scope、schema 初始化、Pointer 泛化、Librarian 分层、source-aware recall 和测试验收由 ADR-029 收敛）
> 日期：2026-05-20
> 纠偏：ADR-029 (2026-05-21)，P0/P1 修复已实施
> 范围：PuddingMemoryEngine、IMemoryLibrary、MemoryRecallService、SubconsciousOrchestrator、Memory Tools、ContextPipeline L6
> 前置：[12记忆图书馆基础设施](12记忆图书馆基础设施.md)、[13记忆与会话数据层](13记忆与会话数据层.md)、[15潜意识LLM子代理系统ADR](15潜意识LLM子代理系统ADR.md)、[28ADR-027Hook事件潜意识学习闭环ADR](28ADR-027Hook事件潜意识学习闭环ADR.md)、[task38-subconscious-memory-engine](../Tasks/task38-subconscious-memory-engine.md)、[task42-hook-event-subconscious-learning](../Tasks/task42-hook-event-subconscious-learning.md)

---

## 1. 背景

Pudding 已经有第一版记忆图书馆基础设施：

- `IMemoryLibrary` 提供 Library、Book、Chapter、Pointer、Tag、FTS、Vector、Branch 等接口。
- `MemoryLibrary` 使用 SQLite + EF Core + FTS5 实现存储与检索。
- `MemoryLibraryConvenience` 提供 `UpsertExperienceAsync`、`SmartSearchAsync` 等 LLM 友好入口。
- `MemoryRecallService` 融合 Library、MemoryFacts、MemoryPreferences 做上下文召回。
- `SubconsciousOrchestrator` 可以从会话中抽取事实和偏好，并写入长期记忆。

现有方向是对的，但边界开始混淆：

- 图书馆 Core 和上层整理策略没有完全分开。
- Convenience 层同时承担便利操作、自动建书、自动指针、深度探索和部分业务路由。
- 运行时检索缺少强 workspace scope，有跨场景召回风险。
- `SourceSessionId` 不足以表达“会话片段、事件、文件、URL、备忘录、子代理 run archive”等来源。
- Tag 树仍是 `BookIndexes.TagPath` 字符串聚合，无法支持节点级治理。
- `MemoryFacts/Preferences` 与 `MemoryLibrary` 双轨并存，短期可接受，长期会造成召回和写入重复。

本 ADR 决定以渐进方式重构记忆图书馆，使其成为树状数据基础设施，而不是业务策略容器。

---

## 2. 核心决策

### ADR-028-A：Memory Library Core 是低智能基础设施

**决定**：`MemoryLibrary Core` 只负责确定性数据操作，不调用 LLM，不执行业务判断。

Core 允许：

- Library / Book / Chapter CRUD。
- TreeNode / BookMount 管理。
- Pointer / SourceReference 管理。
- FTS / Vector / Tree / Pointer 查询。
- Archive / delete / version / access metadata。

Core 禁止：

- 判断什么值得记住。
- 自动决定写入哪本书。
- 调用潜意识 LLM。
- 自动衰减、合并、拆分、重命名。
- 决定哪些记忆应注入 prompt。

原因：

- 图书馆应该像数据库和文件系统：稳定、可审计、可测试。
- 智能整理会变化，基础设施不能随提示词漂移。
- Core 必须能在无 LLM、测试环境、离线迁移中可靠运行。

### ADR-028-B：新增 MemoryLibrarian 承担智能整理

**决定**：新增 `IMemoryLibrarian`，作为潜意识 LLM 与图书馆 Core 之间的整理层。

职责：

- 把 `ExperiencePackage` 转成一组结构化 library mutations。
- 选择或创建 Book。
- 创建 Chapter。
- 创建 SourceReference 和 Pointer。
- 挂载 TreeNode。
- 生成树维护提案。
- 检测重复、合并候选和冲突候选。

建议契约：

```csharp
public interface IMemoryLibrarian
{
    Task<ExperienceWriteResult> IngestExperienceAsync(
        MemoryIngestionRequest request,
        CancellationToken ct = default);

    Task<IReadOnlyList<MemoryTreeOperation>> PlanTreeMaintenanceAsync(
        string workspaceId,
        string libraryId,
        CancellationToken ct = default);

    Task ApplyTreeOperationAsync(
        MemoryTreeOperation operation,
        CancellationToken ct = default);
}
```

规则：

- Librarian 可以调用 LLM。
- Librarian 可以做策略判断。
- Librarian 输出必须是结构化操作。
- Core 只执行通过验证的结构化操作。

### ADR-028-C：运行时检索必须 workspace scoped

**决定**：所有运行时记忆召回、工具读取、工具写入和后台整合都必须传递 `workspaceId`，并默认限制在当前 workspace 内。

受影响路径：

- `search_memory`
- `save_memory`
- `grep_memory`
- `manage_memory`
- `MemoryRecallService`
- `ContextPipeline` L6
- `SubconsciousOrchestrator`
- `MemoryExplorerSubAgent`

禁止行为：

- 工具在已知 workspace 时仍硬编码 `default`。
- Runtime 使用 unscoped FTS 作为主召回路径。
- 跨 workspace 搜索默认开启。

允许行为：

- 老接口保留为兼容 API。
- 当调用方没有 workspace 信息时，可以显式 fallback 到 `default` 并在日志中标记。
- 管理员未来可以通过显式参数启用跨 workspace 检索。

### ADR-028-D：SourceReference 是标准溯源机制

**决定**：新增 `SourceReferences` 作为一等数据结构。`SourceSessionId` 只作为 session 级快速字段保留。

目标表：

```text
SourceReferences
  SourceReferenceId
  WorkspaceId
  OwnerType        book | chapter | tree_node | pointer
  OwnerId
  TargetType       session | session_event | session_slice | url | file | memo | subagent_run
  TargetId
  TargetRange
  Label
  Description
  CreatedAt
```

推荐 pointer string：

```text
session:{sessionId}
session-event:{eventId}
session-slice:{sessionId}#{messageStartId}..{messageEndId}
file:{path}#L{start}-L{end}
url:{httpsUrl}
memo:{memoPath}
subagent-run:{runId}
```

Source resolution 必须返回状态：

```text
resolved | missing | unauthorized | unsupported
```

原因：

- 提纯记忆不是原始会话。
- Agent 需要在必要时还原完整上下文。
- 未来 run archive、JSONL 会话、事件队列、外部 URL 都需要统一指针模型。

### ADR-028-E：TreeNode 取代 TagPath 成为长期目录模型

**决定**：新增真实树节点模型，`BookIndexes.TagPath` 降级为兼容投影和搜索加速索引。

目标表：

```text
MemoryTreeNodes
  NodeId
  WorkspaceId
  LibraryId
  ParentNodeId
  Path
  Name
  Summary
  NodeType         root | category | shelf | topic | system
  Status           active | archived
  SortOrder
  CreatedAt
  UpdatedAt

BookTreeMounts
  Id
  BookId
  NodeId
  Weight
  CreatedAt
```

允许：

- 一本 Book 挂载到多个 TreeNode。
- TreeNode 有自己的摘要和状态。
- TreeNode 可以被重命名、移动、归档。
- LLM 可以提出树维护操作。

禁止：

- LLM 直接用自然语言改树。
- 删除节点时直接删除来源和书籍。
- 用扁平 tag 字符串承载所有目录治理。

### ADR-028-F：Pointer 泛化为任意节点间引用

**决定**：Pointer 从 chapter-only 引用升级为 generalized pointer。

目标结构：

```text
Pointers
  PointerId
  WorkspaceId
  SourceType       library | book | chapter | tree_node | source_reference
  SourceId
  TargetType       library | book | chapter | tree_node | session | session_event | url | file | memo | subagent_run
  TargetId
  TargetLabel
  Description
  Relevance
  CreatedAt
```

兼容规则：

- 旧 `ChapterId` 字段暂时保留。
- 旧 `GetPointersAsync(chapterId)` 映射为 `SourceType=chapter && SourceId=chapterId`。
- 新写入同时填 `WorkspaceId`、`SourceType`、`SourceId`。

### ADR-028-G：MemoryFacts / Preferences 保留为兼容 fallback

**决定**：不在本 ADR 中删除 `MemoryFacts` 和 `MemoryPreferences`。

短期定位：

- 兼容旧的潜意识抽取路径。
- 作为 `MemoryRecallService` 的 fallback。
- 保持现有用户事实和偏好召回能力。

长期方向：

- 用户档案、用户偏好、决策记录等都应可表达为 Library system books。
- `MemoryFacts/Preferences` 的最终迁移需要单独 ADR，避免在本重构中扩大风险。

### ADR-028-H：默认系统 books 是 seed data，不是业务规则

**决定**：每个 workspace/library 可以懒创建默认 books：

- `航海日志`
- `用户档案`
- `用户偏好`
- `决策记录`
- `经验教训`
- `交接索引`

规则：

- 默认 books 懒创建，不在空 workspace 中制造噪音。
- 用户和 Librarian 可以重命名、归档、重组。
- Core 不根据 book 名称执行特殊逻辑。

---

## 3. 新目标分层

```text
ContextPipeline L6
  -> MemoryRecallService / RecallRuntime
     -> IMemoryLibrary scoped query
     -> MemoryFacts/Preferences fallback
     -> source-aware result

SubconsciousWorkerService
  -> ISubconsciousOrchestrator
     -> IMemoryLibrarian
        -> IMemoryLibrary Core

Agent Tools
  -> read tools: IMemoryLibrary / IMemoryRecallService
  -> write/manage tools: IMemoryLibrarian for ingestion, IMemoryLibrary for explicit CRUD
```

Dependency direction:

```text
PuddingRuntime
  -> PuddingCore abstractions
  -> PuddingMemoryEngine services via DI

PuddingMemoryEngine
  -> PuddingCore abstractions
  -> EF Core / SQLite

PuddingCore
  -> DTOs and contracts only
```

---

## 4. Migration Plan

### Phase 1：Scope 与溯源安全基线

目标：先消除跨 workspace 风险，补齐最小 source 元数据。

交付：

- memory tools 传递 runtime workspace。
- scoped FTS/search overloads。
- `ChapterRecord` 暴露 `SourceReference` 和 `ReferenceType`。
- `MemoryRecallService` 使用 scoped library search。
- workspace 隔离测试。

不做：

- 不引入 TreeNode 表。
- 不删除 Facts/Preferences。
- 不迁移旧数据。

### Phase 2：SourceReference 一等化

目标：让每条新写入的提纯记忆能回溯来源。

交付：

- `SourceReferenceEntity`。
- `SourceReferenceRecord` / create request DTO。
- `AddSourceReferenceAsync` / `GetSourceReferencesAsync`。
- `SaveMemoryTool`、`ManageMemoryTool`、`SubconsciousOrchestrator` 写 source reference。
- session/url/file/memo source round-trip tests。

不做：

- 不要求旧数据全部补 source。
- 不做完整 UI。

### Phase 3：TreeNode 目录模型

目标：让记忆图书馆具备真正的可治理目录。

交付：

- `MemoryTreeNodeEntity`。
- `BookTreeMountEntity`。
- TreeNode CRUD。
- Book mount/unmount。
- `BookIndexes.TagPath` projection。
- lazy seed default books。

不做：

- 不让 LLM 直接改树。
- 不移除 TagPath。

### Phase 4：Librarian 分层

目标：把智能整理从 Convenience/Core 中移出。

交付：

- `IMemoryLibrarian`。
- `MemoryLibrarian`。
- `MemoryLibraryConvenience` 变成兼容 facade。
- `SubconsciousOrchestrator` 经 Librarian 写入 Library。

不做：

- 不重写所有抽取 prompt。
- 不强制显式 CRUD 走 Librarian。

### Phase 5：RecallRuntime 清理

目标：让 L6 召回以 Library 为主路径，同时保留旧事实偏好 fallback。

交付：

- `RecalledMemory` 增加 book/chapter/tree/source metadata。
- Library scoped search 优先。
- Source-aware recall output。
- ContextPipeline 在 aggressive compaction 下压缩 source metadata。

不做：

- 不默认注入完整来源链。
- 不强制 LLM rerank。

### Phase 6：与 ADR-027 事件化潜意识闭环对齐

目标：潜意识学习通过事件和持久 Job 触发后，写入 Library 时带 source event。

交付：

- `SubconsciousJob.SourceEventId` 写入 source reference。
- Worker 经 `ISubconsciousOrchestrator -> IMemoryLibrarian -> IMemoryLibrary`。
- 旧 `Channel<ConsolidationJob>` 保持过渡兼容。

不做：

- Hook 不直接调用 Librarian。
- 主对话完成不等待记忆写入。

---

## 5. Error Handling

| 错误 | 处理 |
|------|------|
| workspace 缺失 | 允许 fallback 到 `default`，记录 warning/diagnostic |
| source target 不存在 | 写入 source reference，但 resolve 返回 `missing` |
| source target 无权限 | resolve 返回 `unauthorized`，不泄露内容 |
| tree operation 无效 | 拒绝应用，记录 Librarian operation failure |
| FTS scoped query 失败 | fallback 到 compatibility recall，记录 warning |
| Librarian LLM 失败 | 保留原始 `ExperiencePackage` 或标记 job retry |
| old pointer row 缺 workspace | 通过 chapter -> book -> library 反查 workspace，失败则仅 legacy read |

---

## 6. Backward Compatibility

必须保持：

- 旧 Library/Book/Chapter 可读取。
- 旧 chapter-scoped pointers 可读取。
- 旧 FTS API 可调用，但 runtime 主路径不用旧 API。
- `MemoryFacts/Preferences` 继续参与召回。
- `MemoryLibraryConvenience` 继续注册，直到调用方迁移完成。
- 现有 `MemoryLibraryTests` 和 `ContextPipeline` 测试继续通过。

---

## 7. Testing Requirements

### Unit tests

- scoped search prevents cross-workspace recall。
- source reference CRUD and resolve status。
- generalized pointer maintains chapter pointer compatibility。
- tree node create/list/mount。
- default books lazy seed。
- MemoryLibrarian ingest writes source references。

### Integration tests

- Agent loop completion -> subconscious consolidation -> Library chapter with source session/event。
- New user message -> ContextPipeline L6 recalls scoped memory。
- `grep_memory toc` browses books/tree without crossing workspace。
- archived books disappear from active recall。

### Regression tests

- existing `MemoryLibraryTests` pass。
- existing context pipeline tests pass。
- FTS5 in-memory setup still works。

---

## 8. Acceptance Criteria

ADR-028 complete 后必须满足：

1. Memory Library Core 不调用 LLM。
2. Runtime 记忆检索使用 workspace scoped API。
3. Memory tools 不在已知 workspace 时硬编码 `default`。
4. 新写入的 Chapter 可以带 source reference。
5. SourceReference 可以表达 session、session event、session slice、file、url、memo、subagent run。
6. TreeNode 可以作为目录节点独立创建和浏览。
7. Book 可以挂载到多个 TreeNode。
8. `BookIndexes.TagPath` 继续可用，但不再是长期目录模型的唯一来源。
9. `MemoryFacts/Preferences` 作为 fallback 保留。
10. `SubconsciousOrchestrator` 写入路径可迁移到 `IMemoryLibrarian`。
11. ContextPipeline L6 继续能注入简洁有效的召回记忆。
12. 新增 tests 覆盖 workspace isolation、source reference、tree node、tool workspace behavior。

---

## 9. 不做事项

- 不一次性删除 `MemoryFacts/Preferences`。
- 不一次性迁移所有旧记忆。
- 不让 Hook 直接调用记忆图书管理员。
- 不让 LLM 自然语言直接改数据库树结构。
- 不默认跨 workspace 检索。
- 不把完整 source chain 默认塞入 prompt。
- 不把 `BookIndexes.TagPath` 立即删除。

---

## 10. 相关设计文档

- [Memory Library Infrastructure Refactor Design](../superpowers/specs/2026-05-20-memory-library-infrastructure-refactor-design.md)
- [12记忆图书馆基础设施](12记忆图书馆基础设施.md)
- [28ADR-027Hook事件潜意识学习闭环ADR](28ADR-027Hook事件潜意识学习闭环ADR.md)
