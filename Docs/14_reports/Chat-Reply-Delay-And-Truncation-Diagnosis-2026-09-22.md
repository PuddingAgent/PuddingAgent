# 聊天页缺陷诊断：回复「晚一条」+ 正文渲染不全（2026-09-22）

> 状态：**诊断中（未修复）**。本文只记录**已核实的事实**与**已被证伪的假设**，防止重复走死路。
> 报告人：`default.global_general-assistant.6a8`｜样本会话：`206a9b48ec904ebb93e7541131fbb835`

---

## 一、用户观测（原始描述）

> 用户发送消息之后，agent 的消息不显示。用户发送下一条的时候，上一条的 Agent 消息才显示。同时存在渲染问题……并没有完全渲染完成。

症状两条，**互相独立**：
- **缺陷 A（晚一条）**：新一轮的回复在本轮不显示，须等下一次发送才出现。
- **缺陷 B（渲染不全）**：卡片正文从开头渲染到中途截断；同一卡片上下各出现一次 `8 段思考 · 17 次工具`；页面无滚动条。

---

## 二、已核实事实（证据充分，可作为基线）

### 2.1 数据层完全正常 —— 缺陷 100% 在客户端展示链

对同一条样本消息（`messageId=8c65d43f70ad414aa7fd0351a818e4d2`，`turnId=cf7fa224…`）做**四源对拍**：

| 源 | 结果 |
|---|---|
| DB `ChatMessages.content` | **2355 字符** |
| `GET /api/.../conversation` | 该消息 `content` **2355 字符**，`status=succeeded` |
| `GET /api/.../messages/{id}/process-items` | **780 条**明细（`text 63` / `thinking 653` / `tool_call 32` / `tool_result 32`）；**缺 `sequence` 项 = 0**；63 个 `text` 项拼接 = **2355 字符**，尾部逐字符一致 |
| 事件流 `message.content.appended` | **63 条**事件、字段 `delta`、求和 = **2355**，缺口 0；且 `turn.completed` 位于**最后一条 delta 之后**（seq 1394414 → 1394416） |

⇒ 排除：数据丢失、事件截断、「终态闩锁吞掉尾部」、明细缺项/未知 kind。

### 2.2 服务端两侧锚点齐备

- `turn.started` 于 `2026-09-22T10:12:25Z`（=18:12:25 本地）**已在** `conversation_events`；下一个是 18:21:32。
- 运行中 `GET /conversation` 实测：`activeRun.status=running` **存在**，且返回**已终态**消息全文（2886 字符）。

### 2.3 前端不存在「发现 Turn 缺正文就回拉 transcript」的机制

全仓仅两处直接重拉权威转录：
- `hooks/useMessageSend.ts:647` —— **自己发送被受理后**
- `hooks/useSessionSelection.ts:202` —— 会话选择/mount

终态正文**只**来自 SSE 事件（`useSessionEventProjection.ts:954`：`resolveTerminalAssistantMarkdown(turn.assistant.answerMarkdown, ev.reply)`）。
⇒ 与用户观测「**发下一条时上一条才出现**」**完全吻合**：只有发送路径的 reconcile 会补。

### 2.4 默认走 agent-client 投影模式（per-session SSE 被主动停掉）

`client/featureFlag.ts` 默认 `true`（仅显式写 `'0'` 才关）；`useChatState.ts:1219-1229` 记录 `chat.sse.skipped` / `agent projection owns message loading`。
⇒ 该模式下**实时真值只来自轮询**，SSE 兜底不存在。

---

## 三、已被证伪的假设（勿重复验证）

| # | 假设 | 证伪依据 |
|---|---|---|
| **F1** | 「本地乐观 Turn 因 `projectedAssistantIsTerminal` 恒假而压制投影」 | `AgentConversationProjectionService.cs:691` 把 conversation DTO 全部消息状态**硬编码为 `succeeded`** ⇒ 投影 turn 永不 pending ⇒ 该分支直接 `continue`，**不覆盖** |
| **F2** | 「内容块窗口 `INITIAL_VISIBLE_TURN_BLOCKS=40` 丢掉尾部」 | `execution-flow/TurnContentStream.tsx:228-232`：`visibleBlocks = blocks.slice(hiddenBlockCount)` —— 切的是**数组头部**，**尾部恒保留**；按钮文案「加载较早 N 个内容块」语义一致 ⇒ 与「前缀在、尾部缺」**方向相反** |
| **F3** | 「投影有文本就关掉全文兜底（`AgentMessageBubble.tsx:394-411`）是主因」 | 只能解释「正文变短」，**不能解释从中间断**；且 63 段 text 拼接已证完整（§2.1） |
| **F4** | 「末条为 `system` 消息导致 `conversationNeedsProjectionCatchUp` 误判已追平」（本轮新增证伪） | 该函数实现仅 `messages[last].role === 'user'`（`client/chatClientStore.ts:31-36`）⇒ 末条 `system` 确实返回 `false`；**但**入口短路还要求 `matchingStatus.eventCursor === existingConv.eventCursor` 等四条件同时成立，**单纯"末条 system"不足以触发跳过**，需实测 cursor 值才能判定 |

