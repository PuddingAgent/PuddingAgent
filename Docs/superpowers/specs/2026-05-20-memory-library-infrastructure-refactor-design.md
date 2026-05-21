# Memory Library Infrastructure Refactor Design

> 日期：2026-05-20
> 状态：draft
> ADR：[29ADR-028记忆图书馆基础设施重构ADR](../../07架构/29ADR-028记忆图书馆基础设施重构ADR.md)
> 关联任务：`task08-memory.md`、`task38-subconscious-memory-engine.md`、`task39-session-persistence.md`、`task40-context-compaction.md`、`task42-hook-event-subconscious-learning.md`

## 1. 目标

本阶段目标是把记忆图书馆重构为稳定的树状数据基础设施，让它像数据库和文件系统一样提供存储、索引、查询、修改、移除、目录、指针和溯源能力，而不是在底层写死业务整理规则。

核心结果：

- `MemoryLibrary Core` 成为低智能、强结构、可审计的基础设施层。
- 潜意识 LLM 通过独立的 `MemoryLibrarian` 层管理图书馆，而不是把整理策略塞进 Core。
- 每条提纯记忆都能通过 pointer/source reference 回溯到原始会话、事件、文件片段或外部 URL。
- Workspace/Scene 隔离在所有读写和检索路径中生效。
- Book、Chapter、TreeNode、Pointer、Index 都有明确职责，支持图书馆自然增长。
- 现有 `MemoryFacts/Preferences` 与 `MemoryLibrary` 双轨逐步收敛，不做高风险一次性迁移。

## 2. 当前基线

已经具备：

- `IMemoryLibrary` 提供 Library、Book、Chapter、Pointer、Tag、FTS、Vector、Branch 的基础接口。
- `MemoryLibrary` 使用 SQLite + EF Core + FTS5 实现核心存储和检索。
- `MemoryLibraryConvenience` 提供上层写入和智能检索入口。
- `MemoryRecallService` 融合 Library、MemoryFacts、MemoryPreferences 进行召回。
- `SubconsciousOrchestrator` 能抽取事实和偏好，并同步写入 `MemoryFacts/Preferences` 与 `MemoryLibrary`。
- `ContextPipeline` 已有 L6 recalled memory 层，能把召回结果注入上下文。
- `MemoryTools.cs` 提供 `save_memory`、`manage_memory`、`grep_memory` 工具。

主要缺口：

- `MemoryLibraryConvenience` 同时承担基础设施便捷方法、业务路由、自动指针、后台深度探索，边界过宽。
- `MemoryLibrary` 的搜索接口缺少 workspace/library scope，存在跨场景召回风险。
- 多个工具硬编码 `workspaceId = "default"`，不符合 Scene/Workspace 隔离原则。
- `ChapterEntity` 有 `SourceReference` 和 `ReferenceType` 字段，但 record、接口、schema 初始化和工具参数没有完整贯通。
- Tag 树只是 `BookIndexes.TagPath` 的路径聚合，不支持节点身份、重命名、移动、合并、节点级指针和目录治理。
- Pointer 只能挂在 Chapter 下，不能表达 Book、TreeNode、SourceEvent、SessionSlice 等更丰富关系。
- `MemoryFacts/Preferences` 与 `MemoryLibrary` 双轨并存，召回和写入职责重复。
- 潜意识学习仍保留 `Channel<ConsolidationJob>` 兼容路径，尚未完全对齐 task42 的事件化 Job 队列。

## 2.1 与既有基础设施文档的关系

`Docs/07架构/12记忆图书馆基础设施.md` 定义了第一版 Memory Library 的正确方向：Library、Book、Chapter、Pointer、TagPath、FTS5 与 Convenience API。该文档仍作为基础设施起点保留。

本设计不推翻该基础设施文档，而是对现有实现做四点修正：

- 把“LLM 友好操作层”从 Core 中拆出，明确为 `MemoryLibrarian` 或兼容 facade，避免便利 API 演变成业务策略容器。
- 把 `BookIndexes.TagPath` 升级为真实 TreeNode 的兼容投影，支持目录治理和节点级操作。
- 把 `SourceSessionId` 升级为通用 `SourceReference`，满足会话、事件、文件、URL、备忘录、run archive 的完整回溯。
- 把所有检索和管理接口纳入 workspace scope，避免跨场景记忆污染。

因此，已有 `12记忆图书馆基础设施.md` 是基线说明；本 spec 和 ADR-028 是重构决策与迁移计划。

## 3. 设计原则

### 3.1 图书馆不定义业务

图书馆只回答：

