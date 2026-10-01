# Chat 前端性能实测报告（长消息 · 滚动 · 活动回合更新）

日期：2026-10-01
范围：对 [Chat 前端性能诊断报告](Chat-Frontend-Performance-Diagnosis-2026-10-01.md) 的源码推断做运行时实测验证。
状态：**仅新增测量脚手架与本文档，未修改任何产品代码、未重启 Desktop/Core、未写入数据库、未调用 LLM。**

---

## 一、结论摘要

1. **诊断的 P1「长消息内部成本」得到实测坐实。** 单条 50KB Markdown 回复在时间线里是**一个虚拟行**：
   行高 36962px、挂载正文 50483 字符、却只有 **11 个元素节点**（该内容以极少 DOM 承载极长文本）。
   滚动该时间线出现 **213ms 长任务**、`renderToPaint` 最高 167ms。虚拟化按「消息」为单位，
   一条超长回复进入范围即整条挂载，与诊断一致。
2. **诊断的 P1「缓存写入阻塞展示」在本数据集上不成立**：`cache.loadConversation` 16ms、
   `cache.saveConversation` 1–18ms，都远小于同轮 `api.getConversation` 的 124ms；
   写盘不是本场景的主导项（详见 §3.2 的限定条件）。
3. **新发现（诊断未覆盖，且已 A/B 验证）：虚拟化容器的 flex 收缩缺陷会破坏滚动可达性。**
   `[data-testid="chat-message-viewport-content"]` 以 `style.height = totalSize`（38871.9px）承载虚拟高度，
   但它是 `display:flex; overflow-y:auto` 滚动容器（`.acss-ntkkqu`, `flex:1 1 0%`）的子项，
   未显式设置 `flex-shrink`，默认 `1` → **计算高度被压回 662px**，内联虚拟高度失效。
   后果：滚动容器 `scrollHeight` 不再等于 `totalSize`，而是等于「当前已挂载绝对定位行」产生的高度。
   滚到顶触发长行卸载后，`scrollHeight` 从 **38964px 塌到 1374px**，
   `scrollTop = scrollHeight` 被钳制在 **676px**，**用户无法用程序化/贴底方式回到长回复**。
   仅加 `flex-shrink: 0` 后：`content` 计算高度恢复 38980.2px、`scrollHeight` 稳定在 39016px、
   贴底可精确回到 38318px。**零成本修复，建议优先于任何渲染微优化。**
4. **`content-visibility: auto` 的单条超长消息收益不可测**，因为塌缩缺陷先钳制了滚动范围
   （off 态可滚动 44426px，on 态只剩 676px，两者不在同一滚动区间）。
   在**多条高行**（10×6KB）场景下它有效：长任务 1×101ms → **0**，`renderToPaint` p95 96ms → **22ms**。
   ⇒ 先修 flex-shrink，再评估 content-visibility。
5. 历史分页场景在**滚动容器塌缩后无法继续 prepend**（`afterHeight` 1374px），
   即该缺陷同时压制了「上翻历史」路径的可测性。

---

## 二、测量方法

| 项 | 取值 |
|---|---|
| 被测构建 | 运行中的产品栈：`PuddingDesktop.exe` → `PuddingAgent.exe --desktop-child --data-root D:\Data`，`http://localhost/` 提供 `wwwroot/admin` 生产构建 |
| 浏览器 | 独立 profile 的 Edge **Edg/154.0.4258.37**（与目标 WebView2 `154.0.4258.37` 同版本引擎），CDP 端口 9333 |
| 未采用 | 直接附加运行中的 WebView2：其命令行**没有** `--remote-debugging-port`，无法附加；重启 Desktop 会打断用户会话，遂不改动 |
| 数据源 | CDP `Fetch` 域拦截 `GET /api/workspaces/{ws}/agents/{ag}/conversation`，返回合成 `AgentConversationView`；其余链路全真实 |
| 不改动 | 不写 IndexedDB 之外的内容、不写数据库、不调用 LLM、不改产品代码、不重启服务 |
| 埋点 | 生产构建自带 `?perf=1` 诊断（`window.__PUDDING_PERF__.snapshot()`），含 longtask / layout-shift / fetch / workflow.step / commit / paint |
| 脚手架 | [TestScripts/perf](../TestScripts/perf)（`record.mjs` + 6 个定向诊断脚本，零第三方依赖） |