---

## 四、当前收敛：两个「P0-perf 启发式短路门」

缺陷 A 的真凶收敛到 `client/chatClientStore.ts` 的**两处基于启发式的快照丢弃**（而非服务端问题）：

### 门 1 —— 入口短路（`syncSelectedAgent`，`:379-406`）

```ts
const projectionCatchUpPending = conversationNeedsProjectionCatchUp(existingConv);
if (matchingStatus && existingConv
    && matchingStatus.eventCursor === existingConv.eventCursor
    && !matchingStatus.activeRunId
    && !existingConv.activeRun
    && !projectionCatchUpPending) {
  recordPerfStep(..., 'sync.skipped.cursorMatch', ...);
  return;                                     // ← 完全不发 API 请求
}
```

其自身文档注释（`:21-27`）已承认该风险：*「cursor 可能先于 assistant 消息行到达 read model。因此以 user 消息结尾的快照不安全。」*
⚠️ **但保护条件只覆盖「末条是 user」一种形态**。

### 门 2 —— `isConversationSame` 语义等价丢弃（`:99-111` + 调用点 `:452-466`）

```ts
a.eventCursor === b.eventCursor &&
a.messages.length === b.messages.length &&      // ← 只比长度，不比内容
(a.activeRun?.runId ?? null) === (b.activeRun?.runId ?? null) &&
a.mainSessionId === b.mainSessionId
```
⇒ 若助手正文是**在同一条已存在的消息上被填充**（而非新增一行），`messages.length` **不变** ⇒ 新快照被判「语义相同」⇒ `return`，**不写 IndexedDB、不 setState**。

两门共用同一形态：**用启发式推断"没有变化"，取代服务端真值**（与既有 SKILL `server-authoritative-state-over-client-inference` 同族）。

---

## 五、定因所需的判别实验（下一步）

按代价从低到高，**任一即可定因**：

1. **前端埋点**：在 `sync.skipped.cursorMatch` 与 `sync.skipped.same` 两处打印当时的 `eventCursor` / `messages.length` / `activeRun.runId`，复现一次「晚一条」，看命中哪一门。
2. **服务端对拍**：记录那一轮 `turn.completed` 时刻的 conversation `eventCursor` 与 agent-status `eventCursor`；若两者在回复可见前已相等 ⇒ 坐实门 1。
3. **零改码客户端实验**：设 `localStorage['pudding-agent-client-arch'] = '0'` 后刷新（回到 SSE 模式）——
   - 不再「晚一条」⇒ 病灶在投影/轮询链；
   - 仍「晚一条」⇒ 病灶在更底层的合并渲染。
4. 同类实验：`localStorage['pudding-exec-flow-proj'] = '0'` → 看正文是否恢复完整（判定缺陷 B 是否在内容块投影渲染）。

---

## 六、修复方向（**尚未实施**，按风险排序）

| 方案 | 位置 | 风险 |
|---|---|---|
| **S1** 终态事件无法归属 Turn 时（现直接 `return` 丢弃，日志原文「消息被吞」）改为走已有 reconcile 端口做**幂等合并**回拉 | `useSessionEventProjection.ts:1477-1500` / `:1580-1590` | 中（须经 `reconcileCompletedSessionMessages`，否则重复卡片） |
| **S2** 补偿轮询门槛从「仅有活动消息」放宽为「或存在活动 Turn」 | `utils/chatStateUtils.ts:980-992` | 低（只补事件不建 Turn，须与 S1 配套） |
| **S3** `isConversationSame` 增补**内容指纹**（末条消息 id + 正文长度），不再只看 `messages.length` | `client/chatClientStore.ts:99-111` | 低-中（仅影响"是否 setState"，不改变数据） |
| **S4** 入口短路保护条件扩展：末条为 `system` 且存在未闭合 turn 时同样不跳过 | `client/chatClientStore.ts:379-406` | 低（收紧跳过条件，代价是略增轮询） |
| **S5** 让「尾部只有用户消息」的 turn 不再渲染成「已完成且空白」 | `useSessionHistoryProjection.ts:154` 或渲染门 `types.ts:263-277` | 中（可能把"真没回复"误报为运行中） |
| **S6** 服务端 activeRun 锚点放宽 | `AgentConversationProjectionService.cs:225`（现有测试断言旧语义） | 中（契约变更） |

**纪律**：先立**取红测试**（对样本消息做字符级断言：渲染长度必须 = 2355），再动实现；不靠启发式补丁。

---

## 七、附：本轮方法学教训

- 子代理给出的「最可能生效点」排序**可被证伪**：本轮即两次推翻（F1/F2）。⇒ 父级必须亲自读源码复核，不能直接采纳排序。
- 引用 `git log -S` 结论**必须写「计数变化点」**，不能写「最后出现」（字面量可能只是被搬迁）。
- 统计/计数类结论（如 `fix` 提交数 168）必须**留下确切命令与时间边界**，否则事后无法复现口径。