- 有哪些 Library、Book、Chapter、TreeNode、Pointer。
- 如何写入、更新、删除、归档。
- 如何按全文、标签树、向量、指针、来源进行查询。
- 如何返回可验证的来源链。

图书馆不回答：

- 什么内容值得记住。
- 应该放进哪本书。
- 哪个标签更自然。
- 什么时候合并、拆分、衰减或遗忘。
- 哪条记忆应该注入当前 prompt。

这些判断属于潜意识 LLM 或召回运行时。

### 3.2 结构先于智能

先让数据结构正确、可迁移、可测试，再接入更强的 LLM 自组织策略。V1 不追求完全自动整理，只保证每一次整理动作都有明确数据结构承载。

### 3.3 原文可回溯

提纯记忆不是原始记忆。每个 Chapter 必须能指向至少一种来源：

- `session`：会话 ID。
- `session_event`：事件 ID 或消息 ID。
- `session_slice`：会话文件路径 + 起止行或消息 UUID。
- `url`：外部资料。
- `file`：本地文件路径。
- `memo`：交接备忘录。

召回结果默认返回提纯片段；需要核实时可以沿 pointer 回到原始材料。

### 3.4 Workspace 是强隔离边界

所有 Library、Book、Chapter、TreeNode、Pointer、SearchResult 都必须可追溯到 workspace。跨 workspace 检索需要显式授权，不作为默认路径。

### 3.5 兼容迁移

不一次性删除 `MemoryFacts/Preferences`。第一阶段让 Library 成为结构化主路径，Facts/Preferences 作为兼容和召回补充；第二阶段再将它们降级为视图、缓存或迁移源。

### 3.6 可解释的自动化

潜意识 LLM 可以建议分类、合并、拆分和重命名，但这些动作必须先表达为结构化操作：

```text
create_node | mount_book | move_book | rename_node | merge_node | archive_node | add_pointer
```

Core 只执行已经验证的操作，不直接执行自然语言意图。这样可以把 LLM 判断和数据库变更分开审计。

## 4. 目标架构

```text
Agent Loop / User Message
        |
        v
ContextPipeline L6
        |
        v
RecallRuntime
  - fast lexical recall
  - tree-guided recall
  - pointer/source expansion
  - optional LLM rerank
        |
        v
MemoryLibrary Core
  - Library / Book / Chapter
  - TreeNode / TreeEdge
  - Pointer / SourceReference
  - FTS / Vector / Index

Agent completed event
        |
        v
Subconscious Job Queue
        |
        v
MemoryLibrarian
  - extract
  - purify
  - classify
  - mount/move tree nodes
  - merge/split books
  - create source pointers
        |
        v
MemoryLibrary Core
```

## 5. Module Boundaries

### 5.1 MemoryLibrary Core

Location:

- `Source/PuddingCore/Abstractions/IMemoryLibrary.cs`
- `Source/PuddingCore/Abstractions/MemoryLibraryDtos.cs`
- `Source/PuddingMemoryEngine/Data/MemoryLibrary.cs`
- `Source/PuddingMemoryEngine/Data/MemoryLibraryDbContext.cs`
- `Source/PuddingMemoryEngine/Entities/LibraryEntities.cs`

Responsibilities:

- CRUD for library, book, chapter, tree node, pointer, source reference.
- Scoped FTS and vector search.
- Tree navigation primitives.
- Pointer and backlink lookup.
- Archive/delete semantics.
- Version and access metadata.

Forbidden responsibilities:

- LLM prompting.
- automatic classification.
- automatic merge/split policy.
- background exploration.
- context injection formatting.

### 5.2 MemoryLibraryIndex

Location:

- New `Source/PuddingMemoryEngine/Services/MemoryLibraryIndexService.cs`
- New abstraction `IMemoryLibraryIndexService`.

Responsibilities:

- maintain FTS/index consistency where EF cannot express it cleanly.
- expose scoped search plans: FTS, tag/tree, vector, pointer expansion.
- provide index diagnostics for admin panels and tests.

This replaces the indexing parts currently split across `MemoryLibrary`, `MemoryLibraryConvenience`, and `TagTreeIndexer`.

### 5.3 MemoryLibrarian

Location:

- New `Source/PuddingCore/Abstractions/IMemoryLibrarian.cs`
- New `Source/PuddingMemoryEngine/Services/MemoryLibrarian.cs`

Responsibilities:

- turn extracted experience packages into library mutations.
- ask memory LLM where to mount memory in the tree.
- create default books when needed.
- update tables of contents.
- detect duplicate chapters/books.
- create source pointers.
- propose tree maintenance operations.

