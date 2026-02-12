# 实施规约：Background Extractor（后台事实搬运工）

> 版本：v1.0 | 日期：2026-07-11
> 借鉴来源：Claude Code 的 extractMemories + Auto-Dream
> 原则：增强现有 ConsolidateAsync，不新建服务

---

## 一、现有基础设施（惊喜）

```
AgentLoop 完成
  → SubconsciousConsolidationHook.OnCompletedAsync()   ← 已有！
    → Channel<ConsolidationJob>                        ← 已有！
      → SubconsciousWorkerService (BackgroundService)  ← 已有！
        → ISubconsciousOrchestrator.ConsolidateAsync()  ← 已有！
          → 从 DB 读会话消息                            ← 已有！
          → LLM 提取事实                                ← 已有！
          → 去重合并写入                                 ← 已有！
```

**只需增强 ConsolidateAsync，不需要新建任何服务或 Hook。**

---

## 二、要做什么

### 当前 ConsolidateAsync 的问题

| 现状 | 问题 |
|------|------|
| 只处理 `LastUserMessage + LastAssistantReply` | 丢失完整会话上下文 |
| 未识别 `[PreCompactFlush]` 消息 | Flush 产出被忽略 |
| 写入时无 tag/概述/类型 | 记忆散落，不可检索 |
| 不维护 INDEX.md | 无结构化索引入口 |
| 不更新项目档案 | 项目知识不积累 |

### 目标行为

```
ConsolidateAsync(job)
  ├─ 1. 加载完整会话消息（包含 [PreCompactFlush] 系统消息）
  ├─ 2. 调用 Flash LLM 提取结构化事实
  │     输入: 会话文本 + [PreCompactFlush] 优先级提示
  │     输出: 每条事实带 {类型, 标题, 概述, tags, 所属Book}
  ├─ 3. 去重检查（对比已有记忆）
  ├─ 4. 写入记忆图书馆（结构化 Chapter）
  │     - save_memory(type=chapter, book=目标Book, title=标题,
  │                    content=内容, tags=标签, source_ref=会话ID)
  ├─ 5. 增量更新 INDEX.md
  └─ 6. 记录 JobLog（可观测）
```

---

## 三、改动文件

### 文件 1: `SubconsciousOrchestrator.cs`

**改动**：增强 `ConsolidateAsync` 方法

```
// 在现有 "加载会话消息" 之后，增加：
// ---- Background Extractor 增强 START ----

// 1. 识别 [PreCompactFlush] 消息作为优先输入
var flushFacts = ExtractFlushFacts(conversationText);

// 2. 构建增强的 LLM 提示词（包含 Flush 事实优先提示）
var extractionPrompt = BuildExtractionPrompt(conversationText, flushFacts);

// 3. 调用 Flash LLM 提取结构化事实
var extractedFacts = await ExtractStructuredFactsAsync(extractionPrompt, ct);

// 4. 每条事实：去重 → 写入记忆图书馆 Chapter
foreach (var fact in extractedFacts)
{
    if (await IsDuplicateAsync(fact, ct)) continue;
    await WriteStructuredChapterAsync(fact, job, ct);
}

// 5. 增量更新 INDEX.md
await UpdateIndexAsync(job.WorkspaceId, extractedFacts, ct);

// ---- Background Extractor 增强 END ----
```

### 文件 2: `SubconsciousOrchestrator.cs`（新增辅助方法）

```
// 从会话文本中提取 [PreCompactFlush] 标记的事实
private static List<string> ExtractFlushFacts(string conversationText)
{
    // 匹配 "[PreCompactFlush" 开头的行 → 提取 "- " 开头的事实
}

// 构建结构化提取的 Flash LLM 提示词
private static string BuildExtractionPrompt(
    string conversationText, List<string> flushFacts)
{
    // 包含：提取目标、分类规则、输出格式（JSON schema）
    // 明确要求: type, title(≤20字), summary(≤50字), tags, targetBook
}

// 调用 Flash LLM 提取事实
private async Task<List<ExtractedFact>> ExtractStructuredFactsAsync(
    string prompt, CancellationToken ct)
{
    // 使用已有的 _memoryLlmClient
}
```

### 文件 3: 新增 `BackgroundExtractorTypes.cs`（DTO）

```
// PuddingMemoryEngine 内新增
public sealed record ExtractedFact
{
    public string Type { get; init; }        // user|project|feedback|reference
    public string Title { get; init; }       // ≤20字
    public string Summary { get; init; }     // ≤50字概述
    public string Content { get; init; }     // 完整内容
    public string[] Tags { get; init; }      // 标签列表
    public string TargetBook { get; init; }  // 目标 Book 名称
}
```

