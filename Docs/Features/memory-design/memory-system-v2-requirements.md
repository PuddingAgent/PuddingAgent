# 记忆系统 v2 核心需求

> 创建时间: 2026-06-30
> 来源: 默认助手与用户的深度讨论
> 状态: requirements-complete / minimal-v1-review
> 最近修订: 2026-07-05，切换为 Memory Notes 驱动的最小必要性 V1
> 前置规划: `Docs/superpowers/specs/2026-07-01-memory-v2-foundation-prerequisites.md`
> 简化决策: `Docs/superpowers/specs/2026-07-04-memory-v2-wiki-book-v1-simplification.md`
> V1 必要性设计: `Docs/superpowers/specs/2026-07-05-memory-v2-minimal-v1-necessity-design.md`

---

## 零、当前推进规则

Memory System v2 的下一阶段切换为 Memory Notes 驱动的最小必要性 V1。目标是先跑通 `compression memory notes -> session.compressed -> background worker -> subconscious LLM -> page update -> Book/Page upsert`，不再用 F0-F10 或多 intent coordinator 作为 V1 串行门槛。

V1 判断标准：

```text
默认不做。
只有已经证明缺它系统跑不起来，才进入 V1。
```

当前约束：

- V1 只实现最小链路：压缩阶段 `memoryNotes`、Hook、最小持久任务、SubconsciousWorkerService、Wiki Page 写入入口。
- 潜意识 LLM 的主输入是压缩阶段显意识 LLM 产出的 `memoryNotes`，不是完整会话窗口；原始消息只作为可选回查证据。
- 潜意识 LLM 输出目标 Page 的最终内容，不能选择 reuse/append/supersede/merge。
- Validator v1 只做 JSON shape 和必填字段校验；workspace/agent/library scope 由框架 job context 固定，不由 LLM 决定。
- 写入入口负责 get-or-create Book/Page，并用 LLM 输出的最终内容 replace Page body。
- 写入入口不做段落级 merge、heading merge、冲突检测或语义判断；需要保留旧内容时，由潜意识 LLM 读取当前页面后写入完整最终内容。
- 语义一致性判断不使用内容 hash；内容 hash 只能用于审计、缓存或诊断等精确字节场景，不能决定记忆复用、取代或合并。
- F0-F10、TTL、复杂调度、完整 observability、Admin UI v2、Knowledge Graph 写入、repair apply 均后置，不阻塞 V1。
- 本阶段是开发环境推进，不保留旧数据兼容性；允许通过重置 SQLite 数据库获得干净 schema，不把旧数据迁移、回填、兼容读取作为验收条件。
- Admin 前端的 Memory Library 管理界面仍是 Memory v2 总体验收项，但不作为 Wiki Book v1 MVP 阻塞项。

### 0.0 Memory Notes 驱动的 V1 极简链路

V1 链路：

```text
会话压缩
       │
       ▼
compression summary + memoryNotes
       │
       ▼
session.compressed
       │
       ▼
持久化任务（简单 enqueue/dequeue/complete/fail）
       │
       ▼
SubconsciousWorkerService
       │
       ▼
潜意识 LLM（主输入 memoryNotes，可选回查原始消息/当前页面）
       │
       ▼
page update JSON
       │
       ▼
Wiki Page 写入入口（upsert replace）
```

Plan v1：

```json
{
  "schema": "pudding.memory_wiki_page_update.v1",
  "updates": [
    {
      "book": "记忆系统设计",
      "page": "/Memory v2/V1 原则",
      "content": "# V1 原则\n\n- 默认不做，除非证明缺它系统跑不起来。\n- 潜意识 LLM 以压缩阶段的 memory notes 为主输入。\n"
    }
  ]
}
```

写入入口职责：

1. 标准化 Book title。
2. 标准化 Page path。
3. Book 不存在则创建。
4. Page 不存在则创建。
5. 用 `content` 替换 Page body。
6. 保存 Page。

### 0.1 Memory Library 第一性抽象

Memory Library 不是业务记忆策略本身，而是一个底层数据存储设施，定位类似数据库。它只提供可持久化、可检索、可追踪、可维护的数据结构和操作能力；不预设“什么内容该记住”、不预设“这是偏好/经验/技能/项目事实”的业务判断。

对 Agent 和人类暴露的基础形态应是层级笔记本：

```text
MemoryLibrary
└── Notebook / Book
    └── Page
        └── Page
            └── Page
```

当前代码中的 `Book` 可继续作为 Notebook 的兼容命名，`Chapter` 可继续作为 Page 的兼容命名；设计语义上不再把 `Chapter` 限定为“书籍章节”，而是递归页面节点。是否进行 `Chapter -> Page` 的代码级命名迁移，作为后续独立迁移议题，不阻塞 Memory v2。

在层级笔记本之上叠加知识图谱层：

```text
Page --related_to--> Page
Page --supersedes--> Page
Page --supported_by--> SourceReference
Page --extracts--> Fact
Fact --contradicts--> Fact
Notebook/Page --mounted_in--> TreeNode
```

因此 Memory Library 的目标形态是 **SQLite-backed hierarchical notebook database with knowledge graph overlay**：

- SQLite 是唯一事实源，负责身份、作用域、关系、版本、删除、索引、溯源和并发一致性。
- Markdown 是 Page 内容格式，不是独立事实源；md 文件可以作为导出、长文本或外部文档，但不承担核心一致性。
- 知识图谱复用底层节点与关系，不另起一套文件系统 Wiki 或第二套图数据库。
- 上层 Agent、潜意识 LLM、召回策略负责解释业务语义；Memory Library 只负责存储与查询。

---

## 一、问题诊断

### 1.1 当前症状

| 症状 | 证据 |
|------|------|
| 45 Books，76% 冗余 | 8 主题各重复 4-5 个 Book，26 archived |
| `delete_book` 返回 ok 但 list 未变化 | 39 次 delete 调用，list 仍为 45 |
| `save_memory` 写入时无检索回环 | 同名 Book 每次创建新副本 |
| 记忆维护依赖意识 Agent | 心跳协议手动整理，不可持续 |
| 记忆注入无元数据标注 | L6-CONTEXT-AUGMENT 不标注来源/置信度 |

### 1.2 根因（第一性原理）

记忆系统的核心矛盾：**存储无限 vs 检索有限 vs 上下文更有限**。

冗余的危害不在存储层（磁盘便宜），而在检索层——污染有限检索结果，占据更有限的上下文预算。

根因链条：
```
save_memory 写入时不检索已有
  → 相同主题创建新 Book
  → 旧 Book archived 但从未清理
  → 45 Books 中 76% 冗余
  → 检索时扫描垃圾数据
  → 浪费上下文预算 + 噪声污染
```

---

## 二、核心架构：三层分离 + HOOK 驱动

```
┌─────────────────────────────────────────┐
│  意识层 (me, pro 模型)                    │
│  - 对话、推理、决策                      │
│  - 不负责记忆维护                        │
│  输入: 用户消息 + system prompt           │
└──────────────┬──────────────────────────┘
               │ HOOK: on_session_compressed
               │ (Pudding 框架触发，不可跳过)
┌──────────────▼──────────────────────────┐
│  潜意识层 (flash 模型，后台异步)           │
│  - 接收上一个 Session 的完整原始消息流     │
│  - 决定：记忆放哪里、更新什么、淘汰什么    │
│  - 不建树，只决定位置                     │
│  - 维护 SKILL                            │
│  - 维护 INDEX.md                         │
│  输出: 一组记忆操作指令                   │
└──────────────┬──────────────────────────┘
               │ 操作指令
┌──────────────▼──────────────────────────┐
│  存储层                                   │
│  - Session Log (原始消息流)               │
│  - 记忆图书馆 (Notebook/Page Tree + KG)   │
│  - SKILL (可执行知识)                     │
│  - 外部 .md 文件 (长文本)                 │
│  - INDEX.md (总索引)                      │
└─────────────────────────────────────────┘
```

### 2.1 为什么必须三层分离

| 现状态：一层 | 目标态：三层 |
|-------------|-------------|
| 我在心跳时手动整理记忆 | 潜意识 LLM 后台异步维护 |
| 记忆维护占用我的上下文 | 上下文完全隔离，不影响对话 |
| 是否整理取决于我是否"记得" | HOOK 触发 → 强制执行 |
| 整理时只能看到压缩摘要 | 潜意识拿到完整原始消息 |
| 压缩时新建 Book，旧的不淘汰 | 潜意识决定更新/淘汰 |

### 2.2 "不可能三角"分配

| 维度 | 负责层 | 说明 |
|------|--------|------|
| 效果（精准召回） | 意识层 + 存储层 | 语义检索、向量召回 |
| 效率（缓存命中） | 存储层 + 框架 | 缓存策略、PINNED 稳定 |
| 简单性（低维护成本） | 潜意识层 | 后台异步，意识层无感 |

