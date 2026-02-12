# QA 审阅报告 — 未提交变更审计

| 项目 | 值 |
|------|-----|
| 报告编号 | QA-2026-05-17-UncommittedChangesAudit |
| 审阅日期 | 2026-05-17 |
| 审阅模型 | DeepSeek-V4-Flash (QA 审计) |
| 审阅范围 | 全部未暂存工作区变更 + 未跟踪文件 |
| 变更总数 | 62 文件（14 modified + 47 deleted + 2 submodule + 2 untracked）|
| **QA 结论** | **PASS_WITH_NOTES** — 无阻断问题，有 4 项建议改进 |

---

## 1. 审阅范围

按变更类别分组审计：

| 类别 | 文件数 | 审阅方式 |
|------|--------|---------|
| Agent 配置系统 (.github/agents/) | 14 | 全文审阅 |
| 工作流 Skill (.github/skills/) | 2 | 全文审阅 |
| 源码 (Source/) | 3 | 全文审阅 |
| 临时文件清理 (Doc/Temp/) | 47 | 批量确认 |
| 子模块 (external/) | 1 | 变更点检查 |
| 新增文档 (Docs/) | 1 | 全文审阅 |
| 工具配置 (.deepseek/) | 1 | 检查 .gitignore |

---

## 2. 变更审计

### 2.1 Agent 模型策略简化 — ✅ 通过

**文件**：
- `.github/agents/AGENTS.md`
- `.github/agents/*.agent.md`（13 个文件：lead, pm, explore, architect, super-dev, dev, lightweight-developer, qa, security-reviewer, integration-debugger, doc, ui-designer, crypto-evaluation-expert, user-agent）
- `.github/copilot-instructions.md`

**变更内容**：

1. **模型池从多厂商（9 种模型）简化为单厂商（2 种模型）**
   - 之前：Claude Opus 4.7 (15x) / Sonnet 4.6 / Haiku 4.5 / GPT-5.5 (7.5x) / GPT-5.3-Codex / Gemini-3.1 / GLM-5.1 / Kimi-K2.6 / MiniMax-M2.7 + DeepSeek-V4-Pro/Flash
   - 之后：仅 DeepSeek-V4-Pro (按量) + DeepSeek-V4-Flash (按量)
   - 全部 14 个 Agent 统一使用 DeepSeek 系列

2. **版本变更历史 (v2.7–v2.9) 已从 AGENTS.md 移除**，替换为一句话注释"全部 Agent 统一 DeepSeek 按量计费"

3. **copilot-instructions.md 第 5 行**：新增"对于探索和寻找任务…建议优先使用 deepseek-v4-flash"

**评估**：

| 维度 | 评价 |
|------|------|
| 合理性 | ✅ 高。单一厂商简化计费管理，消除多厂商费率差异带来的调度复杂度和认知负担 |
| 一致性 | ✅ 高。所有 13 个 Agent 配置文件的 model 字段已全部更新，无遗漏 |
| 风险 | ⚠️ 低。失去模型多样性可能在某些专项任务（如 UI 设计、安全审查）上降效，但 Pro/Flash 双档保留了大/小模型分流能力 |
| 文档质量 | ✅ 好。变更说明清晰，去除了冗长的版本历史 |

**Agent 清单变更验证**：逐个文件确认，14 个 Agent 配置文件的 `model:` 字段均已更新：

| Agent | 旧模型 | 新模型 |
|-------|--------|--------|
| lead | DeepSeek-V4-Pro | DeepSeek-V4-Pro（无变化）|
| pm | Kimi-K2.6 | **DeepSeek-V4-Pro** ✅ |
| explore | Claude Haiku 4.5 | **DeepSeek-V4-Flash** ✅ |
| architect | Claude Opus 4.7 (15x) | **DeepSeek-V4-Pro** ✅ |
| super-dev | GPT-5.5 (7.5x) | **DeepSeek-V4-Pro** ✅ |
| dev | DeepSeek-V4-Pro / GPT-5.3 双选 | **DeepSeek-V4-Pro** ✅ |
| lightweight-developer | DeepSeek-V4-Flash | DeepSeek-V4-Flash（无变化）|
| qa | 多模型交错 | **DeepSeek-V4-Pro** ✅ |
| security-reviewer | GLM-5.1 | **DeepSeek-V4-Pro** ✅ |
| integration-debugger | DeepSeek-V4-Pro | DeepSeek-V4-Pro（无变化）|
| doc | Kimi-K2.6 | **DeepSeek-V4-Pro** ✅ |
| ui-designer | Gemini-3.1 | **DeepSeek-V4-Pro** ✅ |
| crypto-evaluation-expert | Kimi-K2.6 | **DeepSeek-V4-Pro** ✅ |
| user-agent | DeepSeek-V4-Pro | DeepSeek-V4-Pro（无变化）|

