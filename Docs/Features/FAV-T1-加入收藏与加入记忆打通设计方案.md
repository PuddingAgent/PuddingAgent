# FAV-T1「加入收藏」与「加入记忆」打通设计方案

- 任务看板卡：`FAV-T1`（`70700a1fc70f4787ac430232e9ceaafc`，p1）
- 状态：设计定稿（待评审）｜ 日期：2026-09-12
- 范围：**纯设计文档，零源码改动**。实施拆分见 §9。
- 事实基线：`temp/recon-favorite-memory-menu.md`（父级右键菜单全链路测绘，2026-09-12；本文件关键行号已对当前磁盘复核）

---

## 0. 文档位置说明

仓库既有设计文档目录约定为 **`Docs/Features/`**（命名模式 `XXX设计方案.md`，现存 14+ 份，如 `Docs/Features/AdminChatComposer浮层重设计方案.md`、`Docs/Features/Chat图片消息回放与前端旧Bundle缓存修复方案.md`），**不存在 `Docs/Design/`**。本文件遵循既有约定落在 `Docs/Features/`，文件名保留 `FAV-T1` 前缀以便与看板卡互查。

---

## 1. 需求解读（用户原话要点，不可曲解）

用户需求原文四条要点（FAV-T1 卡逐字归档）：

1. 右键菜单增加「加入收藏」。「加入收藏」与「加入记忆」**概念不同但可 UI 二合一**。
2. **加入收藏 = 用户侧主动**：把对话记录保存到 PuddingAgent，便于日后查找翻阅（一个会话线程里通常讨论了多件事）。需要：
   - 新增**数据库结构**存储用户收藏（相当于"打星标"，是钉住的增强版，可跨多个会话）；
   - 用户可**查看 / 复制 / 编辑 / 批注 / 取消收藏**。
3. **加入记忆 = Agent 侧自主**：实现可变为"主动给 Agent 发一条消息"（如"记住 XXX，消息 id 是 yyy"），**由 Agent 自己决定怎么记**。建议**记忆全文存外部**，Memory 只存**快照 + 索引**，以便低 token 快速翻阅。
4. **PuddingAgent 可反查用户收藏**，用来跟踪/校准"我对用户指令的理解"；用户也可把收藏内容发给 PuddingAgent。

### 1.1 概念边界（本设计的立论基础）

| | 加入收藏（Favorite） | 加入记忆（Memory） |
|---|---|---|
| 所有权 | **用户**（用户资产） | **Agent**（Agent 资产） |
| 内容 | 被收藏消息的**原文快照**（证据，不可篡改） | Agent 提炼后的**理解**（快照 + 索引，全文外置） |
| 写入方 | 前端直接调 HTTP API 入库，**不经 Agent** | 前端发一条结构化消息 → Agent 用 `save_memory` 决定怎么记 |
| 用途 | 用户日后翻阅；Agent 反查以校准对用户意图的理解 | Agent 跨会话的长期记忆 |
| 失效 | 用户显式取消收藏才消失 | Agent 自管理（archive/更新） |

> 关键推论：因为 Agent 会**反查收藏来理解用户原意**，收藏的原文快照必须**不可变**——否则 Agent 读到的是被篡改过的"历史"，反查失真（这是 Q3/Q4 裁决的根因）。

---

## 2. 现状测绘摘要（全部 file:line，已对当前磁盘复核）

### 2.1 右键菜单（9 项，10→9：「固定为上下文」已于 commit `9fdacf5` 删除）

- 菜单定义：`Source/PuddingPlatformAdmin/src/pages/chat/components/ContextMenu.tsx:74` `buildMenuItems(turnId, isUser, callbacks)`；分组渲染 `:218/:263-268`。
- 回调契约：`ContextMenu.tsx:149` `ContextMenuCallbacks`；「加入记忆」项 `:130-133`（`label: '加入记忆'`，`icon: <StarOutlined/>`，`onClick: callbacks.onAddToMemory(turnId)`）；回调声明 `:157` `onAddToMemory: (turnId: string) => void`。
- handler（`src/pages/chat/index.tsx`）：复制 `:255-271`、引用回复 `:273-296`、删除 `:298-300`、朗读 `:301-307`、重新执行 `:309-313`、修改指令并重跑 `:315-319`、**加入记忆 `:321-323`（TODO 空实现）**、钉住 `:324-346`、创建分支 `:347-349`（TODO）。
- 条件插入模式（新菜单项零破坏接入的模板）：`ContextMenu.tsx:93-102` `...(callbacks.onPin ? […] : [])`。
- 挂载：`index.tsx:786-798`（Portal 到 body，React.lazy `:39-45`）；数据来源 `index.tsx:224-242` `handleContextMenu` ← `MessageRow.tsx:315-316` / `AgentMessageBubble.tsx:567-569`。

### 2.2 「钉住」= localStorage 单槽（收藏不可照抄）