### 2.3 潜意识 Agent Runtime 模型

潜意识不是普通聊天 LLM，也不是可调用工具的通用 Agent。它是一个受限的、后台异步运行的 Memory Maintenance Agent。

**运行边界**:
- 触发时机：上一个对话窗口结束、`session.compressed` Hook 完成、或等价的会话压缩完成事件之后。
- 输入范围：主输入为压缩阶段显意识 LLM 产出的 `memoryNotes`；可选读取当前目标 Page 内容和必要的原始消息证据。
- V1 输出范围：只输出结构化 `memory_wiki_page_update.v1`，用于描述要更新哪个 Book/Page 以及该 Page 的最终 Markdown 内容。
- 后续增强输出范围：可再引入复用、取代、合并、降权、归档、过期或索引维护建议，但不进入 Wiki Book v1。
- 执行范围：由 Pudding 框架的 Wiki Page 写入入口执行；潜意识 LLM 不直接写库。

**禁止能力**:
- 不接触物理电脑环境。
- 不调用终端、文件系统、浏览器、网络、外部 API 或普通工具。
- 不读取当前允许输入范围以外的 workspace、agent、session 或 memory library。
- 不等待人审计；无人值守运行时只能自动执行、自动拒绝、自动重试或自动隔离。
- 不用内容 hash 判断语义一致性。
- 不让 LLM 选择 workspace、agent、library；这些 scope 由框架 job context 固定。

### 2.4 Memory Library 与潜意识 LLM 的职责切分

Memory Library 不承担智能整理职责。它只提供底层操作：

- 创建、读取、更新、删除 Notebook/Page。
- 维护 Page 的递归父子结构。
- 维护通用关系边。
- 维护来源引用、版本链、状态、索引和检索。
- 提供 workspace、agent、library 作用域隔离。

潜意识 LLM 是 Memory Maintenance Agent，只负责基于输入证据提出结构化维护计划：

- 哪些原始消息应提纯为 Page 或 Fact。
- 应写入哪个 Notebook/Page 位置。
- 哪些 Page/Fact 应建立 `related_to`、`depends_on`、`contradicts`、`supersedes` 等关系。
- 哪些旧 Page 应被取代、归档或降权。

执行仍由框架 validator 与 write coordinator 负责。潜意识 LLM 不直接操作 SQLite、不接触文件系统、不调用通用 Agent 工具。

**无人值守降级语义**:
- 合法 page update JSON：进入 Wiki Page 写入入口。
- 非法 JSON 或缺必填字段：job failed 或 retry，具体取决于简单队列策略。
- 低置信度、quarantine、delete active、跨 scope plan 等复杂降级不进入 Wiki Book v1；scope 不从 plan 读取。

---

## 三、核心需求（R1-R9）

### R1: 写入前检索回环（强制）

**问题**: `save_memory` 写入时不检查已有，导致重复 Notebook/Page。

**需求**:
- 每次 `save_memory` 调用，框架必须先检索同名 Notebook/Page 是否存在
- 存在 → 更新现有 Notebook/Page，旧内容标记 `superseded_by`
- 不存在 → 新建
- 此检查由框架层执行，不依赖 Agent 判断

**实现位置**: `SaveMemoryTool` 或框架层 `MemoryLibrary` 写入管道

---

### R2: 取代语义（强制）

**问题**: 没有"新版本取代旧版本"的概念。archived 的 Book 不知道被谁取代。

**需求**:
- 每条记忆记录必须支持 `superseded_by` 字段
- 新版本写入时，框架自动在旧版本上设置 `superseded_by → 新版本ID`
- 检索时自动排除 `superseded_by != null` 的记录
- 除非用户显式搜索历史版本

**数据结构示例**:
```json
{
  "chapter_id": "abc123",
  "content": "用户偏好: 代码风格 X",
  "superseded_by": "def456",    // 被哪个新版本取代
  "superseded_at": "2026-06-30T10:00:00Z",
  "confidence": "confirmed",
  "source": "exact"
}
```

---

### R3: 半衰期管理（强制）

**问题**: 所有记忆被视为等权重永久有效。实际上不同类型衰减速度完全不同。

**需求**:

| 记忆类型 | TTL | 过期行为 |
|----------|-----|----------|
| 会话摘要 | 7 天 | 降级为归档，不主动召回 |
| 决策记录 | 30 天 | 标记需重新验证 |
| 经验教训 | 90 天 | 长期保留 |
| 用户档案 | 永久 | 仅在更新时修改 |
| 用户偏好 | 永久 | 仅在更新时修改 |
| 系统日志 | 7 天 | 自动清理 |
| 工具使用记录 | 3 天 | 自动清理 |

**实现**:
- 每条记忆创建时按类型自动设 TTL
- 框架层定期扫描过期记忆，自动归档/清理
- 检索时 TTL 过期的不召回（除非显式搜索）

---

### R4: HOOK 触发机制（框架层）

**问题**: 记忆维护靠 Agent 自觉执行，不可靠。

**需求**:
- 框架在以下时机触发 HOOK：
  - `on_session_compressed` — Session 压缩完成
  - `on_session_closed` — Session 关闭
  - `on_error_detected` — 检测到系统错误
- HOOK 触发后，框架自动启动潜意识 LLM 任务
- HOOK 不可跳过，不可被 Agent 覆盖
- 任务异步执行，不阻塞意识层

**HOOK 触发流程**:
```
Session 压缩完成
       │
       ▼
  HOOK: on_session_compressed
       │
       ├── 框架收集上一个 Session 的完整原始消息流
       │
       ├── 框架调用潜意识 LLM (flash 模型)
       │   ├── 输入: 完整消息流 + 当前记忆库状态
       │   └── 输出: 一组记忆操作指令
       │
       └── 框架执行操作指令:
          ├── 更新 Notebook X / Page Y
          ├── 新建 Notebook Z 或 Page Z
           ├── 标记 superseded
           ├── 清理 TTL 过期
           └── 更新 INDEX.md
```

---

### R5: 潜意识 LLM 职责边界（明确）

**问题**: 如果职责不清晰，潜意识 LLM 会做不该做的事（如生成低质量内容）。

**职责（必须做）**:
- 决定信息应写入哪个 Book。
- 决定信息应写入哪个 Page path。
- 生成该 Page 的最终 Markdown 内容。
- 第一阶段不决定 reuse/append/supersede/merge，不决定 TTL，不决定删除。

**不负责（禁止做）**:
- 从零构建记忆内容 ← 意识层产出
- 生成 SKILL 的完整正文 ← 只能在意识层产出的基础上微调
- 修改决策 ← 只能建议
- 删除 active 的记忆 ← 只能 archive 或标记 superseded
- 调用普通工具、终端、文件系统、浏览器、网络或外部 API
- 依赖人审计才能继续执行

**边界原则**: "潜意识只产出 page update；写入层负责找到页面、替换内容、保存页面。"

---

### R6: 写入预算（Agent 层约束）

**问题**: 不是所有信息都值得记。什么都记 = 什么都找不到。

**需求**:
- 写入前必须通过"会被未来检索到吗？"测试
- 以下情况**不应该**写入记忆库：
  - 纯信息搬运（已在 Session Log 中）
  - 一次性临时数据
  - 工具调用中间结果
  - 已在记忆库中存在且未变化的信息
- 以下情况**必须**写入：
  - 决策 + 为什么这么决策
  - 遇到的问题 + 解决方案
  - 用户的偏好和约束
  - 文件路径 + 一句话描述

---

### R7: 检索防污染（注入格式）

**问题**: 当前 L6-CONTEXT-AUGMENT 注入记忆时不标注元数据，Agent 无法区分精确记录和模糊摘要。

**需求**:
- 所有注入的记忆必须附带元数据标签：
  - `[时间戳]` — 创建时间
  - `[来源: exact | summary | inferred]` — 精确记录 / 压缩摘要 / 推断
  - `[置信度: confirmed | uncertain]` — 已确认 / 不确定
  - `[已被取代]` — 如果有新版本
- Agent 看到低置信度/摘要来源标记时必须降低信任权重
- Agent 看到"已被取代"标记时必须拒绝使用

**注入格式示例**:
```
[SYSTEM] 以下是可能相关的历史记忆（仅供参考，请以当前对话为准）：
- [2026-06-28] 用户开发了 github_search 工具 | 来源: summary | 置信度: high
- [2026-06-25] 用户偏好精细化规则设计 | 来源: exact | 置信度: confirmed
- [2026-06-22] 端口配置 8080 | 来源: inferred | 置信度: uncertain ⚠️
```

---

### R8: "继续"类模糊查询特殊处理（Agent 层强制）

**问题**: 模糊查询走语义检索，召回错误记忆，Agent 按错误上下文行动。

