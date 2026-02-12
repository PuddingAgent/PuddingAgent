# ADR-047：记忆图书馆知识图谱演进方案

> 状态：**Proposed**  
> 日期：2026-06-03  
> 范围：MemoryLibrary、MemoryFacts、MemoryPreferences、MemoryRecallService、MemoryLibrarian、PuddingPlatformAdmin 记忆图书馆管理台、Admin API  
> 关联：[29ADR-028记忆图书馆基础设施重构ADR](29ADR-028记忆图书馆基础设施重构ADR.md)、[30ADR-029记忆图书馆ADR-028纠偏与验收闭环方案](30ADR-029记忆图书馆ADR-028纠偏与验收闭环方案.md)、[31ADR-030记忆图书馆Page管理器ADR](31ADR-030记忆图书馆Page管理器ADR.md)、[15潜意识LLM子代理系统ADR](15潜意识LLM子代理系统ADR.md)

---

## 1. Context

Pudding 的记忆图书馆当前已经形成两条并行能力：

- 可读的图书馆结构：`Library -> MemoryTreeNode -> Book -> Chapter`，用于管理员浏览、编辑、归档和溯源。
- 可召回的潜意识事实：`MemoryFacts`、`MemoryPreferences`，用于从会话中提纯用户事实和偏好，并参与 `MemoryRecallService`。

这两条路径解决了“记住内容”和“管理内容”的第一阶段问题，但长期方向仍不够表达真实记忆网络：

- 章节、事实、实体不是孤立存在的，它们之间有关系。
- 事实和实体具有属性，例如时间、地点、置信度、来源、状态、版本。
- 同一实体可能有别名、重复项、冲突事实和多条证据。
- 管理员需要看见“为什么系统相信这件事”，而不是只看提纯后的自然语言片段。
- 召回不应只依赖全文和向量，也应能沿实体和关系扩展上下文。

因此，记忆图书馆需要从“树状 Page 管理器”演进为“可读文档层 + 可推理图谱层”的组合。

---

## 2. Decision

记忆图书馆不推翻 ADR-028/029/030 的树状模型，而是在其上新增知识图谱层。

核心分层：

```text
Human-readable layer
  Library
    MemoryTreeNode
      Book
        Chapter

Graph reasoning layer
  GraphEntity
  GraphFact
  GraphRelation
  GraphAttribute
  GraphEvidence
  GraphProposal
```

### 2.1 文档层继续服务人类管理

`Library / TreeNode / Book / Chapter` 仍是管理员的主要阅读和整理模型：

- TreeNode 表达目录、主题、货架和系统分组。
- Book 表达一组完整记忆或主题档案。
- Chapter 表达可阅读、可编辑、可溯源的内容块。
- SourceReference 和 Pointer 继续承担原始来源与文档间引用。

这些结构不要求具备完整图谱语义。它们的首要职责是让人能读、能管、能追溯。

### 2.2 图谱层服务实体、事实、关系和推理

新增图谱层承载结构化知识：

- Entity：人、组织、地点、项目、概念、工具、疾病、文件、Agent 等实体。
- Fact：一条可被核验的断言，例如“姚明出生于上海”。
- Relation：实体、事实、文档之间的有向关系，例如“出生地”“隶属于”“来源于”“提及”。
- Attribute：实体或事实的属性，例如时间、地点、单位、数值、别名、置信度。
- Evidence：Fact 或 Relation 与 Chapter、SourceReference、session、file、url 的证据绑定。
- Proposal：LLM 提出的待审核实体、事实、关系、合并和冲突处理建议。

### 2.3 事实必须是一等对象

不要把所有语义都压扁成 `subject -> predicate -> object` 的边。事实需要独立身份。

例如：

```text
GraphFact
  statement: "姚明出生于上海"
  subject: GraphEntity("姚明")
  predicate: "出生地"
  object: GraphEntity("上海")
  time: null
  place: GraphEntity("上海")
  confidence: 0.92
  status: accepted
  evidence: Chapter / SourceReference / session slice
```

原因：

- 同一实体关系可能随时间变化。
- 两条来源可能支持或反驳同一断言。
- LLM 抽取结果需要审核、合并、废弃和版本化。
- 召回需要返回事实本身、证据链和相关实体，而不只是边。

### 2.4 管理台默认显示局部图谱