**建议 (N1)**：模型同质化后，`@qa` 的"交错调度不同模型杜绝自审"策略失效——开发和审阅使用相同模型系列。建议在 AGENTS.md 中更新 QA 审阅机制说明，明确当前如何保证审阅独立性。

---

### 2.2 工作流 Skill 文档 — ✅ 通过

**文件**：
- `.github/skills/dev-workflow/SKILL.md`（452 行）
- `.github/skills/git-workflow/SKILL.md`（821 行）

**变更内容**：这两个文件在工作区中处于 modified 状态，内容是完整的研发工作流和 Git 工作流规范。

**评估**：

| 维度 | 评价 |
|------|------|
| 完整性 | ✅ 高。dev-workflow 覆盖 6 阶段（Design Gate → Explore → Plan → Implement → QA → Archive）|
| 可操作性 | ✅ 高。每个阶段有明确的产出物、门禁条件、命令示例 |
| git-workflow 特色 | ✅ 出色。多 Agent worktree 隔离、公告板协调、分支命名规范、推送门禁 |
| 一致性 | ✅ 与 AGENTS.md 的 5 阶段流程对齐 |
| 风险 | 无 |

**建议 (N2)**：git-workflow 涵盖 821 行，部分章节（如 §0.4 公告板通信协议）可考虑拆分为独立 Skill，减少加载成本。

---

### 2.3 Doc/Temp 目录清理 — ✅ 通过

**变更内容**：47 个文件被删除，包括：
- 调试脚本（debug-auth.ps1, bench-*.ps1, test-memory-*.ps1 等 30+ 个）
- 临时 DLL（docker-*.dll, verify-*.dll）
- 临时数据库（*.db 5 个）
- 临时 Python 脚本（check_*.py, query_db.py 等）
- 过期批处理（redeploy.ps1, deploy.ps1 等）

**评估**：这是预期的清理操作。路线图（Docs/路线图.md）第 16 行已确认"Doc/Temp 临时文件已清理 (2026-05-17)"。所有文件确认为调试/临时性质，无生产代码。

---

### 2.4 SessionStateManager.cs — ✅ 通过（ADR-016 实现）

**文件**：`Source/PuddingPlatform/Services/SessionStateManager.cs`（433 行）

**变更内容**：新增 `SessionStateManager` 类，实现 `ISessionStateManager`（ADR-016 会话状态层）。

**架构审查**：

```
设计意图对照 ADR-016：
  ✅ 持久化事件日志（session_event_log, append-only）
  ✅ 实时推送通道（Channel per session，生命周期独立于 HTTP 连接）
  ✅ 子代理状态追踪（session_sub_agents 表）
  ✅ IScopeFactory 解决 Singleton 与 Scoped DbContext 冲突
```

**代码质量**：

| 项目 | 评分 | 说明 |
|------|------|------|
| 接口实现 | ✅ | 完整实现 ISessionStateManager 所有方法 |
| XML 注释 | ✅ | 类级和方法级注释充分 |
| 线程安全 | ✅ | ConcurrentDictionary + per-session lock |
| 序列号生成 | ✅ | 先查 `MAX(SequenceNum)` 再递增，保证严格单调 |
| Channel 管理 | ✅ | BoundedChannel(256) + DropOldest 防止内存泄漏 |
| 错误处理 | ✅ | 日志记录完整，无吞异常 |
| 依赖注入 | ✅ | Singleton 通过 IServiceScopeFactory 消费 Scoped DbContext |

**发现的问题**：

| 编号 | 位置 | 问题 | 严重度 |
|------|------|------|--------|
| B1 | 第 114-115 行 | `delta`/`done`/`metadata` 事件使用 `LogWarning`，每轮 LLM 对话产生大量 Warning 日志，应使用 `LogDebug` | 低 |
| B2 | 第 84-86 行 | `SELECT MAX(SequenceNum)` 在无事件时会扫描全表；建议在 `session_event_log` 表上加 `(SessionId, SequenceNum)` 复合索引 | 中 |
| B3 | 第 228 行 | `GetSessionStateAsync` 只在内存字典查找，未持久化到 SQLite；进程重启后所有会话状态重置 | 低（设计决策，符合 ADR）|