**需求**:
- 遇到"继续""上次""那个"等无锚点指代 → **不走语义检索**
- 强制流程:
  1. `query_session_logs` 查最近会话
  2. `goal_read` 读当前目标状态
  3. 找到候选后 → **向用户确认**（不默默行动）
  4. 用户确认后 → 再检索相关记忆

---

### R9: 真删除（框架层修复）

**问题**: `delete_book` 返回 `ok` 但 `list_books` 未变化。39 次调用验证。

**需求**:
- `delete_book` 必须从索引中真正移除
- `list_books` 必须反映实时状态（非缓存）
- 或者：如果不支持真删除，框架应返回明确错误而非假 `ok`

---

## 四、详细设计方案、步骤与验收标准

本节把 R1-R9 拆成可执行步骤。每个步骤都必须由 Pudding 框架、后台 Worker、存储管道或硬编码上下文管道实现；Agent 显意识提示词只能作为辅助约束，不能作为唯一实现。

### Step 0: 建立记忆 v2 基线与重置边界

**覆盖需求**: 全部 R1-R9 的实施前置。

**目标**:
- 明确现有 `IMemoryLibrary`、`IMemoryLibraryConvenience`、`SaveMemoryTool`、`ManageMemoryTool`、`SubconsciousConsolidationHook`、`ContextPipeline` 的职责边界。
- 明确本阶段不保留旧数据兼容性；开发环境允许直接重置 SQLite 数据库和 FTS 索引。
- 形成可重复验证的基线测试，先证明当前问题存在，再逐项修复。
- 明确 `Book/Chapter` 是当前代码兼容命名，设计语义分别为 `Notebook/Page`。

**设计方案**:
- 新增或整理一组 Memory v2 contract tests，覆盖 Notebook 创建、Page 写入、重复写入、页面更新、删除、检索过滤、上下文注入格式。
- 为现有记忆数据建立一次只读诊断命令：统计 active/archived/superseded Notebook 数、重复标题、孤儿 Page、孤儿 Pointer、FTS 索引残留。
- 提供开发环境重置路径：停止服务、清理 SQLite/FTS/本地记忆数据、重新启动并生成干净默认 Library。
- 不设计旧数据迁移、旧 schema backfill 或旧索引兼容读取；必要时只保留只读诊断输出，帮助确认重置前后的差异。

**约束条件**:
- 不以兼容旧数据为目标；如果 schema 变化需要破坏旧数据，优先重置数据库。
- 可暂时保留现有 `save_memory` / `manage_memory` 参数契约作为开发效率兼容，但不能因此牺牲 v2 数据模型。
- 当前实现可继续使用 `BookId`、`ChapterId` 字段；对外设计和新增文档优先使用 Notebook/Page 语义。
- 不允许把迁移逻辑塞进普通读取路径，避免每次检索都触发重写。
- 诊断命令默认只读；重置命令必须显式触发，并只能面向开发环境。

**验收标准**:
- 存在一组可运行的 Memory v2 contract tests，至少覆盖 R1、R2、R7、R9 的关键行为。
- 诊断输出能列出重复 Notebook、archived Notebook、superseded Page 记录和 FTS 残留数量。
- 存在可复现的开发环境数据库重置步骤，重置后 Memory v2 schema、FTS、默认 Library 初始化正常。
- 验收不要求旧数据库中的历史 Book/Chapter 数据可被新代码继续读取。

---

### Step 0.5: Memory Library Page Tree 与通用知识图谱底座

**覆盖需求**: R1、R2、R5、R7 的存储前置；也是潜意识 LLM 自动整理前置。

**目标**:
- 将 Memory Library 明确为层级笔记数据库，而不是业务记忆策略。
- 支持 `Notebook -> Page -> Page -> Page` 的递归页面结构，供人类和 Agent 直接浏览。
- 在 Page Tree 之上提供通用知识图谱关系，不把业务语义写死在存储层。
- 为潜意识 LLM 暴露可维护的底层图谱操作能力，而不是另起一套工具或 md 文件系统。

**设计方案**:
- 兼容层：
  - 当前 `Book` 继续对应 Notebook。
  - 当前 `Chapter` 继续对应 Page。
  - 当前 `ChapterRelation` 可作为 Page-to-Page relation 的第一阶段实现。
- Page Tree v1：
  - Page 支持 `ParentPageId`、`PageOrder`、`Path` 或等价字段。
  - 支持 `create_page`、`create_child_page`、`list_child_pages`、`move_page`、`update_page`。
  - Page 内容继续使用 Markdown；Markdown 是内容格式，不是事实源。
- Generic Relation v1：
  - 规划从 `ChapterRelation` 演进为 `MemoryRelation`。
  - Relation 字段应包含：`SourceNodeType`、`SourceNodeId`、`TargetNodeType`、`TargetNodeId`、`RelationType`、`Weight`、`Confidence`、`SourceReferenceId`、`CreatedByJobId`、`Status`。
  - 第一阶段可先支持 `page -> page`；第二阶段扩展到 `page/fact/preference/source_reference/tree_node` 任意节点。
- SourceReference：
  - Page、Fact、Relation 都必须能挂来源证据。
  - 来源证据只记录可回溯引用，不把完整原始上下文长期复制进图谱边。
- 工具复用：
  - Agent `manage_memory` 与潜意识写入协调器复用同一组 Memory Library service/facade。
  - 潜意识 LLM 不使用普通 Agent 工具箱，只输出结构化 plan；框架将 plan 映射到底层 Page/Relation 操作。

**约束条件**:
- Memory Library 不判断“这是不是值得记忆”；写入预算和业务判断属于 Agent/潜意识/召回策略。
- 不做 md-only Wiki：文件可以导出或外链，但 SQLite 仍是唯一事实源。
- 不做 pure graph：Page 的 Markdown 叙事内容必须保留，不能把长期记忆全部拆成 triples。
- 不在第一阶段做通用实体抽取；先使用现有稳定节点：Notebook、Page、Fact、Preference、SourceReference、TreeNode、Pointer。
- 不因命名修订立即大规模迁移表名和字段名；避免把当前阶段扩展成无关的代码命名迁移。旧数据不作为兼容约束。

**验收标准**:
- 文档和接口说明中明确 `Book = Notebook compatibility name`、`Chapter = Page compatibility name`。
- 能创建多级 Page，并按父子关系稳定列出。
- 能给 Page 建立 `related_to`、`depends_on`、`supersedes`、`contradicts`、`supported_by` 等关系，且关系带来源与置信度。
- 检索结果能返回 Page 路径、来源引用、关系摘要和是否被取代。
- 潜意识 dry-run plan 能表达 Page 写入和 Relation 写入，不需要接触文件系统或普通工具。

---

### Step 0.6: Admin 记忆图书馆管理界面纳入验收

**覆盖需求**: R1、R2、R5、R7、R9 的可视化管理与验证入口。

**目标**:
- 在 Admin 前端提供 Memory Library v2 的管理界面，让人类能直接查看和维护 Notebook/Page Tree 与知识图谱关系。
- 把前端管理能力纳入本次验收，避免后端数据结构完成但无法可视化检查。
- 复用现有 `/admin/memory-library` 路由和 `Memory Library Admin API`，升级为 v2 语义。

**设计方案**:
- 页面入口：
  - 保留现有 `Source/PuddingPlatformAdmin/config/routes.ts` 中的 `/memory-library`。
  - 页面语义从 Book/Chapter 管理升级为 Notebook/Page Tree 管理。
- 核心视图：
  - 左侧 Workspace/Agent/Library 选择。
  - 中间 Notebook/Page Tree，支持展开多级 Page。
  - 右侧 Page 详情，展示 Markdown 内容、来源、状态、版本链、关系边。
  - 搜索区展示命中 Page 的路径、来源、置信度、是否 superseded。
- 管理操作：
  - 创建 Notebook。
  - 创建 Page / 子 Page。
  - 编辑 Page 标题、内容、排序、父子位置。
  - 添加/查看关系边：`related_to`、`depends_on`、`supersedes`、`contradicts`、`supported_by`。
  - 查看 SourceReference 与潜意识 Job 写入来源。
  - 删除或归档操作必须显示影响范围，开发环境可允许真删除。
- 调试与验收：
  - 页面应能触发或查看潜意识 dry-run/execute 结果的摘要，但调试 API 必须继续归类在 debug-only API 分组。
  - 前端管理操作必须经过后端 MemoryWriteCoordinator 或等价协调层，不能绕过框架直接拼接低层写入。

**约束条件**:
- UI 不承担业务记忆判断；它只提供底层结构和关系的管理入口。
- 不为了兼容旧数据设计复杂降级 UI；开发环境以重置后的 v2 schema 为准。
- 不把完整原始会话、完整 LLM 输出或大段工具输出直接展示为长期页面内容；只展示脱敏/截断摘要和可追溯引用。
- 前端包管理使用 `pnpm`，不使用 `npm`。
- 界面风格遵循现有 Admin 的工作台风格：信息密度适中、结构清晰、避免营销页式布局。

