# Memory System v2 F9 Reset, Repair & Admin Tools Design

> Date: 2026-07-03
> Status: design-for-review
> Scope: 开发环境重置、诊断修复、Admin Memory Library v2 管理界面、验收路径

---

## 1. 目标

F9 的目标是把 Memory v2 从“后端管道可运行”推进到“可重置、可诊断、可被人类和 Agent 管理验证”的状态。

F9 不承担潜意识 LLM 的推理质量，也不承担 Memory Library 的底层 schema 设计。它负责四件事：

1. 提供开发环境可复现的 Memory v2 数据重置路径。
2. 提供只读诊断和 dry-run 修复报告。
3. 升级现有 `/admin/memory-library` 为 Notebook/Page Tree + Knowledge Graph 管理界面。
4. 确保 Admin/API 写入不绕过 `MemoryWriteCoordinator`。

Memory Library 的语义是 SQLite-backed hierarchical notebook database with knowledge graph overlay。当前代码里的 `Book` 是 Notebook 兼容命名，`Chapter` 是 Page 兼容命名。

---

## 2. 非目标

- 不做生产环境数据迁移。
- 不保留旧数据兼容性；开发环境可以重置 SQLite/FTS/本地记忆数据。
- 不把 Admin UI 变成业务记忆判断器；UI 只管理底层 Notebook/Page/Relation/SourceReference。
- 不让 Admin UI 直接调用底层 `IMemoryLibrary` primitives 执行写入。
- 不让潜意识 LLM 自动真删除 active memory。
- 不在第一阶段做通用实体抽取图谱。

---

## 3. 当前状态

已有基础：

- 前端路由：`Source/PuddingPlatformAdmin/config/routes.ts` 已有 `/memory-library`。
- 前端页面：`Source/PuddingPlatformAdmin/src/pages/memory-library` 已有 Tree、Editor、Inspector、Search 组件。
- 前端 API：`Source/PuddingPlatformAdmin/src/services/platform/api.ts` 已有 Memory Library Admin API 方法。
- 后端 API：`Source/PuddingPlatform/Controllers/Api/MemoryLibraryAdminController.cs` 已有管理端控制器。
- 后端服务：`Source/PuddingPlatform/Services/MemoryLibraryAdminService.cs` 已有管理服务。

当前缺口：

- UI 和 DTO 仍以 Book/Chapter 为主语义，不是 Notebook/Page Tree。
- Admin 写入服务直接调用底层 `IMemoryLibrary`，没有统一进入 `MemoryWriteCoordinator`。
- Page 仍不是真正递归 Page；当前更接近 TreeNode + mounted Book + Book chapters。
- 关系展示主要是 Pointer/Sources，缺少 Generic Relation 管理。
- 缺少明确的开发环境 reset 工具和验收脚本。

---

## 4. 架构

```text
Admin UI (/admin/memory-library)
  -> Memory Library Admin API
  -> MemoryLibraryAdminService
  -> MemoryWriteCommand mapper
  -> MemoryWriteCoordinator
  -> IMemoryLibrary / MemoryLibraryDbContext
```

读取路径可以继续走 Admin service + `IMemoryLibrary` 只读接口。

写入路径必须逐步改成 command：

- create notebook
- update notebook
- create page
- update page
- move page
- archive/delete
- add relation
- add source reference

重置和诊断工具不走 UI 常规写入路径，但必须有显式开发环境保护和结构化输出。

---

## 5. 数据与 API 语义

### 5.1 兼容命名

| 当前字段/API | v2 语义 | 说明 |
| --- | --- | --- |
| `Book` | Notebook | 兼容阶段继续存在 |
| `Chapter` | Page | 后续新 API 优先使用 Page |
| `TreeNode` | Page tree/category node | 当前可作为树结构阶段性承载 |
| `ChapterRelation` | Page relation | 后续演进为 Generic Relation |
| `Pointer` | Typed reference edge | 可保留为轻量引用 |
| `SourceReference` | Evidence link | Page/Fact/Relation 都应可挂载 |

### 5.2 Admin API v2 方向

第一阶段可以保留现有路由，但新增语义层：

- `GET /api/admin/memory-library/.../libraries`
- `GET /api/admin/memory-library/.../libraries/{libraryId}/tree`
- `GET /api/admin/memory-library/.../pages/{pageId}`
- `POST /api/admin/memory-library/.../notebooks`
- `POST /api/admin/memory-library/.../pages`
- `PUT /api/admin/memory-library/.../pages/{pageId}`
- `POST /api/admin/memory-library/.../pages/{pageId}/move`
- `POST /api/admin/memory-library/.../relations`
- `GET /api/admin/memory-library/.../relations`

兼容路由 `/books`、`/chapters` 可以保留，但内部应映射到 Notebook/Page command。

---

## 6. Reset 工具

### 6.1 目标

提供开发环境可复现重置流程，避免为旧数据兼容付出复杂迁移成本。

### 6.2 设计

Reset 必须显式触发，并只面向开发环境：