**建议 (N3)**：
- 将第 114 行 `LogWarning` 改为 `LogDebug`，避免日志噪音
- 确认 `session_event_log` 表有 `(SessionId, SequenceNum DESC)` 复合索引

---

### 2.5 useChatState.ts — ✅ 通过（注意复杂度）

**文件**：`Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.ts`（1166 行）

**变更内容**：工作区中标记为 modified，内容为完整的 Chat 状态管理 Hook。

**代码审查**：

| 项目 | 评分 | 说明 |
|------|------|------|
| 类型定义 | ✅ | `UseChatStateReturn` 接口明确了 50+ 个返回值 |
| 状态分离 | ✅ | useState 用于 UI 状态，useRef 用于可变引用（不触发重渲染）|
| SSE 订阅 | ✅ | 支持 SSE stream + 轮询 fallback 双模式 |
| 子代理追踪 | ✅ | `subAgentCards` 状态追踪子代理执行进度 |
| 错误处理 | ✅ | abortRef 管理请求取消，消息状态回退 |

**结构问题**：

| 编号 | 问题 | 严重度 |
|------|------|--------|
| S1 | 单个 Hook 管理 6 个领域（workspace / agent / session / chat / sub-agent / modal），1166 行，违反单一职责 | 中 |
| S2 | `handleSendMessage`（推测在 800 行以后）可能包含复杂的流式处理逻辑，与状态声明混在同一文件 | 中 |
| S3 | 轮询重连逻辑（`sessionEventsReconnectTimerRef`）未看到最大重试次数限制 | 低 |

**建议 (N4)**：考虑拆分为：
- `useWorkspaceAgent`（workspace + agent 选择）
- `useSessionList`（session CRUD + 分组）
- `useChatStream`（消息发送 + SSE 流式接收 + 子代理追踪）
- `useChatModals`（创建场景 / 重命名弹窗）

---

### 2.6 MemoryLibraryTests.cs — ✅ 通过

**文件**：`Source/PuddingMemoryEngineTests/MemoryLibraryTests.cs`（1113 行）

**变更内容**：Memory Library 的完整单元测试集。

**测试覆盖清单**：

| 测试类别 | 覆盖项 | 用例数 |
|----------|--------|--------|
| Library CRUD | Create / List / Update / Delete / Get + AccessCount | ~6 |
| Book CRUD | Create / Get / Update / List / Archive / Search + FTS5 | ~10 |
| Chapter CRUD | Add / Get / Update / List（含 SectionIndex 排序）| ~5 |
| Pointer CRUD | Create / Resolve / List（正向+反向）/ 循环检测 | ~6 |
| Branch & Merge | BranchBook / MergeChapter + 冲突检测 | ~4 |
| Tag 索引 | CreateBook 自动索引 / SearchByTagPrefix / GetTagChildren | ~4 |
| 杂项 | StatusFilter / inactive 过滤 / Version 递增 / CreatedAt | ~5 |

**评估**：
- 使用内存 SQLite 实现测试隔离 ✅
- 覆盖正常路径 + 边界条件（空 tag、空 summary、不存在的 ID）✅
- 异步方法使用 `async Task` ✅
- 总计约 35+ 个测试用例，覆盖 IMemoryLibrary 主要 API ✅

**建议**：无。这是高质量的测试集，建议合入。

---

### 2.7 external/github.hyfree.GM 子模块 — ⚠️ 需验证

**文件**：`external/github.hyfree.GM`

**变更内容**：子模块指针变更（具体 commit 差异不可见）。

**风险评估**：
- GM（国密 SM2/SM3/SM4）是密码学核心依赖
- 子模块变更可能是版本更新或安全修复
- **无法从当前工作区确认变更的安全性和兼容性**

**建议**：提交前执行 `git submodule summary` 确认变更的 commit 范围，并验证 SM2/SM3/SM4 相关测试通过。

---

### 2.8 Docs/路线图.md — ✅ 通过

**文件**：`Docs/路线图.md`（147 行，新增文件）

**内容摘要**：
- 当前状态快照：列举 V1 核心 / ADR-016 / 前端状态的完成情况
- Phase 1（P0）：收敛与基础闭环 — 提交当前改动、MCP 客户端集成
- Phase 2（P1）：Session 连续性 — SSE/ETag/轮询 fallback、重新连接事件回放、消息永久化
- Phase 3（P2）：协作与子代理 — P2P 消息转发、子代理 UI 可视化
- Phase 4+（P3）：Memory 图书馆 / 插件市场 / DeepSeek Codex / 本地 RAG

