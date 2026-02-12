# Fact-First Memory Library Design

> 状态：Draft  
> 日期：2026-06-04  
> 范围：Pudding 记忆图书馆底层数据模型重建  
> 决策摘要：以 Fact 为一等对象，环境上下文、新鲜度、关联度作为底层基础设施；不再以 `Library / TreeNode / Book / Chapter` 作为核心模型。  
> 替代方向：`Docs/07架构/48ADR-047记忆图书馆知识图谱演进ADR.md` 和 `Docs/superpowers/plans/2026-06-03-memory-graph-phase1.md` 描述的是旧树状图书馆上的图谱投影方案，不作为本轮实现依据。

---

## 1. 背景

旧版记忆图书馆把管理员可读结构放在中心：图书馆、目录节点、书、章节。这个模型适合浏览和编辑文本块，但不适合作为长期记忆的底层事实库：

- 事实、实体、来源和关系被压在文档结构里，难以独立审核、过期、替换和召回。
- 同一事实可能有多个证据、多个上下文、多个相关实体，树状位置不能表达这些关系。
- 事实会变旧，且不同类型事实的半衰期差异很大。股价可能按秒衰减，黄金价格可能按天，道路施工可能按周，某个街道存在一家饭店可能按年。
- 很多事实只有在特定环境上下文中成立。安装 MySQL 的步骤不能套用到 SQL Server；南方饮食偏好不能无条件外推到北方推荐。
- 管理系统需要支持 LLM、管理员和外部策略写入判断结果，但底层不应该替代它们做业务语义裁决。

因此，本设计直接重建记忆图书馆底层模型。图谱视图可以作为后续投影出现，但底层不是“把旧树状文档画成图”，而是先存储可核验、可衰减、可关联、可审计的事实网络。

---

## 2. 设计目标

1. **Fact-first**：事实是记忆系统的主对象。实体、证据、上下文、关联和修订都围绕事实组织。
2. **强隔离**：所有数据必须受 `WorkspaceId + AgentId` 约束；不再支持无 Agent 的 workspace-only 记忆图书馆。
3. **上下文可表达**：事实可以携带结构化环境上下文，查询时可以计算上下文匹配度。
4. **新鲜度可计算**：事实可以携带 LLM 填写的半衰期、观察时间、验证时间和有效期，底层负责计算新鲜度分数和状态。
5. **关联度可存储**：实体之间、事实之间、事实与实体之间可以表达关联强度、证据和上下文。
6. **状态只存储不裁决**：底层保存 `pending / accepted / rejected / superseded / archived` 等状态，但不自动把待审核事实提升为已接受事实。
7. **证据优先**：正式事实必须绑定来源证据，避免无出处的长期记忆污染召回。
8. **数据库式内核**：知识图谱承担类似数据库的角色，提供数据结构、事务、索引、约束、查询、审计和基础计算，不定义业务。
9. **开放词表**：事实类型、实体类型、关联类型、上下文维度和排序权重由 LLM、Librarian、管理员或上层业务策略提供，底层只保存和校验格式。
10. **可渐进落地**：第一阶段先完成 schema、领域服务、计算和测试；管理 UI、Runtime 召回和旧数据导入后置。

---

## 3. 非目标

- 不继续维护旧版 `Library / TreeNode / Book / Chapter` 作为核心数据模型。
- 不在第一阶段实现图谱可视化管理界面。
- 不引入外部图数据库作为强依赖。
- 不在底层服务里实现 LLM 抽取、事实验真、自动接纳或业务半衰期策略。
- 不在底层服务里内置业务类型体系，例如疾病关系、地理关系、软件安装关系、饮食偏好关系。
- 不要求自动迁移旧图书馆数据。后续如需导入，只能作为显式导入工具把旧内容转成待审核事实。
- 不在第一阶段改造 Runtime 召回链路。

---

## 4. 基础设施边界

知识图谱层应像数据库，而不是像业务专家系统。

底层负责：

- 数据结构：事实、实体提及、关联、证据、上下文、新鲜度、修订。
- 一致性：租户隔离、引用完整性、状态合法性、字段范围校验。
- 存取能力：事务、索引、过滤、分页、排序、聚合和局部邻域查询。
- 基础计算：新鲜度分数、通用上下文匹配分数、关联权重聚合。
- 可解释性：证据链、revision、source hash、reason 字段。

底层不负责：

- 定义某个业务领域有哪些实体类型。
- 定义某个业务领域有哪些关系类型。
- 判断某条业务关系是否真实。
- 决定某种事实类型的默认半衰期。
- 决定上下文维度的业务含义。
- 决定召回排序的业务权重。

因此，`FactType`、`EntityType`、`AssociationType` 和 `ContextJson` 的 key 都是开放字符串。底层可以提供可选的 schema registry 来帮助校验“这个 Agent 当前允许哪些类型和上下文字段”，但 registry 本身仍是数据，不是写死在代码里的业务规则。

---

## 5. 核心对象

```text
MemorySpace
  Fact
    FactEvidence
    FactContext
    FactFreshness
    FactEntityMention
    FactAssociation
    FactRevision
```

`MemorySpace` 只承担事实集合的命名、权限边界和管理分组职责，不能退化成旧版 Library。事实不属于章节；事实属于某个 Agent 的某个 MemorySpace。

---

## 6. 记忆管理角色

新版记忆图书馆必须同时支持两类写入者：

1. **显意识 LLM / 运行中的 Agent**：Agent 可以在执行任务时主动管理自己的记忆。
2. **潜意识 Librarian LLM**：会话结束后或按日运行，负责异步总结、提取、合并、补证据和发现冲突。

这两类写入者都是 Memory Library Core 的调用方。它们可以拥有策略和判断能力，但底层仍只提供数据库式基础设施。

### 6.1 显意识 LLM 自主管理自己的记忆

