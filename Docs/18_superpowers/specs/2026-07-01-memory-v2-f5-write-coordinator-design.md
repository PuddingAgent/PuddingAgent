# Memory System v2 F5 Write Coordinator Design

> Date: 2026-07-01
> Status: partial-execute-append
> Scope: Memory Write Coordinator 契约、命令模型、plan operation 映射、dry-run/audit 执行路径、测试验收矩阵
> Revision: 2026-07-03，补充 Notebook/Page Tree 语义、开发环境重置边界和 Admin 管理入口约束
> Revision: 2026-07-04，Wiki Book v1 将 F5 MVP 收敛为 edit-page write entry；多 intent coordinator 降级为后续增强

---

## 1. 目标

F5 的长期目标是建立唯一的记忆写入协调层。所有来自显意识工具、潜意识计划、Admin/API 的写入、取代、合并、归档、删除请求，最终都必须进入同一套规则。

Wiki Book v1 的 F5 MVP 不再实现多 intent coordinator，而是收敛为一个 Wiki Page 写入入口：框架收到 `edit_page` plan 后，找到 Book/Page，合并内容，写入。

Memory Library 的设计语义是 Notebook/Page Tree + Knowledge Graph overlay。当前代码中的 `Book` 是 Notebook 的兼容命名，`Chapter` 是 Page 的兼容命名；F5 可以继续返回 `BookId/ChapterId` 字段，但文档和后续 API 设计应按 Notebook/Page 理解。

完成 F5 MVP 后，应具备：

1. 潜意识 F4-lite plan 不直接写 `MemoryLibrary`，必须进入 Wiki Page 写入入口。
2. 写入入口只接受 `book/page/content` 形式的 edit。
3. Book 不存在则创建。
4. Page path 不存在则创建。
5. Page 存在则合并内容，不重复相同段落。

---

## 2. 非目标

F5 第一阶段不做：

- 不重新设计 `MemoryLibrary` 存储 schema。
- 不做 TTL、半衰期清理或过期扫描。
- 不做历史冗余批量迁移。
- 不把潜意识 LLM 接到真实写入。
- 不用内容 hash 判断语义一致性。
- 不让 `delete` plan 自动真删除 active 记忆。
- 不建设前端 UI；F5 只定义 Admin/API 写入必须进入 coordinator，具体 `/admin/memory-library` 界面属于 F9。
- 不承担旧数据兼容、历史 schema 迁移或 backfill；开发环境可通过重置 SQLite/FTS/本地记忆数据验收。
- Wiki Book v1 不实现 `reuse_existing / append_new / supersede_existing / merge_candidates` 作为 LLM 选择的操作。
- Wiki Book v1 不实现 dry-run 阶段机、冲突检测或低置信度 quarantine。

这些分别属于 F6/F9/Admin 工具或后续执行器阶段。

---

## 3. 设计选项

| 选项 | 方案 | 优点 | 风险 | 结论 |
| --- | --- | --- | --- | --- |
| A | 继续在 `UpsertExperienceAsync` 内扩展策略 | 改动小 | `save_memory`、潜意识、Admin 会继续分叉；审计结果难统一 | 不采用 |
| B | 直接让 F4 plan executor 调用 `MemoryLibrary` | 潜意识链路最快落地 | 绕过显意识工具规则，容易产生重复写入和不可解释删除 | 不采用 |
| C | 新增 `MemoryWriteCoordinator`，所有入口转换为 command 后统一执行 | 边界清晰，便于 dry-run、审计、回滚和测试 | 需要先设计命令/结果模型，再迁移入口 | 后续增强采用 |

2026-07-04 修订：长期仍采用选项 C 作为后续增强方向，但 Wiki Book v1 MVP 先采用更小的选项 D。

| 选项 | 方案 | 优点 | 风险 | 结论 |
| --- | --- | --- | --- | --- |
| D | Wiki Page Write Entry，只处理 `edit_page` | 最小可运行，LLM 不做操作分类，重复 Book 风险最低 | 后续高级版本链/TTL/repair 需再补 | MVP 采用 |

---

## 4. 架构边界

F5 位于 F4 plan protocol 和 `MemoryLibrary` 之间，也位于 runtime tools 和 `MemoryLibrary` 之间。

Wiki Book v1 MVP 架构：

```text
memory_wiki_edit_plan.v1
          |
          v
   WikiPageWriteEntry
     | normalize book/page
     | get-or-create Book
     | get-or-create Page
     | merge content
     | save
          |
          v
     MemoryLibrary
```

长期增强架构：

```text
save_memory / manage_memory / Admin API
                  |
                  v
          MemoryWriteCommand
                  |
F4 MemoryMaintenancePlan -> PlanOperationMapper
                  |
                  v
        MemoryWriteCoordinator
          | validate source/scope
          | resolve candidates
          | decide write action
          | execute or dry-run
          | emit audit envelope
                  |
                  v
             MemoryLibrary
```

