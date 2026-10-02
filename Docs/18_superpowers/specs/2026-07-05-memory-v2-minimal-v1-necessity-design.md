# Memory System v2 Minimal V1 Necessity Design

> Date: 2026-07-05
> Status: design-for-review
> Scope: 重新定义 Memory v2 V1，只保留“缺它系统跑不起来”的部分

---

## 1. V1 原则

V1 的判断标准：

```text
默认不做。
只有已经证明缺它系统跑不起来，才进入 V1。
```

Memory v2 V1 不是成熟记忆治理系统，也不是完整知识图谱系统。V1 只验证一件事：

```text
会话压缩阶段产生的 memory notes
  -> 后台潜意识 LLM
  -> 更新一个 Wiki Page
```

如果这条链路不能稳定运行，TTL、Coordinator、复杂 Validator、Admin UI、观测指标和知识图谱都没有意义。

---

## 2. 第一性原理链路

V1 链路：

```text
ContextCompactionService
  -> conscious LLM compression
  -> compression summary + memory notes
  -> session.compressed
  -> memory job
  -> SubconsciousWorkerService
  -> subconscious LLM
  -> page update JSON
  -> WikiPageWriteEntry
  -> MemoryLibrary Book/Page
```

关键修正：

- 潜意识 LLM 的主输入不是完整会话窗口。
- 主输入是压缩阶段显意识 LLM 已经提取出的 `memoryNotes`。
- 原始会话消息只作为可选回查证据，不作为 V1 的常规大上下文输入。

这样避免让潜意识 LLM 在全窗口里大海捞针，也复用了压缩阶段已经完成的“重要信息识别”工作。

---

## 3. V1 必要性论证

| 部分 | V1 结论 | 必要性论证 |
| --- | --- | --- |
| `memoryNotes` | 必须有 | 没有它，潜意识只能扫完整会话，成本高且职责重复 |
| `session.compressed` Hook | 必须有 | 没有它，记忆维护无法自动触发 |
| 后台 Worker | 必须有 | 潜意识 LLM 不能阻塞前台会话和压缩流程 |
| 持久任务 | 必须有最小形态 | 进程重启或后台延迟时不能丢掉压缩后的记忆线索 |
| 潜意识 LLM | 必须有 | 需要 LLM 把 notes 归档到合适的 Book/Page，并生成目标页面内容 |
| JSON 输出 | 必须有极小形态 | 需要一个机器可读接口连接 LLM 与写入层 |
| JSON shape 校验 | 必须有 | 非法 JSON 无法写入，必须失败或重试 |
| WikiPageWriteEntry | 必须有 | 写入必须有一个统一入口，避免到处直接写 MemoryLibrary |
| Book/Page 存储 | 必须有 | 这是记忆库的最小数据形态 |
| 原始会话回查 | V1 可选 | 只有 memory note 不足以判断时才需要 |
| Debug trigger | V1 可选 | 如果测试能覆盖链路，可不进 V1；如果需要手工评估 LLM 行为，则保留最小 API |

---

## 4. V1 明确不做

以下能力没有证明“缺它 V1 跑不起来”，因此不进入 V1：

- 多操作 plan：`reuse_existing`、`append_new`、`supersede_existing`、`merge_candidates`。
- `MemoryWriteCoordinator` 作为多 intent 协调层。
- 低置信度隔离、quarantine、manual review。
- TTL、半衰期、生命周期治理。
- 复杂调度：idle、预算、全局限流、workspace rolling budget。
- 完整 Trace/Metrics/Insights 体系。
- Admin Memory Library v2 管理界面。
- Knowledge Graph relation 写入。
- 复杂候选检索和防污染 retrieval contract。
- 内容 hash 语义去重。
- 旧数据兼容迁移。

这些能力可以在 V1 跑通后逐项重新论证；不能因为“以后可能需要”提前进入 V1。

---

## 5. V1 输入

压缩阶段必须产出轻量 `memoryNotes`。

V1 最小形态：

```json
{
  "memoryNotes": [
    "用户要求 Memory v2 V1 默认不做，除非证明缺它系统跑不起来。",
    "潜意识整理应优先使用压缩阶段产出的 memory notes，而不是扫描完整会话窗口。"
  ]
}
```

V1 不要求 note 带分类、置信度、候选页面或 source message ids。

后续可扩展形态：

```json
{
  "memoryNotes": [
    {
      "text": "用户偏好简洁设计，反对过度抽象。",
      "suggestedBook": "用户偏好",
      "suggestedPage": "/设计偏好",
      "sourceMessageIds": ["optional"]
    }
  ]
}
```

扩展形态不进入 V1。

---

## 6. V1 输出

潜意识 LLM 输出目标页面的最终内容，而不是“新增片段让写入层合并”。

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

这把语义合并责任放回 LLM，框架只做确定性的 upsert。

---

## 7. 写入规则

`WikiPageWriteEntry` 只做确定性存储动作：

```text
for each update:
  normalize book title
  normalize page path
  get or create book in job scope
  get or create page by path
  replace page content with update.content
  save
```

V1 不做段落级 merge、不做 heading merge、不做内容冲突检测。

如果需要保留旧页面内容，潜意识 LLM 必须先读取当前页面内容，并在输出的 `content` 中自行保留。

---

## 8. 隔离边界

LLM 不决定归属边界。

以下字段来自 job context：

- workspaceId
- agentId / agentInstanceId
- sessionId
- libraryId
- hookEventId / jobId

LLM 输出中的 scope 字段即使存在也忽略。V1 的 JSON 校验只检查 schema、updates、book、page、content。

---

## 9. 失败处理

V1 失败处理保持最小：

| 失败 | V1 行为 |
| --- | --- |
| 没有 memory notes | job completed no-op |
| LLM 返回非法 JSON | job failed |
| updates 为空 | job completed no-op |
| book/page/content 缺失 | job failed |
| 写库失败 | job failed |

V1 不做 quarantine、不做人工审计、不做复杂 retry policy。

---

## 10. 验收标准

- 压缩阶段能产出 `memoryNotes`。
- `session.compressed` job 能携带或读取这些 `memoryNotes`。
- Worker 能在不扫描完整会话窗口的情况下调用潜意识 LLM。
- 潜意识 LLM 能基于 `memoryNotes` 和可选当前页面内容输出 `memory_wiki_page_update.v1`。
- 写入入口能创建 Book。
- 写入入口能创建 Page path。
- 写入入口能替换 Page content。
- 第二次写同一 Book/Page 不创建重复 Book/Page。
- 完整链路不依赖 Coordinator、TTL、Admin UI、复杂 scheduler 或知识图谱。

---

## 11. 下一步

下一步只做 V1 实施计划：

1. 找到压缩阶段当前 summary 输出结构。
2. 增加最小 `memoryNotes` 输出与持久化/传递路径。
3. 将潜意识 Worker 输入改为 `memoryNotes + current page content`。
4. 定义并校验 `memory_wiki_page_update.v1`。
5. 实现 `WikiPageWriteEntry` 的 upsert replace。
6. 用脚本或测试跑通一次端到端链路。

任何想加入 V1 的新能力，都必须先补一条“缺它系统跑不起来”的论证。