- `utils/pinnedMessage.ts`：key `pudding_pinned_message` `:1`；`PinnedMessage{messageId?,turnId,preview,fullText,pinnedAt}` `:4-13`；摘要 `summarizePinnedMessage`（前 3 行/120 字）`:14-23`；引用拼装 `buildPinnedMessageQuote` `:32-41`；**`savePinnedMessage` `:56-59` 为 `setItem` 覆盖写（单槽，无列表）**。
- handler：`index.tsx:324-346`（取 `turn.userMessage.dbMessageId`，助手消息无此值 → `undefined` 兜底 `:340`）；另有行内按钮 `index.tsx:380-395` `handlePinTurn`。
- 消费：`PinnedMessageButton.tsx`（单击引用 `:48-62`、双击取消 `:64-71`）挂载于 `MessageList.tsx:1371-1373`。
- **无任何后端实体/接口**（`ChatMessageEntity` 无 pinned 列；`MessageApiController` 无消息级写接口，见 2.5）。

### 2.3 「加入记忆」= 前端 TODO 空实现 + 既有记忆写入能力在 Admin 侧

- 菜单项 `ContextMenu.tsx:130-133`，handler `index.tsx:321-323` 空实现。
- 记忆图书馆 HTTP API（**全部在 `api/admin/*` 且 `[Authorize]`**）：`Source/PuddingPlatform/Controllers/Api/MemoryLibraryAdminController.cs:15`；GET overview `:34`/libraries `:46`/tree `:74`/book `:98`/search `:127`；写 POST tree-nodes `:151`、POST books `:176`、PUT books/{bookId} `:200`、POST chapters `:218`。服务层 `Source/PuddingPlatform/Services/MemoryLibraryAdminService.cs`；前端 service `src/services/platform/api.ts:3365-3520`（`createAgentMemoryTreeNode :3422`、`createAgentMemoryBook :3435`、`createAgentMemoryChapter :3458` 等）。Admin 页 `src/pages/memory-library/index.tsx`（路由注册 `src/pages/Admin.tsx:28-31`）。
- Agent 侧工具：`Source/PuddingRuntime/Tools/BuiltIns/Memory/SaveMemoryTool.cs`（action ∈ upsert|delete|set_important|get_important `:394`；`set_important` 分支 `:91/:106-128/:152-158`）；`MemoryToolArgs.cs:11`（type=fact/preference/summary/chapter）。
- L4-PINNED 层数据源：`Source/PuddingPlatform/Services/ImportantMemoryService.cs:12-16`（读写 `agents/{agentId}/memory/important_memory.md`；`MaxLines=100 :17`、`MaxChars=1000 :19`；`WriteAsync :149`），**无任何 Controller 消费**，唯一写入口是 Agent 工具 `save_memory action="set_important"`。
- 检索工具（本设计 Q6 的契约模板）：`Source/PuddingRuntime/Tools/BuiltIns/Memory/MemoryLibraryTool.cs:13-19`（`[Tool(id:"search_memory", …, category: ToolCategory.Memory, permission: ToolPermissionLevel.Low, safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe)]`）；执行入口 `:45-61`；FTS 检索 `SearchChaptersFtsScopedAsync` `:86-89` + `SmartSearchAsync` 兜底 `:91-93`；参数契约 `SearchMemoryArgs :146-151`。

### 2.4 关键既有事实

- **`ChatMessages` 是投影表不是事实源**：`Source/PuddingPlatform/Services/ConversationProjector.cs:15`（"从 Event Log 按 checkpoint 重放并生成 ChatMessages 物化视图"）、`SessionEventsController.cs:542`。⇒ 在投影表上加列/以 `ChatMessages.Id` 做外键都有被重放冲掉的风险（见 §10 R1）。
- **消息实体结构**：`Source/PuddingPlatform/Data/Entities/ChatMessageEntity.cs` —— `[Key] long Id :12-13`；稳定业务键 `MessageId`（幂等键，`[MaxLength(64), Column("message_id")]` `:16-17`）；`SessionId/WorkspaceId/AgentInstanceId :19-23`；`TurnId/CommandId/UserId`（均可空，string(64)）`:40-47`；`MetadataJson :49-50`；`ContentPartsJson`（ADR-077 多模态信封）`:56-57`；`CreatedAt`（Unix ms）`:60`。**`UserId` 已有先例**（Q7 依据）。
- **消息 API 无写路径**：`Source/PuddingPlatform/Controllers/Api/MessageApiController.cs` —— `[Authorize]+[Route("api/sessions/{sessionId}/messages")]` 类头（recon 实测 route `:20`），仅 `[HttpGet]` 列表（游标分页，默认 20/上限 50，`:72` 起）与 `[HttpGet("token-stats")]`（`:124` 起）。**收藏不能挂在这个路由下**（见 §5）。
- **消息全文检索已有机制**：`MessageSearchController.cs:12-15`（`[Authorize]`+`[Route("api/messages")]`），`POST /search :29` 复用 `IRawSessionLogService.GrepMessagesAsync`（**Lucene FTS**，`:43-52`）+ `MessageTopicService.SearchTopicsAsync`。⇒ 收藏搜索优先 LIKE/EF（量小），量大后可挂 Lucene（§5.4）。
- **SQLite schema 演进先例（ADR-077）**：`Source/PuddingPlatform/Services/ChatMessageSchemaBootstrapper.cs:16-38` `EnsureCreatedAsync`（`IsSqlite()` 守卫 `:21` + 幂等 `ALTER TABLE`）；`:40-77` `ColumnExistsAsync`（`pragma_table_info` 参数化查询，可复用）。EF Migrations 目录并存（`Source/PuddingPlatform/Migrations/20260503062428_AddAgentChatMessages.cs` 等）。
- **DbSet 注册**：`Source/PuddingPlatform/Data/PlatformDbContext.cs:28` `DbSet<ChatMessageEntity> ChatMessages`（DbSet 清单 `:10-153`）。
- **删除是纯前端态**：`hooks/useChatState.ts:1400-1402`（仅过滤前端 `turns` 数组，无后端调用）。⇒ 收藏必须快照落库，否则"收藏仍在、原文不可见"。
- **收藏能力现状 = 零**：全仓（PuddingPlatformAdmin/src、PuddingPlatform、PuddingCore、PuddingRuntime、PuddingHost、PuddingMemoryEngine 分目录穷尽检索）无 favorite/bookmark/收藏 实体、字段、接口或 UI；唯一命中是「加入记忆」复用的 `StarOutlined` 图标（`ContextMenu.tsx:11/:130`）。
- **稳定键来源链**：`types.ts:74-75`（dbMessageId = ChatMessages.Id，仅用户消息有）→ `hooks/useSessionHistoryProjection.ts:150` → `client/checkpointStore.ts:24/:73/:98`。