显意识 LLM 应该拥有自己 MemorySpace 的隐式自主管理权，不需要额外授权流程。它可以在对话和任务执行过程中直接更正、补充和修改自己的记忆图书馆。

它应该可以维护自己的 MemorySpace，包括：

- 主动记录当前任务中明确获得的事实。
- 给事实补充 evidence、context、freshness 和 entity mentions。
- 建立实体、事实、上下文之间的 association。
- 标记自己写入的事实为 superseded 或 archived。
- 把自己 MemorySpace 内的 pending fact 更新为 accepted。
- 更正自己 MemorySpace 内事实的 statement、payload、context、freshness 和 association。
- 查询、解释、重新验证和修正自己的历史记忆。

这里的“无需权限”指不需要对自有 MemorySpace 走额外授权或审批，不表示可以跳过底层数据约束。它仍然受以下系统边界限制：

- 只能操作当前 `WorkspaceId + AgentId` 下自己的 MemorySpace。
- 所有写入必须有 evidence。
- 所有状态变更必须写 revision。
- 所有修改必须保留 before/after 和 actor 信息。
- Agent 不能绕过 freshness、context 和 evidence 结构直接写入不可解释的自然语言黑箱。
- Agent 对其他 Agent 的记忆只能读取或引用，不能修改，除非被显式授予跨 Agent 管理权限。
- 硬删除、跨 Agent 修改、schema update、全局实体 merge/split 仍属于系统级高风险操作。

### 6.2 潜意识 Librarian LLM

潜意识 Librarian LLM 是异步记忆维护者。它不在用户交互主链路里抢答，而是在会话结束、任务结束、空闲窗口或每日计划任务中运行。

潜意识 LLM 通常可以配置为廉价、快速、大上下文的 flash 类模型。它适合处理大量 message 级 transcript，而不是低层会话消息帧、delta 帧或 UI 事件帧。只要保留 source id 和 message range，它可以用较大的上下文窗口暴力扫描本轮会话、近期会话和候选资料。

职责：

- 从会话 transcript、tool results、session events 和文件证据中抽取候选 facts。
- 生成 entity mentions 和 associations。
- 填写 context、freshness、half-life、confidence 和 reason。
- 对新候选 fact 做去重、相似合并、冲突检测和 supersede 建议。
- 给旧事实补充证据或更新 LastVerifiedAt。
- 发现 stale/expired facts，并生成 refresh proposals。
- 产生日志和 revision，供管理台审计。

潜意识 Librarian LLM 可以有比普通 Agent 更高的记忆维护权限，但仍由能力策略控制。它可以自动 accepted 哪些事实，不由 Memory Library Core 决定，而由独立的 Librarian policy 决定。

### 6.3 同步和异步写入的关系

同步显意识 LLM 写入解决“任务过程中立即需要记住、修正或使用”的问题。异步 Librarian 写入解决“会话结束后系统化整理”的问题。

推荐关系：

```text
Runtime Agent
  -> create/update/accept own facts during task
  -> attach immediate evidence
  -> use facts in current context

Subconscious Librarian
  -> read completed sessions and daily windows
  -> consolidate facts/entities/associations
  -> deduplicate and detect conflicts
  -> update freshness and revisions
  -> apply or propose status changes according to policy
```

如果两者写入冲突，底层不自动裁决。冲突应表现为：

- 新 fact 的 `Status = pending`。
- AssociationType 可由上层设置为 `contradicts` 或其他开放类型。
- Revision 记录冲突来源。
- 管理台或 Librarian policy 决定 accepted、rejected 或 superseded。

### 6.4 潜意识触发策略

潜意识记忆提取可以延迟执行，不要求阻塞用户响应。

推荐触发条件：

- 会话显式停止或关闭。
- 会话不活动达到配置阈值，默认建议为 15 分钟。
- 每日定时任务，对当天 message 级 transcript 做批处理。
- Runtime 检测到会话产生大量新消息、重要工具结果或用户明确表达长期偏好。

调度要求：

- 使用 message 级 transcript 作为主要输入，而不是逐帧事件流。
- consolidation job 必须幂等，避免同一消息范围重复写入。
- 每个 job 记录 `workspaceId`、`agentId`、`sessionId`、message range、触发原因和模型配置。
- 异步写入允许延迟；如果本轮对话已经结束，结果可以服务下一轮召回。
- 失败要记录 job log，不能影响显意识 LLM 的主响应链路。

---

## 7. MemorySpace

`MemorySpace` 是新版记忆图书馆的空间边界。

字段：

- `MemorySpaceId`：稳定 ID。
- `WorkspaceId`：工作区 ID。
- `AgentId`：Agent 实例 ID，必填。
- `Name`：空间名，例如“默认记忆空间”。
- `Description`：空间说明。
- `Status`：`active / archived`。
- `CreatedAt`、`UpdatedAt`。

规则：

- 同一个 `WorkspaceId + AgentId` 可以有多个 MemorySpace。
- Agent 创建时应自动创建默认 MemorySpace。
- 所有事实、证据、上下文、关联和修订都必须能追溯到同一个 `WorkspaceId + AgentId`。

---

## 8. Fact

`Fact` 是长期记忆的最小可管理单元。事实可以是自然语言断言，也可以携带结构化 payload。

字段：

- `FactId`：稳定 ID。
- `WorkspaceId`、`AgentId`、`MemorySpaceId`。
- `Statement`：人类可读断言，例如“用户的主要开发机器在 2026-06-04 安装了 OpenSSL”。
- `StructuredPayloadJson`：结构化事实内容。可以表达 subject、predicate、object、数值、单位、时间、地点、版本等。
- `FactType`：开放事实类型字符串，由调用方或可选 schema registry 定义。底层只要求非空、长度合法、格式稳定。
- `Confidence`：事实置信度，范围 `0.0` 到 `1.0`。
- `Status`：`pending / accepted / rejected / superseded / archived`。
- `SupersededByFactId`：被替代事实 ID。
- `CreatedByType`：`llm / admin / system / tool / import`。
- `CreatedById`：创建者标识。
- `CreatedAt`、`UpdatedAt`、`AcceptedAt`、`RejectedAt`、`ArchivedAt`。

