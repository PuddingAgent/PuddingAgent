# 31 ADR-030 记忆图书馆 Page 管理器方案

> 状态：**proposed**
> 日期：2026-05-21
> 范围：PuddingPlatformAdmin、PuddingPlatform Admin API、MemoryLibrary read/write facade、TreeNode/Book/Chapter/SourceReference/Pointer 管理界面
> 前置：[29ADR-028记忆图书馆基础设施重构ADR](29ADR-028记忆图书馆基础设施重构ADR.md)、[30ADR-029记忆图书馆ADR-028纠偏与验收闭环方案](30ADR-029记忆图书馆ADR-028纠偏与验收闭环方案.md)

---

## 1. 背景

记忆图书馆不是普通数据库表，也不是单纯的向量库。它的长期形态更接近一棵可自然生长的 page tree：

- `Library` 是一个 workspace 下的图书馆。
- `TreeNode` 是目录树中的 page / folder。
- `Book` 是可挂载到一个或多个 TreeNode 的文档容器。
- `Chapter` 是 Book 内的 section / block。
- `SourceReference` 记录 page 或 block 的原始来源。
- `Pointer` 记录 page、book、chapter、source 之间的引用。

如果第一版只做数据库表格，会降低管理员理解和维护记忆的效率。管理员更需要像 Notion 一样浏览记忆树、打开 page、阅读和编辑内容，同时仍能看到底层来源、引用和审计信息。

本 ADR 决定：记忆图书馆管理器的第一版采用 **Notion-style Memory Pages**，而不是传统数据库表管理器。

---

## 2. 核心决策

### ADR-030-A：UI 以 Page Tree 为主模型

**决定**：Admin 后管新增 `Memory Library Pages`，默认视图是 workspace-scoped page tree。

界面三栏：

```text
左侧：Memory Page Tree
  Library
    TreeNode
      Book Page

中间：Page Editor / Reader
  Book title
  Book summary
  Chapters as sections / blocks

右侧：Inspector
  SourceReferences
  Pointers / backlinks
  Metadata
  Audit hints
```

原因：

- 记忆图书馆的核心认知模型是树，不是表。
- 管理员需要从大分类逐步钻取到具体记忆。
- Page 风格比 CRUD 表格更适合查看长文本、章节和来源。

### ADR-030-B：像 Notion，但不复制 Notion 的自由文档模型

**决定**：前端可以提供 Notion 风格体验，但写入仍走结构化实体，不存一整块自由 Markdown 作为唯一事实源。

映射：

```text
Page tree node      -> MemoryTreeNode
Book page           -> Book + BookTreeMount
Section / block     -> Chapter
Source panel        -> SourceReference
Backlinks panel     -> Pointer
```

允许：

- 用 page tree 浏览和创建 TreeNode。
- 用 block-like section 编辑 Chapter。
- 用 Inspector 管理 source 和 pointer。

禁止：

- 前端把整页内容作为不可解析 Markdown blob 覆盖 Book/Chapter。
- 绕过 `IMemoryLibrary` 直接操作 SQLite。
- 用 UI 规则承载 LLM 整理策略。

### ADR-030-C：第一版开放轻量编辑，危险操作延后

**决定**：V1 支持低风险编辑，不支持物理删除和批量重构。

V1 支持：

- workspace 切换。
- Library / TreeNode / Book / Chapter 浏览。
- 创建 TreeNode page。
- 创建 Book page 并挂载到当前 TreeNode。
- 编辑 Book title / summary。
- 新增 Chapter section。
- 编辑 Chapter title / content / importance。
- Archive Book / Chapter / TreeNode。
- 查看 SourceReferences。
- 查看 Pointers 和 backlinks。
- FTS 搜索并跳转到 page + chapter。

V1 不支持：

- 物理删除。
- 批量移动目录。
- 跨 workspace 搜索。
- LLM 自动重排目录。
- 多人实时协同编辑。
- 富文本块系统。

原因：

- 记忆数据有长期价值，删除和批量重构需要更强审计。
- 当前 ADR-028/029 仍在巩固基础设施，不应同时引入复杂编辑器。

### ADR-030-D：Admin API 独立于 Runtime Tools

**决定**：新增 Admin 专用 API，不复用 `save_memory`、`grep_memory`、`manage_memory` runtime tools 作为前端接口。