---

## 四、LLM 提示词设计

```
你是 Pudding 的记忆整理服务。从会话中提取值得长期保留的事实。

### 提取规则
1. 优先处理 [PreCompactFlush] 标记的内容（Agent 在压缩前标注为重要）
2. 只提取以下类型：
   - user: 用户偏好、习惯、沟通风格、技能
   - project: 项目事实（名称、技术栈、目录、关键配置、决策）
   - feedback: 纠正、已验证方法、要避免的陷阱
   - reference: 外部系统、文档、链接指针
3. 不要提取：能用 git 找到的、一次性操作、调试细节、任务状态

### 输出格式（JSON）
{
  "facts": [
    {
      "type": "project",
      "title": "≤20字的标题",
      "summary": "≤50字的一句话概述",
      "content": "完整的事实描述",
      "tags": ["标签1", "标签2"],
      "targetBook": "目标记忆Book名称（如：项目知识、用户档案、经验教训）"
    }
  ]
}

### 会话内容
{conversationText}
```

---

## 五、写入记忆图书馆

利用已有的 `save_memory` 路径，写入结构化 Chapter：

```
save_memory(
  type: "chapter",
  book: fact.TargetBook,
  title: fact.Title,
  content: fact.Content,        // 完整内容
  summary: fact.Summary,         // 作为 chapter summary
  tags: fact.Tags,
  source_reference: $"session:{job.SessionId}",
  reference_type: "internal"
)
```

### Book 映射

| 提取类型 | 目标 Book | 说明 |
|---------|----------|------|
| `user` | 用户档案 / 用户偏好 | 个人信息→档案，习惯→偏好 |
| `project` | 项目知识 | 技术栈、配置、决策 |
| `feedback` | 经验教训 | 已验证方法、陷阱 |
| `reference` | 交接索引 | 外部指针 |

---

## 六、可观测

### 日志格式

```
[BackgroundExtractor] started session={sid} flushFacts={N} totalChars={C}
[BackgroundExtractor] extracted facts={Total} types={user:X,project:Y,...}
[BackgroundExtractor] written chapters={W} skipped={S}(duplicates)
[BackgroundExtractor] completed duration={ms}ms
```

### Agent 自查

```
query_session_logs(grep="[BackgroundExtractor]")
search_memory("session:{sessionId}")  → 查看该会话产生的记忆
```

### 关键指标

| 指标 | 含义 | 目标 |
|------|------|------|
| 每次提取数 | 每个会话平均提取几条事实 | 2-8 条 |
| 去重跳过率 | 已有记忆的比例 | < 50% |
| 索引更新耗时 | INDEX.md 维护开销 | < 100ms |
| 写入成功率 | Chapter 写入成功比例 | > 95% |

---

## 七、架构约束

1. ❌ 不新建服务 — 增强已有 `ConsolidateAsync`
2. ❌ 不修改 Hook/Worker — 基础设施不变
3. ❌ 不阻塞主对话 — 全程后台异步
4. ✅ 只用 Flash 模型 — 控制成本
5. ✅ 去重在写入前 — 减少冗余
6. ✅ 失败降级 — 单条写入失败不影响其他

---

## 八、与 Pre-Compaction Flush 的关系

```
Pre-Compaction Flush         Background Extractor
─────────────────────        ────────────────────
时机: 压缩前（同步）          时机: 会话后（后台异步）
输入: 当前窗口消息            输入: 完整会话 + Flush 产出
输出: [PreCompactFlush]      输出: 结构化 Chapter（含tag/概述）
      系统消息                         ↓
         ↓                   记忆图书馆（可检索、可归档）
    Background Extractor      
    的优先输入源
```

---

## 九、验收标准

### 功能
- [ ] 会话结束后，`search_memory` 可查到带 tag/概述的新记忆
- [ ] 记忆条目有 `source_reference` 指向源会话
- [ ] `[PreCompactFlush]` 内容被优先处理
- [ ] 重复事实被跳过（去重生效）

### 性能
- [ ] 单次 ConsolidateAsync 耗时 < 30s
- [ ] INDEX.md 增量更新 < 100ms

### 可观测
- [ ] 日志包含提取数、类型分布、去重跳过数、耗时
- [ ] Agent 可通过 `search_memory` 查到新记忆