管理界面不默认渲染全量知识图谱。默认图谱视图从当前选中对象展开：

- 选中 Chapter：显示该章节提取出的实体、事实、关系和证据。
- 选中 Book：显示本书覆盖的实体和事实簇。
- 选中 Entity：显示该实体的一跳关系、关键事实、来源章节和冲突项。
- 选中 Fact：显示主语、谓词、宾语、属性、证据和审核历史。

默认只展开 1 跳，允许管理员手动扩展到 2 跳。超过 2 跳应走搜索、筛选或专门的探索视图。

---

## 3. Non-goals

本 ADR 不要求一次性完成以下能力：

- 不替换现有 `MemoryLibrary` 树状 Page Manager。
- 不删除 `MemoryFacts` 和 `MemoryPreferences`。
- 不引入外部图数据库作为强依赖。
- 不做全自动图谱重排和未经审核的批量写入。
- 不做多人实时协同编辑。
- 不做全量大图默认渲染。
- 不把 LLM 输出的自然语言直接当成已确认事实。

---

## 4. Domain Model

### 4.1 GraphEntity

实体是图谱中的稳定对象。

目标字段：

```text
GraphEntities
  EntityId
  WorkspaceId
  AgentId
  LibraryId
  EntityType       person | organization | place | project | concept | tool | event | document | agent | other
  CanonicalName
  Description
  AliasesJson
  PropertiesJson
  Confidence
  Status           pending | accepted | rejected | archived | merged
  MergedIntoEntityId
  CreatedAt
  UpdatedAt
```

规则：

- `WorkspaceId` 是强隔离边界。
- `AgentId` 对齐 ADR-030 的 agent scoped Library。
- `LibraryId` 表示实体所属的主要图书馆，跨 Library 关系未来另行设计。
- `AliasesJson` 用于名称归一化，但不直接替代实体合并。
- `PropertiesJson` 只承载低风险扩展属性，核心关系仍应建模为 Fact 或 Relation。

### 4.2 GraphFact

事实是可审核、可溯源、可冲突处理的断言。

目标字段：

```text
GraphFacts
  FactId
  WorkspaceId
  AgentId
  LibraryId
  Statement
  SubjectEntityId
  Predicate
  ObjectEntityId
  ObjectLiteral
  QualifiersJson
  OccurredAt
  ValidFrom
  ValidTo
  Confidence
  Status           pending | accepted | rejected | superseded | archived
  SupersededByFactId
  CreatedAt
  UpdatedAt
```

规则：

- `ObjectEntityId` 和 `ObjectLiteral` 二选一或按谓词定义组合使用。
- 时间不只一个字段：`OccurredAt` 表示事件发生时间，`ValidFrom/ValidTo` 表示事实有效期。
- 地点、单位、数值、角色、条件等放入 `QualifiersJson`，后续可逐步规格化。
- `Statement` 是人类可读摘要，不是唯一事实源。

### 4.3 GraphRelation

关系表达图谱对象之间的有向连接。

目标字段：

```text
GraphRelations
  RelationId
  WorkspaceId
  AgentId
  LibraryId
  SourceType       entity | fact | chapter | book | tree_node | source_reference
  SourceId
  RelationType
  TargetType       entity | fact | chapter | book | tree_node | source_reference | url | file | session
  TargetId
  Label
  PropertiesJson
  Confidence
  Status           pending | accepted | rejected | archived
  CreatedAt
  UpdatedAt
```

关系类型示例：

```text
mentions
supports
contradicts
derived_from
located_in
member_of
owns
authored_by
works_for
causes
depends_on
related_to
```

### 4.4 GraphEvidence

证据把事实或关系绑定到原始来源。

目标字段：

```text
GraphEvidence
  EvidenceId
  WorkspaceId
  AgentId
  LibraryId
  ClaimType        fact | relation | entity
  ClaimId
  SourceReferenceId
  ChapterId
  SourceType       chapter | source_reference | session | session_event | session_slice | file | url | memo | subagent_run
  SourceId
  SourceRange
  Quote
  SupportType      supports | contradicts | mentions | weak_support
  Confidence
  CreatedAt
```

规则：

- `Quote` 只能保存短摘录或摘要，不能复制大段来源。
- `SourceReferenceId` 优先于散落的 source 字段。
- 证据缺失时，事实只能停留在 `pending` 或低置信 `accepted`，不能作为高可信召回依据。