---

## 3. 七个设计问题的裁决

### Q1 收藏粒度：单条消息 vs 话题片段容器

**裁决：原子 = 单条消息收藏（V1 落地）；容器 = 「收藏夹」分组（V2），表结构 V1 预留 `collection_id` 外键列。**

- 理由：① 右键菜单作用域天然是单条消息，单条是最小可用原子；② 用户"一个线程讨论多件事"的痛点由**跨会话汇总的收藏库**（Q2）+ 搜索解决，不强制要求 V1 就做多选建片段；③ 容器涉及多选 UI 与"从会话追加"，独立成 V2 不阻塞主线。
- 被否：**只做容器**——V1 无多选 UI，容器只能装单条，是过度设计；**只做单条且不留分组字段**——V2 加列要走 bootstrap/迁移，预留成本≈0。

### Q2 "可以加入多个会话"：条目跨会话聚合，还是收藏库全局汇总

**裁决：全局收藏库（workspace 作用域）为主，条目自带 `session_id` 溯源；UI 提供"全部收藏 / 按会话过滤"两种视图。**

- 理由：用户收藏的是"内容"，不是"会话的从属关系"。一条消息永远只属于一个会话（物理事实），"条目属于多个会话"在语义上不成立；跨会话的是**收藏库本身**。
- 被否：**仅会话内收藏列表**——无法跨线程翻阅，直接违背需求初衷；**条目多会话归属（多对多）**——伪需求。

### Q3 "编辑"改什么

**裁决：可编辑 = `title`（标题/备注名）+ `user_note`（用户批注）；`content_snapshot` 原文快照永不可变（服务端 PATCH 白名单强制）。**

- 理由：需求第 4 条要求 PuddingAgent 反查收藏以理解用户原意。原文被改 ⇒ Agent 读到失真证据。当前平台消息本身不可编辑（重跑=新消息），快照即"收藏那一刻的历史"，具备存证价值。
- 被否：**编辑原文**——篡改历史，反查失真；**只存 messageId 不存快照**——见 §10 R3（前端删除不落库，原消息可能不可见/不可达）。

### Q4 "批注"谁能写

**裁决：`user_note`（用户批注）与 `agent_note`（Agent 批注）分字段存储；V1 只开放 `user_note` 写路径（收藏 API 归用户），`agent_note` 仅建列预留。**

- 理由：分字段是需求"收藏与记忆概念分离"在数据层的直接映射——用户批注=用户原意，Agent 批注=机器理解，混存会让反查方无法区分"用户说的"和"Agent 以为的"。Agent 侧写 `agent_note` 需要 Agent 持有对收藏表的写权限，与 Q6"只读工具"冲突，故 V1 预留不开放；未来若开放，走独立的内部写服务（非用户 API、非 Agent 工具），并记录写入来源 runId。
- 被否：**单字段混存**——无法区分原意与推断；**V1 就开 Agent 写路径**——扩大攻击面，且没有明确消费方。

### Q5 记忆全文的"外部"落点

**裁决：沿用既有记忆图书馆（Books/Chapters + FTS 检索已具备）作全文外置存储；Agent 的 Memory（`save_memory`）只存快照 + 指针（book/chapter/messageId）。**

- 理由：① 图书馆已有完整 CRUD（`MemoryLibraryAdminController.cs:151/:176/:200/:218`）与 FTS（`MemoryLibraryTool.cs:86-93` `SearchChaptersFtsScopedAsync` + `SmartSearchAsync` 兜底）；② 平台既有约定即"Memory 是快速索引不是日记本，全文外置"（与用户建议一致）；③ `search_memory` 已是 Agent 习惯的检索入口，全文进图书馆意味着**零新增检索基础设施**。
- 落点规范：「加入记忆」触发的 Agent 记忆，全文写入该 workspace 默认图书馆的专属 Book（建议 `对话收藏与记忆/Favorites`，由 Agent 调 `manage_memory`/图书馆工具创建）；`save_memory` 条目 value 存 `{favoriteId?, messageId?, book, chapter 指针, 摘要}`。
- 被否：**新目录 `memory/favorites/*.md`**——与图书馆双轨，检索割裂、两套索引；**全文直接塞 Memory 库**——token 膨胀，违背既有约定。

### Q6 Agent 反查收藏的形态

**裁决：新增独立只读工具 `search_favorites`（契约见 §7），不并入 `search_memory`。**