长期增强边界规则：

- 多操作 F4 validator 只判定 plan 是否结构合法；若后续恢复 F5 coordinator，F5 必须在执行前重新校验 workspace、source、candidate 和 action。
- Coordinator 可以调用 `IMemoryLlmClient` 做语义判断，但语义判断必须被限制在候选集合内。
- Coordinator 不接收自由文本“帮我记一下”作为最终动作；自由文本必须先转换成 command。
- `MemoryLibrary` 保持底层 CRUD/索引能力，不承载跨入口业务策略。
- `MemoryLibraryConvenience` 在迁移后只保留薄 wrapper，内部调用 Coordinator；该 wrapper 只服务开发期调用兼容，不承担旧数据兼容。
- `/admin/memory-library` 的创建 Notebook、创建/移动/编辑 Page、添加关系边、删除/归档等写入动作，后续都必须转换为 command 后进入 Coordinator。

Wiki Book v1 边界规则：

- LLM plan 只包含 book/page/content。
- workspace/agent/library/session/job 来自框架上下文，不从 LLM plan 读取。
- Validator 只做 JSON shape 和必填字段校验。
- 写入入口不做多意图决策。

---

## 5. Wiki Book v1 Edit Model

第一阶段输入：

```csharp
public sealed record MemoryWikiEditPlan
{
    public string Schema { get; init; } = "pudding.memory_wiki_edit_plan.v1";
    public IReadOnlyList<MemoryWikiPageEdit> Edits { get; init; } = [];
}

public sealed record MemoryWikiPageEdit
{
    public required string Book { get; init; }
    public required string Page { get; init; }
    public required string Content { get; init; }
    public string? Reason { get; init; }
}
```

写入算法：

```text
for each edit:
  bookTitle = normalize(edit.book)
  pagePath = normalize(edit.page)
  book = getOrCreateBook(workspaceId, libraryId, bookTitle)
  page = getOrCreatePage(book, pagePath)
  page.Content = merge(page.Content, edit.Content)
  save(page)
```

合并规则：

- Page 不存在：创建并写入 content。
- Page 为空：直接写入 content。
- Page 已包含相同段落：跳过重复段落。
- Page 有同名 heading：追加到该 heading 下。
- 找不到 heading：追加到末尾。

---

## 5.1 Command Model（后续增强）

第一阶段引入 `MemoryWriteCommand` 作为所有入口的统一输入。

建议字段：

```csharp
public sealed record MemoryWriteCommand
{
    public required string CommandId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string Intent { get; init; }
    public required MemoryWriteSource Source { get; init; }
    public IReadOnlyList<MemoryWriteCandidate> Candidates { get; init; } = [];
    public MemoryWritePayload? Payload { get; init; }
    public MemoryWriteExecutionMode Mode { get; init; } = MemoryWriteExecutionMode.DryRun;
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
```

`Intent` 建议固定为：

- `reuse_existing`
- `append_new`
- `supersede_existing`
- `merge_candidates`
- `archive`
- `delete_requested`
- `update_index`
- `update_skill_pointer`

注意：F4 的 `delete` action 映射到 `delete_requested`，不是直接真删除。真删除只允许显式用户/Admin 工具在强校验后进入 `delete_book` / `delete_chapter` 专用路径。

命名约束：第一阶段 DTO 可继续使用 `BookId`、`ChapterId`，但语义上分别表示 `NotebookId`、`PageId`。新增字段或新 API 应优先使用 Notebook/Page 术语，避免继续强化“固定书籍章节”的业务含义。

---

## 6. Source Model

`MemoryWriteSource` 必须保留来源身份，不允许匿名写入：

```csharp
public sealed record MemoryWriteSource
{
    public required string SourceKind { get; init; }
    public string? SessionId { get; init; }
    public string? HookEventId { get; init; }
    public string? SubconsciousJobId { get; init; }
    public string? PlanId { get; init; }
    public string? OperationId { get; init; }
    public string? ToolCallId { get; init; }
    public string? AdminUserId { get; init; }
    public string? AgentId { get; init; }
    public string? AgentTemplateId { get; init; }
    public string? MemoryLibraryId { get; init; }
}
```

For `SourceKind = subconscious_plan`, `SubconsciousJobId`, `PlanId`, `OperationId` and `AgentId` are required. `MemoryLibraryId` is optional only while the upstream durable job payload cannot identify a library; once known, it must be propagated and validated as part of the target memory scope.

`SourceKind` 第一阶段支持：

- `runtime_tool`
- `subconscious_plan`
- `admin`
- `migration`
- `test`

验收约束：