**场景**（每场景自成闭环：清 IndexedDB → 装桩 → 回应用根 → 进 chat → 测量）：

| 场景 | 构造 | 对照目标 |
|---|---|---|
| `cold` | 清 IndexedDB + 禁 HTTP 缓存，真实 20 条会话 | 冷加载关键路径与缓存步骤耗时 |
| `warmLong` | 20 条 + 1 条 50KB 长回复 | 长回复首渲染成本、行高与 DOM 规模 |
| `scrollLong` | 同上，双向滚动 2 趟 | 长行进出视口的滚动成本与可达性 |
| `abPatches` | 同一 50KB 时间线 × {无补丁, `flex-shrink:0`, `content-visibility:auto`} | 修复与优化候选的因果验证 |
| `historyPage` | 40 条历史，滚到顶触发 prepend | 上翻分页与锚点 |
| `streaming` | 活动回合桩：`conversation` 逐次变长 + 期间向输入框打字 | 活动回合更新链路与「流式同时输入」 |

> 口径说明：Edge 与 WebView2 同为 Chromium 154，**相对分项可比**；绝对毫秒受窗口尺寸/扩展/GPU 影响，
> 跨设备不可直接比较。

---

## 三、实测结果

### 3.1 长回复的渲染与 DOM 规模（`warmLong`）

| 指标 | 实测值 |
|---|---|
| 长行正文长度 | 50483 字符（合成 50KB Markdown：标题/段落/表格/围栏代码/列表） |
| 长行高度 | **36962px** |
| 长行内元素节点数 | **11**（`table`/`pre` 均为 0 → 该体量以极稀疏 DOM 承载） |
| 已挂载虚拟行数 | 8 / 21 items，`data-virtualized="true"` |
| 列表 `scrollHeight` | 38964px，`content` 内联高度 38871.9px |
| 总 DOM 节点 | 1394（时间线正文 50976 字符） |
| `chat.output.paint` | `renderToPaint` 72ms、`commitToPaint` 41ms（n=1，节流后样本） |
| `browser.longtask` | 1×62ms |
| `chat.markdown.render` | `commitMs` 117ms（此值为 render 起点→被动 effect，含调度与 effect 刷新等待，**非纯解析时间**） |

**判读**：诊断「虚拟化以整条消息为单位，长回复进入视口后仍完整挂载」成立；
成本主要体现在 **单个 36962px 行的布局与文本流**，而非 DOM 节点数量（仅 11 个节点）。
这也解释了为什么单纯减少节点数或加 memo 不会消除该项成本——瓶颈在长文本的布局与解析，不在节点规模。

`scrollLong` 期间：`browser.longtask` 无样本，但 §3.3 的 A/B 在同一构造下量到 **213ms 长任务**（见下）。

### 3.2 冷加载与缓存步骤（`cold`）

同一次 `agent.select` 的工作流分项（`chat.workflow.step`，单位 ms）：

| 步骤 | 耗时 | 备注 |
|---|---|---|
| `agent.select.cache.loadConversation` | 16 | `cacheHit=false`，冷态 |
| `agent.select.api.getConversation` | **124** | 同轮最慢项 |
| `agent.select.cache.saveConversation` | **1** | 写在 API 之后 |
| `agent.select.select.finish` | 141 | 整轮 |
| `agent.mainSession.api.ensureMainSession` | 29 | |
| `agent.status.cache.loadStatuses` | 20 | |
| `browser.longtask` | 3 次，p95=100ms，max=100ms | |
| 总 DOM 节点 | 1617；`scrollHeight` 12201px | |