The librarian is allowed to be smart. The library is not.

The librarian should be the only component allowed to translate `ExperiencePackage` into multiple library mutations. Agent tools may still call Core directly for explicit user commands such as listing books or adding a known pointer.

### 5.4 RecallRuntime

Location:

- evolve `IMemoryRecallService` and `MemoryRecallService`.

Responsibilities:

- receive current user message and workspace.
- query Library Core and compatibility stores.
- use tree, FTS, vector, pointer and source metadata to rank results.
- return concise `RecalledMemory` items with source IDs.
- indicate whether source expansion is needed.

It should not mutate memory.

### 5.5 Agent Tools

Location:

- `Source/PuddingRuntime/Services/Tools/MemoryLibraryTool.cs`
- `Source/PuddingRuntime/Services/Tools/MemoryTools.cs`

Responsibilities:

- expose safe operations to agents.
- require workspace-aware request context.
- separate low-risk read tools from medium-risk write/manage tools.
- return source metadata in JSON outputs.

Tools should not hardcode `default` except as an explicit fallback when no workspace is supplied by the runtime.

## 6. Data Model Changes

### 6.1 Chapter source fields

Extend `ChapterRecord` and `AddChapterAsync` to include:

```csharp
string? SourceReference,
string? ReferenceType
```

Allowed `ReferenceType` values:

- `none`
- `session`
- `session_event`
- `session_slice`
- `url`
- `file`
- `memo`

`SourceSessionId` remains as a fast-path field for session-level lookup. `SourceReference` stores the precise pointer string.

### 6.2 Explicit source references

Add source references as first-class rows instead of overloading Chapter fields:

```text
SourceReferences
  SourceReferenceId
  WorkspaceId
  OwnerType        book | chapter | tree_node | pointer
  OwnerId
  TargetType       session | session_event | session_slice | url | file | memo
  TargetId
  TargetRange
  Label
  Description
  CreatedAt
```

V1 can keep Chapter source columns for compatibility while writing SourceReferences for new data.

Recommended pointer string forms:

```text
session:{sessionId}
session-event:{eventId}
session-slice:{sessionId}#{messageStartId}..{messageEndId}
file:{absoluteOrWorkspaceRelativePath}#L{start}-L{end}
url:{httpsUrl}
memo:{memoPath}
subagent-run:{runId}
```

The pointer string is an identifier, not a guarantee that the target currently exists. Resolution should return a typed status: `resolved`, `missing`, `unauthorized`, or `unsupported`.

### 6.3 Tree nodes

Add real tree nodes:

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
```

Add book mounts:

```text
BookTreeMounts
  Id
  BookId
  NodeId
  Weight
  CreatedAt
```

Keep `BookIndexes.TagPath` temporarily as a denormalized compatibility index derived from `MemoryTreeNodes.Path`.

### 6.4 Pointer scope

Generalize pointers:

```text
Pointers
  PointerId
  WorkspaceId
  SourceType       library | book | chapter | tree_node | source_reference
  SourceId
  TargetType       library | book | chapter | tree_node | session | session_event | url | file | memo
  TargetId
  TargetLabel
  Description
  Relevance
  CreatedAt