状态语义：

- `pending`：已记录但未被正式接纳，可供管理台审核和策略处理。
- `accepted`：已被外部策略接纳，可被上层策略作为稳定事实使用。
- `rejected`：保留审计，上层策略通常会在普通查询中排除。
- `superseded`：被新事实替代，上层策略通常会排除，但可用于历史追溯。
- `archived`：管理归档，上层策略通常会排除。

约束：

- 创建事实必须在同一事务中至少写入一条 `FactEvidence`。
- 将事实更新为 `accepted` 时，底层只校验证据存在、字段合法和租户一致，不判断语义真假。
- `superseded` 状态必须填写 `SupersededByFactId`。

---

## 9. FactEvidence

`FactEvidence` 记录事实的来源。证据不等于“绝对真实”，只表示系统为什么保存这条事实。

字段：

- `EvidenceId`。
- `WorkspaceId`、`AgentId`、`MemorySpaceId`、`FactId`。
- `SourceType`：`user_assertion / session_slice / session_event / file / url / tool_result / subagent_run / manual_note / external_observation / import`。
- `SourceId`：来源对象 ID，例如 session id、file id、url hash、tool call id。
- `SourceRange`：来源范围，例如消息区间、文件行号、网页片段位置。
- `QuoteSummary`：证据摘要，避免长期保存大段原文。
- `EvidenceHash`：来源内容 hash，用于去重和篡改检测。
- `Confidence`：证据可信度，范围 `0.0` 到 `1.0`。
- `CreatedAt`。

规则：

- 一个事实可以有多条证据。
- 多个事实可以引用同一来源，但必须各自有独立 evidence 记录。
- Evidence 只记录来源和摘要；大体量原文仍由原始日志、文件或外部存储承担。

---

## 10. FactContext

`FactContext` 表达事实成立的环境上下文。上下文是结构化条件，不是普通标签。

字段：

- `ContextId`。
- `WorkspaceId`、`AgentId`、`MemorySpaceId`、`FactId`。
- `ContextJson`：结构化上下文。
- `ContextHash`：规范化上下文 hash。
- `CreatedAt`、`UpdatedAt`。

上下文示例：

```json
{
  "product": "mysql",
  "system": "windows",
  "version": "8.0",
  "region": "south_china",
  "locale": "zh-CN",
  "user": "default",
  "project": "PuddingAgent",
  "task": "database_installation",
  "environment": "developer_machine",
  "time_scope": {
    "valid_from": "2026-06-04T00:00:00Z",
    "valid_to": null
  },
  "scenario": "local_setup"
}
```

底层职责：

- 存储上下文。
- 提供精确匹配、包含匹配和调用方权重匹配的基础函数。
- 为查询返回 `ContextMatchScore`，范围 `0.0` 到 `1.0`。

非底层职责：

- 判断“MySQL 步骤是否能用于 SQL Server”这类业务语义。
- 决定缺失上下文时是否可以外推。
- 自动补全上下文维度。
- 在代码里内置 `product / system / region / project` 等业务维度含义。

上下文匹配基础规则：

- 查询上下文与事实上下文完全一致时，分数为 `1.0`。
- 查询请求可以传入 `RequiredKeys`。这些 key 的值冲突时，分数为 `0.0`。
- 查询请求可以传入 `WeightedKeys`。这些 key 按调用方给定权重参与打分。
- 查询上下文包含事实所需的全部 `RequiredKeys` 且无冲突时，基础分不低于 `0.8`。
- 未声明为 required 的 key 缺失时不直接判零，只按权重降低分数。
- 如果调用方没有传入 required/weighted 配置，底层只做通用结构匹配，不推断业务强弱维度。

---

## 11. FactFreshness

`FactFreshness` 表达事实随时间变旧的方式。LLM、管理员或外部策略填写半衰期和衰减类型；底层只保存字段并计算分数。

字段：

- `FreshnessId`。
- `WorkspaceId`、`AgentId`、`MemorySpaceId`、`FactId`。
- `ObservedAt`：事实被观察到的时间。
- `LastVerifiedAt`：事实最后被验证的时间。
- `ValidFrom`、`ValidTo`：硬有效期。
- `HalfLifeSeconds`：半衰期秒数。`DecayKind = stable` 时可为空。
- `DecayKind`：`stable / exponential / linear / step / realtime`。
- `StaleThreshold`：进入 stale 的分数阈值，默认 `0.5`。
- `ExpiredThreshold`：进入 expired 的分数阈值，默认 `0.1`。
- `RefreshHint`：`web_search / user_confirm / tool_check / none`。
- `FreshnessReason`：LLM 或策略填写的原因，例如“道路施工通常按周衰减”。
- `CreatedAt`、`UpdatedAt`。

计算规则：

```text
reference_time = max(ObservedAt, LastVerifiedAt) where non-null
age_seconds = max(0, now - reference_time)
```

如果 `ValidTo` 非空且 `now > ValidTo`，`FreshnessScore = 0.0`，`FreshnessStatus = expired`。

各衰减类型：

- `stable`：没有硬过期时 `FreshnessScore = 1.0`。
- `exponential`：`FreshnessScore = pow(0.5, age_seconds / HalfLifeSeconds)`。
- `linear`：`FreshnessScore = max(0.0, 1.0 - age_seconds / (2 * HalfLifeSeconds))`。
- `step`：`now <= ValidTo` 时为 `1.0`，否则为 `0.0`；如果没有 `ValidTo`，则使用 `HalfLifeSeconds` 作为硬有效窗口。
- `realtime`：按 `exponential` 计算，但如果 `age_seconds > 2 * HalfLifeSeconds`，直接降为 `0.0`。

状态规则：

