# Memory System v2 Wiki Book v1 Simplification

> Date: 2026-07-04
> Status: design-decision
> Scope: 将 Memory v2 MVP 从多操作维护系统收敛为 Wiki Book edit-page 模型
> Revision: 2026-07-05，V1 进一步收敛为 memory notes 驱动，并由 LLM 输出页面最终内容

---

## 1. 结论

Memory v2 V1 不再以 `reuse_existing / append_new / supersede_existing / merge_candidates` 作为潜意识 LLM 的 plan 操作语义。

V1 采用 Memory Notes 驱动的 Wiki Book 模型：

```text
conscious compression
  -> memory notes
  -> session.compressed
  -> durable queue
  -> SubconsciousWorkerService
  -> subconscious LLM
  -> page update JSON
  -> wiki page write entry
  -> Notebook/Page storage
```

潜意识 LLM 不再扫描完整会话窗口来“大海捞针”。它以压缩阶段产出的 `memoryNotes` 为主输入，必要时只读回查原始消息或当前页面内容。

潜意识 LLM 只回答一个问题：目标 Page 的最终内容是什么？

```json
{
  "schema": "pudding.memory_wiki_page_update.v1",
  "updates": [
    {
      "book": "记忆系统设计",
      "page": "/Memory v2/V1 原则",
      "content": "# V1 原则\n\n- 默认不做，除非证明缺它系统跑不起来。\n"
    }
  ]
}
```

写入层负责找到 Book/Page、创建缺失节点、替换页面内容和保存。LLM 不决定 reuse、append、supersede、merge；写入层也不做语义合并。

---

## 2. 为什么简化

旧设计把“维护记忆”拆成多种操作、validator、coordinator、scheduler、metadata lifecycle 和治理门禁。它适合成熟系统，但对 MVP 来说复杂度过高。

Wiki Book 模型的错误成本较低：最坏是某个页面内容不够好，后续会话可以继续编辑同一页。相比当前重复创建大量 Book 的问题，先把写入稳定收敛到固定页面路径更重要。

2026-07-05 进一步简化：写入层不做段落级 merge。潜意识 LLM 负责读当前页面并输出最终页面内容；写入层只做 deterministic upsert replace。

---

## 3. 保留的硬边界

“简化”不等于让 LLM 选择所有边界。

这些字段不由 LLM 决定：

- workspaceId
- agentId / agentInstanceId
- sessionId
- libraryId
- source hook/job id

这些由框架从 `session.compressed` job context 注入。LLM plan 只包含 `book`、`page`、`content`。

因此 Validator v1 只校验 JSON 形状和必填字段；scope 隔离不是 plan validator 的职责，而是 write entry 使用 job context 固定目标 scope。

---

## 4. 组件

### 4.1 Hook

保留 `session.compressed` Hook。它只负责发布会话压缩完成事件。

### 4.2 Durable Queue

保留持久化任务队列，MVP 只需要：

- enqueue
- dequeue
- completed
- failed

已有 lease/retry/dead_letter 字段可以存在，但不作为 Wiki Book v1 的核心依赖。调度、预算、idle 检测后置。

### 4.3 Subconscious Worker

保留 `SubconsciousWorkerService`。Worker 的主输入是压缩阶段产出的 `memoryNotes`，同时可读取当前目标 Page 内容。原始消息只作为可选证据回查，不作为常规大上下文输入。

### 4.4 Plan

Plan v1 只有一种结果：page update。

```json
{
  "schema": "pudding.memory_wiki_page_update.v1",
  "updates": [
    {
      "book": "string",
      "page": "/path/to/page",
      "content": "markdown"
    }
  ]
}
```

### 4.5 Write Entry

Write entry 的算法：

```text
for each update:
  normalize book title
  normalize page path
  get or create book
  get or create page by path
  replace page body with content
  save page
```

V1 不做 LLM 级冲突检测，不做 multi-action decision，不做低置信度 quarantine，不做段落级 merge。

---

## 5. 写入策略

V1 写入策略必须简单、确定、可重复：

- 页面不存在：创建并写入 `content`。
- 页面存在：用 `content` 替换页面 body。
- 需要保留旧内容：潜意识 LLM 必须读取当前页面，并在输出的最终 `content` 中保留。

后续可以引入 section merge，但不进入 V1，除非能证明缺它 V1 跑不起来。

---

## 6. 推迟项

以下项不作为 Wiki Book v1 V1 阻塞项：

- 多操作 plan：reuse / append / supersede / merge。
- 低置信度 quarantine。
- TTL / 半衰期。
- 复杂 retrieval contract。
- worker idle / budget / scheduling。
- 完整 observability 指标体系。
- Admin UI v2。
- repair apply。
- 生产环境历史迁移。
- 段落级 merge / heading merge。

这些可以保留为后续 v2.x 能力，但不能阻塞 edit-page MVP 跑通。

---

## 7. V1 验收

- 压缩阶段能产出 `memoryNotes`。
- `session.compressed` 能 enqueue 一个 memory job，并携带或关联 `memoryNotes`。
- Worker 能消费 job 并调用潜意识 LLM。
- Worker 不需要把完整会话窗口作为主输入喂给潜意识 LLM。
- LLM 输出 `memory_wiki_page_update.v1`。
- JSON shape 校验失败时 job failed，成功时进入 write entry。
- write entry 能创建 Book。
- write entry 能创建 nested Page path。
- 对同一 Book/Page 连续写入不会创建重复 Book。
- 对同一 Page 连续写入会替换页面内容，不追加重复段落。
- 完整链路不需要 Admin UI、不需要 TTL、不需要多操作 plan。

---

## 8. 与现有设计的关系

本决策覆盖之前 F4/F5 中的多操作 plan 作为 MVP 目标的设定，并被 2026-07-05 的最小必要性设计进一步约束。

保留原则：

- Hook 触发保留。
- 后台 Worker 保留。
- 持久任务保留，但简化使用。
- 统一写入入口保留，但语义从 multi-intent coordinator 收敛为 page update upsert replace。

降级原则：

- F0-F10 不再作为 MVP 串行门槛。
- F3/F6/F7/F8/F9/F10 移入后续增强。
- F5 当前实现可作为承载 write entry 的代码位置，但 V1 不需要暴露多意图 command 或 merge coordinator。