```

Compatibility:

- Existing `ChapterId` remains for old rows.
- New code writes `WorkspaceId`, `SourceType = chapter`, `SourceId = chapterId`.
- `GetPointersAsync(chapterId)` maps to generalized pointer lookup.

### 6.5 Default system books

Create default books per workspace/library when absent:

- `航海日志`：重要事件和任务进展，不记录流水账。
- `用户档案`：稳定个人事实。
- `用户偏好`：偏好、习惯、风格。
- `决策记录`：架构、产品、技术选型。
- `经验教训`：故障、踩坑、复盘。
- `交接索引`：指向 memo、session、run archive 的轻量索引。

These are seed data, not hardcoded business rules. Users and librarian can rename, archive, or reorganize them.

Default books should be created lazily on first write or explicit initialization. A clean workspace with no memory activity should not receive noisy seed rows.

## 7. Interface Changes

### 7.1 Core API additions

Add to `IMemoryLibrary`:

```csharp
Task<IReadOnlyList<BookRecord>> ListBooksAsync(string libraryId, string workspaceId, int limit = 50, CancellationToken ct = default);
Task<IReadOnlyList<RankedResult>> SearchChaptersFtsScopedAsync(string workspaceId, string query, int topK = 20, CancellationToken ct = default);
Task<IReadOnlyList<TreeNodeRecord>> GetTreeChildrenAsync(string workspaceId, string libraryId, string? parentNodeId, CancellationToken ct = default);
Task<TreeNodeRecord> CreateTreeNodeAsync(string workspaceId, string libraryId, string? parentNodeId, string name, string? summary, CancellationToken ct = default);
Task<BookTreeMountRecord> MountBookAsync(string bookId, string nodeId, int weight = 1, CancellationToken ct = default);
Task<SourceReferenceRecord> AddSourceReferenceAsync(SourceReferenceCreateRequest request, CancellationToken ct = default);
Task<IReadOnlyList<SourceReferenceRecord>> GetSourceReferencesAsync(string ownerType, string ownerId, CancellationToken ct = default);
```

Keep old methods during migration and implement them as wrappers with legacy behavior.

### 7.2 Librarian API

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

`MemoryLibraryConvenience.UpsertExperienceAsync` becomes a thin compatibility wrapper that calls `IMemoryLibrarian.IngestExperienceAsync`.

### 7.3 Recall API additions

Extend `RecalledMemory`:

```csharp
public string? BookId { get; init; }
public string? ChapterId { get; init; }
public string? TreePath { get; init; }
public IReadOnlyList<SourceReferenceSummary> Sources { get; init; }
public bool NeedsVerification { get; init; }
```

This lets ContextPipeline inject concise memory while UI/tools can inspect sources.

## 8. Migration Plan

### Phase 1: Safety and scope

- Thread workspace ID through `save_memory`, `manage_memory`, `grep_memory`, and `search_memory`.
- Add scoped search overloads and use them in recall paths.
- Add source fields to records and tool outputs.
- Add tests proving workspace A cannot recall workspace B memory.

Non-goals:

- Do not introduce TreeNode tables yet.
- Do not remove `MemoryFacts/Preferences`.
- Do not change `ContextPipeline` prompt shape except to avoid cross-workspace recall.
- Do not migrate existing data.

### Phase 2: Source references

- Add `SourceReferences` entity/table.
- Write source references from `SaveMemoryTool`, `ManageMemoryTool`, and `SubconsciousOrchestrator`.
- Add backlinks from source to memory.
- Add tests for session/url/file/memo source round trips.

Non-goals:

- Do not implement full source rendering UI.
- Do not require every old row to have a source reference.
- Do not block writes when optional source resolution fails.

### Phase 3: Tree nodes

- Add `MemoryTreeNodes` and `BookTreeMounts`.
- Seed default root and system books for workspace libraries.
- Keep `BookIndexes.TagPath` as compatibility projection.
- Add tree navigation APIs and tests.

Non-goals:

- Do not let LLM apply destructive tree rewrites directly.
- Do not remove `BookIndexes.TagPath`.
- Do not require one book to have only one tree position.

### Phase 4: Librarian split

- Introduce `IMemoryLibrarian`.
- Move `UpsertExperienceAsync`, automatic book selection, chapter append, source pointer creation, and tree mount logic out of `MemoryLibraryConvenience`.
- Reduce `MemoryLibraryConvenience` to a compatibility facade.

Non-goals:

- Do not rewrite extraction prompts in this phase.
- Do not make Librarian mandatory for explicit CRUD operations.

### Phase 5: Recall runtime cleanup

- Update `MemoryRecallService` to query Library first with scoped APIs and source metadata.
- Keep `MemoryFacts/Preferences` as compatibility fallback.
- Return richer `RecalledMemory` with source summaries.
- Update `ContextPipeline` formatting to include source hints only when useful.

Non-goals:

- Do not inject full source chains into the prompt by default.
- Do not require LLM reranking for fast recall.

### Phase 6: Eventized subconscious path

- Align `SubconsciousOrchestrator` writes with `IMemoryLibrarian`.
- Preserve the existing `Channel<ConsolidationJob>` path until task42 persistent queue lands.
- Once task42 lands, worker consumes durable jobs and writes source references using `SourceEventId`.

Non-goals:

- Do not make Hook call Librarian directly.
- Do not block Agent completion on memory writes.

## 8.1 Backward Compatibility

The migration must preserve these compatibility behaviors:

- Existing books, chapters, pointers and FTS rows remain readable.
- `GetPointersAsync(chapterId)` continues to work for old chapter-scoped pointers.
- `SearchBooksFtsAsync` and `SearchChaptersFtsAsync` remain available, but new runtime paths use scoped overloads.
- `MemoryFacts/Preferences` continue to be queried until Library recall has parity.
- `MemoryLibraryConvenience` remains registered in DI until all callers move to `IMemoryLibrarian`, `IMemoryLibraryIndexService`, or `IMemoryRecallService`.

## 8.2 Deprecation Policy

Deprecation should be explicit and staged:

| Item | Phase introduced | Phase deprecated | Removal earliest |
|------|------------------|------------------|------------------|
| unscoped FTS runtime use | existing | Phase 1 | after Phase 5 parity |
| `MemoryLibraryConvenience` as strategy holder | existing | Phase 4 | after all callers migrate |
| `MemoryFacts/Preferences` as primary recall source | existing | Phase 5 | separate migration ADR |
| `BookIndexes.TagPath` as source of truth | existing | Phase 3 | after TreeNode projection is stable |
| chapter-only pointer model | existing | Phase 2/3 | after generalized pointer read APIs are stable |

## 9. Testing Strategy

### Unit tests

- `MemoryLibraryTests`
  - scoped FTS never crosses workspace.
  - source reference CRUD and backlinks.
  - tree node create/list/mount.
  - generalized pointer compatibility.

- `MemoryLibrarianTests`
  - ingest creates default library/book when absent.
  - ingest appends chapter to existing book by exact title.
  - source reference is written for session/url/file/memo.
  - suggested tree paths create or reuse nodes.

- `MemoryRecallServiceTests`
  - library results include source metadata.
  - facts/preferences fallback remains active.
  - RRF ranking remains deterministic.

- `MemoryToolsTests`
  - tools honor request workspace.
  - no hardcoded default when workspace is supplied.
  - JSON outputs expose source IDs.

### Integration tests

- Agent loop completion triggers subconscious consolidation and writes a library chapter with source session.
- A new user message recalls the chapter through ContextPipeline L6.
- `grep_memory toc` can browse books and tree nodes.
- Deleting or archiving a book removes it from active recall while preserving source references where configured.

### Regression tests

- Existing `MemoryLibraryTests` still pass.
- Existing context pipeline tests still pass.
- FTS5 works in SQLite in-memory test setup.

## 10. Risks

### Risk: schema drift between EF and SQL initializer

Mitigation:

- Update `LibraryEntities.cs`, `MemoryLibraryDbContext.cs`, `init_library.sql`, and in-memory test setup in the same phase.
- Add tests that create DB via EF and via SQL initializer if both paths remain supported.

### Risk: dual memory systems stay forever

Mitigation:

- Define `MemoryFacts/Preferences` as compatibility fallback in Phase 1-5.
- After Library recall reaches parity, add a separate migration plan to convert facts/preferences into system books.

### Risk: LLM reorganizes tree destructively

Mitigation:

- Librarian emits tree operations first.
- Core applies only validated operations.
- Archive old nodes before destructive rename/move.
- Keep source references immutable unless explicitly migrated.

### Risk: recall prompt gets noisy

Mitigation:

- `RecallRuntime` returns compact snippets by default.
- Source details are available to tools/UI but not always injected into prompt.
- ContextPipeline trims source metadata under aggressive compaction.

## 11. Acceptance Criteria

- Memory Library core APIs remain deterministic and do not call LLMs.
- New writes can store source references and return them through read APIs.
- Read/search/manage tools honor workspace ID.
- Scoped search prevents cross-workspace memory leakage.
- Tree nodes can be browsed independently from flat tag strings.
- Default system books are seeded per workspace/library without blocking user-defined organization.
- Subconscious consolidation writes through Librarian or its compatibility facade.
- ContextPipeline L6 continues to inject useful recalled memory.
- Existing tests pass, with new tests covering workspace isolation, source references, tree nodes, and tool workspace behavior.

## 12. First Implementation Slice

The first implementation slice should be intentionally small:

1. Add workspace-aware tool parameter handling and remove hardcoded `default` from memory tools when runtime supplies a workspace.
2. Add scoped search overloads to `IMemoryLibrary` and use them in `MemoryRecallService`.
3. Add `SourceReference` DTO/entity/table and record fields.
4. Add tests for workspace isolation and source reference round trip.

This slice strengthens the infrastructure without forcing immediate tree migration.

## 13. ADR Mapping

ADR-028 should encode the following decisions:

- Memory Library Core is storage infrastructure and must not call LLMs.
- `MemoryLibrarian` owns LLM-guided organization and ingestion.
- Workspace-scoped retrieval is mandatory for runtime paths.
- `SourceReference` is the standard mechanism for original-session and external-resource recovery.
- TreeNode becomes the long-term directory model; `BookIndexes.TagPath` becomes compatibility projection.
- `MemoryFacts/Preferences` remain compatibility fallback until a later migration ADR.