- 理由：① 数据域不同——收藏是用户侧结构化 DB 表，记忆是 Agent 侧图书馆（Books/Chapters/FTS），检索路径完全不同；② 权限语义不同——收藏含用户批注等用户资产，独立工具便于未来加隐私过滤/脱敏，而不牵动记忆检索；③ 描述上可引导模型正确选择（"用户星标过的重要对话" vs "用户档案/偏好/摘要"）；④ 契约模板现成（`MemoryLibraryTool`，§2.3）。
- 被否：**并入 `search_memory` 做双源检索**——单工具多数据域会让结果归属模糊、超时叠加，且违背"工具单一职责"；**靠用户手动把收藏发给 Agent**——只是补充通道（需求原文也保留了这个用法），不是反查主路径。

### Q7 权限与多用户

**裁决：表结构预留 `user_id` 列（照抄 `ChatMessageEntity.UserId` 惯例：nullable string(64)，`ChatMessageEntity.cs:46-47`）；V1 查询按 `workspace_id` 作用域过滤，API 归属 `[Authorize]`（与既有消息 API 一致），不做 per-user 分化。**

- 理由：当前平台为单用户（`AppUserEntity` 存在但聊天链路未按 user 过滤，`MessageApiController` 即先例）；预留列成本≈0，未来启用 per-user 时仅改查询谓词，无迁移。
- 被否：**V1 就做 user 维度隔离**——鉴权上下文尚未贯通 userId 到聊天 API，属无消费方的提前设计。

---

## 4. 数据模型（DDL 级）

### 4.1 新表 `ChatFavorites`（SQLite，与既有库同文件）

```sql
CREATE TABLE IF NOT EXISTS "ChatFavorites" (
    "id"                 INTEGER PRIMARY KEY AUTOINCREMENT,
    "favorite_id"        TEXT    NOT NULL,              -- 稳定业务 ID（幂等键，32 位 N 格式 Guid，前端/Agent 引用它）
    "workspace_id"       TEXT    NOT NULL,              -- 收藏库作用域
    "session_id"         TEXT    NOT NULL,              -- 来源会话（溯源）
    "dedup_key"          TEXT    NOT NULL,              -- 幂等去重键 = messageId ?? turnId（应用层写入）
    "message_id"         TEXT,                          -- ChatMessages.message_id（用户消息有；助手消息 NULL）
    "turn_id"            TEXT,                          -- 前端 turnId 兜底键（助手消息无 messageId 时必填）
    "chat_message_id"    INTEGER,                       -- ChatMessages.Id（仅加速参考，投影重建后可能失效，见 §10 R1）
    "role"               TEXT    NOT NULL,              -- 'user' | 'assistant'
    "title"              TEXT,                          -- 用户可编辑标题（不改原文）
    "preview"            TEXT    NOT NULL,              -- 摘要：前 3 行/120 字（语义复用 pinnedMessage.ts:14-23）
    "content_snapshot"   TEXT    NOT NULL,              -- 收藏时刻原文快照（不可变）
    "content_parts_json" TEXT,                          -- ADR-077 多模态部件快照（版本化信封 {v:1,parts:[...]}，可空）
    "user_note"          TEXT,                          -- 用户批注（可编辑）
    "agent_note"         TEXT,                          -- Agent 批注（V1 预留，无写路径，见 Q4）
    "collection_id"      TEXT,                          -- 收藏夹分组（V2 启用，V1 恒 NULL，见 Q1）
    "user_id"            TEXT,                          -- 多用户预留（V1 NULL，照 ChatMessageEntity.cs:46-47 惯例）
    "tags_json"          TEXT,                          -- 标签 JSON 数组（V2，可空）
    "created_at"         INTEGER NOT NULL,              -- favoritedAt（Unix ms）
    "updated_at"         INTEGER NOT NULL               -- 批注/标题最近修改（Unix ms）
);

CREATE UNIQUE INDEX IF NOT EXISTS "UX_ChatFavorites_FavoriteId"
    ON "ChatFavorites" ("favorite_id");

-- 同一工作区同一会话内，同一消息（或同 turn）不可重复收藏（toggle 语义由 DELETE+POST 承载）
CREATE UNIQUE INDEX IF NOT EXISTS "UX_ChatFavorites_Dedup"
    ON "ChatFavorites" ("workspace_id", "session_id", "dedup_key");

-- 列表主查询：按工作区倒序分页
CREATE INDEX IF NOT EXISTS "IX_ChatFavorites_Workspace_Created"
    ON "ChatFavorites" ("workspace_id", "created_at" DESC);

-- 按会话过滤视图
CREATE INDEX IF NOT EXISTS "IX_ChatFavorites_Session"
    ON "ChatFavorites" ("session_id", "created_at" DESC);

-- Agent 反查：按消息定位收藏
CREATE INDEX IF NOT EXISTS "IX_ChatFavorites_MessageId"
    ON "ChatFavorites" ("message_id");
```

### 4.2 EF 实体与注册