- `FreshnessScore <= ExpiredThreshold`：`expired`。
- `FreshnessScore <= StaleThreshold` 且大于 `ExpiredThreshold`：`stale`。
- 其他情况：`fresh`。

字段合法性：

- `exponential / linear / realtime` 必须有正数 `HalfLifeSeconds`。
- `step` 必须有 `ValidTo` 或正数 `HalfLifeSeconds`。
- `StaleThreshold` 必须大于 `ExpiredThreshold`。
- 底层不为不同 `FactType` 自动填半衰期，也不维护业务事实类型到半衰期的内置映射。

---

## 12. FactEntityMention

`FactEntityMention` 记录事实中出现的实体。第一阶段不把 Entity Registry 作为主写入对象，避免过早引入实体合并和全局身份裁决。

字段：

- `MentionId`。
- `WorkspaceId`、`AgentId`、`MemorySpaceId`、`FactId`。
- `EntityKey`：实体稳定键。可由 LLM 或上层策略生成，例如 `person:yao_ming`、`software:mysql`、`place:shanghai`。
- `EntityType`：开放实体类型字符串，由调用方或可选 schema registry 定义。示例值可以是 `person`、`software`、`place`，但底层不内置这些业务含义。
- `DisplayName`：展示名。
- `Role`：`subject / object / context / source / target / attribute / other`。
- `AliasesJson`：别名。
- `PropertiesJson`：实体属性快照，例如时间、地点、单位、版本。
- `Confidence`。
- `CreatedAt`。

规则：

- EntityKey 是查询和关联的基础，但不代表全局唯一真相。
- 后续可以从 accepted facts 的 mentions 中派生 `EntityRecord` 索引，用于搜索、合并建议和图谱视图。
- 第一阶段不要求自动实体消歧。
- 底层可以保存类型和别名，但不判断两个实体是否应该合并；合并建议由 LLM、管理员或外部策略生成。

---

## 13. FactAssociation

`FactAssociation` 表达实体、事实或上下文之间的关联强度。关联不是绝对关系，而是带来源、权重和上下文的可审计记录。

字段：

- `AssociationId`。
- `WorkspaceId`、`AgentId`、`MemorySpaceId`。
- `FactId`：产生或支撑该关联的事实。
- `SourceKind`：`entity / fact / context`。
- `SourceKey`。
- `TargetKind`：`entity / fact / context`。
- `TargetKey`。
- `AssociationType`：开放关联类型字符串，由调用方或可选 schema registry 定义。示例值可以是 `related_to`、`supports`、`contradicts`，但底层不内置业务语义。
- `Weight`：关联强度，范围 `0.0` 到 `1.0`。
- `Confidence`：关联判断置信度。
- `ContextJson`。
- `EvidenceIdsJson`：支撑该关联的证据 ID 列表。
- `ObservedAt`。
- `HalfLifeSeconds`：关联自身的可选半衰期。
- `Reason`：LLM 或策略填写的关联理由。
- `CreatedAt`、`UpdatedAt`。

规则：

- LLM、管理员或外部策略负责填写 `AssociationType`、`Weight`、`Confidence` 和半衰期。
- 底层只做合法性校验、索引、聚合和查询。
- 通用聚合关联强度为 `Weight * Confidence`。如果存在关联半衰期，聚合时再乘以关联新鲜度分数。
- 召回是否使用 pending facts 产生的关联由查询参数或上层策略决定；底层只按状态过滤条件执行。
- 底层不判断 `supports`、`contradicts`、`installed_on` 等类型的业务含义。

---

## 14. FactRevision

`FactRevision` 记录事实和其附属结构的关键变更。

字段：

- `RevisionId`。
- `WorkspaceId`、`AgentId`、`MemorySpaceId`、`FactId`。
- `RevisionType`：`create / update_statement / update_status / update_context / update_freshness / update_association / supersede / archive / restore`。
- `BeforeJson`。
- `AfterJson`。
- `ActorType`：`llm / admin / system / tool / import`。
- `ActorId`。
- `Reason`。
- `CreatedAt`。

规则：

- 所有状态变更必须写 revision。
- 关键字段更新必须写 revision，包括 statement、payload、context、freshness、association。
- Revision 是审计记录，不参与事实匹配和召回排序。

---

## 15. 写入流程

同步 Agent 写入流程：

1. 显意识 LLM 工具调用 `CreateFact`、`UpdateFact` 或 `UpdateFactStatus`，提交 statement、structured payload、context、freshness、entity mentions、associations 和 evidence。
2. 底层在单事务中校验租户一致性、字段合法性、证据存在和引用完整性。
3. 对当前 `WorkspaceId + AgentId` 下自己的 MemorySpace，显意识 LLM 可以直接写入、修正、accept、supersede 或 archive。
4. 底层不要求额外授权流程，但必须记录 evidence、revision、actor 和 before/after。
5. 底层记录 revision，但不判断语义真假，不自动提升状态。

异步 Librarian 写入流程：

1. 会话结束、任务结束、空闲窗口或每日计划任务触发 consolidation job。
2. Librarian 读取 transcript、tool results、session events、文件证据和已有 facts。
3. Librarian 生成候选 facts、entity mentions、associations、context 和 freshness。
4. Librarian 调用 Memory Library Core 写入 pending facts，或在 policy 允许时写入 accepted facts。
5. Librarian 对重复事实生成 merge/supersede proposal，对冲突事实生成 conflict association。
6. 底层保存所有 revision 和 evidence，不替 Librarian policy 做业务裁决。

状态推进边界：

```text
pending --external decision--> accepted
pending --external decision--> rejected
accepted --external decision--> superseded
accepted --admin/system--> archived
superseded --admin/system--> archived
```

底层禁止的行为：

- 根据 confidence 自动接纳事实。
- 根据 evidence 数量自动接纳事实。
- 根据 freshness 自动删除事实。
- 根据上下文匹配自动改写事实。
- 根据 `FactType`、`EntityType` 或 `AssociationType` 执行业务专用逻辑。