目标 Controller：

```text
PuddingPlatform.Controllers.Api.MemoryLibraryAdminController
```

API 路由：

```http
GET  /api/admin/memory-library/workspaces/{workspaceId}/overview
GET  /api/admin/memory-library/workspaces/{workspaceId}/libraries
GET  /api/admin/memory-library/libraries/{libraryId}/tree
GET  /api/admin/memory-library/tree-nodes/{nodeId}
POST /api/admin/memory-library/tree-nodes
PUT  /api/admin/memory-library/tree-nodes/{nodeId}
POST /api/admin/memory-library/tree-nodes/{nodeId}/archive

GET  /api/admin/memory-library/books/{bookId}
GET  /api/admin/memory-library/books/{bookId}/chapters
POST /api/admin/memory-library/books
PUT  /api/admin/memory-library/books/{bookId}
POST /api/admin/memory-library/books/{bookId}/archive

GET  /api/admin/memory-library/chapters/{chapterId}
POST /api/admin/memory-library/chapters
PUT  /api/admin/memory-library/chapters/{chapterId}
POST /api/admin/memory-library/chapters/{chapterId}/archive

GET  /api/admin/memory-library/search?workspaceId=&query=&topK=
GET  /api/admin/memory-library/sources?ownerType=&ownerId=
GET  /api/admin/memory-library/pointers?workspaceId=&sourceType=&sourceId=
```

原因：

- Runtime tools 是 Agent 能力入口，不适合作为管理 UI 的稳定 API。
- Admin API 需要权限、审计、分页、错误模型和写操作保护。

### ADR-030-E：所有查询和写入必须 workspace scoped

**决定**：Page Manager 的所有 API 必须显式绑定 workspace。

规则：

- `workspaceId` 来自路由或请求体。
- `libraryId/bookId/chapterId/nodeId` 需要在后端反查所属 workspace。
- 前端切换 workspace 后必须清空当前选中的 node/book/chapter。
- 搜索只查当前 workspace。
- 跨 workspace 管理必须另开管理员专用能力，本 ADR 不做。

### ADR-030-F：UI 风格是克制的数据工作台

**决定**：沿用 Ant Design Pro，做高密度、低装饰、可扫描的后台工具。

视觉原则：

- 左树固定宽度，支持折叠。
- 中间编辑区最大宽度约 900px，长文阅读清晰。
- 右 Inspector 宽度约 360px，可折叠。
- 用 `ProTable` / `Tree` / `Drawer` / `Tabs` / `Typography` / `Space` / `Tag`。
- 不做营销式 hero，不做大面积装饰渐变。
- 状态色克制：active、archived、system、dirty、source-missing。

原因：

- 这是运维和管理工具，目标是重复使用效率。
- Notion 风格体现在 page tree 和 section 编辑，而不是视觉装饰。

---

## 3. 页面信息架构

### 3.1 顶部工具条

```text
WorkspaceSelect | SearchInput | New Page | New Book | Refresh
```

要求：

- `WorkspaceSelect` 复用现有 workspace API。
- 搜索框默认查当前 workspace 的 chapter FTS。
- 新建操作默认挂到当前选中的 TreeNode。

### 3.2 左侧 Memory Page Tree

节点类型：

```text
library
tree_node: root | category | shelf | topic | system
book_page
```

行为：

- 点击 TreeNode：中间显示目录 page。
- 点击 Book：中间显示 Book page。
- 节点右键或更多菜单：rename、archive、new child page、new book here。
- system 节点默认只读，除非进入高级模式。

### 3.3 中间 Page Editor

TreeNode page：

- 标题、摘要、路径。
- 子 page 列表。
- 挂载 books 列表。
- 空状态可创建 child page 或 book。

Book page：

- Book title。
- Summary。
- metadata tags。
- Chapters 作为 sections。
- 每个 section 可展开编辑 title/content/importance。

编辑策略：

- V1 使用表单和 textarea，不做富文本。
- 单个 Chapter 保存，避免整页覆盖。
- 未保存状态在 section 层显示。

### 3.4 右侧 Inspector

Tabs：

```text
Info | Sources | Links | Audit
```

Info：

- ids、workspace、created/updated、status、version、access count。