```text
stop services
backup optional diagnostics summary
delete or recreate memory SQLite tables / db
delete or rebuild FTS tables
clear local memory runtime artifacts if needed
start services
ensure default workspace / agent / library
run schema and smoke verification
```

可落地为：

- `Tools/Diagnostics/query_memory_library.py reset --dev-only --confirm <token>`
- 或 `Tools/Diagnostics/reset_memory_v2.py --dev-only --confirm <token>`
- 或后端 debug-only API + CLI 包装。

### 6.3 约束

- 不能默认执行。
- 不能在生产环境配置下执行。
- 必须打印被清理的数据库路径、表数量、重建结果。
- 必须写 Trace/Metrics 或至少写结构化诊断日志。

---

## 7. Repair 与诊断

F9 的 repair 第一阶段以只读诊断和 dry-run 为主。

诊断项：

- duplicate notebook title
- orphan page/chapter
- orphan pointer/relation
- broken superseded chain
- active item visible after archive/delete
- source reference missing owner
- FTS result points to missing page
- Admin tree node points to missing book/page

输出格式：

```json
{
  "schema": "pudding.memory_f9_diagnostics.v1",
  "workspaceId": "default",
  "libraryId": "library-id",
  "counts": {},
  "findings": [],
  "suggestedActions": []
}
```

Repair apply 必须后置，且必须通过 `MemoryWriteCoordinator` 或专用 verified repair path；不能让脚本直接批量改业务表。

---

## 8. Admin UI v2

### 8.1 页面布局

沿用现有 `/admin/memory-library`，升级为工作台式三栏：

- 左栏：Workspace / Agent / Library selector + Notebook/Page tree。
- 中栏：Page editor / Markdown preview / metadata。
- 右栏：Inspector，展示 relations、source references、versions、diagnostics。

### 8.2 必备能力

- 加载默认 workspace/agent/library。
- 展示 Notebook/Page Tree。
- 创建 Notebook。
- 创建 Page / child Page。
- 编辑 Page title/content/importance/status。
- 移动 Page。
- 查看 SourceReference。
- 查看和新增 relation。
- 搜索 Page，结果显示 path、status、source、superseded marker。
- 查看潜意识 Job 写入摘要。

### 8.3 UI 约束

- 不做营销页；首屏就是管理工作台。
- 不用 UI 判断“是否值得记忆”。
- 不展示完整原始会话或完整 LLM 输出，只展示可追溯引用和截断摘要。
- 前端包管理使用 `pnpm`。
- 视觉保持现有 Admin 的紧凑、安静、可扫描风格。

---

## 9. Observability

F9 必须记录：

- reset requested / rejected / executed
- diagnostics started / completed
- admin write command generated
- admin write command executed / rejected
- UI-visible relation/source/reference load failures

Metrics 维度：

- workspaceId
- agentId
- libraryId
- operation
- status
- findingType
- affectedCount

---

## 10. 测试与验收

后端：

- reset 工具在非 dev 环境拒绝执行。
- reset 后 schema/FTS/default library 初始化正常。
- diagnostics 能发现 orphan pointer、broken supersede、FTS stale result。
- Admin create/update/archive/delete 进入 coordinator。
- Admin source identity 必须包含 admin user / workspace / agent / library。

前端：

- `/admin/memory-library` 可以加载默认 workspace/agent/library。
- tree 展开稳定。
- Page 详情可显示 content、source、status、relations。
- 创建 Notebook、创建 child Page、编辑 Page 后刷新仍存在。
- 搜索结果显示 path/source/status/superseded。
- 关系边新增后 Inspector 可见。

端到端：

- 重置开发数据库。
- 启动服务。
- Admin UI 创建 Notebook/Page/Relation。
- 搜索命中新 Page。
- 诊断脚本无 orphan/stale critical findings。

---

## 11. 分阶段落地

F9a Reset/diagnostics design:

- 定义 reset 命令和保护条件。
- 定义 diagnostics JSON schema。
- 不接 UI。

F9b Admin API command 化:

- 将 create/update/archive/delete 映射为 `MemoryWriteCommand`。
- 保留读取路径。
- 增加 admin source identity。

F9c Admin UI v2 semantic upgrade:

- 文案和 DTO 语义从 Book/Chapter 转为 Notebook/Page。
- 增加 relation/source/version inspector。
- 增加基础前端测试。

F9d Playwright / smoke:

- 覆盖 `/admin/memory-library` 加载、树展开、创建、编辑、搜索。

F9e Repair apply:

- 在 dry-run 诊断稳定后，增加受限 repair apply。
- apply 必须可审计、可回滚或可通过版本链恢复。

---

## 12. 验收门槛

F9 完成时必须满足：

- 开发环境可重置 Memory v2 数据。
- 重置后默认 Library 可用。
- Admin UI 可管理 Notebook/Page Tree。
- Admin 写入不会绕过 `MemoryWriteCoordinator`。
- 关系、来源、版本状态可被 UI 和诊断脚本查看。
- 至少一组后端测试、一组前端测试或 Playwright smoke 覆盖关键路径。