**验收标准**:
- `/admin/memory-library` 能加载默认 workspace/agent/library，并展示 Notebook/Page Tree。
- 能在 UI 中创建 Notebook、创建子 Page、编辑 Page，并刷新后保持结构稳定。
- 能在 UI 中新增并查看 Page 关系边，关系带类型、权重/置信度和来源。
- 搜索结果显示 Page 路径、来源、状态、superseded 标记和关系摘要。
- 删除/归档操作后，UI、搜索结果和后端 API 状态一致。
- 至少有一组前端测试或 Playwright 验收覆盖页面加载、树展开、Page 详情和基础写入路径。

---

### Step 1: R9 真删除闭环

**覆盖需求**: R9。

**目标**:
- `delete_book` 的语义从“看起来成功的归档”收敛为“真删除或明确失败”。
- `list_books`、FTS、Pointer、Page/Chapter、BookIndex 对删除结果保持一致。

**设计方案**:
- `manage_memory delete_book` 调用 `IMemoryLibrary.DeleteBookAsync`，不再调用 `ArchiveBookAsync`。
- 删除失败时返回 `status=error` 和明确原因，例如 `book not found`，禁止返回假 `ok`。
- 底层删除继续由 `MemoryLibrary.DeleteBookAsync` 负责级联清理 Notebook/Book、Page/Chapter、Pointer、BookIndex，并依赖 SQLite trigger 更新 FTS。

**约束条件**:
- 只在显式 `delete_book` 时真删除；普通过期、取代、潜意识淘汰默认走 archive/superseded，不得静默真删 active 记忆。
- 删除必须 workspace scoped：不能删除其他 workspace 的 Book。
- 如果底层存储无法保证级联清理，则必须中止并返回错误，不能半删。

**验收标准**:
- `manage_memory delete_book` 后立即调用 `list_books`，结果不再包含该 Book。
- `GetBookReadOnlyAsync(bookId)` 返回 null。
- 该 Book 下章节内容不能再被 `grep_memory/search_memory` 搜到。
- 不存在 Book 时返回 `status=error`，而不是 `status=ok`。

**当前状态**:
- 已完成首个修订：`manage_memory delete_book` 已改为调用底层 `DeleteBookAsync`，并新增回归测试。

---

### Step 2: R1 写入前检索回环

**覆盖需求**: R1。

**目标**:
- 所有框架级记忆写入都先查已有 Notebook/Page，避免同主题或同位置重复增长。
- 让“写入”默认成为 upsert，而不是盲目 append。

**设计方案**:
- 在 `IMemoryLibraryConvenience.UpsertExperienceAsync` 前增加 `MemoryWriteCoordinator` 或等价写入协调层。
- 写入流程固定为：
  1. 解析 workspace、library、memory type、canonical book title。
  2. 通过 BookRegistry/canonical title 查找目标 Book。
  3. 在目标 Notebook/Page 子树内按标题、source reference、语义检索和当前 Agent 作用域召回候选 Page；候选召回只负责缩小范围，不用内容 hash 判断语义一致性。
  4. 候选存在且未 superseded：进入更新/取代流程。
  5. 候选不存在：创建新 Page。
- 对 `save_memory`、潜意识写入、Admin 手工写入统一走同一个协调层，禁止多个入口各自实现去重。
- Book 级并发保护必须下沉到存储层：同一 `LibraryId + Title + active` 建唯一索引；`CreateBookAsync` 先查 active Book，唯一冲突后重读并返回已存在 Book。

**约束条件**:
- 检索回环必须在框架层执行，不能要求 Agent 先自己调用 search。
- 可以依赖潜意识/记忆 LLM 判断候选是否语义一致；确定性规则只负责 workspace、Book、Agent 作用域和来源边界过滤，不能用 hash 代替语义判断。
- 唯一索引只用于 Notebook 标题级幂等和并发防重，不用于 Page/Fact 内容语义一致性判断。
- 模糊相似度命中只能进入“候选”，不能直接覆盖；高风险类型如用户偏好、决策记录必须保留来源指针。
- 同一 workspace 内去重；不同 workspace 不能互相影响。

**验收标准**:
- 连续两次 `save_memory` 写入同一 `notebook/title/content`，Notebook 数不增加。
- 连续两次写入同一主题但不同内容时，目标 Notebook 不重复创建。
- 写入结果返回是否命中 existing Notebook/Page，以及采用 create/update/supersede 的决策。
- 并发写入同一 title 不产生两个同名 active Book。

---

### Step 3: R2 取代语义与版本链

**覆盖需求**: R2。

**目标**:
- 新旧记忆之间存在明确的版本关系，旧版本不会继续污染默认检索。
- Agent 可以在需要时显式追溯历史版本。

**设计方案**:
- 为 Page/Fact 级记录增加取代元数据；当前代码中的 Chapter 已落地：
  - `Status`: `active | superseded`
  - `SupersededByChapterId`
  - `SupersededAt`
  - 后续 Fact 层继续补 `Supersedes`、`ReplacementReason`、更完整状态枚举。
- 写入协调层在更新同一事实/偏好/决策时创建新版本，旧版本标记 `superseded`，并写入新版本 ID。
- 默认检索只返回 active 记录；精确 ID 读取保留历史审计能力。
- 增加显式历史检索开关，例如 `include_history=true` 或 `mode=history`。

**约束条件**:
- 不允许物理覆盖旧内容后丢失来源；旧版本至少保留来源、时间、取代原因。
- `superseded` 不等于 `archived`：前者表示被新版本替代，后者表示生命周期降级。
- 检索默认必须排除 superseded；只有用户显式查历史时才返回。
- 版本链必须防循环：A 不能最终 supersede 自己。

**验收标准**:
- 写入新偏好“用户改为 X”后，旧偏好记录存在但状态为 `superseded`。
- 默认 `search_memory` 不返回旧偏好；`include_history=true` 可返回旧偏好，并标注已被谁取代。
- 注入 L6 上下文时不会注入 superseded 记录；如果显式注入历史，必须带 `[已被取代]` 标签。
- 数据库层或服务层测试覆盖版本链、防循环、默认过滤。

---

### Step 4: R3 半衰期与生命周期管理

**覆盖需求**: R3。

**目标**:
- 记忆按类型自然衰减，避免“所有东西永久 active”。
- 过期行为可审计、可回滚，不影响用户档案和稳定偏好。

**设计方案**:
- 为记忆记录增加生命周期元数据：
  - `MemoryType`
  - `CreatedAt`
  - `LastValidatedAt`
  - `ExpiresAt`
  - `DecayPolicy`
  - `LifecycleStatus`
- 建立默认策略表：
  - 会话摘要：7 天后归档。
  - 决策记录：30 天后标记 `needs_revalidation`，等待未来会话证据自动复核，不自动删除。
  - 经验教训：90 天后可压缩，不默认删除。
  - 用户档案/用户偏好：永久，仅更新取代。
  - 系统日志：7 天后清理。
  - 工具使用记录：3 天后清理。
- 潜意识 Worker 或独立 maintenance Worker 定期扫描，生成 lifecycle actions：archive、compress、mark_review、delete。
- 删除和压缩前写诊断事件，保留短预览、来源标识、原因和影响数量；可保存事件去重用的输入指纹，但不得把 hash 当作语义一致性的判断依据。

**约束条件**:
- 半衰期策略不能只靠自然语言提示词；必须有硬编码默认表。
- 永久类记忆不得自动过期删除。
- 自动清理不能删除 active 决策和用户偏好，只能标记或取代。
- Debug 模式可以保存短预览；默认长期指标不得保存完整敏感内容。

**验收标准**:
- 创建不同 type 的记忆时自动写入对应 `ExpiresAt` 或永久标记。
- 到期扫描后，会话摘要变 archived，工具使用记录被清理，用户偏好仍 active。
- 默认检索不返回过期 archived 记录。
- 诊断工具能查询每轮生命周期处理的数量、类型、动作和失败原因。

---

### Step 5: R4 HOOK 触发与后台 Job 化

**覆盖需求**: R4。

**目标**:
- 记忆维护由框架生命周期事件触发，不依赖 Agent 主动想起来。
- 后台任务异步执行，不阻塞用户对话和 SSE done。

**设计方案**:
- R4 不做单点回调，采用 Hook System v2：`IHookPublisher` 将框架生命周期点发布为强类型 Hook 事件，再复用 `IInternalEventBus` / `IPriorityEventQueue` / `EventDispatcher` 进行持久化和派发。
- 把 `on_session_compressed`、`on_session_closed`、`on_error_detected` 标准化为内部事件。
- Hook 只发布事件，不直接调用潜意识 LLM，不直接写 MemoryLibrary。
- 新增或复用潜意识事件处理器，把事件转成持久化 `SubconsciousJobs`：
  - `memory.consolidate_session`
  - `memory.review_error`
  - `memory.lifecycle_maintenance`