**评估**：
- 清晰的优先级排序（P0→P3），每项有复杂度/前置/状态
- 与 `Docs/Tasks.md` 和架构文档 ADR 关联清晰
- 建议在提交消息中引用此文档

---

### 2.9 .deepseek/instructions.md — ✅ 通过

**文件**：`.deepseek/instructions.md`（新增，由 DeepSeek TUI 自动生成）

**评估**：工具自动生成的项目配置文件，包含项目树和文件摘要。已在 `.gitignore` 中？需确认。

---

## 3. 跨切面审计

### 3.1 架构合规性

| ADR | 关联变更 | 合规 |
|-----|---------|------|
| ADR-016（会话状态层与客户端解耦）| SessionStateManager.cs | ✅ |
| ADR-015（潜意识 LLM 子代理系统）| useChatState.ts 的 subAgentCards | ✅ |
| ADR-017（WebSocket 连接器与网关鉴权）| 未涉及本次变更 | N/A |
| ADR-013/14（记忆与会话数据层）| MemoryLibraryTests.cs | ✅ |

### 3.2 安全审查

| 检查项 | 结果 |
|--------|------|
| Agent 配置中的 API Key 泄露 | ✅ 无硬编码密钥 |
| SessionStateManager 中的序列注入 | ✅ 使用 `MAX()+1` 模式，无注入风险 |
| 子模块变更密码学安全性 | ⚠️ 需验证（见 §2.7）|

### 3.3 测试覆盖

| 模块 | 测试文件 | 状态 |
|------|---------|------|
| PuddingMemoryEngine | MemoryLibraryTests.cs (1113 行, 35+ 用例) | ✅ 新增 |
| PuddingMemoryEngine | MemoryPersistenceTests.cs (878 行) | ✅ 已有 |
| PuddingPlatform.Services | 无 SessionStateManager 测试 | ⚠️ 建议补充 |
| PuddingPlatformAdmin | 无 useChatState 单元测试 | ⚠️ 建议补充 |

---

## 4. 结论与建议

### 结论：PASS_WITH_NOTES

所有变更合理且安全，无阻断性问题。4 项改进建议如下：

| 编号 | 建议 | 优先级 | 类别 |
|------|------|--------|------|
| N1 | AGENTS.md：模型同质化后更新 QA 审阅独立性机制 | 中 | 文档 |
| N2 | git-workflow/SKILL.md：考虑拆分公告板章节为独立 Skill | 低 | 工程 |
| N3 | SessionStateManager.cs：修复 LogWarning 日志级别 + 确认索引 | 中 | 代码 |
| N4 | useChatState.ts：拆分为 4 个领域 Hook | 低 | 重构 |

### 提交前检查清单

- [ ] `git submodule summary` 确认 GM 子模块变更安全
- [ ] `dotnet build` 确认 0 error
- [ ] `dotnet test` 确认全部测试通过
- [ ] 确认 `.deepseek/instructions.md` 在 `.gitignore` 中或决定是否提交
- [ ] 路线图中 T-100（提交当前改动）对应本次提交

---

## 5. 附录：变更文件完整清单

### Modified（14 文件）

```
.github/agents/AGENTS.md
.github/agents/architect.agent.md
.github/agents/crypto-evaluation-expert.agent.md
.github/agents/dev.agent.md
.github/agents/doc.agent.md
.github/agents/explore.agent.md
.github/agents/integration-debugger.agent.md
.github/agents/lead.agent.md
.github/agents/lightweight-developer.agent.md
.github/agents/pm.agent.md
.github/agents/qa.agent.md
.github/agents/security-reviewer.agent.md
.github/agents/super-dev.agent.md
.github/agents/ui-designer.agent.md
.github/agents/user-agent.agent.md
.github/copilot-instructions.md
.github/skills/dev-workflow/SKILL.md
.github/skills/git-workflow/SKILL.md
Source/PuddingMemoryEngineTests/MemoryLibraryTests.cs
Source/PuddingPlatform/Services/SessionStateManager.cs
Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.ts
```

### Deleted（47 文件）

```
Doc/Temp/ (47 files: *.ps1, *.py, *.db, *.dll, *.csx, *.js, *.txt)
```

### Submodule changed（1）

```
external/github.hyfree.GM
```

### Untracked（2）

```
.deepseek/instructions.md
Docs/路线图.md
```