**判读**：
- 结果显示写盘仅 1–18ms，**远小于** `api.getConversation` 的 124ms。在本次数据集（20 条消息、
  单条 50KB）上，**「等待 IndexedDB 写入才展示」不是主导项**。
- 但诊断指出的**顺序问题在代码层依然存在**：`selectAgent` / `syncSelectedAgent` 中
  `await cache.saveConversation(...)` 都发生在 `set(...)` 之前（静态核实见 §4），
  即写盘延迟仍然串在展示关键路径上。其**绝对影响取决于快照体量**：本数据集 20 条 →
  1–18ms；当会话含多条超长回复或大量 processItems 时，整份 `AgentConversationView`
  的结构化克隆与 put 会线性放大，届时诊断的该项建议才会显现收益。
- 因此本报告**下调**该条的优先级排序：先修 §3.3 的可达性缺陷（高，且零成本），
  再按会话体量决定是否重排缓存写入顺序。

### 3.3 滚动可达性缺陷与 A/B 验证（`abPatches`）

同一 50KB 长回复时间线，三种处理下的**可用性**与成本（`scrollTop = scrollHeight` 后测量）：

| 处理 | 滚到顶后 `scrollHeight` | `scrollTop=scrollHeight` 落点 | `content` 计算高度 | `content` `flex-shrink` | 长行可否到达 |
|---|---|---|---|---|---|
| 无补丁（现状） | **1374px**（从 38964 塌缩） | **676px**（被钳制） | 662px（内联 38871.9px 失效） | `1` | 程序化贴底失败 |
| `flex-shrink: 0` | **39016px**（不塌缩） | **38318px** | **38980.2px** | `0` | ✅ 精确回到长行 |
| `content-visibility: auto` | 1374px（仍塌缩） | 676px | 662px | `1` | 未修复 |

机制（实测链）：
1. `chat-message-list` 的真实样式为 `.acss-ntkkqu { min-height:0; flex:1 1 0%; display:flex; overflow-y:auto }`。
2. `viewport-content` 是它的 flex 子项，`MessageList.tsx` 只设置了 `height: totalSize`，
   未设置 `flex-shrink`，默认 `1` → 被压缩：`offsetHeight` 恒为 662–698px，
   而**内联的 38871.9px 完全不生效**（`computed height: 662px`）。
3. 因此虚拟高度不由容器承载，而由「已挂载的绝对定位行」的溢出撑起：
   长行在挂载时 → `scrollHeight` 38964px；长行卸载后 → `scrollHeight` 1374px。
4. `totalSize` 未变（`content` 内联高度始终 38871.9px），于是**虚拟高度与真实滚动高度脱钩**，
   贴底被钳制到 676px，长回复不可程序化到达。

**滚动成本 A/B**（`diag-layoutcost.mjs`，同一构造、同一滚动脚本）：

| 构造 | 处理 | 长任务 | `renderToPaint` | 可滚动总量 |
|---|---|---|---|---|
| 单条 50KB | `content-visibility` off | 213ms ×1 | 38ms(p95) | 44426px |
| 单条 50KB | `content-visibility` on | 175ms ×1 | 167ms(p95) | **676px（被塌缩钳制）** |
| 10 × 6KB 高行 | off | **101ms ×1** | 96ms(p95) | 43223px |
| 10 × 6KB 高行 | on | **0** | **22ms(p95)** | 9981px |

**判读**：
- **单条超长消息**：塌缩使 on/off 不在同一滚动区间，该组数据不可用于比较；
  且单行内部没有可跳过的屏外子块，`content-visibility` 理论上收益有限——与诊断的预期一致。
- **多条高行**：收益明确而显著（长任务 101ms→0，`renderToPaint` p95 96→22ms），
  与诊断「对冻结内容块减少屏外布局」的建议方向吻合，但**必须在修掉塌缩之后才可作为独立改动验收**。