### 4.5 GraphProposal

LLM 抽取和整理建议必须先进入 Proposal。

目标字段：

```text
GraphProposals
  ProposalId
  WorkspaceId
  AgentId
  LibraryId
  ProposalType     create_entity | create_fact | create_relation | merge_entity | reject_fact | supersede_fact
  PayloadJson
  SourceReferenceId
  ChapterId
  Confidence
  Status           pending | accepted | rejected | applied | failed
  ReviewerId
  ReviewNote
  CreatedAt
  ReviewedAt
  AppliedAt
```

规则：

- LLM 不直接写入 `accepted` 图谱。
- 管理员或受信任策略可以把 Proposal 应用为实体、事实、关系变更。
- 应用失败必须保留错误信息，不能静默丢弃。

---

## 5. API Boundary

图谱管理 API 必须独立于 Runtime Tools，归属 Admin API。

建议 Controller：

```text
PuddingPlatform.Controllers.Api.MemoryGraphAdminController
```

初始只读 API：

```http
GET /api/admin/memory-graph/workspaces/{workspaceId}/agents/{agentId}/libraries/{libraryId}/entities
GET /api/admin/memory-graph/workspaces/{workspaceId}/agents/{agentId}/libraries/{libraryId}/facts
GET /api/admin/memory-graph/workspaces/{workspaceId}/agents/{agentId}/libraries/{libraryId}/relations
GET /api/admin/memory-graph/workspaces/{workspaceId}/agents/{agentId}/libraries/{libraryId}/neighborhood?sourceType=&sourceId=&depth=
GET /api/admin/memory-graph/workspaces/{workspaceId}/agents/{agentId}/libraries/{libraryId}/evidence?claimType=&claimId=
GET /api/admin/memory-graph/workspaces/{workspaceId}/agents/{agentId}/libraries/{libraryId}/proposals?status=
```

后续写入 API：

```http
POST /api/admin/memory-graph/workspaces/{workspaceId}/agents/{agentId}/libraries/{libraryId}/entities
PUT  /api/admin/memory-graph/workspaces/{workspaceId}/agents/{agentId}/libraries/{libraryId}/entities/{entityId}
POST /api/admin/memory-graph/workspaces/{workspaceId}/agents/{agentId}/libraries/{libraryId}/facts
PUT  /api/admin/memory-graph/workspaces/{workspaceId}/agents/{agentId}/libraries/{libraryId}/facts/{factId}
POST /api/admin/memory-graph/workspaces/{workspaceId}/agents/{agentId}/libraries/{libraryId}/relations
POST /api/admin/memory-graph/workspaces/{workspaceId}/agents/{agentId}/libraries/{libraryId}/proposals/{proposalId}/accept
POST /api/admin/memory-graph/workspaces/{workspaceId}/agents/{agentId}/libraries/{libraryId}/proposals/{proposalId}/reject
```

安全规则：

- 路由中的 `workspaceId/agentId/libraryId` 是安全边界，请求体同名字段不作为授权依据。
- 后端必须反查 `entityId/factId/relationId/proposalId` 是否属于当前 scope。
- legacy `AgentId = null` Library 只能通过兼容只读入口投影为图谱，不自动绑定到任意 Agent。
- 跨 workspace 图谱探索必须另开管理员专用能力。

---

## 6. Admin UI

记忆图书馆管理台演进为四种视图，但仍是同一工作台，不新增营销式页面。

```text
Top Toolbar
  Workspace | Agent | Library | Search | View: Pages / Graph / Facts / Review

Left Pane
  Pages: Memory Page Tree
  Graph: Entity type filters and relation filters
  Facts: Fact filters
  Review: Proposal queue

Center Pane
  Pages: existing Book / Chapter editor
  Graph: local graph neighborhood
  Facts: searchable fact table
  Review: proposal diff and apply preview

Right Inspector
  Info | Properties | Relations | Evidence | Audit
```

### 6.1 Pages View

保持 ADR-030 的三栏 Page Manager：

- 左侧 Tree。
- 中间 Book / Chapter。
- 右侧 Source / Links / Metadata。

新增图谱提示：

- Chapter 下显示已抽取实体数、事实数、待审核建议数。
- Book 下显示主要实体和高置信事实摘要。
- Inspector 增加 Graph 标签页，跳转到局部图谱。