---

## 16. Agent-facing Memory Tools

暴露给 Agent 的工具应该围绕事实库操作设计，而不是围绕旧版 Book/Chapter 管理设计。

工具分层：

### 16.1 普通 Agent 默认可用工具

普通 Agent 应该可以读取和维护自己的记忆。对自己的 MemorySpace，显意识 LLM 不需要额外授权即可更正和修改，但写入必须可审计、可追溯、可回滚。

- `memory.search_facts`：按文本、实体、上下文、新鲜度和状态过滤查询 facts。
- `memory.get_fact`：读取单条 fact 的 statement、payload、context、freshness、mentions、associations 和 revisions。
- `memory.explain_fact`：读取事实证据链、来源摘要、revision 和当前可信度组件。
- `memory.query_entities`：按 entity key、display name、alias 或上下文查询实体提及。
- `memory.query_associations`：查询实体、事实、上下文之间的局部关联。
- `memory.propose_fact`：写入带 evidence 的 pending fact。
- `memory.propose_association`：写入带 fact/evidence 支撑的 pending association。
- `memory.attach_evidence`：给已有 fact 追加 evidence。
- `memory.update_freshness`：更新自己有权管理 fact 的 freshness 字段。
- `memory.accept_fact`：将自己 MemorySpace 内的 pending fact 改为 accepted。
- `memory.supersede_fact`：用新 fact 替代旧 fact。
- `memory.archive_fact`：归档自己 MemorySpace 内的 fact。
- `memory.update_fact_context`：修正上下文。
- `memory.update_association`：修正关联权重、上下文或 reason。

### 16.2 跨边界或系统级管理工具

这些工具不属于普通 Agent 对自有 MemorySpace 的默认能力，只有管理员、受控高权限 Agent 或系统策略可以调用。

- `memory.reject_fact`：拒绝 pending fact。拒绝通常影响审核流，属于管理行为。
- `memory.merge_entities`。
- `memory.split_entity`。
- `memory.update_schema`。
- `memory.rebuild_indexes`。
- `memory.import_legacy_memory`。
- `memory.hard_delete`。

### 16.3 潜意识 Librarian 工具

潜意识 Librarian LLM 需要批处理和整理能力，工具可以更面向 consolidation：

- `memory.consolidate_session`：基于一次会话生成 facts、entities、associations、freshness 和 evidence。
- `memory.consolidate_daily`：基于一天的会话窗口做总结和提取。
- `memory.find_similar_facts`：查找可能重复、可合并或可替代的 facts。
- `memory.detect_conflicts`：查找上下文内冲突事实。
- `memory.refresh_stale_facts`：为 stale/expired facts 生成验证建议或更新 LastVerifiedAt。
- `memory.apply_librarian_decision`：按 Librarian policy 批量 accept、reject、supersede 或 archive。

权限原则：

- 读工具是低风险。
- 显意识 LLM 修改自己 MemorySpace 的事实是默认能力，不需要额外授权。
- propose、attach evidence、accept、archive、supersede 自有事实不需要额外授权，但必须写 revision。
- reject、merge、schema update、跨 Agent 修改是系统级高风险。
- hard delete 默认禁止，除非管理员显式授权。
- 所有工具必须接收 Runtime 注入的 `WorkspaceId` 和 `AgentId`，不能信任 LLM 自己传入的租户参数。
- 所有写工具必须返回 `fact_id`、`revision_id`、`evidence_id` 或 `association_id`，方便后续引用和审计。

旧工具迁移：

- `search_memory` 迁移为 `memory.search_facts`。
- `save_memory` 迁移为 `memory.propose_fact`、`memory.attach_evidence`、`memory.update_freshness`。
- `manage_memory` 不再作为普通 Agent 默认工具；对应能力拆成明确的 fact/entity/association 管理工具。
- `grep_memory` 迁移为 `memory.search_facts` 和 evidence/source search。

---

## 17. 上下文合成管线

记忆图书馆不只是后台存储，也应该参与每次用户消息的上下文合成。用户每次询问都可以触发记忆召回，召回可以同步执行，也可以异步执行。

### 17.1 每次用户消息触发召回

当收到用户 message 时，Runtime 应创建一次 context request：

```text
User message
  -> build context request
  -> recall from memory library
  -> recall from session transcript
  -> recall from external/project knowledge sources
  -> synthesize context pack
  -> conscious LLM response
```

同步召回适用于高价值、低延迟的上下文，例如用户身份、当前项目、最近明确偏好、正在执行任务的事实。同步召回会增加响应延迟，但如果能明显提升回答质量，可以接受。

异步召回适用于大范围搜索、跨会话梳理、多跳关联探索、低置信度资料筛选。异步结果如果赶不上当前首 token，可以进入以下路径：

- 在本轮后续生成步骤中补充。
- 作为下一轮对话的 warm context。
- 写入 context cache。
- 触发潜意识 Librarian 更新事实库。

### 17.2 潜意识 LLM 参与上下文提取

潜意识 LLM 不只负责会后写入，也可以在上下文合成阶段负责“找上下文”。

推荐做法：

- 显意识 LLM 或 Runtime 生成当前消息的检索意图和上下文需求。
- 潜意识 LLM 并发读取候选 facts、associations、session messages、source summaries 和其他资料库结果。
- 潜意识 LLM 用大上下文窗口做筛选、压缩和排序，输出 `ContextPack`。
- 显意识 LLM 只消费压缩后的高价值上下文，避免主模型吞吐压力和卡顿。

由于潜意识模型通常廉价、快速、上下文大，可以并发运行多个搜索 lane：

```text
lane 1: search facts by text/entity/context
lane 2: expand associations around matched entities
lane 3: grep recent session messages
lane 4: scan source/evidence summaries
lane 5: find stale/conflicting facts that need caution
```

每个 lane 独立超时，最终由 context synthesizer 合并结果。这样一个 lane 慢或失败，不会阻塞整个响应。