- Worker 根据空闲信号、workspace 限流和优先级异步执行 Job。
- Job 输入必须包含 sessionId、workspaceId、agentInstanceId、message range、payload fingerprint、触发原因。
- 当前 `session.compressed` 第一条链路采用来源事件级幂等键：`memory.consolidate_session:{workspaceId}:{originalSessionId}:{compactionId}`。该键只用于同一次框架来源操作去重，不用于判断两段内容是否语义一致。
- 完整设计见 `Docs/superpowers/specs/2026-06-30-hook-system-v2-design.md`；第一实现目标是 `session.compressed`。

**约束条件**:
- Hook 不可被 Agent 覆盖或跳过。
- Hook 不允许长时间阻塞主循环；只做事件发布和轻量校验。
- Job 必须幂等：同一 session/message range/job type 不重复创建未终态 Job。
- 后台失败必须进入 retry/dead_letter，不得吞掉。
- 不允许使用内容 hash 作为记忆语义一致性判断；语义一致性由候选召回后的 LLM/框架判定负责。

**验收标准**:
- Session compressed 后生成一条可查询的潜意识 Job。
- 主对话完成路径不等待潜意识 LLM 结果。
- 同一 session 重复触发不会产生重复 pending Job。
- Worker 成功后 Job 状态变 completed；失败后可见 retry 或 dead_letter。
- 诊断日志能按 sessionId/traceId 关联 Hook 事件、Job、写入结果。
- Hook 单测能断言 `session.compressed` 只入队 `memory.consolidate_session`，并携带 source hook、event id、compaction id。

---

### Step 6: R5 潜意识 LLM 职责边界与执行协议

**覆盖需求**: R5。

**目标**:
- 潜意识 LLM 是受限后台 Agent：只读会话证据和已有记忆，只输出维护计划，不接触外部电脑环境。
- 最终写入由框架校验后的操作协议自动执行、自动拒绝、自动重试或自动隔离。

**设计方案**:
- 潜意识 LLM 输出结构化 plan，而不是自由文本：
  - `targetNotebook` / compatibility `targetBook`
  - `targetPage` / compatibility `targetChapter`
  - `operation`: `create | update | supersede | archive | skip | propose_skill_update`
  - `memoryType`
  - `confidence`
  - `sourceMessageRange`
  - `reason`
- 框架执行器对 plan 做校验：
  - Notebook 是否存在或允许创建。
  - 目标 Page 是否属于当前 workspace。
  - active 记忆是否允许 archive/supersede。
  - 是否超过写入预算。
- 对 SKILL 和 INDEX.md 的修改只允许小范围 patch 计划或候选提案；高风险变更自动降级为 `quarantined` 或 `rejected`，不等待人审计。

**约束条件**:
- 潜意识不得从零编造长期记忆内容；内容必须来自 session log、显意识产物或已有记忆。
- 潜意识不得直接删除 active 记忆。
- 潜意识不得修改用户决策，只能记录“某决策被更新/取代”的事实。
- 所有操作必须带来源消息范围和来源标识，保证可回溯。
- 潜意识不得调用普通工具、终端、文件系统、浏览器、网络或外部 API。
- 运行时不得依赖人工确认；低置信度自动隔离，非法边界自动拒绝，格式错误自动按队列重试。

**验收标准**:
- 潜意识输出非结构化文本时，执行器拒绝执行并记录错误。
- 缺少 sourceMessageRange/source identity 的写入 plan 被拒绝。
- 尝试删除 active 记忆的 plan 被降级为 archive/supersede 或拒绝。
- 成功执行的每条写入都能回溯到原始 session message range。
- 低置信度 plan 生成 `quarantined/defer_for_recheck/complete_quarantined` 结果，不进入写库，也不等待人审。

---

### Step 7: R7 检索防污染与 L6 注入格式

**覆盖需求**: R7。

**目标**:
- Agent 在上下文里看到记忆时，能区分精确事实、摘要、推断和低置信内容。
- 被取代、过期、低置信记忆不再无提示地污染推理。

**设计方案**:
- 统一召回结果 DTO，至少包含：
  - content/snippet
  - createdAt/updatedAt
  - sourceKind: `exact | summary | inferred`
  - confidence: `confirmed | high | medium | uncertain`
  - memoryStatus
  - sourceReference
  - supersededBy
  - expiresAt/lifecycleStatus
- `ContextPipeline` 的 L6-CONTEXT-AUGMENT 只通过统一 formatter 注入，不允许工具或服务直接拼接裸文本。
- 注入标题固定提示：历史记忆仅供参考，当前对话和显式用户指令优先。
- 默认过滤 superseded/expired；显式历史模式必须标注 `[已被取代]` 或 `[已归档]`。

**约束条件**:
- 注入格式必须稳定，避免破坏 prefix cache；可变内容集中在列表项。
- 不保存完整上下文到长期 metrics；只保存长度、数量、hash、类型分布。
- 低置信内容不得用强确定措辞注入。
- L6 注入不得超过上下文预算上限，超限时按 score 和可信度裁剪。

**验收标准**:
- L6 注入的每条记忆都带时间、来源、置信度。
- superseded 记录默认不注入。
- summary/inferred 记录有明显标签，不会伪装为 exact。
- ContextPipeline 单测断言注入格式、过滤规则、预算裁剪。

---

### Step 8: R6 写入预算与显意识写入约束

**覆盖需求**: R6。

**目标**:
- 控制 Agent 主动写入的频率和质量，防止把流水账写进长期记忆。
- 把“什么值得记”从提示词愿望变成工具/框架可验证规则。

**设计方案**:
- 在 `save_memory` 前加入质量门禁：
  - 空内容、过短内容、工具中间结果、无来源内容给出 warning 或拒绝。
  - 与已有 active 记忆完全重复时返回 duplicate，不写入。
  - 类型为 preference/profile/decision/lesson/path-index 时允许较高优先级。
- 增加写入预算计数：
  - 每轮对话最大主动写入数。
  - 每 session 最大新增 Notebook/Page 数。
  - 潜意识 Job 每次最大写入/更新/归档数量。
- 超预算时返回结构化结果，要求进入压缩或合并，而不是继续新增。

**约束条件**:
- 预算不能阻止必须保存的用户偏好、安全约束和关键决策。
- 预算规则必须可配置，但默认值保守。
- 不得依赖 Agent 自我约束作为唯一门禁。
- 质量过滤只做短预览、来源标识和结构化分类诊断，避免泄露完整敏感内容。

**验收标准**:
- 同一 session 超过主动写入上限时，`save_memory` 返回预算错误或写入建议。
- 工具中间结果、一次性临时数据默认不进入长期记忆。
- 用户偏好、决策、经验教训、重要路径说明通过质量门禁。
- 诊断指标能统计写入尝试数、拒绝数、重复数、超预算数。

---

### Step 9: R8 模糊查询保护

**覆盖需求**: R8。

**目标**:
- 用户说“继续”“上次”“那个”时，系统不再直接语义召回一条看似相关但错误的记忆。
- 先通过当前 goal 和最近 session 建立锚点，再决定是否检索记忆。

**设计方案**:
- 在用户消息进入语义记忆召回前增加 ambiguous-intent guard。
- 触发词包括但不限于：`继续`、`接着`、`上次`、`那个`、`刚才`、`之前的`，并结合消息长度和缺少实体判断。
- 触发后默认流程：
  1. 读取当前 goal 状态。
  2. 查询最近 session logs/message transcript。
  3. 生成 1-3 个候选锚点。
  4. 候选不唯一时向用户确认。
  5. 用户确认后再执行相关记忆检索。
- 如果只有一个高置信当前活跃目标，可继续，但必须在响应或内部 trace 中记录使用的锚点。

**约束条件**:
- 模糊查询保护必须在框架层或上下文管道层执行，不能只靠 Agent 提示词。
- 不允许对无锚点“继续”直接调用语义 search_memory。
- 用户明确给出任务 ID、文件路径、PR、sessionId 时不应误拦截。
- 保护逻辑不能阻塞普通明确查询。

**验收标准**:
- 输入“继续”且无 active goal 时，不触发语义记忆检索，并要求确认。
- 输入“继续 task42”或“继续 memory-system-v2”时，可建立锚点后再检索。
- 错误候选不会被静默采用。
- 有测试覆盖中文模糊指代、明确锚点、单一 active goal 三类路径。

---

### Step 10: INDEX.md、Skill 与外部文件维护

**覆盖需求**: R4、R5、R6、R7 的文件侧延伸。

**目标**:
- 长文本和可执行知识不全部塞进 MemoryLibrary；外部 Markdown、Skill、INDEX.md 由框架维护索引关系。
- 潜意识只提出维护计划，框架执行最小 patch。