### 6.2 Graph View

局部图谱是默认体验：

- 初始中心节点来自当前选中的 Chapter、Book、Entity 或 Fact。
- 默认深度为 1。
- 节点按类型区分：Entity、Fact、Chapter、Book、Source。
- 边显示 RelationType。
- 支持筛选实体类型、关系类型、状态和置信度。
- 支持点击节点更新 Inspector。

交互限制：

- 不默认加载全量图。
- 超过阈值时折叠为分组节点，例如“还有 42 条相关事实”。
- 不在画布上直接执行危险写入，写入走 Inspector 或 Review。

### 6.3 Facts View

事实视图用于审计和批量浏览：

- 表格列：Statement、Subject、Predicate、Object、Time、Place、Confidence、Status、Evidence Count、UpdatedAt。
- 支持按实体、谓词、时间、地点、状态、置信度筛选。
- 点击事实打开 Inspector。
- 可从事实跳转到证据 Chapter 和 Graph View。

### 6.4 Review View

Review 是所有 LLM 图谱写入的入口：

- 展示候选实体、候选事实、候选关系、实体合并、冲突处理。
- 显示来源证据和短摘录。
- 显示应用前后的结构化 diff。
- 支持接受、拒绝、编辑后接受。
- 支持按 Book、Chapter、来源会话过滤。

视觉原则：

- 采用数据工作台风格，高密度、低装饰、可扫描。
- 图谱画布是工具面板，不做沉浸式装饰。
- 保持 Pudding 后台的克制设计语言。
- 所有可点击元素有清晰 hover、focus 和 loading 状态。
- 大图操作必须有性能保护和空状态。

---

## 7. Ingestion And Review Flow

潜意识写入链路分为两步：提案和应用。

```text
Session / Chapter / SourceReference
        |
        v
MemoryLibrarian extracts graph candidates
        |
        v
GraphProposal pending
        |
        v
Admin review or trusted policy
        |
        v
GraphEntity / GraphFact / GraphRelation / GraphEvidence
```

规则：

- `MemoryLibrarian` 可以调用 LLM 抽取候选实体、事实和关系。
- `MemoryLibrary Core` 和 `MemoryGraph Core` 只执行结构化操作。
- 默认策略下，LLM 提取结果进入 `pending`。
- 受信任低风险规则可以自动应用，例如同一 Chapter 内的 `mentions` 关系。
- 高风险操作必须人工审核，例如实体合并、事实覆盖、冲突事实拒绝。

---

## 8. Recall Integration

图谱进入召回后，`MemoryRecallService` 从三路召回演进为四路融合：

```text
Library FTS / Vector
MemoryFacts / Preferences fallback
Graph entity / fact neighborhood
Source-aware expansion
```

建议流程：

1. 使用全文、向量或关键词命中 Chapter、Book、Fact、Entity。
2. 将命中对象映射到局部图谱中心节点。
3. 沿高置信 `accepted` 关系扩展 1 跳。
4. 读取相关 `GraphEvidence` 和 `SourceReference`。
5. 使用 RRF 或加权评分融合文档召回、事实召回和图谱召回。
6. 返回给上下文装配层时保留来源摘要和图谱路径。

召回输出建议增加：

```text
RecalledMemory
  GraphEntities
  GraphFacts
  GraphPath
  EvidenceSummaries
```

注意：

- pending / rejected 图谱数据默认不进入运行时 prompt。
- 低置信事实可以用于探索，但不能作为强断言注入。
- 冲突事实应显式标记，不能静默选择其中一个。

---

## 9. Migration Strategy

### Phase 0：ADR 和模型确认

产出：

- 本 ADR。
- 后续实施计划。
- 数据模型和 API 边界评审。

不做：

- 不新增数据库表。
- 不改运行时召回。
- 不改前端 UI。

### Phase 1：只读图谱投影

目标：

- 不新增或少量新增表。
- 将现有 `MemoryFacts`、`MemoryPreferences`、`Pointer`、`SourceReference`、`Book/Chapter` 投影为只读图谱 DTO。
- 在管理台增加 Graph View 的只读局部图谱。

验收：

- 从 Chapter 能看到提取出的事实和来源。
- 从 Book 能看到相关实体和事实簇。
- 从 Fact 能跳转到证据 Chapter。
- 不改变现有写入链路。

### Phase 2：实体和事实表落地