### 3.4 历史分页（`historyPage`）

- 加载 40 条后 `scrollHeight` 38964px（长回复仍在其中）。
- 滚到顶：`afterTop=0`、`afterHeight` **1374px**、`anchorShiftDelta = -38266`。
- **受 §3.3 缺陷影响**：塌缩后容器不再具备完整滚动范围，prepend 路径无法在受控条件下继续观察。
  该场景的长任务样本为 1×51ms、`renderToPaint` 47ms。

### 3.5 活动回合更新链路（`streaming`）

静态核实先确认了通道（见 §4）：该构建 `agent-client` 架构默认开启 → 主会话被标记 projection-owned
→ `useChatState` 显式 `stopSessionEventStream()`（记 `chat.sse.skipped`），
活动回合内容由 `index.tsx` 的 **1200ms 轮询 `conversation`** 提供，**不使用 SSE `/events/stream`**。

实测（桩让每次轮询返回更长正文，期间向输入框打字）：

| 指标 | 实测值 |
|---|---|
| 轮询次数（本轮样本） | 4–17 次，单次 `agent.selectedSync` 3–10ms |
| 打字注入 | 770 字符 / 60 帧，耗时 680–1150ms |
| `chat.output.paint` | n=1，`renderToPaint` 64ms、`commitToPaint` 31ms |
| `chat.markdown.render` | n=1，`commitMs` 124ms |
| `browser.longtask` | 1×65ms |
| `browser.layoutShift` | 20 次，CLS 0.055 |
| `chat.queue.snapshot` | 3 次 |

**采样边界（必须声明）**：本场景 `paint`/`commit`/`markdown` 样本数少于注入次数，
原因是埋点有 **250ms(commit/paint) / 500ms(markdown) 的按事件名节流**，
且行卸载时会取消尚未触发的 rAF（`MessageItem.tsx` cleanup）——该会话含 50KB 长行、
长行频繁卸载，因此**本报告不下「每轮更新耗时」的结论**。
要量化「每 delta 的全列表重算成本」，需在图
（`MessageList.tsx` 的 `projectedTurns → visibleTurns → buildVirtualMessageItems → viewportItems`
链路）上补 **逐次 commit 计数与 CPU 采样**，见 §5。

---

## 四、静态核实（与实测交叉验证）

由独立子代理以只读方式核实（行号来自当前工作树）：

| 问题 | 结论 | 证据 |
|---|---|---|
| 虚拟化粒度 | 一条**消息块** = 一个虚拟项；**无**子块/分片虚拟化 | `viewport/messageProjection.ts:84-107,130-140`、`components/MessageList.tsx:1297-1316` |
| 高度估计与缓存 | `estimateSize` 按项、`measureElement` 按 `data-viewport-item-id`；高度缓存在 `useRef(Map)`，**不持久化** | `viewport/useMessageViewportRuntime.ts:184,197-228` |
| overscan | `overscan: 6`，无自定义 rangeExtractor，无「保留高行」规则 | 同上 `:215` |
| 列表级重算 | 每个 delta 都会重跑 `projectedTurns`/`visibleTurns`/`buildVirtualMessageItems`/`viewportItems(renderWeight)`，并对每个挂载行做深度 memo 比较 | `components/MessageList.tsx:913-1024`、`MessageRow.tsx:99-158` |
| Markdown 分块 | slice 与 `MarkdownBlock` 均有 `React.memo`，但每次文本变化都重扫全文；slice key 含 `text.length` → **尾块每 delta 重挂载** | `IncrementalMarkdown.tsx:83-102,112-133` |
| 缓存写入顺序 | `selectAgent`/`syncSelectedAgent` 均 **先 `await cache.saveConversation` 再 `set`** | `client/chatClientStore.ts:312→328`、`:509→530` |
| 活动回合通道 | 轮询 `GET .../conversation`（1200ms/5000ms），SSE 被显式停用 | `client/featureFlag.ts:3-6`、`useChatState.ts:1094-1097,1253-1264`、`index.tsx:177-197` |
| 埋点语义 | `markdown.render.commitMs` = render 起点→被动 effect（**含调度/effect 等待**）；`output.commit.renderToCommitMs` 为 pre-paint；`output.paint.renderToPaintMs` 到**下一个 rAF**（**不含真实绘制/光栅**） | `MarkdownBlock.tsx:29-52`、`MessageItem.tsx:98-150`、`perfEventRuntime.ts:70-91` |