- `subconscious_plan` 必须带 `SubconsciousJobId + PlanId + OperationId`。
- `runtime_tool` 至少带 `SessionId` 或 `ToolCallId`。
- `admin` 必须带 `AdminUserId`。
- 缺少来源的 command 被拒绝，不进入 `MemoryLibrary`。

---

## 7. Execution Modes

F5 第一阶段必须支持三种执行模式：

| Mode | 行为 | 用途 |
| --- | --- | --- |
| `ValidateOnly` | 只做 command/schema/source/workspace 检查 | 快速拒绝非法入口 |
| `DryRun` | 解析候选、产出将要执行的 write command result，但不写库 | 潜意识 F4 到 F5 的默认模式 |
| `Execute` | 在通过校验和策略后写入 `MemoryLibrary` | 后续接入 `save_memory` 和显式 Admin 操作 |

第一阶段实现建议先完成 `ValidateOnly + DryRun`，再接 `Execute`。这样可以先证明映射和审计不会破坏现有写入。

---

## 8. Plan Operation Mapping

F4 `MemoryMaintenanceOperation` 到 F5 `MemoryWriteCommand` 的映射如下：

| F4 action | F5 intent | 需要字段 | 第一阶段执行策略 |
| --- | --- | --- | --- |
| `reuse_existing` | `reuse_existing` | `target.chapterId` / PageId | dry-run 返回 matched，不写库 |
| `append_new` | `append_new` | `proposedContent`，可选 `proposedTitle` | dry-run 计算目标 Notebook/Page，execute 才 append |
| `supersede_existing` | `supersede_existing` | `target.chapterId + proposedContent` / PageId + content | dry-run 标记 supersede candidate，execute 才调用版本链 |
| `merge_candidates` | `merge_candidates` | `sources[] + proposedContent` | 第一阶段只 dry-run，不 execute |
| `deprecate` | `archive` | `target` | 第一阶段只 dry-run，不 execute |
| `delete` | `delete_requested` | `target + explicit reason` | 不自动 execute |
| `update_index` | `update_index` | `target` | 第一阶段只 dry-run |
| `update_skill_pointer` | `update_skill_pointer` | `target/pointer` | 第一阶段只 dry-run |

执行前必须重新调用 `MemoryMaintenancePlanValidator` 或等价 command validator。F4 envelope 的 `accept_for_execution` 只是候选信号。

---

## 9. Coordinator Result Envelope

F5 输出 `MemoryWriteResultEnvelope`，用于 Job result、工具返回、诊断和未来回滚。

建议字段：

```csharp
public sealed record MemoryWriteResultEnvelope
{
    public string Schema { get; init; } = "pudding.memory_write_result.v1";
    public required string CommandId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string Status { get; init; }
    public required string Mode { get; init; }
    public required string Intent { get; init; }
    public string? Decision { get; init; }
    public string? BookId { get; init; }        // compatibility: NotebookId
    public string? ChapterId { get; init; }     // compatibility: PageId
    public string? SupersededChapterId { get; init; } // compatibility: SupersededPageId
    public IReadOnlyList<string> ErrorCodes { get; init; } = [];
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
```

`Status` 建议值：

- `accepted`
- `dry_run`
- `executed`
- `reused`
- `quarantined`
- `rejected`

审计约束：

- envelope 不保存原始 LLM 全文。
- envelope 不保存完整上下文，只保存 source IDs、计数、短 reason/error code。
- `Execute` 成功时必须返回实际 `BookId/ChapterId` 兼容字段，语义为 NotebookId/PageId，或被复用的目标 ID。
- `DryRun` 不得伪造已写入 ID，可返回 candidate ID 和 predicted action。

---

## 10. Validation And Safety Gates

F5 command validator 至少检查：

- `WorkspaceId` 非空，且所有候选/target/source reference 都在同一 workspace。
- `SourceKind` 和必需 source IDs 匹配。
- `Intent` 是支持值。
- `append_new` 必须有 payload content。
- `supersede_existing` 必须有 target Page/Chapter 和 payload content。
- `reuse_existing` 必须有 target Page/Chapter。
- `delete_requested` 在自主潜意识维护路径中直接拒绝，返回 `autonomous_delete_not_allowed`；真实删除只能来自显式用户/Admin 删除管道。
- `Execute` 模式不得来自未通过 F4/F5 双重校验的潜意识 plan。
- `Confidence` 低于阈值时降级到 `quarantined`，不等待人审计。

这层校验要和 F4 validator 重叠一部分。重复不是浪费，而是防止 F4 result 被篡改或过期。

---

## 11. Observability

F5 必须写两类证据：

Trace：

- `memory_write.validate`
- `memory_write.dry_run`
- `memory_write.execute`
- `memory_write.reject`

Metrics：