- 新实体：`Source/PuddingPlatform/Data/Entities/ChatFavoriteEntity.cs`，属性与上表一一对应，**列名用 `[Column("snake_case")]` 特性对齐**（照 `ChatMessageEntity.cs:16-17/:40-47/:56-57` 的既有风格）。
- DbContext：`PlatformDbContext.cs` 追加 `DbSet<ChatFavoriteEntity> ChatFavorites`（插在 `:28` ChatMessages 邻近分组），Fluent 配置中声明唯一索引与最大长度（与 DDL 一致，保证 `EnsureCreated` 全新建库时模型即 DDL）。
- **迁移方式（沿 ADR-077 先例，不新增 EF Migration）**：新建 `Source/PuddingPlatform/Services/ChatFavoriteSchemaBootstrapper.cs`，照 `ChatMessageSchemaBootstrapper.cs:16-38/:40-77` 模式：
  - `IsSqlite()` 守卫；
  - 用 `pragma_table_info`（可复用其 `ColumnExistsAsync`，建议抽公共）判断表是否存在；
  - 不存在则执行 §4.1 全部 `CREATE TABLE/INDEX IF NOT EXISTS`；存在则跳过（幂等，可重复执行）。
  - 宿主启动链路与 `ChatMessageSchemaBootstrapper.EnsureCreatedAsync` 同点调用（先后无依赖）。
- 为什么独立小表而不是给 `ChatMessages` 加列：投影表被重放重建时加列会被冲掉（§2.4/§10 R1）；独立表 + `message_id`/`turn_id` 逻辑键与投影解耦。

### 4.3 与记忆图书馆的关系（Q5 的数据流）

```
用户点「加入记忆」
  └─► 前端拼结构化引用消息（复用 index.tsx:273-296 onQuote 的拼装格式）
        「记住这条消息：\n> 消息ID：{dbMessageId|turnId}\n> 请通过Query Session Log工具获取原始信息\n> 摘要：…」
  └─► 自动发送 → Agent 收到 → Agent 自行调用 save_memory / manage_memory
        ├─ 全文/要点 → 记忆图书馆 Book「对话收藏与记忆」Chapter（全文外置）
        └─ save_memory 条目 → { 摘要, book/chapter 指针, messageId }（快照+索引）

用户点「加入收藏」
  └─► 前端直接 POST /api/favorites 入库（不经 Agent）
```

打通点：收藏详情提供「让 Agent 记住这条」按钮（与「加入记忆」同一条消息链路，载荷携带 `favoriteId`，Agent 记忆里即可回链收藏）。

---

## 5. 后端 API 设计

### 5.1 控制器

新建 `Source/PuddingPlatform/Controllers/Api/ChatFavoriteApiController.cs`：

- `[Authorize]`、`[ApiController]`、`[Route("api/favorites")]`
- 主构造函数注入 `PlatformDbContext`（照 `MessageApiController` 先例）。

> 不挂 `MessageApiController` 的理由：其路由 `api/sessions/{sessionId}/messages` 是**会话作用域**（`MessageApiController.cs` 类头），而收藏列表是**workspace 作用域、跨会话**的独立资源集合；硬塞会让路由语义与分页参数打架。

### 5.2 端点

| 方法 | 路径 | 说明 |
|---|---|---|
| `POST` | `/api/favorites` | 创建收藏（幂等：命中 `UX_ChatFavorites_Dedup` 返回 200 + `alreadyFavorited:true` + 既有条目；否则 201） |
| `GET` | `/api/favorites?workspaceId=&sessionId=&query=&role=&before=&limit=` | 游标分页列表（`before`=上一页最老 `created_at`，默认 20/上限 50，对齐 `MessageApiController` 分页约定）；`query` 非空时对 `title/preview/content_snapshot/user_note` 做 EF `LIKE` 检索 |
| `GET` | `/api/favorites/{favoriteId}` | 详情（含 `content_snapshot` 全文与 `content_parts_json`） |
| `PATCH` | `/api/favorites/{favoriteId}` | **仅接受 `title`、`user_note`**；请求体含其他字段（尤其 `content_snapshot`/`content_parts_json`）返回 400（Q3 不可变约束的服务端强制）；`updated_at` 服务端刷新 |
| `DELETE` | `/api/favorites/{favoriteId}` | 取消收藏（物理删除；V2 可选回收站） |

### 5.3 请求/响应 DTO（摘要）

```csharp
// POST /api/favorites 请求体
public record CreateFavoriteRequest(
    string WorkspaceId,
    string SessionId,
    string DedupKey,            // messageId ?? turnId，服务端校验非空
    string? MessageId,
    string? TurnId,
    long? ChatMessageId,
    string Role,                // user|assistant
    string? Title,
    string Preview,
    string ContentSnapshot,     // 服务端截断上限 64KB（超出截断并置 metadata.truncated=true）
    string? ContentPartsJson);

// 列表项（不含全文，详情才有）
public record FavoriteListItemDto(
    string FavoriteId, string SessionId, string? MessageId, string? TurnId,
    string Role, string? Title, string Preview, string? UserNote,
    long CreatedAt, long UpdatedAt, bool HasContentParts);

// PATCH 请求体
public record UpdateFavoriteRequest(string? Title, string? UserNote);
```

### 5.4 搜索的演进路径

V1 用 EF `LIKE`（收藏量级小、索引在 `workspace_id+created_at`）；当单 workspace 收藏 > 数千条或需要分词时，把收藏快照同步进既有 Lucene 通道（`IRawSessionLogService.GrepMessagesAsync` 同栈，`MessageSearchController.cs:43-52` 先例），接口签名不变。

---

## 6. 前端 UI 设计

### 6.1 菜单入口（零破坏接入）