### 17.3 ContextPack

上下文合成结果应是结构化 `ContextPack`，不是一段不可解释的长文本。

建议字段：

- `PackId`。
- `WorkspaceId`、`AgentId`、`SessionId`、`MessageId`。
- `QueryIntent`。
- `Facts`：fact id、statement、confidence、freshness、context score、evidence summary。
- `Associations`：source、target、type、weight、reason。
- `Evidence`：source type、source id、message range、quote summary。
- `Warnings`：stale、expired、conflict、low confidence。
- `GeneratedBy`：conscious/runtime/subconscious。
- `CreatedAt`。

Runtime 可以把 ContextPack 注入显意识 LLM，也可以把它缓存给下一轮对话。

### 17.4 延迟和预算

上下文合成需要支持三种预算：

- `sync_fast`：主响应前的短预算，获取最关键记忆。
- `async_parallel`：与主响应并发运行，能赶上就注入，赶不上就缓存。
- `idle_deep`：会话空闲或结束后深度整理，生成长期事实更新。

具体时间预算由 Runtime 配置决定，Memory Library Core 不写死。

---

## 18. 埋点日志和诊断统计

记忆图书馆、潜意识 Librarian 和上下文合成管线必须有可观测性。否则无法判断召回是否有效、潜意识是否拖慢系统、facts 是否被污染、ContextPack 是否真的帮助显意识 LLM。

### 18.1 事件埋点

建议记录以下结构化事件：

- `memory.fact.created`
- `memory.fact.updated`
- `memory.fact.status_changed`
- `memory.evidence.attached`
- `memory.association.created`
- `memory.freshness.computed`
- `memory.context_match.computed`
- `memory.search.started`
- `memory.search.completed`
- `memory.context_request.started`
- `memory.context_lane.started`
- `memory.context_lane.completed`
- `memory.context_pack.created`
- `memory.context_pack.injected`
- `memory.consolidation.queued`
- `memory.consolidation.started`
- `memory.consolidation.completed`
- `memory.consolidation.failed`
- `memory.conflict.detected`
- `memory.stale_fact.detected`

每条事件至少包含：

- `EventId`。
- `EventType`。
- `WorkspaceId`、`AgentId`。
- `SessionId`、`MessageId`、`ConversationTurnId`，如果适用。
- `MemorySpaceId`、`FactId`、`AssociationId`、`EvidenceId`、`ContextPackId`，如果适用。
- `ActorType`：`conscious_llm / subconscious_librarian / runtime / admin / tool / import`。
- `ActorId`。
- `CorrelationId`：串联一次用户消息、召回、ContextPack、LLM 响应和异步整理。
- `StartedAt`、`CompletedAt`、`DurationMs`。
- `Status`：`started / succeeded / failed / skipped / timed_out`。
- `ErrorCode`、`ErrorMessage`，如果失败。
- `PropertiesJson`：开放扩展字段。

### 18.2 诊断统计

需要支持按 workspace、agent、session、message、模型、时间窗口聚合统计。

核心指标：

- 召回次数。
- 同步召回耗时 p50/p95/p99。
- 异步 lane 耗时 p50/p95/p99。
- ContextPack 生成次数、注入次数、缓存命中次数。
- 每个 ContextPack 的 fact 数、association 数、evidence 数、warning 数。
- 召回命中 facts 数、过滤掉的 stale/expired facts 数、冲突 warning 数。
- 潜意识 consolidation job 数、成功数、失败数、超时数、重试数。
- consolidation 队列长度和等待时间。
- 每次 consolidation 处理的 message 数、token 数、模型名、估算成本。
- 新建 fact 数、更新 fact 数、状态变更数、supersede 数、archive 数。
- evidence 追加数和无 evidence 写入拒绝数。
- freshness 重新计算数和 stale/expired 分布。
- conflict detected 数和 unresolved conflict 数。
- 显意识 LLM 自主写入数、潜意识 Librarian 写入数、管理员写入数。

这些指标用于回答：

- 每次用户消息是否触发了召回。
- 同步召回是否值得它带来的延迟。
- 潜意识 LLM 是否有效降低显意识 LLM 的上下文压力。
- 哪些 facts 经常被召回但过期、冲突或低置信。
- 哪些 Agent 的记忆写入最频繁、最容易冲突。
- 异步 consolidation 是否积压。

### 18.3 诊断视图

管理台后续需要提供诊断视图：

- **Context Pipeline Trace**：按一次用户 message 展示 context request、各 lane 召回、ContextPack、注入结果和耗时。
- **Consolidation Jobs**：展示潜意识 job 队列、状态、触发原因、处理 message range、模型、token、成本和失败原因。
- **Memory Write Audit**：展示 fact/evidence/association/revision 的写入来源和 actor。
- **Recall Quality**：展示召回命中、使用、丢弃、stale、conflict、low confidence 的统计。
- **Agent Memory Health**：展示每个 Agent 的 facts 总量、pending/accepted 分布、stale 分布、冲突数、孤立实体数和关联密度。

### 18.4 存储原则

埋点日志和统计不应该污染 Fact/Evidence 主模型。

推荐分层：

- 明细事件写入 append-only diagnostics event log。
- 高频聚合写入 stats summary 表。
- Runtime timeline 可以引用 memory diagnostic event id。
- Token 和模型成本统计可以复用现有 token 统计基础设施，但必须能关联到 `ContextPackId` 或 `ConsolidationJobId`。
- 错误日志必须能从管理台定位到具体 session/message/job/fact。

诊断数据可以有保留期限。长期事实库不能依赖诊断日志才能解释事实，事实解释必须仍然通过 Evidence 和 Revision 完成。

---

## 19. 查询和服务接口

第一阶段领域服务需要提供以下能力：