**设计方案**:
- `memory/INDEX.md` 作为人可读索引，记录 `memory/` 下文件、用途、更新时间、来源。
- 长文档写入外部 `.md` 后，MemoryLibrary 只保存指针、摘要、标签和来源；如需指纹只用于追踪版本，不用于语义去重。
- Skill 更新走候选 patch：小修可自动应用，高风险改动需要显意识/用户确认。
- INDEX 更新与 MemoryLibrary 写入在同一个 Job 结果中记录，但失败隔离：INDEX 更新失败不回滚核心记忆写入，只进入 retry。

**约束条件**:
- INDEX 不是唯一事实来源；数据库记录仍是结构化检索事实来源。
- 不允许潜意识生成完整 Skill 正文覆盖现有 Skill。
- 文件路径必须 workspace scoped，禁止写出允许目录。
- Markdown 文件必须可被 grep 和诊断工具发现。

**验收标准**:
- 新增 `memory/*.md` 文件后，INDEX 有对应条目。
- 长文本记忆检索结果能指向外部文件和章节。
- INDEX 更新失败可诊断、可重试，不导致主写入失败。
- Skill 更新有 patch 记录和来源指针。

---

### Step 11: 可观测性、诊断和回滚

**覆盖需求**: 全部 R1-R9 的运维验收。

**目标**:
- 每次记忆写入、取代、删除、归档、注入都能回答“为什么发生、影响了什么、能否回滚”。
- 将记忆 v2 纳入现有 Trace/Metrics/Insights 三层可观测体系。

**设计方案**:
- Trace 层记录 Job、事件、操作 plan、来源消息范围、结果、错误。
- Metrics 层写入聚合字段：写入数、更新数、superseded 数、删除数、重复命中数、注入数量、低置信数量、预算拒绝数。
- 当前 R4 第一阶段 Metrics 已落地在潜意识 Job 状态转换上：`SubconsciousJobQueue` 写入 `category=memory`、`name=subconscious_job.enqueue|lease|complete|retry|dead_letter`，维度包含 `job_type`、`source_hook_name`、`job_id`、`job_status`、`workspace_id`、`session_id`、`retry_count` 和来源事件标识；该指标只记录结构化事实，不保存完整任务内容。
- Insights 层提供诊断脚本：
  - 重复 Notebook/Page 报告。
  - superseded 链检查。
  - 过期记忆报告。
  - L6 注入污染风险报告。
  - 潜意识 Job 队列报告：`query_metrics.py subconscious-jobs` 按 `job_type + source_hook_name` 输出 enqueue/lease/complete/retry/dead_letter、completion/retry/dead-letter rate 和最后错误。
- 回滚策略：
  - 真删除只对显式 delete 使用，无法从普通 archive 恢复时必须依赖备份。
  - supersede/archive 可通过历史模式恢复为 active。
  - Job 失败不会影响主对话完成。

**约束条件**:
- 默认不保存完整工具参数、完整上下文或完整敏感内容。
- 指标字段必须稳定命名，summary 只能辅助。
- 诊断工具优先读结构化表，必要时再回溯 JSONL/session timeline。
- 回滚必须 workspace scoped。

**验收标准**:
- 每次 memory Job 可按 sessionId/traceId 查询完整链路。
- `query_metrics.py` 或等价工具能输出记忆写入、取代、删除、注入统计。
- 诊断脚本能发现重复 Notebook/Page、断裂 superseded 链、过期 active 记忆。
- 故障时 Job 进入 retry/dead_letter，并可导出诊断包。

---

## 五、实施优先级

| 优先级 | 需求 | 理由 | 实施位置 |
|--------|------|------|----------|
| **P0** | R1 写入前检索回环 | 防止冗余持续增长 | 框架层 |
| **P0** | R2 取代语义 | 让"版本"概念生效 | 框架层数据模型 |
| **P0** | R4 HOOK 触发机制 | 让维护自动化 | 框架层 |
| **P0** | R9 真删除 | 当前 delete 是假的 | 框架层 bug fix |
| **P1** | R3 半衰期管理 | 自动淘汰过期记忆 | 框架层 |
| **P1** | R5 潜意识 LLM | HOOK 后的执行者 | 框架层 + 模型 |
| **P1** | R7 检索防污染 | 注入格式规范 | 框架层 L6 |
| **P2** | R6 写入预算 | Agent 行为约束 | SOUL 层 |
| **P2** | R8 模糊查询保护 | Agent 行为约束 | PINNED 层 |

---

## 六、与现有系统提示词层的关系

| 层 | 覆盖需求 | 性质 |
|---|---|---|
| `SECURITY-GUIDE` | R1/R2/R4（框架强制规则） | 框架硬编码注入 |
| `PINNED` | R6/R8（Agent 行为约束） | 记忆库注入 |
| `SOUL` | R6/R8 补充细节 | 模板渲染 |
| `L6-CONTEXT-AUGMENT` | R7（注入格式） | 框架运行时注入 |

---

## 七、实现修订记录