- `ContextMenu.tsx`：第 2 组「加入记忆」（`:130-133`）之后插入「加入收藏」项；用可选回调 `onAddToFavorite?: (turnId: string) => void` + 条件插入（照抄 `:93-102` `...(callbacks.onPin ? […] : [])` 模式），不破坏既有 props 契约与测试。
- `ContextMenuCallbacks`（`:149-159`）追加 `onAddToFavorite?`。
- 图标：收藏用 **`StarOutlined`（用户原话"相当于打星标"，星标语义最贴切）**；「加入记忆」建议同步改用 `HeartOutlined` 以消除视觉冲突（若坚持不动既有图标，文字标签亦可区分，属外观项，实施时二选一）。

### 6.2 handler（`index.tsx`）

- `onAddToFavorite(turnId)`：取文本与 `dbMessageId`（复用 `:324-346` `onPin` 的取值模式：`contextMenu.content || (role==='user' ? turn.userMessage.text : turn.assistant.answerMarkdown)`；`dedupKey = dbMessageId ?? turnId`）→ 调 `favoriteMessage(...)` → `messageApi.success('已加入收藏')`；重复收藏（API 幂等返回 alreadyFavorited）提示"已在收藏中"。
- `onAddToMemory(turnId)`（`index.tsx:321-323` TODO 实装）：拼结构化引用（复用 `:273-296` `onQuote` 的拼装 + `pinnedMessage.ts:32-41` 格式）→ **自动发送**（见 §10 开放问题 ③）→ Agent 决定怎么记。

### 6.3 收藏面板（V1 = Drawer 抽屉）

- 入口：聊天页头部「收藏」按钮（参照 `PinnedMessageButton.tsx` 的挂载位风格），打开右侧 Drawer。
- 功能（对应需求"查看/复制/编辑/批注/取消收藏"）：
  1. **列表**：`preview + title + role 徽标 + 来源会话名 + 时间`，按 `created_at` 倒序，滚动分页（复用 GET 游标）；
  2. **过滤**：全部 / 按会话（下拉选 session）/ 按角色；搜索框走 `query` 参数；
  3. **复制**：复制 `content_snapshot` 全文；
  4. **编辑/批注**：行内展开编辑 `title` 与 `user_note`（PATCH）；
  5. **取消收藏**：二次确认 → DELETE；
  6. **引用到输入框**：以 `pinnedMessage.ts:32-41` 同款结构化引用格式回填输入框；
  7. **跳转原文**：按 `session_id`（+`message_id`）跳回来源会话并定位（复用既有消息加载/定位能力）；
  8. **让 Agent 记住这条**（打通按钮）：生成携带 `favoriteId` 的结构化消息并自动发送。
- 多模态收藏条目：`content_parts_json` 非空时列表显示"含图片"徽标，详情区复用既有消息内容部件渲染（`ChatContentPartDto` 语义，`MessageApiController.cs:37-40`）。
- V2 演进：独立 `/favorites` 路由页（照 `/memory-library` 注册模式，`src/pages/Admin.tsx:28-31`）+ 收藏夹分组管理。

### 6.4 前端 service

`src/services/platform/api.ts` 追加（挨着既有消息服务）：`createFavorite(...)`、`listFavorites(params)`、`getFavorite(favoriteId)`、`updateFavorite(favoriteId, {title?, userNote?})`、`deleteFavorite(favoriteId)`。

---

## 7. Agent 工具契约（`search_favorites`，只读）

新建 `Source/PuddingRuntime/Tools/BuiltIns/Favorites/SearchFavoritesTool.cs`，契约**逐字段照 `MemoryLibraryTool.cs:13-19/:45-61/:146-151` 模式**：

```csharp
[Tool(
    id: "search_favorites",
    name: "search_favorites",
    description: "搜索用户收藏的聊天记录（用户主动星标/收藏的原文证据，含用户批注）。当需要回忆用户明确标记重要的历史对话、校准对用户指令含义的理解时使用。与 search_memory 的区别：search_memory 检索的是 Agent 提炼的记忆，本工具检索的是用户亲口说过的原文。",
    category: ToolCategory.Memory,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe)]
public sealed class SearchFavoritesTool : PuddingToolBase<SearchFavoritesArgs>
// ExecuteCoreAsync → ToolExecutionResult.Ok(JsonSerializer.Serialize(response)) / Fail
```

参数（`SearchFavoritesArgs`，`[ToolParam]` 标注）：

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `query` | string | 是 | 关键词；服务端对 `title/preview/content_snapshot/user_note` 做 LIKE |
| `sessionId` | string? | 否 | 限定来源会话 |
| `limit` | int | 否 | 1-50，默认 10 |

返回（JSON 字符串，语义示例）：

```json
{
  "query": "…",
  "total": 3,
  "favorites": [
    { "favoriteId": "…", "sessionId": "…", "messageId": "…", "turnId": "…",
      "role": "user", "title": null, "preview": "…", "userNote": "…",
      "createdAt": 1760000000000 }
  ],
  "hint": "引用原文时给出 favoriteId 与 sessionId；需要全文时可请用户粘贴或用引用格式发起。"
}
```

实现要点：