- `CreateMemorySpaceAsync(workspaceId, agentId, request, ct)`。
- `EnsureDefaultMemorySpaceAsync(workspaceId, agentId, ct)`。
- `CreateFactAsync(workspaceId, agentId, memorySpaceId, request, ct)`。
- `GetFactAsync(workspaceId, agentId, factId, ct)`。
- `SearchFactsAsync(workspaceId, agentId, request, ct)`。
- `UpdateFactStatusAsync(workspaceId, agentId, factId, request, ct)`。
- `AddEvidenceAsync(workspaceId, agentId, factId, request, ct)`。
- `UpdateFreshnessAsync(workspaceId, agentId, factId, request, ct)`。
- `GetFactsByEntityAsync(workspaceId, agentId, entityKey, request, ct)`。
- `GetAssociationsAsync(workspaceId, agentId, request, ct)`。
- `ComputeFreshness(now, freshness)`。
- `ComputeContextMatch(queryContext, factContext, matchOptions)`。

查询参数应支持：

- `Status`：按调用方传入的状态集合过滤；调用方未传时可由应用层策略决定是否只取 `accepted`。
- `IncludePending`：显式包含 pending。
- `FactType`：按开放字符串过滤。
- `EntityKey`。
- `ContextFilter`。
- `FreshnessStatus`。
- `MinConfidence`。
- `MinAssociationWeight`。
- `Limit`、`Cursor`。

通用评分组件：

```text
text_match_score
confidence
freshness_score
context_match_score
association_score
```

底层可以返回这些组件分数，也可以提供调用方传入权重的通用线性组合：

```text
score = w_text * text_match_score
      + w_confidence * confidence
      + w_freshness * freshness_score
      + w_context * context_match_score
      + w_association * association_score
```

权重由查询请求或上层策略提供。底层不内置某个业务场景的默认召回排序。第一阶段可以先提供结构化过滤和确定性排序，全文和向量召回可以后置。

---

## 20. 存储和索引

SQLite 第一阶段表：

- `MemorySpaces`
- `MemoryFacts`
- `MemoryFactEvidence`
- `MemoryFactContexts`
- `MemoryFactFreshness`
- `MemoryFactEntityMentions`
- `MemoryFactAssociations`
- `MemoryFactRevisions`
- `MemoryGraphSchemas`
- `MemoryContextPacks`
- `MemoryDiagnosticEvents`
- `MemoryDiagnosticStats`

必需索引：

- `MemorySpaces(WorkspaceId, AgentId, Status)`。
- `MemoryFacts(WorkspaceId, AgentId, MemorySpaceId, Status)`。
- `MemoryFacts(WorkspaceId, AgentId, FactType, Status)`。
- `MemoryFacts(WorkspaceId, AgentId, SupersededByFactId)`。
- `MemoryFactEvidence(WorkspaceId, AgentId, FactId)`。
- `MemoryFactEvidence(WorkspaceId, AgentId, SourceType, SourceId)`。
- `MemoryFactEvidence(WorkspaceId, AgentId, EvidenceHash)`。
- `MemoryFactContexts(WorkspaceId, AgentId, FactId)`。
- `MemoryFactContexts(WorkspaceId, AgentId, ContextHash)`。
- `MemoryFactFreshness(WorkspaceId, AgentId, FactId)`。
- `MemoryFactEntityMentions(WorkspaceId, AgentId, EntityKey)`。
- `MemoryFactEntityMentions(WorkspaceId, AgentId, FactId)`。
- `MemoryFactAssociations(WorkspaceId, AgentId, SourceKind, SourceKey)`。
- `MemoryFactAssociations(WorkspaceId, AgentId, TargetKind, TargetKey)`。
- `MemoryFactAssociations(WorkspaceId, AgentId, FactId)`。
- `MemoryFactRevisions(WorkspaceId, AgentId, FactId, CreatedAt)`。
- `MemoryGraphSchemas(WorkspaceId, AgentId, Scope, Name)`。
- `MemoryContextPacks(WorkspaceId, AgentId, SessionId, MessageId, CreatedAt)`。
- `MemoryDiagnosticEvents(WorkspaceId, AgentId, CorrelationId, CreatedAt)`。
- `MemoryDiagnosticEvents(WorkspaceId, AgentId, EventType, CreatedAt)`。
- `MemoryDiagnosticStats(WorkspaceId, AgentId, WindowStart, MetricName)`。

后续增强：

- 对 `Statement` 增加 FTS。
- 对结构化 payload、context、entity properties 增加规范化索引。
- 对 accepted facts 建实体索引或局部图谱缓存。
- 对事实 statement 或 evidence summary 建向量索引。

`MemoryGraphSchemas` 是可选基础设施表，用来保存调用方定义的类型和上下文字段约束。它不包含代码内置业务逻辑。

`MemoryContextPacks` 是可选缓存表，用来保存上下文合成结果。它不是长期事实来源，不能替代 Fact/Evidence。

`MemoryDiagnosticEvents` 和 `MemoryDiagnosticStats` 用于跟踪记忆写入、召回、ContextPack、潜意识 consolidation 和错误诊断。它们不参与长期事实推理。

---

## 21. 与旧模型的关系

旧版 `Library / TreeNode / Book / Chapter` 不再作为新版记忆图书馆核心模型。

处理策略：

- 新功能不依赖旧表。
- 新 API 不暴露旧版图书馆概念。
- 新管理界面应围绕 MemorySpace、Fact、Evidence、Context、Freshness、EntityMention、Association 和 Revision 设计。
- 旧数据如需保留，只保留在旧接口或一次性导入工具中。
- 一次性导入工具必须把旧内容导入为 `pending` facts，并设置 `SourceType = import`，不能默认 accepted。

---

## 22. 分阶段落地

### Phase 0：规格确认

- 固定事实优先模型。
- 明确旧树状模型不作为核心实现依据。
- 确认底层只支撑数据结构、计算和查询，不负责 LLM 语义裁决。
- 确认知识图谱层按数据库式基础设施设计，不在代码里定义业务类型体系。