Sources：

- SourceReference list。
- target type / target id / range / label / resolve status。
- 支持复制 pointer。
- session source 支持跳转到 session 页面。

Links：

- outgoing pointers。
- backlinks。

Audit：

- V1 显示最近操作摘要；后续接入 RuntimeActivity / AuditEvent 后扩展为完整时间线。
- 后续接入 RuntimeActivity / AuditEvent。

---

## 4. 后端边界

新增服务建议：

```csharp
public interface IMemoryLibraryAdminService
{
    Task<MemoryLibraryOverviewDto> GetOverviewAsync(string workspaceId, CancellationToken ct);
    Task<IReadOnlyList<MemoryLibraryTreeNodeDto>> GetTreeAsync(string libraryId, CancellationToken ct);
    Task<MemoryPageDto> GetTreeNodePageAsync(string nodeId, CancellationToken ct);
    Task<MemoryBookPageDto> GetBookPageAsync(string bookId, CancellationToken ct);
    Task<IReadOnlyList<MemorySearchResultDto>> SearchAsync(string workspaceId, string query, int topK, CancellationToken ct);
}
```

Admin service 只做 UI 聚合：

- 调用 `IMemoryLibrary` primitives。
- 做 workspace ownership 校验。
- 做 DTO projection。
- 做写操作审计。

不做：

- LLM 整理。
- 自动分类。
- 跨 workspace 合并。

---

## 5. 实施阶段

### Phase 1：Read-only Page Explorer

目标：

- Admin 菜单出现 `Memory Library`。
- 可选择 workspace。
- 可加载 library tree。
- 可打开 TreeNode page 和 Book page。
- 可查看 chapters。

验收：

- 切换 workspace 后树和 page 清空并重新加载。
- 没有 library 时显示空状态。
- API 不返回其他 workspace 数据。

### Phase 2：Search and Source Trace

目标：

- 支持 workspace scoped FTS 搜索。
- 搜索结果点击后定位到 Book + Chapter。
- Inspector 显示 SourceReferences。
- session source 可跳转到 `/session` 或 chat/session 页面。

验收：

- workspace A 搜索不会返回 workspace B。
- source pointer 可复制。
- source 缺失显示明确状态。

### Phase 3：Guarded Editing

目标：

- 创建 TreeNode。
- 创建 Book 并挂载。
- 编辑 Book title / summary。
- 新增和编辑 Chapter。
- Archive Book / Chapter / TreeNode。

验收：

- 所有写操作需要确认或保存按钮。
- archive 不物理删除。
- 保存失败时不更新本地 optimistic state。

### Phase 4：Pointers and Backlinks

目标：

- Inspector 显示 outgoing pointers 和 backlinks。
- 支持新增 pointer。
- 支持从 pointer 跳转到目标 page/source。

验收：

- Pointer API workspace scoped。
- chapter/book/tree_node/source_reference 都可作为 source。

### Phase 5：Audit and E2E

目标：

- 写操作记录 audit/runtime activity。
- Playwright 覆盖树浏览、搜索、编辑、archive。

验收：

- `pnpm typecheck` / `pnpm test` / Playwright smoke 通过。
- 后端 API tests 覆盖 ownership guard。

---

## 6. 风险

- **TreeNode 和 Book 双模型复杂**：UI 必须清晰区分目录 page 和 book page。
- **编辑体验过度膨胀**：V1 使用 section textarea，不引入块编辑器。
- **误删长期记忆**：V1 只 archive，不物理删除。
- **跨 workspace 泄漏**：后端每个 by-id API 都必须反查 workspace。
- **Source resolution 不完整**：V1 先显示 pointer 和 resolve status，不强制所有类型可跳转。

---

## 7. 完成定义

ADR-030 V1 完成后必须满足：

- 管理员能在 Admin 中以 page tree 方式浏览记忆图书馆。
- 管理员能打开 Book page 并查看 Chapter sections。
- 管理员能按 workspace 搜索并跳转到结果。
- 管理员能查看 SourceReferences 和 Pointers。
- 管理员能创建/编辑/归档 TreeNode、Book、Chapter。
- 所有 API 均 workspace scoped，并有测试覆盖。
- 前端在 1440px、1024px、768px、375px 宽度下无主要布局遮挡。