- workspace 取 `context.WorkspaceId`（照 `MemoryLibraryTool.cs:53-56` 模式）。
- 数据访问：新接口 `IChatFavoriteQueryService`（`SearchAsync(workspaceId, sessionId?, query?, limit, ct)`），实现放在 PuddingPlatform 服务层、直接查 `PlatformDbContext`；工具类经 DI 注入接口（先例：`SaveMemoryTool` 注入 PuddingPlatform 的 `IImportantMemoryService`，`SaveMemoryTool.cs:29/:36`）。
- **严格只读**：无任何写方法；`ReadOnly | ConcurrencySafe` 标记生效。
- 注册：与 `SaveMemoryTool`/`MemoryLibraryTool` 同一批内置工具注册链路；上线后在 `code_map.md` 与工具清单文档补条目。

---

## 8. 「钉住 × 加入收藏 × 加入记忆」边界对照

| | 钉住（pinned） | 加入收藏（favorite） | 加入记忆（memory） |
|---|---|---|---|
| 归属/性质 | 前端暂存槽 | **用户资产**（DB 持久） | **Agent 资产**（图书馆全文 + Memory 快照索引） |
| 存储 | localStorage 单槽覆盖写（`pinnedMessage.ts:56-59`） | SQLite `ChatFavorites` 多条、跨会话 | 图书馆 Book/Chapter（全文）+ `save_memory` 条目（快照+指针） |
| 数量 | 同一时刻 1 条 | 无限（用户管理） | Agent 决定 |
| 写入方 | 前端 | 前端 HTTP API（**不经 Agent**） | Agent（`save_memory`/图书馆工具），触发方=前端自动发消息 |
| Agent 可读 | 否（纯前端） | 是（`search_favorites` 只读） | 是（`search_memory`） |
| 原文可变性 | 随槽覆盖丢失 | 快照不可变（Q3） | Agent 提炼物，Agent 自更新 |
| 消费 UI | 单条气泡按钮 | 收藏面板（列表/搜索/编辑/批注/取消） | 记忆图书馆 Admin 页 + Agent 上下文层 |
| 相互动作 | 引用格式被三者共用 | 可一键「让 Agent 记住」（→记忆链路） | 记忆条目可携带 favoriteId/messageId 回链 |

---

## 9. 分期实施（原子子任务，每项含验收标准）

> 执行纪律：F1→F2 串行（同工程，构建锁）；F3/F4 为前端链，可与后端并行但需先冻结 §5 契约（contract-first）；F6 依赖 F1；全部落地后更新 `code_map.md`。构建/测试全程串行（MSB3027 文件锁先例）。

### F1 数据层（后端）
`ChatFavoriteEntity` + `PlatformDbContext` 注册 + `ChatFavoriteSchemaBootstrapper`（§4）。
**验收**：① 全新库 `EnsureCreated` 后 `ChatFavorites` 表与 4 索引存在且列名与 DDL 一致；② 旧库上 bootstrap 连续执行两次无异常无重复建表；③ `dotnet test` 数据层单测全绿（trx 逐用例取证）。

### F2 后端 API（后端，依赖 F1）
`ChatFavoriteApiController` 五端点（§5）+ DTO + 幂等 + 不可变白名单。
**验收**：① POST 幂等（重复收藏返回 200+alreadyFavorited，不产生第二行）；② PATCH 提交 `contentSnapshot` 字段返回 400；③ DELETE 后列表不再可见；④ 分页 `before/limit` 与 404 路径有测试覆盖。

### F3 菜单与服务链路（前端，依赖 F2 契约冻结）
「加入收藏」菜单项 + `onAddToFavorite` handler + `api.ts` 五个 service 函数。
**验收**：① 用户/助手消息右键均可收藏（助手消息走 turnId 兜底）；② 重复收藏提示"已在收藏中"；③ 断网/500 有错误提示不静默；④ 既有 ContextMenu 测试不回归（可选回调不破坏 props）。

### F4 收藏面板（前端，可与 F3 并行）
Drawer 列表/搜索/过滤/复制/编辑批注/取消/引用/跳转原文（§6.3）。
**验收**：① 跨会话汇总可见且可按会话过滤；② 搜索命中 `preview` 与 `user_note`；③ 编辑仅影响 `title/user_note`（原文区只读展示）；④ 取消收藏后列表即时移除；⑤ 组件测试覆盖加载/空态/错误态。

### F5 「加入记忆」实装（前端，依赖 F3 的 handler 位置）
`index.tsx:321-323` TODO 实装：结构化引用 + 自动发送；收藏详情「让 Agent 记住」同链路（携带 favoriteId）。
**验收**：① 点击后消息发出且格式含 messageId/摘要（与 `onQuote` 同构）；② Agent 回复确认并产生 `save_memory` 条目（会话日志可查）；③ favoriteId 出现在 Agent 记忆指针中。

### F6 `search_favorites` 工具（Runtime，依赖 F1）
工具 + `IChatFavoriteQueryService` + 注册。
**验收**：① 契约测试：`ReadOnly|ConcurrencySafe` 标记、query 必填校验、limit 夹取 1-50；② Agent 会话实测：搜索命中已收藏条目并返回 favoriteId；③ 与 `search_memory` 结果域互不混淆（各回各的）。

### F7 文档收口
更新 `Source/code_map.md`（新实体/控制器/工具条目）与本设计文档状态行。
**验收**：`code_map.md` 含 `ChatFavoriteEntity`/`ChatFavoriteApiController`/`SearchFavoritesTool` 三条索引。

---

## 10. 风险与开放问题

### 风险