---

## 五、修复建议（按实测收益排序）

1. **P0 · 修滚动可达性（零成本、因果已实证）**
   在 `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageList.tsx:1287-1295`
   的虚拟化分支里，为 `chat-message-viewport-content` 补 `flexShrink: 0`
   （与 `height: totalSize` 配套），或用 `contain: layout paint` / `overflow: hidden` 隔离后代溢出。
   验收：滚到顶后 `scrollHeight` 不下降；`content` 计算高度 == `totalSize`；
   `scrollTop = scrollHeight` 能精确落回长行底部（实测 38318px）。
   注意：`contain: layout paint` 在 §3.3 的探针中会把 `scrollHeight` 压到 698px 并**丢失滚动范围**，
   而 `flex-shrink: 0` 实测同时保住虚拟高度与滚动范围，**优先采用后者**。
2. **P1 · 长消息内部成本**：先修 1，再评估「超长消息块级渐进挂载/虚拟化」。
   `content-visibility` 对**多条高行**有效（长任务→0），对**单条超长行**需在修复后重测再定。
3. **P1 · 列表投影重算**：本轮未能量化（采样边界见 §3.5）。建议先补测量再改：
   按 `turnId` 缓存投影、保持未变化对象引用，只在内容变化时重算权重。
4. **P2 · Markdown 尾块重挂载**：slice key 去掉 `text.length`（改用 `offset` 身份 + 内容哈希），
   配合「冻结前缀 + 只重扫尾部」。需覆盖表格、围栏与跨块引用定义。
5. **P2 · 缓存写入顺序**：本数据集写成 1–18ms，不构成主导；建议先增大快照体量复测
   （多条 50KB + 大 processItems），若写盘仍 <20ms 则不必改动展示顺序。

---

## 六、复现方式

```powershell
# 1) 独立 Edge（Chromium 154，与 WebView2 同版本）
Start-Process 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe' -ArgumentList `
  '--remote-debugging-port=9333', "--user-data-dir=$PWD\temp\perf-profile", `
  '--no-first-run','--no-default-browser-check','--headless=new','about:blank'

# 2) 全套场景 / 单场景
node TestScripts/perf/record.mjs all
node TestScripts/perf/record.mjs warmLong

# 3) 汇总与定向诊断
node TestScripts/perf/summarize.mjs
node TestScripts/perf/diag-cssbox.mjs      # 定位 662px 压制来源
node TestScripts/perf/diag-flexshrink.mjs  # 验证 flex-shrink:0
node TestScripts/perf/diag-layoutcost.mjs  # content-visibility A/B
```

结果写入 `temp/perf/out/`。详见 [TestScripts/perf/README.md](../TestScripts/perf/README.md)。

---

## 七、遗留与边界

- **未量化**：每 delta 的全列表投影/权重 CPU；React commit 次数随更新数的增长；
  真实多个长回复同时加载时的投影成本。需补 commit 计数/CPU 采样（见 §5.3）。
- **未覆盖**：真实 WebView2 进程内的绝对耗时（其未开调试端口，未重启）；
  冷启动首包；图片/附件解码；200 条以上历史的真实 prepend（受 §3.3 缺陷阻塞）。
- **数据来源**：全部为合成会话（与真实消息同结构、同渲染路径），非现网统计；
  样本量偏小（多数场景 n=1–3 个 paint 样本），分位数仅供参考。
- `content-visibility` 的单条超长消息结论**在修复塌缩前不可采信**。