### Phase 1：底层 schema 和领域服务

- 新增表、实体、DTO、领域服务和单元测试。
- 实现 MemorySpace 默认创建。
- 实现 Fact 创建、状态更新、证据绑定、上下文存储、新鲜度计算、实体提及和关联存储。
- 实现 workspace + agent 严格隔离。
- 实现开放类型字段和可选 schema registry 的存取能力。
- 定义 Agent-facing memory tools 的权限矩阵和参数 schema。
- 定义 memory diagnostics event 和 stats schema。

### Phase 2：Librarian 写入适配

- MemoryLibrarian 将抽取结果写入 pending facts。
- LLM 填写 context、freshness、half-life、association weight、confidence 和 reason。
- LLM 或上层策略填写 FactType、EntityType、AssociationType 和上下文匹配配置。
- 外部策略或管理员负责接纳、拒绝、替代和归档。
- 增加 session stop、session idle 15 minutes 和 daily consolidation 三类触发路径。
- 增加重复事实、冲突事实和 stale facts 的 proposal 生成。
- 使用 message 级 transcript 作为主要输入，不使用低层会话消息帧作为提取单位。

### Phase 3：上下文合成管线集成

- 每次用户 message 触发 context request。
- 实现同步快速召回和异步并发召回。
- 使用潜意识 LLM 生成结构化 ContextPack。
- 支持多 lane 并发搜索 facts、associations、session messages、source summaries 和 stale/conflict warnings。
- ContextPack 可注入本轮显意识 LLM，也可缓存到下一轮。
- 记录 context request、lane、ContextPack 和注入结果的 correlation trace。

### Phase 4：管理 API 和管理界面

- 管理台显示事实列表、状态、证据、上下文、新鲜度、实体提及、关联和修订历史。
- 提供接纳、拒绝、替代、归档和重新验证操作。
- 图谱视图可以作为局部探索视图出现，但不是底层模型。
- 增加 Context Pipeline Trace、Consolidation Jobs、Memory Write Audit、Recall Quality 和 Agent Memory Health 诊断视图。

### Phase 5：Runtime 召回策略增强

- Runtime recall 支持按上下文、新鲜度、实体和关联查询 facts。
- Runtime 召回策略决定状态过滤、新鲜度阈值、上下文权重和排序权重。
- Pending facts 只在调试、审核或显式策略中使用。

---

## 23. 验收标准

Phase 1 完成时必须满足：

- Agent 创建时可以确保默认 MemorySpace 存在。
- Fact 创建必须绑定至少一条 Evidence。
- Fact 状态变更必须记录 Revision。
- 所有新表和查询都强制校验 `WorkspaceId + AgentId`。
- Freshness 支持 `stable / exponential / linear / step / realtime` 并有确定性测试。
- Context 支持调用方传入 required keys、weighted keys、包含匹配和缺失维度降权。
- Association 支持权重、置信度、上下文、证据引用和实体/事实双向查询。
- FactType、EntityType、AssociationType 均为开放字符串，底层不内置业务枚举。
- 可选 schema registry 能保存上层定义的类型约束，但不在代码里固定业务词表。
- Agent-facing tools 按读取、提议、管理、Librarian、管理员能力分层。
- 显意识 LLM 可以无额外授权地管理自己的 MemorySpace，但所有写入和状态变更必须有 revision。
- 潜意识 Librarian 可以从 session stop、session idle 15 minutes 和 daily windows 中异步生成、合并、冲突检测和刷新 proposals。
- 上下文合成管线支持每次用户 message 触发召回，并支持同步快速召回、异步并发召回和 idle deep consolidation。
- ContextPack 是结构化结果，包含 facts、associations、evidence、warnings 和生成来源。
- 记忆写入、召回、ContextPack、潜意识 consolidation 都有结构化事件埋点。
- 诊断统计可以按 workspace、agent、session、message、模型和时间窗口聚合。
- 每次用户 message 的上下文合成过程可以通过 CorrelationId 串联追踪。
- 底层不会自动把 pending 事实提升为 accepted。
- 旧版 Library、TreeNode、Book、Chapter 不出现在新领域服务的主接口中。

---

## 24. 风险和约束

- 事实抽取质量取决于 LLM 和后续审核策略，底层不能通过字段设计消除语义误判。
- 上下文维度过少会导致误用，过多会导致召回困难。第一阶段应允许结构化扩展，不强制固定全量枚举。
- 半衰期由 LLM 填写，可能不稳定。底层需要保存 reason，方便审核和后续策略调整。
- 关联权重不是事实真值，只是检索和探索的排序信号。
- 开放词表会带来类型漂移，例如 `software`、`Software`、`app` 混用。需要上层 schema registry、LLM 提示词和管理台审核来收敛。
- 显意识 LLM 自主管理记忆会带来“自我强化错误”的风险。必须通过 evidence、revision、freshness、conflict detection 和异步 Librarian 修订降低污染。
- 潜意识 Librarian 如果过度自动 accepted，会污染长期记忆。是否自动 accepted 必须由独立 policy 控制，并保留审计。
- 每次用户消息都触发召回会增加系统负载和响应延迟。需要区分同步快速预算、异步并发预算和 idle deep 预算。
- 潜意识 LLM 大上下文暴力扫描适合 message 级 transcript，但必须避免把低层帧、重复 delta 和 UI 事件混入事实提取输入。
- 诊断事件量可能很大。需要采样、保留期限、聚合表和按需明细开关，避免诊断系统反过来拖慢主链路。
- 旧数据导入如果过早自动 accepted，会污染新版事实库，因此导入默认 pending。

---

## 25. 下一步

本规格通过后，再编写实施计划。实施计划应从 Phase 1 开始，优先覆盖 schema、领域服务、默认 MemorySpace 创建、开放类型字段、可选 schema registry、Agent-facing tools、ContextPack、诊断事件、诊断统计、同步/异步召回预算、核心计算函数和测试，不先做 UI。