目标：

- 新增 GraphEntities、GraphFacts、GraphRelations、GraphEvidence。
- 提供 Admin API 的基础 CRUD。
- 管理台提供 Facts View 和 Entity Inspector。
- 支持从旧 `MemoryFacts` 迁移或同步到 GraphFacts。

验收：

- workspace + agent + library scope 完整。
- 图谱 CRUD 有服务测试和 API 测试。
- legacy 数据可只读投影，不被误绑定。

### Phase 3：Proposal 审核流

目标：

- 新增 GraphProposals。
- MemoryLibrarian 抽取候选实体、事实、关系。
- Review View 支持接受、拒绝、编辑后接受。

验收：

- LLM 输出不会直接进入 accepted 图谱。
- Proposal 应用具备结构化 diff。
- 实体合并和事实覆盖可审计。

### Phase 4：图谱增强召回

目标：

- MemoryRecallService 支持基于实体和事实的一跳扩展。
- RecalledMemory 返回 GraphPath 和 EvidenceSummaries。
- ContextPipeline 能按配置启用或禁用图谱召回。

验收：

- 搜索命中实体时能召回其高置信事实和来源章节。
- rejected/pending 不进入默认 prompt。
- 冲突事实明确标记。

### Phase 5：治理和维护

目标：

- 实体去重。
- 事实冲突检测。
- 关系类型治理。
- 图谱健康度指标。
- 批量归档和导出。

验收：

- 管理员能发现重复实体。
- 管理员能处理冲突事实。
- 图谱增长不会使管理台默认视图不可用。

---

## 10. Testing Strategy

后端测试：

- Graph scope 测试：workspace/agent/library 隔离。
- Entity CRUD 测试：别名、属性、状态、合并。
- Fact CRUD 测试：实体对象、字面量对象、时间有效期、状态。
- Evidence 测试：Fact/Relation 到 SourceReference 和 Chapter 的绑定。
- Proposal 测试：pending 到 accepted/rejected/applied 的状态机。
- Recall 测试：图谱召回只使用 accepted 高置信数据。

前端测试：

- Graph View 空状态和局部展开。
- Facts View 筛选和 Inspector 打开。
- Review View 接受/拒绝按钮 loading 和错误状态。
- 页面切换 workspace/agent/library 后清空选中状态。

手工 QA：

- 使用本地管理台验证 Pages/Graph/Facts/Review 四视图切换。
- 验证大节点数量下 Graph View 不默认拉取全量数据。
- 验证来源缺失、权限不足、legacy library 的提示。

---

## 11. Risks

### 11.1 图谱过度建模

风险：过早引入复杂 ontology，会拖慢落地。

控制：

- Phase 1 只读投影。
- Phase 2 先使用宽松的 EntityType / RelationType 字符串。
- PropertiesJson 作为过渡，不急于完全规格化。

### 11.2 LLM 错误污染图谱

风险：抽取错误事实后进入召回，影响 Agent 行为。

控制：

- LLM 写入必须先进入 Proposal。
- pending 默认不进入 prompt。
- accepted 事实必须保留证据。

### 11.3 全量图谱 UI 不可用

风险：图谱节点变多后画布难以阅读，性能下降。

控制：

- 默认局部图谱。
- 默认 1 跳，手动 2 跳。
- 后端分页和节点阈值。
- 过量邻居折叠为聚合节点。

### 11.4 与现有 MemoryFacts 双轨更复杂

风险：GraphFacts 与 MemoryFacts 同时存在，造成数据重复。

控制：

- Phase 1 只做投影。
- Phase 2 明确同步或迁移策略。
- Runtime 召回按配置选择主路径，保留 fallback。

---

## 12. Acceptance Criteria

本 ADR 进入实施前需要满足：

- 数据模型、API 边界和 UI 信息架构评审通过。
- 明确 Phase 1 只读图谱投影为首个实现目标。
- 明确不把 LLM 抽取结果直接写入 accepted 图谱。
- 明确 legacy workspace-only Library 只读兼容，不自动绑定 Agent。

Phase 1 完成标准：

- 管理台在选中 Chapter / Book 时能显示局部图谱。
- 后端能从现有数据返回局部图谱 DTO。
- 不新增危险写操作。
- 现有 Memory Library Page Manager 功能不回退。
- 服务测试、API 测试和前端基础校验通过。