- category: `memory`
- name: `memory_write.command`
- dimensions:
  - `workspace_id`
  - `source_kind`
  - `intent`
  - `mode`
  - `status`
  - `decision`
  - `error_code`
  - `dry_run`

指标只记录结构化事实，不记录完整内容。必要调试时可在 `PUDDING_DEBUG=1` 下写截断预览。

---

## 12. Migration Strategy

推荐按六步迁移：

1. F5a: 新增 DTO、validator、coordinator dry-run，不接现有写入入口。
2. F5b: 增加 F4 plan operation -> command mapper，Worker 只产生 F5 dry-run result，不写库。
3. F5c: 将 F5 dry-run result 写入 `SubconsciousJobResultEnvelope.MemoryWriteResults`，并随 `SubconsciousJobs.ResultJson` 持久化审计，不写库。
4. F5d: 将 `MemoryLibraryConvenience.UpsertExperienceAsync` 改为调用 Coordinator execute，保持开发期调用接口不变，但不承担旧数据兼容。
5. F5e: 将 `save_memory`、`manage_memory` 的写入/删除路径逐步改为显式 command，同时保留底层 `MemoryLibrary` 直接 CRUD 给内部测试和开发环境重置/诊断工具。
6. F5f: 将 Admin Memory Library API 的写入路径逐步改为显式 command，供 F9 `/admin/memory-library` v2 管理界面复用。

每一步都必须可独立回滚。第一阶段完成 F5a/F5b 即可，不要求一次性迁移所有 runtime tools。

---

## 13. 测试与验收矩阵

| 场景 | 验收标准 |
| --- | --- |
| 非法 JSON | edit plan validator 拒绝 |
| 缺 book | edit plan validator 拒绝 |
| 缺 page | edit plan validator 拒绝 |
| 缺 content | edit plan validator 拒绝 |
| 合法单页 edit | 创建或更新目标 Notebook/Page |
| 合法多页 edits | 逐页创建或更新 |
| Book 不存在 | 自动创建 Notebook/Book |
| Page path 不存在 | 自动创建 Page/Chapter |
| 同一段落重复写入 | 不重复追加 |
| 同一 Book/Page 连续写入 | 不增加重复 Notebook/Book 或 Page/Chapter |
| LLM plan 包含 workspace/agent/library | 忽略这些字段，使用 job context |

---

## 14. 下一步

2026-07-04 后，下一步不再扩大多 intent coordinator，而是实现 Wiki Book v1：

1. 定义 `memory_wiki_edit_plan.v1` DTO 和 JSON shape validator。
2. 实现 `WikiPageWriteEntry`。
3. 将 SubconsciousWorkerService 的 dry-run plan 生成改为 edit-page plan。
4. 在开发环境 execute edit-page 写入。
5. 验证同一 Book/Page 不重复创建，同一段落不重复追加。

多 intent coordinator、reuse/supersede/merge、dry-run result envelope、Admin command 化仍保留为后续增强。

---

## 15. Implementation Status

2026-07-01 first implementation:

- Added `MemoryWriteCommand` DTOs, `MemoryWriteCommandValidator` and `MemoryWriteResultEnvelope`.
- Added `MemoryWriteCoordinator` validate/dry-run path.
- Added optional `RuntimeActivity` / `TelemetryMetric` emission for coordinator decisions.
- Added F4 `MemoryMaintenancePlan` operation mapper.
- Verified accepted F4 plans can produce F5 dry-run result envelopes.
- Added `SubconsciousJobResultEnvelope.MemoryWriteResults` so F5 dry-run result envelopes can be embedded in durable Job result JSON.
- Connected durable `SubconsciousWorkerService` accepted F4 plan path to F5 dry-run coordinator and `RecordResultAsync`, without completing jobs or writing memory.
- Added explicit `append_new` execute support in `MemoryWriteCoordinator`, backed by `IMemoryLibrary`; this creates Library/Book/Chapter compatibility records, semantically Library/Notebook/Page, and records `memory_write.execute`.
- Still not connected to subconscious automatic real writes or `save_memory` migration.

2026-07-03 design revision:

- Clarified Memory Library target semantics as Notebook/Page Tree + Knowledge Graph overlay.
- Clarified `BookId/ChapterId` are compatibility fields for NotebookId/PageId in F5 envelopes.
- Clarified development environment reset replaces old-data compatibility and historical schema migration requirements.
- Added Admin Memory Library API command migration as F5f, so F9 `/admin/memory-library` UI writes cannot bypass `MemoryWriteCoordinator`.

2026-07-04 simplification revision:

- Wiki Book v1 supersedes multi intent coordinator as MVP target.
- F5 MVP is now `WikiPageWriteEntry` handling only edit-page writes.
- `reuse_existing / append_new / supersede_existing / merge_candidates` are deferred.
- Validator v1 only checks edit plan JSON shape and required fields.