- **R1 投影重建冲键**：`ChatMessages` 是 Event Log 重放的物化视图（`ConversationProjector.cs:15`），投影重建后 `ChatMessages.Id` 会变。⇒ 缓解：`chat_message_id` 仅作加速参考；逻辑键恒为 `message_id ?? turn_id`；收藏读取不 join 投影表（快照自足）。
- **R2 助手消息无 message_id**：`dbMessageId` 仅用户消息有（`types.ts:74-75`，与 `index.tsx:340` 钉住同困境）。⇒ 缓解：`dedup_key = messageId ?? turnId`，前端收集时对助手消息显式传 turnId。
- **R3 原文可达性**：前端删除是纯前端态（`useChatState.ts:1400-1402`），收藏引用的原消息可能"不可见"。⇒ 缓解：`content_snapshot` 快照落库本设计已强制；跳转原文功能对已删除消息降级为"仅看快照"。
- **R4 快照体积**：超长消息直接入库有膨胀风险。⇒ 缓解：服务端 64KB 截断上限 + `truncated` 标记（§5.3）；`content_parts_json` 只存引用型信封不存字节（沿 ADR-077 摘要语义）。
- **R5 授权作用域**：既有记忆写 API 全在 `api/admin/*`（`MemoryLibraryAdminController.cs:15`），收藏 API 走用户态 `api/favorites` + `[Authorize]`，两者作用域不混用；Agent 反查走 Runtime 工具直查 DB，不经用户 API。
- **R6 基线漂移**：`ContextMenu.tsx`/`index.tsx` 曾在测绘期间被并发改写（recon §0）。⇒ 实施前必须重读两文件取最新行号。
- **R7 UX 习惯改变**：「加入记忆」从空实现变为"自动发送消息"，用户可能不预期输入框被占用/消息发出。⇒ 见开放问题 ③。
- **R8 单槽教训复蹈**：照抄 `pinnedMessage.ts` 会退化成"只能收藏一条"。⇒ 本设计后端多条表 + 前端不引入收藏本地存储，全部以服务端为准（弱网下乐观更新仅做 UI 态，不做本地持久层）。

### 开放问题（需用户/父级拍板）

1. **收藏面板入口**：聊天页头部按钮 + Drawer（本设计推荐）vs 独立 `/favorites` 路由页（V2 再做）。 
2. **快照截断上限**：64KB 是否合适（当前聊天消息普遍 <10KB；含 content_parts 时建议维持 64KB 文本 + 部件引用）。
3. **「加入记忆」发送方式**：默认**自动发送**（本设计推荐，符合"主动给 Agent 发一条消息"的原话）vs 回填输入框让用户手动发送；可选做成偏好项。
4. **`agent_note` 写入机制**：V2 决定（独立内部写服务 + runId 溯源，见 Q4）。
5. **记忆图书馆 Book 命名**：`对话收藏与记忆` 是否合意（仅影响 Agent 全文归档位置）。

---

## 附录 A：证据索引（本文引用的全部 file:line 汇总）

| 主题 | 证据 |
|---|---|
| 菜单结构/契约 | `ContextMenu.tsx:74/:93-102/:130-133/:149-159/:157/:218`；`index.tsx:224-242/:255-349/:786-798` |
| 钉住单槽 | `pinnedMessage.ts:1/:4-13/:14-23/:32-41/:56-59/:66-73`；`index.tsx:324-346/:380-395`；`PinnedMessageButton.tsx:48-71`；`MessageList.tsx:1371-1373` |
| 引用格式/稳定键 | `index.tsx:273-296`；`pinnedMessage.ts:32-41`；`types.ts:74-75`；`useSessionHistoryProjection.ts:150`；`checkpointStore.ts:24/:73/:98` |
| 记忆图书馆 API | `MemoryLibraryAdminController.cs:15/:34/:46/:74/:98/:127/:151/:176/:200/:218`；`api.ts:3365-3520`；`src/pages/Admin.tsx:28-31` |
| Agent 记忆工具 | `SaveMemoryTool.cs:29/:36/:91/:106-128/:152-158/:394`；`MemoryToolArgs.cs:11`；`ContextPipeline.cs:51/:101` |
| L4-PINNED | `ImportantMemoryService.cs:12-16/:17/:19/:149` |
| search_memory 契约模板 | `MemoryLibraryTool.cs:13-19/:45-61/:53-56/:86-93/:146-151` |
| 投影表语义 | `ConversationProjector.cs:15`；`SessionEventsController.cs:542` |
| 消息实体 | `ChatMessageEntity.cs:12-13/:16-17/:19-23/:40-47/:49-50/:56-57/:60` |
| 消息 API/分页 | `MessageApiController.cs` 类头（route `:20`）/`:72`/`:124`；DTO `:24-40` |
| 消息搜索 | `MessageSearchController.cs:12-15/:29/:43-52` |
| Schema 演进先例 | `ChatMessageSchemaBootstrapper.cs:16-38/:21/:40-77`；`PlatformDbContext.cs:28`（DbSet `:10-153`）；`Migrations/20260503062428_AddAgentChatMessages.cs` |
| 删除纯前端态 | `useChatState.ts:1400-1402` |
| 收藏能力为零 | recon 报告 Q6 穷尽检索（PuddingPlatformAdmin/src、PuddingPlatform、PuddingCore、PuddingRuntime、PuddingHost、PuddingMemoryEngine 分目录 0 业务命中） |