| 日期 | 范围 | 状态 | 证据 |
|------|------|------|------|
| 2026-06-30 | R9 真删除 | done | `manage_memory delete_book` 改为调用底层 `DeleteBookAsync`，新增 `ManageMemory_DeleteBook_Removes_Book_From_ListBooks` 回归测试 |
| 2026-06-30 | R1 写入前检索回环（workspace scoped 第一阶段） | partial | `UpsertExperienceAsync` 先确保当前 workspace library，再在当前 workspace library 内做 exact Book lookup；全局 FTS 候选必须过滤到当前 workspace。新增 `SaveMemory_Upsert_Does_Not_Route_To_Same_Title_Book_In_Other_Workspace` 回归测试 |
| 2026-06-30 | R1 写入前检索回环（Chapter 精确重复第一阶段） | partial | `UpsertExperienceAsync` 在目标 Book 内追加 Chapter 前先按 title/content/agentInstanceId 精确匹配已有 Chapter，命中则复用原 `chapterId`。新增 `SaveMemory_Upsert_Reuses_Existing_Chapter_For_Same_Title_And_Content` 回归测试 |
| 2026-06-30 | R1 写入前检索回环（LLM 语义判定第一阶段） | partial | Exact 未命中且存在 `IMemoryLlmClient` 时，`UpsertExperienceAsync` 将当前 Book 内同 Agent 作用域候选交给记忆 LLM 判断 `reuse_existing|supersede_existing|append_new`；置信度不足、JSON 非法或返回非法 chapterId 时退回 append。新增 `SaveMemory_Upsert_Uses_Llm_To_Reuse_Semantically_Equivalent_Chapter` 回归测试 |
| 2026-06-30 | R2 取代语义（Chapter 版本链阶段） | partial | 记忆 LLM 返回 `supersede_existing` 且通过候选合法性/置信度校验时，`UpsertExperienceAsync` 调用 `SupersedeChapterAsync` 创建新的 active Chapter，并将旧 Chapter 标记为 `superseded`、写入 `SupersededByChapterId/SupersededAt`；`ListChaptersAsync`、FTS、向量和 scoped 检索默认过滤旧版本，`GetChapterAsync` 保留精确审计。`manage_memory list_chapters` 与 `grep_memory` 支持显式 `include_history=true` 返回历史版本并标注取代链。`SaveMemory_Upsert_Uses_Llm_To_Supersede_Existing_Chapter` 覆盖版本链、默认过滤和显式历史检索；Fact 层完整取代元数据仍待后续实现 |
| 2026-06-30 | R1 并发同名 Book 防重 | partial | `Books` 增加 `UX_Books_Library_Title_Active` 唯一索引，约束同一 Library 内 active Book 标题唯一；`CreateBookAsync` 改为先查 active Book，唯一冲突后重读返回已有 Book。新增 `CreateBook_ConcurrentSameTitle_ShouldReturnSingleActiveBook` 回归测试；内容 hash 仍不参与语义一致性判断 |
| 2026-06-30 | R4 Hook System v2 设计 | design | 新增 `Docs/superpowers/specs/2026-06-30-hook-system-v2-design.md`，确定 Hook v2 为强类型发布层 + 现有持久事件队列 + 内部处理器 + 只读外部 Hook；`session.compressed` 作为首个落地事件，Hook 只发布事件，不直接调用潜意识 LLM 或写 MemoryLibrary |
| 2026-06-30 | R4 Hook System v2 第一实现 | partial | 新增 `IHookPublisher`、`session.compressed` payload/schema、`HookPublisher`、压缩成功后发布 Hook、`SessionCompressedMemoryMaintenanceHook`；当前桥接到既有 `ConsolidationJob` channel，后续仍需持久 `SubconsciousJobs` |
| 2026-06-30 | R4 持久 SubconsciousJobs 第一实现 | partial | 新增 `SubconsciousJobs` 实体、schema、DbContext 映射、初始化 SQL、`ISubconsciousJobQueue` 与 `SubconsciousJobQueue`；`session.compressed` Hook 现在入队 durable `memory.consolidate_session`，`SubconsciousWorkerService` 优先 lease 持久任务并标记 completed/retry/dead_letter，旧 `ConsolidationJob` channel 保留为兼容 fallback；幂等键只基于来源操作，不做内容 hash 语义去重 |
| 2026-06-30 | R4/R11 SubconsciousJobs Trace 证据层 | partial | `SubconsciousJobQueue` 在 enqueue、lease、complete、retry、dead_letter 状态转换时写入 `RuntimeActivity`，metadata 只包含 jobId/jobType/status、workspace/session、agent/template、source hook/event/compaction 和 retry/lease 信息，不保存完整任务内容；新增 `QueueTransitions_ShouldRecordRuntimeActivities` 回归测试 |
| 2026-07-01 | R4/R11 SubconsciousJobs Metrics/诊断第一阶段 | partial | `SubconsciousJobQueue` 在 enqueue、lease、complete、retry、dead_letter 状态转换时写入 `TelemetryMetricCategories.Memory` 指标；`Tools/Diagnostics/query_metrics.py subconscious-jobs` 可按 `job_type + source_hook_name` 汇总事件数、Job 数、完成率、重试率、死信率和最后错误；新增 `QueueTransitions_ShouldRecordTelemetryMetrics` 与 `test_subconscious_jobs_summarizes_memory_metrics` 回归测试 |
| 2026-07-01 | R4 legacy duplicate-learning 开关 | partial | 新增 `SubconsciousOptions`，默认关闭 `SubconsciousConsolidationHook` 旧 agent-loop channel producer，并默认关闭 `AgentExecutionService` 的 legacy fallback enqueue；仅显式配置 `Subconscious:EnableLegacyConsolidationHook=true` 或 `Subconscious:EnableLegacyAgentExecutionFallback=true` 时启用兼容路径。新增 `RuntimeServiceExtensionsTests` 覆盖默认禁用与显式启用 |
| 2026-07-01 | Memory v2 前置基础设施规划 | design | 新增 `Docs/superpowers/specs/2026-07-01-memory-v2-foundation-prerequisites.md`，将后续推进从功能实现切换为 F0-F10 基础设施分层评审；明确下一推荐里程碑为 F3 Worker Scheduling & Resource Control，并重申语义一致性不使用内容 hash |
| 2026-07-01 | F3 Worker Scheduling & Resource Control 设计展开 | design | 在前置基础设施规划中补充 Milestone A 详细设计：idle detector、workspace limiter、budget gate、scheduler skip reason、配置草案、Trace/Metrics、诊断入口、测试验收矩阵；该阶段明确不调用潜意识 LLM、不生成 plan、不写 MemoryLibrary |
| 2026-07-01 | F3 Worker Scheduling & Resource Control 实施计划 | plan | 新增 `Docs/superpowers/plans/2026-07-01-memory-v2-f3-worker-scheduling-plan.md`，按 TDD 拆分调度契约、队列过滤/统计、`SubconsciousJobScheduler`、Worker 接入、DI、诊断脚本和文档验收；该计划仍保持 F3 边界，不进入潜意识 LLM plan 执行 |
| 2026-07-01 | F3 Worker Scheduling & Resource Control 第一实现 | done-pre-llm | 新增 `SubconsciousOptions.Scheduling`、`SubconsciousJobScheduler`、queue lease query/stats/workspace rolling lease count/schedule skip 记录和诊断聚合；`SubconsciousWorkerService` durable path 现在经 scheduler 执行 enabled、idle、dry-run、global/workspace/session limiter 和每 workspace 滚动窗口 job-count 预算判定。真实 token/cost 预算需等 F4/F5 接入潜意识 LLM plan 和执行指标后再做；仍不进入潜意识 LLM plan 或 MemoryLibrary 写入 |
| 2026-07-01 | F4 Subconscious Plan Protocol 第一实现 | partial | 新增 `MemoryMaintenancePlan` schema、`MemoryMaintenancePlanValidator`、支持 action 集合、validation context/result/error code；validator 可拒绝非法 JSON、跨 workspace 引用、低置信度操作和候选集外引用。新增 `MemoryMaintenancePlanValidatorTests` 覆盖 append/supersede 合法计划与四类拒绝路径；新增 F4 schema/fixtures 文档。该阶段仍不调用潜意识 LLM、不执行 plan、不写 MemoryLibrary |
| 2026-07-01 | F4 Subconscious Plan Generation dry-run 第一实现 | partial-dry-run | 新增 `SubconsciousPlanGenerationService`，调用潜意识 `IMemoryLlmClient` 生成 dry-run `MemoryMaintenancePlan`，立即交给 `MemoryMaintenancePlanValidator` 校验；成功/失败均记录 `RuntimeActivity memory_maintenance_plan.validate` 和 `TelemetryMetric memory_maintenance_plan.validation`。Runtime DI 已注册 plan validator 和 generation service；该阶段仍不执行 plan、不持久化 plan result、不写 MemoryLibrary |
| 2026-07-01 | F4 Job result envelope 第一实现 | partial-result-envelope | 新增 `SubconsciousJobResultEnvelope`、`SubconsciousJobs.ResultJson`、`ISubconsciousJobQueue.RecordResultAsync/GetResultAsync`；dry-run plan 结果可转为 accepted/rejected envelope，并由当前 lease owner 写入 Job 结果。该阶段只持久化结构化结果摘要和 error codes，不保存原始 LLM 全文，不完成 Job，不执行 plan，不写 MemoryLibrary |
| 2026-07-01 | F4 降级策略第一实现 | done-pre-executor | `SubconsciousJobResultEnvelope` 增加 `decision/nextAction`；validator 结果映射为 `accept_for_execution/enqueue_for_execution`、`quarantined/defer_for_recheck/complete_quarantined`、`retry_later/retry_job`、`reject_complete/complete_rejected`。低置信度无人值守隔离，不等待人审；该阶段仍不执行 plan、不写 MemoryLibrary；F5 进入 Write Coordinator 设计。 |
| 2026-07-01 | F5 Memory Write Coordinator 设计 | design | 新增 `Docs/superpowers/specs/2026-07-01-memory-v2-f5-write-coordinator-design.md`，确定统一 `MemoryWriteCoordinator`、`MemoryWriteCommand`、source identity、ValidateOnly/DryRun/Execute 模式、F4 operation 映射、`MemoryWriteResultEnvelope`、Trace/Metrics 和迁移策略；第一轮实现建议只做 DTO、validator、dry-run、F4 mapper 和 audit envelope，不接真正写库。 |
| 2026-07-01 | F5 Memory Write Coordinator 实施计划 | plan | 新增 `Docs/superpowers/plans/2026-07-01-memory-v2-f5-write-coordinator-plan.md`，按 TDD 拆分 DTO/validator、Runtime coordinator dry-run、F4 operation mapper、F4 accepted plan 到 F5 dry-run 证明、observability 和文档验证；计划明确第一轮不迁移 `save_memory`，不接真实 MemoryLibrary 写入。 |
| 2026-07-01 | F5 Memory Write Coordinator dry-run 第一实现 | partial-dry-run | 新增 `MemoryWriteCommand`、`MemoryWriteCommandValidator`、`MemoryWriteResultEnvelope`、`MemoryWriteCoordinator` dry-run、F4 `MemoryMaintenancePlan` operation mapper 和 coordinator Trace/Metrics；合法 F4 plan 可转换成 F5 dry-run 结果，非法 command 被拒绝并记录错误。该阶段仍不接真实 `MemoryLibrary` 写入，不迁移 `save_memory`。 |
| 2026-07-01 | F5 dry-run Job result 衔接 | partial-dry-run-envelope | `SubconsciousJobResultEnvelope` 增加 `MemoryWriteResults`，`SubconsciousPlanGenerationResult.ToJobResultEnvelope(...)` 可接收 F5 dry-run result，`SubconsciousJobs.ResultJson` 可持久化并读回该审计结果。该阶段仍不完成 Job、不执行 plan、不写 `MemoryLibrary`。 |
| 2026-07-02 | F5 Worker durable dry-run 串接 | partial-worker-dry-run | `SubconsciousWorkerService` durable path 在 F4/F5 依赖存在时生成潜意识 dry-run plan、将 accepted operations 映射为 F5 dry-run commands、调用 `MemoryWriteCoordinator` 并通过 `RecordResultAsync` 写入 Job result envelope；该路径不调用旧 orchestrator、不 complete job、不写 `MemoryLibrary`。 |
| 2026-07-02 | F5 explicit append execute 第一实现 | partial-execute-append | `MemoryWriteCoordinator` 在显式 `Mode=execute` 且 `Intent=append_new` 时可通过 `IMemoryLibrary` 创建 Library/Book/Chapter，返回 actual Book/Chapter ID，并记录 `memory_write.execute`。潜意识 Worker 仍固定 dry-run，`save_memory` 尚未迁移。 |
| 2026-07-02 | 潜意识记忆归属隔离 fix | partial-isolation | 新增 `SubconsciousMemoryScope` 作为目标记忆归属边界，F4 `SubconsciousPlanGenerationRequest` 必须携带 workspace/agent/session/library scope；潜意识 LLM 调用仍属于框架角色，但 invocation 与 token usage 使用目标 scope 归属；`MemoryMaintenancePlanValidator` 拒绝跨 workspace/agent/session/library 的 plan source；F5 `subconscious_plan` source 必须携带 `agentId` 并透传 `memoryLibraryId`。当前 Worker durable path 已 enforce workspace/agent/session，library 维度待 job payload 明确携带后 enforce。 |
| 2026-07-02 | 潜意识 LLM 脚本/API 触发与行为评估 | partial-debug-eval | 新增 debug-only `/api/debug/subconscious/trigger` 和 `/api/debug/subconscious/jobs/{jobId}/result`，`Tools/Diagnostics/subconscious_debug.py trigger --wait` 可投递 durable job 并等待结构化 `SubconsciousJobResultEnvelope`。实测 `subconscious-api-eval-20260702-222900` 返回 accepted dry-run plan，F5 `MemoryWriteResultEnvelope.status=dry_run`，Job completed，`Chapters/MemoryPreferences` 对该 SourceSessionId 均未写入。该结论证明当前潜意识链路可评估但仍不自动真实写库。 |
| 2026-07-02 | 潜意识受限后台 Agent 模型修订 | design-code-sync | 明确潜意识是无人值守、后台异步、只读会话和记忆、只输出结构化维护计划的受限 Agent；禁止普通工具、终端、文件系统、浏览器、网络和外部 API；低置信度从 `manual_review/hold_for_review` 改为 `quarantined/defer_for_recheck/complete_quarantined`；潜意识 delete 在 F5 进入 `autonomous_delete_not_allowed`，不等待人审。 |
| 2026-07-03 | Memory Library 层级笔记本 + 知识图谱底座 | design | 将 Memory Library 明确为 SQLite-backed hierarchical notebook database with knowledge graph overlay；`Book` 作为 Notebook 兼容命名，`Chapter` 作为 Page 兼容命名；新增 Step 0.5，规划 Page Tree、Generic Relation、SourceReference、潜意识受限图谱维护入口和 md-only/pure graph 非目标。 |
| 2026-07-03 | 开发环境重置边界 + Admin 验收 | design | 明确本阶段不保留旧数据兼容性，允许重置 SQLite/FTS/本地记忆数据；新增 Step 0.6，将 `/admin/memory-library` 的 Notebook/Page Tree、关系边、来源、搜索、删除/归档和前端测试纳入 Memory v2 验收。 |
| 2026-07-03 | F9 Reset/Repair/Admin Tools 详细设计 | design | 新增 `Docs/superpowers/specs/2026-07-03-memory-v2-f9-reset-repair-admin-tools-design.md`，拆分 reset 工具、diagnostics dry-run、Admin Memory Library v2 管理界面、Admin API command 化、observability、测试与分阶段验收；现有 `/admin/memory-library` 和 `MemoryLibraryAdminService` 是升级对象，不另起入口。 |
| 2026-07-04 | Wiki Book v1 简化决策 | design | 新增 `Docs/superpowers/specs/2026-07-04-memory-v2-wiki-book-v1-simplification.md`；MVP 从多操作 `reuse/append/supersede/merge` 收敛为单一 `edit_page` plan；Validator v1 只做 JSON shape 和必填字段；写入层负责 get-or-create Book/Page、merge content 和 save；F0-F10、TTL、复杂调度、完整观测、Admin UI v2 后置。 |

### 7.1 当前代码侧结论

- `save_memory action=delete type=book` 已经走底层真删除管道。
- `manage_memory delete_book` 原先只归档 Book 并返回 `ok`，会导致 `list_books` 仍看到该 Book；本次已修订为真删除，找不到 Book 时返回明确错误。
- Memory Library 的最新设计语义已调整为 Notebook/Page Tree + Knowledge Graph overlay；当前代码命名中的 `Book/Chapter` 暂作为兼容层保留，后续是否迁移为 `Notebook/Page` 需要单独规划。
- 本阶段是开发环境推进，不要求旧数据兼容；后续 schema 变化可以通过重置数据库验收，不需要为历史 Book/Chapter 数据设计迁移兼容层。
- Admin 前端已有 `/memory-library` 入口和 Memory Library API 基础，后续需升级为 v2 Notebook/Page Tree + KG 管理界面，并作为本次验收项。
- F9 详细设计已明确：Admin 读取路径可以继续复用现有 service，写入路径必须逐步 command 化进入 `MemoryWriteCoordinator`；reset/diagnostics 是开发环境验收工具，不是生产迁移方案。
- 2026-07-05 后，Memory Notes 驱动的 Wiki Book V1 是 Memory v2 当前主线：潜意识 LLM 不再选择 `reuse_existing/append_new/supersede_existing/merge_candidates`；它基于压缩阶段 `memoryNotes` 输出 `memory_wiki_page_update.v1`，写入层只 upsert replace。
- R1 已完成四个第一阶段：`save_memory` 不再把当前 workspace 的写入路由到其他 workspace 的同名/相似 Book；同 Book、同标题、同内容、同 Agent 的重复写入会复用已有 Chapter；字面不同但语义等价的候选可由记忆 LLM 判断后复用已有 Chapter；同一 Library 并发创建同名 active Book 会收敛到同一个 Book。后续继续补来源引用候选；内容 hash 不作为语义一致性依据。
- R2 已完成 Chapter 版本链阶段：`supersede_existing` 会创建新的 active Chapter，旧 Chapter 标记为 `superseded` 并保留精确 ID 审计；默认列表、FTS、向量和 scoped 检索不再返回旧版本；`include_history=true` 可通过 `manage_memory list_chapters` 与 `grep_memory` 显式返回历史版本并标注 `SupersededByChapterId/SupersededAt`。后续继续补 Fact 层完整取代元数据和循环保护。
- R4 已完成 Hook System v2 + 持久 `SubconsciousJobs` 第一实现：不做单点 `on_session_compressed` 回调，改为 `IHookPublisher` 发布强类型 Hook 事件；`ContextCompactionService` 在成功压缩后发布 `session.compressed`，`SessionCompressedMemoryMaintenanceHook` 将事件转成 durable `memory.consolidate_session` Job；`SubconsciousWorkerService` 优先 lease 持久任务并在成功/失败后更新 Job 状态。Trace 证据层和 Metrics 第一阶段已覆盖 Job 状态转换，诊断脚本可输出潜意识 Job 聚合；旧 `ConsolidationJob` channel producer 默认关闭，仅通过 `Subconscious:*Legacy*` 兼容开关显式启用。
- Memory v2 的前置基础设施规划已从 F0-F10 串行推进修订为最小必要性 V1 优先：F3/F4/F5 既有实现作为历史实现和后续增强参考保留；下一步不再选择 `save_memory` wrapper 迁移或 reuse/supersede execute，而是先跑通 `compression memoryNotes -> session.compressed -> durable queue -> SubconsciousWorkerService -> memory_wiki_page_update.v1 -> WikiPageWriteEntry -> MemoryLibrary`。
- 潜意识记忆隔离的当前标准是：调用者身份属于框架潜意识，目标记忆归属由 `SubconsciousMemoryScope` 决定。任何 plan/write source 不能只依赖 workspace/session，必须能追溯到 agent；当目标 library 已知时，也必须限定到该 library。当前代码已覆盖 F4 plan generation、F4 validator、LLM invocation 归属和 F5 source 透传；Worker durable job 仍缺少 library payload，因此当前只 enforce workspace/agent/session，library enforce 需要后续补 `ConsolidationJob` 或 job payload 字段。
- 潜意识调试闭环当前可通过专用 debug API 和脚本触发：`trigger` 只入 durable queue，不绕过真实 worker；`result` 只返回结构化 job result，不暴露完整 LLM 原文。2026-07-02 实测证明旧 F4/F5 dry-run 链路可生成 accepted plan 并完成 Job，且不会写入 `Chapters/MemoryPreferences`；Wiki Book v1 需要把该链路改为真实 `edit_page` 写入验证。
- 潜意识的当前目标定义是受限后台 Agent：主读压缩阶段 `memoryNotes`，可选读当前 Page 和必要原始消息证据；Wiki Book V1 只输出 `memory_wiki_page_update.v1` 的 `book/page/content` updates；不调用普通工具、终端、文件系统、浏览器、网络或外部 API；运行时不等待人审。非法 JSON 或缺字段按队列失败/重试处理，低置信度隔离等复杂降级不进入 V1。
- R3/R4/R5/R7 仍需按本需求和前置基础设施规划继续拆分；不能用 Agent 显意识提示词替代框架管道。
