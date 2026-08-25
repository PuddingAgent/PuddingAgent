# 聊天前端「行为链 + 质感」升级设计与实施

> 日期：2026-08-23
> 状态：实施中（P1 视觉质感 → P2 行为链交错 → P3 工具专用展示卡）
> 基线：`Source/PuddingPlatformAdmin/src/pages/chat`
> 参考实现：`E:\github\deepseek\deepseek-harness\packages\client`
> 关联基线：`Docs/deepseek-harness-message-card-alignment-2026-08-14.md`（行式执行流信息架构，已建成骨架）
> **2026-08-25 后续施工权威入口**：`Docs/Features/Agent消息交错内容流与最新行为组披露完整实施方案.md` + `Docs/07架构/93ADR-079Agent消息交错内容流与最新行为组披露ADR.md`。本文保留调研和实施演进记录；与 ADR-079 冲突的旧折叠描述均以 ADR-079 为准。

## 1. 背景与问题

对照 deepseek-harness 与业界 agent 聊天前端（Hermes Agent / Manus / Claude / ChatGPT），Pudding 聊天前端存在三类差距：

1. **质感不足**：双 token 体系混杂（老暖色 `--earth-brown` 系与新 `--pudding-*` 系并存）、两套行式 chrome 规格并存（`toolcall.styles.ts` 24px 行 vs `execution-flow.styles.ts` 28px 行）、`--pudding-chat-text-secondary` 被引用但从未定义（思考摘要颜色静默回退）、过程信息与正文灰阶不分档。
2. **行为链缺失**：轨迹不按真实发生顺序交错（reasoning 永远置顶聚合、工具树其次，无法表达「推理→工具→推理→工具」）；7/8 presentation renderer 未实现（diff/terminal/search/read/web 全部回落 generic `<pre>`）；工具耗时只在展开态正文里；reasoning 是整段 `<pre>` 无分段；无完成态计量摘要；无 turn 级统计行。
3. **设计哲学未沉淀**：harness/业界的可复用决策没有被文档化为本项目的规范。

## 2. 调研结论

### 2.1 deepseek-harness 的「质感」来源（结构纪律，非品牌色）

| 纪律 | 具体决策 | 采纳 |
|---|---|---|
| 宽度纪律 | 全站 748px 内容轴，输入卡/dock 卡由公式派生 | 已有 `min(720px,100%)`，保持 |
| 灰阶四档语义 | label-primary（正文）/ secondary（强调）/ tertiary（过程正文：Think、工具行）/ caption（装饰标签、耗时）严格分工，过程信息比正文低一档 | ✅ 本期落地 token |
| 16px 节奏 | 消息列 gap、markdown 块 gap、标题边距全是 8 的倍数 | ✅ 沿用 |
| 24px 行式 chrome | 思考/工具/委派/压缩/重试全部压成单行 disclosure（icon+标题+点+截断摘要），默认折叠、整行可点、展开体缩进 22px | 骨架已有，统一两套规格 |
| 用「光」不用 spinner | running = 300px 眩光带 2.6s 扫过行 + 文字 shimmer + StateDot 像素 chase，三种频率区分场景，全部尊重 reduced-motion | ✅ 补齐 reasoning/工具行扫光 |
| 气泡只给用户 | assistant 全宽扁平长文排版（16/28） | 已对齐 |
| 卡片家族统一 | 代码/终端/diff/read/search/IN-OUT 全部同圆角（12px）同表面，banner 与 gutter 变化区分；内部滚动上限 150–260px | ✅ P3 落地 |
| hover 递进披露 | 时间戳/操作按钮/查看全文 hover 才出现，opacity 过渡不 reflow | ✅ 沿用 |
| 错误即内容 | 失败工具折叠摘要直接是错误首行（红），不藏在展开里 | 已对齐 |
| 完成态计量 | turn 尾部 `time · Ran for 15s`；reasoning 完成后摘要变计量 chip | ✅ 本期落地 |

### 2.2 Hermes Agent 与业界原则（12 条可落地）

来源：Hermes Agent（Nous Research，TUI `thinking.tsx`/`messageLine.tsx` + Web Dashboard）、ChatGPT o1、Claude visible/interleaved thinking、Manus、Devin、OpenHands、Perplexity、Claude Code。本次采纳前 8 条：

1. **过程与结论物理分离**：行为链（thinking+工具）与最终回答分开渲染，行为链在回答上方（✅ 已有，本期强化交错）。
2. **折叠三态**：hidden / collapsed(auto) / expanded；思考活跃时摘要跟随最新行，结束落定为计量摘要。
3. **完成态摘要 chip**：收起后不是空白而是「思考 · 12s」「N 工具 · 3m01s」这类计量，用平实语言。
4. **工具卡三区**：头部（状态+可读名+耗时）/ 一行参数摘要 / 展开态 IN-OUT；默认折叠 ≤32px 行高。
5. **状态视觉分级**：running（扫光）/ done（对勾点+耗时）/ failed（红点+错误首行）；颜色+图形双编码。
6. **进度感分层**：首 token 前 shimmer 占位；thinking 与 tool 用不同形态动效；计时 15s 阈值后才出现。
7. **增量 markdown**：只在最后稳定块边界后重解析（已有 `findStableMarkdownBoundary`）。
8. **扁平文档流 + 过程降级**：AI 消息全宽；过程内容统一 tertiary 灰 + 小字号。
9. （后续）计划外显为可勾选清单 —— 已有 EditablePlanCard，不在本期。
10. （后续）交付物升级出聊天流（artifacts）。
11. （已有）随时可打断：Stop/重试。
12. （后续）Trajectory 甘特审计视图 —— 单独排期。

## 3. 设计规范（本期冻结）

### 3.1 文本四档灰 token（双主题）

```
--pudding-chat-text            正文（primary）
--pudding-chat-text-secondary  次要正文/强调辅助
--pudding-chat-text-tertiary   过程正文（思考、工具行摘要）
--pudding-chat-text-caption    装饰标签、耗时、分隔点
```

- legacy `--pudding-chat-text-muted` / `-subtle` 保留为别名（= secondary / tertiary），存量文件不破坏。
- 浅色：primary `#1a1a2e` / secondary `#5c4a3a` / tertiary `#8c7a6a` / caption `#ab9c8e`；
  深色：`#f4efe7` / `#d2c5b5` / `#a99c8d` / `#8d8174`（沿用暖棕族，Pudding 品牌）。

### 3.2 行式 chrome（统一规格）

- 行高最小 28px、可点击区 ≥32px；leading 16px 固定槽；标题 14px/22px 500–600；摘要 13px/20px tertiary 单行 ellipsis；行间距 4px；展开体左缩进 22px、圆角 10px。
- 耗时/exitCode 显示在折叠行尾部：caption 灰、`tabular-nums`、右对齐不挤压摘要（`flexShrink:0` + 摘要 FILL）。
- running 扫光：`toolCallSweep`（38% 宽、1.7s、`--pudding-status-running` 12% 透明度），reduced-motion 降级静态弱光。reasoning 行运行中复用同款。

### 3.3 完成态计量

- reasoning 段完成：折叠摘要 = `思考 · 12s` chip + 首行文本；运行中 = 最新非空行（既有行为）。
- 工具行完成：尾部 `1.2s`（<1s 显示 `123ms`；≥60s 显示 `1m03s`）；error 叠加 `exit 1`。
- turn 终态：正文下方 StatsLine 一行 `N 段思考 · M 工具 · 3m01s · 4.2k tokens`，数据全部来自 canonical 投影/usage（刷新不归零）。

### 3.4 行为链交错（P2 核心 → 2026-08-25 AgentTurnCard 内容块流重构）

- **（2026-08-25 起）** 渲染结构升级为「正文段 ⇄ 行为组」内容块流（`turnContentBlocks.ts` + `TurnContentStream`）：正文（message 节点 → TextBlock）永久可见且只渲染一次，卡片底部不再有第二个 answer bubble；两个正文段之间的最大连续非正文节点序列形成一个可折叠 `ActivityGroup`。按 ADR-079，默认只展开**当前最新 Agent 回合的最后一个行为组**；最终正文到达不关闭它，只有更新行为组或更新 Agent 回合取得披露所有权时，旧组才柔和收起并在动画后卸载成员 DOM。组内 canonical 可披露思考直接完整换行，工具状态直接可见，原始 IN/OUT 仍保持二级懒加载；用户 override 粘性。旧「交错时间线 + 尾段独立正文气泡」的双区域结论作废。
- 渲染顺序 = 投影 `nodes` 的 sequence 顺序；多段 reasoning：每段一个 ReasoningDisclosureRow（段首行/最新行摘要 + 段时长），相邻委派节点聚合为一个 DelegationRow；retry 节点不渲染行（错误行/摘要计数承载）。
- 路径 B（canonical 投影）默认开启；投影为空/灰度关闭时走路径 A adapter：`processItems` 适配为同构行为节点集（无正文段，正文回退整块气泡），两路径共享同一块结构与渲染组件。
- TurnStatus 消费点切 `deriveTurnStatusFromProjection`（投影可用时）；facts 派生保留为回退。
- 配套纪律：`message.completed.reply` 只作非流式兜底（整 turn 无 content delta 时创建唯一正文段），绝不覆盖/复制已有正文段；节点边界只由 canonical sequence 决定。

### 3.5 工具展示卡家族（P3）

- terminal：命令 banner（mono 命令 + 状态 pill + 复制）+ 输出窗口（max-height 224px）+ exit code（非 0 红）。
- diff：unified diff 解析，+/− 行绿/红、文件头、增删行计数。
- read：文件路径 + 行范围 + 内容窗口（12px mono）。
- search：查询词 + 命中数 + 分组匹配预览（命中数组有界 20 条 + 省略计数）。
- web：浏览器动作 + URL + 页面标题。
- 数据源优先 `presentation.meta`（G1/G2 契约），缺失从 payload/arguments 安全解析；未注册类型回落 Generic。
- 卡片家族统一外观：banner + 内容窗口两段结构，圆角走 `--pudding-chat-radius-md`（10px token，比 IN/OUT 原始卡的 6px 大一档以区分语义卡与原始文本卡）、表面 `--pudding-chat-surface(-muted)`；payload 即调用参数 JSON（含 command/path/query/url 类字段）时不在卡正文重复展示（banner 摘要 + IN 卡已承载）。

## 4. 分期实施

| 期 | 内容 | 关键文件 |
|---|---|---|
| P1 | 四档 token、chrome 统一、工具行耗时+扫光、reasoning 计量 chip、StatsLine、用户气泡 22px | `global.style.ts`、`styles/execution-flow.styles.ts`、`styles/toolcall.styles.ts`、`ToolCallRow.tsx`、`ReasoningDisclosureRow.tsx`、新 `TurnStatsLine.tsx`、`styles/user.styles.ts` |
| P2 | 交错时间线组件 + 路径 A adapter、多段 reasoning、AgentMessageBubble 接线、TurnStatus 切投影、路径 B 默认开 | 新 `execution-flow/ExecutionFlowTimeline.tsx`、`AgentMessageBubble.tsx`、`client/featureFlag.ts`、`projections/executionFlowProjector.ts`（补段末时间戳） |
| P3 | 五类 presentation renderer + 注册 | `presentation/renderers/{terminal,diff,read,search,web}.tsx`、`presentation/PresentationRegistry.ts` |

## 5. 实施结果（2026-08-23）

三期当日全部落地，chat 目录 Jest 103/106 套件通过（`IntentConsole/InputArea/DevPanel` 3 套件为 HEAD 存量失败，语音采集/图片暂存/基准加载用例，与本次无关，已在干净 HEAD 验证）：

- **P1**：`global.style.ts` 双主题补 `--pudding-chat-text-{secondary,tertiary,caption}`（修复 `-secondary` 从未定义的静默 bug；muted/subtle 保留为别名）；`execution-flow.styles.ts`/`toolcall.styles.ts` 全量切四档 token；工具行折叠尾部的耗时/exit 计量槽 + running 扫光（`executionFlowRowSweep`）；ReasoningDisclosureRow 段时长 chip；`TurnStatsLine` 终态计量行；用户气泡 22px。
- **P2**：`ExecutionFlowTimeline`（路径 A/B 统一 entry ViewModel + 尾部段 current 判定 + 统计派生）；投影器 `ReasoningNode.lastOccurredAt`；AgentMessageBubble 三固定区块替换为交错时间线、TurnStatus 切投影派生、token 行升级为 StatsLine；`featureFlag` 路径 B 默认开（`'0'` 逃生门）。
- **P3**：`rendererKit`（卡片家族样式 + meta/payload 安全读取 + 复制反馈）+ terminal/diff/read/search/web 五类 renderer 注册进 `PresentationRegistry`。
- 新增测试：`ExecutionFlowTimeline.test.tsx`（交错顺序/多段 current/委派聚合/统计）、`TurnStatsLine.test.tsx`、`renderers.test.tsx`（五类 + 注册表）、`formatDuration` 用例、ToolCallRow 耗时/exit 用例、ReasoningDisclosureRow chip/扫光用例；更新 CU-10 分派断言（terminal 不再回落 Generic）。

## 6. 追加迭代（2026-08-23 下午，截图反馈）

真实会话截图评审后的修正与增强：

1. **DelegationRow 路径 A 数据源修复**：`types.ts buildMessageBlocks` 原先把全部 subagent 条目从主消息 processItems 滤除，导致 DelegationRow 在路径 A 永远无数据（截图「行为轨迹未显示」的确定原因之一）。改为仅滤除高频 `subagent_progress`（托盘坞承载），`subagent_spawned/subagent_completed` 作为父级有界委派事实保留（对齐基线文档 §5.5）。
2. **委派等待大卡退役**：CurrentActivityPanel 委派卡与 TurnStatus（delegating 阶段 + 计时）、DelegationRow 三处重复且白底突兀；现委派等待态只由 TurnStatus + DelegationRow 承载，CurrentActivityPanel 仅保留 system 阶段。
3. **悬浮操作条去白卡**：agent 侧 messageActionsNew 从「绝对定位白色药丸 + 阴影 + bottom:-26 悬浮」（与 StatsLine 重叠、风格不统一）改为 harness IconActions 模式——正文下方常驻透明图标行、28px 热区、tertiary 图标色、`margin-left:-6px` 光学对齐；用户侧 userMessageActions 同步去白卡。
4. **emoji 收敛**：`MarkdownBlock.preprocessMarkdown` 将正文 emoji run 包 `<span data-md-emoji>`（0.95em，围栏代码/行内 code 跳过），消除 emoji 比正文大一档的突兀感。
5. **列表节奏放半档**：markdown 列表块 8px、li 间 2px。
6. **TurnStatus 阶段墨球**（[thinking-orbs](https://github.com/Jakubantalik/thinking-orbs)，MIT）：20px inline 档单色墨球替换 TurnStatus leading 槽的 StateDot 像素追逐，九种状态映射阶段——`pending=breathing / connecting=connecting / reasoning=working / executing=solving / delegating=weaving / answering=composing`。「不喧宾夺主」约束：全局仅此一颗动画（其余行保持静态 StateDot + 扫光）、单色墨不引入新色、speed 0.9、主题显式绑定 `data-pudding-theme`（库 auto 检测的是 `data-theme`，不匹配）；库自带 reduced-motion 静帧降级与离屏暂停。测试侧补齐 setupTests matchMedia 现代 API（addEventListener）。

## 7. 对齐复盘修复（2026-08-23，DeepSeek/Hermes 源码级调研对照）

针对三条调研结论的差距核对与修复（已对齐项不重复建设）：

| 项 | 调研结论 | 现状核对 | 动作 |
|---|---|---|---|
| 行为链 | 消息流内嵌 + 折叠 + 递归调用树；按渲染成本计价防 DOM 爆炸 | ExecutionFlowTimeline/ToolCallTree 已落地；`shouldVirtualizeMessageViewport` 已是内容权重计价（16k 阈值） | 无需改动 |
| 悬浮工具条 | 首选消息块内 inline footer（harness），布局天然稳定 | agent 侧已 inline；**用户侧仍是绝对定位悬浮** | 用户侧转 inline 右对齐行（同 agent 族样式，-6px 光学对齐） |
| 滚动丝滑 | scrollTop 单一写入者 + instant snap + FOLLOW_THRESHOLD=24 吸底 + 上滚停跟随 + 跳底按钮锚定 composer 上方 | 单一写入者/instant/上滚停跟/回底按钮均已具备；**吸底阈值 80px 过宽**（底部附近想停被反复拉回=不丝滑体感来源）；**控制簇 position:fixed + bottom:112px 写死**（composer 变高即错位） | `BOTTOM_THRESHOLD_PX` 80→24（对齐 harness）；控制簇改锚定 `messageListShell` 右下角（absolute，结构上等效 Hermes measured-composer 锚定，无需测量桥） |
| 质感 | 语义 token 两层 + 字号阶梯 + antialiased + 语义状态色 | 四档灰阶/阶梯/antialiased/状态色均已就位 | 无需改动 |

## 8. 输入框 IME 卡顿修复（2026-08-23）

**症状**：中文拼音输入（组合期逐键 → 候选 → 上屏）打字卡顿。

**根因**（两层放大）：
1. 草稿态（draftValue）住在 IntentConsole（1244 行、大量 antd 组件）里，组合期每个按键 `setDraftValue` 全量重渲染整个 composer；
2. 每次上屏 lift（`onInputChange` → useChatState 的 `setInputValue`）触发 ChatPage→ChatMain 整链重渲染，且 ChatMain 的 `handlePinnedQuote` 依赖 `inputValue` 导致回调身份逐次变化、击穿 MessageList 的 React.memo——消息流随打字整树重渲染。

**修复**：
1. 新叶子组件 `ComposerTextInput`（memo）：textarea + 草稿态 + IME 组合守卫（组合期逐键不 lift，compositionEnd 一次性 lift）+「/」命令面板（含键盘导航）全部下沉；按键只重渲染叶子。非组合输入保持逐键 lift（发送读父级 state，零竞态、行为不变）。
2. IntentConsole 只订阅低频事件：`onFocusChange`、`onHasTextChange`（仅空↔非空翻转，驱动 composerActive/发送按钮门控）；外部改写（语音转写/组图提示词/发送清空）走 ref API（setValue/getValue/focus）。
3. `lastLiftedRef` 自 lift 回显抑制：父级 inputValue 与最近 lift 值相等时是 echo 不采纳，仅真正外部改写（引用插入等）采纳进草稿——修复"父级滞后 prop 被误判为外部改写→草稿被清空"的抖动回环（旧实现同隐患，被受控父组件遮住）。
4. ChatMain `handlePinnedQuote` 改经 `inputValueRef` 读取，回调身份稳定，MessageList memo 恢复生效——消息流不再随打字重渲染。

测试：`ComposerTextInput.test.tsx` 8 用例锁定契约（组合不 lift/一次性 lift、逐键 lift、hasText 翻转、命令面板、外部采纳、Enter+图片拦截、ref.setValue、回显回环回归）。

## 9. 流式滚动跳变 + 输出自然度修复（2026-08-23）

**症状**：流式输出期间视口突然停在上方旧消息位置，随后猛跳回底部（跳变、不丝滑）；文字呈 bursts 式蹦出（不自然）。

**根因**：
1. **跳变**：follow effect 依赖缺 `totalSize`。流式内容增长时写入底部用的是「测量修正前」的 scrollHeight；随后虚拟器按真实 DOM 高度修正 totalSize，但这次修正不再触发底部写入（fingerprint 未变），视口停留在半高位置（显示旧消息），直到下一个 delta 才猛跳回——跳变感的直接来源。
2. **不自然**：AgentMessageBubble 覆盖打字机参数（tick 40ms / maxLag 48），滞后余量过小，追平激进；hook 自带的 B2 自适应（24ms tick、流速率追踪、拥堵降速、按滞后分档 charsPerTick）被压制。

**修复**：follow effect 补 `totalSize` 依赖（每次高度修正重新收敛底部）；ResizeObserver 在 auto 模式下对底部阈值内的布局漂移也收敛（收缩/图片加载）；移除打字机参数覆盖，交还 hook 自适应默认。

## 10. Append 风格流式输出（2026-08-23，照抄 harness 架构）

**症状**：① 输出感觉慢；② 消息被分割成多个块、每块单独输出，节奏混乱。

**根因**：
1. 节奏分裂：MessageItem 对含块级语法的尾段整段渲染 `liveText`（瞬间蹦出，绕过打字机），纯文字尾段逐字打——两种节奏交替 = "块状输出"观感；
2. 人为限速：打字机基础步长固定 2 字/24ms（~83 字/秒），不跟流速率；滞后余量上限 200 字（落后可达 2 秒+，追赶成爆发）；
3. 全量重解析：每次提交把整段 stableMarkdown 重跑 ReactMarkdown，长文流式 O(n·parse)。

**修复**（对齐 deepseek-harness MarkdownText/IncrementalMarkdownParser 架构，不复制其品牌样式）：
1. 新组件 `IncrementalMarkdown`：围栏外空行切块（fence 内空行保护、连续空行跳过、前缀偏移），冻结块 memo 缓存（key=偏移:长度），提交只重解析尾部块 → O(tail)；
2. MessageItem：stable 走增量渲染；live 尾段一律消费打字机推进的 `visibleLiveText`（语法尾段走 markdown 前缀渐进渲染，未完成 fence/表格按前缀呈现）；
3. 打字机 append 节奏：基础步长 = 流速率 × tick（clamp 2..24），visible ≈ 到达速率；滞后分档仅作追赶兜底；adaptiveMaxLag 收紧 [24,120]（平滑余量而非落后缓冲）。

**慢的归因**（前端 vs 后端）：修复后前端链路增量延迟 ≈ delta 批处理 80ms + tick 平滑 24ms + 滞后余量 ≤120 字（约 1-1.5 秒内追平）；若仍感觉慢，剩余为主因在 LLM/网络的产出速率——可用消息落定后的 TurnStatsLine（总时长 × tokens 换算 字/秒）对照：DeepSeek 正常散文产出约 40-100 字/秒，明显低于该区间即后端问题。

## 11. 明确不做（本期）

- Trajectory 甘特图审计视图（单独排期）。
- 复制 harness 品牌 CSS/图标/字体；不引入新 UI 框架。
- `styles.ts` residual 全量迁移（仅迁移本期触碰样式）。
- 后端事件契约改动。

## 12. 正文分段交错 + 重复输出修复（2026-08-24）

用户反馈两项：① 消息输出会重复输出；② 文本输出/思维链/工具调用应交错（状态1：文本1 → 思维链 → 工具；状态2：文本1 → 思维链(折叠) → 工具(折叠) → 文本2）。对照 harness 源码级调研结论：**单条 assistant message 是多 block（text/reasoning/tool-call）按出现序排列，一个 turn 内多个 step 消息节点按全局 seq 交错；reasoning/工具行默认折叠一行摘要，文本段永远可见**。Pudding 此前把全部正文增量合并进单一 `MessageNode` 且时间线跳过 message 节点——中间文本（工具调用前的输出）永远沉底拼进尾块，交错语义在投影层就丢失。

### 12.1 后端交错保序（TurnOutputChunker）

- **缺陷**：非 delta 事件（工具调用/结果）到达时不 flush 已缓冲正文/思考，跨轮文本被合并成一个排在工具事件之后的分块，「文本 → 工具 → 文本」的轮次边界在 canonical sequence 持久层丢失。
- **修复**：`Feed` 的非 delta 分支先 `FlushPendingContent` 再透传事件；每轮文本以独立 `message.content.appended` 事件先于其后的工具事件落盘。
- 测试：`TurnOutputChunkerPayloadOwnershipTests` 补 2 例（content/thinking 先于工具事件、第二轮文本不与第一轮合并）。

### 12.2 前端投影分段（executionFlowProjector）

- `MessageNode` 语义升级为「一个连续正文段」：相邻 content 增量合并为一段；任何非 content 节点事件（tool/delegation/retry/terminal，含 reasoning delta）切段；后续 content 开新段，按各自首事件 sequence 交错进 nodes（对齐 harness 每 step 一条 AssistantMessageNode）。
- `message.completed/failed` 应用到当前开放段并关闭；空 delta/纯空白段被投影后过滤（对齐 reasoning 空段语义，failed 段保留 errorMessage）。

### 12.3 时间线内联文本段 → AgentTurnCard 内容块流（2026-08-25 重构，旧结论作废）

- **（旧方案，已退役）**「时间线内联中间段 + 尾段独立正文气泡」的双区域渲染：中间段与尾段由两套组件承载，边界依赖 `deriveTrailingMessageNode` 尾段判定；投影分段与整块正文的一致性靠 `segmentRendering` 守卫兜底。实测存在两大缺陷：`message.completed.reply` 以全量文本覆盖尾段导致重复/错段/两大块；卡片底部尾段气泡与时间线段构成事实上的第二正文源。
- **（现行方案）** `TurnContentStream` 内容块流（`turnContentBlocks.ts`）：投影 nodes → `TextBlock`（永久可见）⇄ `ActivityGroupBlock`（可折叠，折叠时成员 DOM 卸载）交错块流；`ExecutionFlowTimeline` / `deriveTrailingMessageNode` / `buildEntriesFromProcessItems` / `messageSegments` 退役。`answerMarkdown` 仅作持久化正文、复制/TTS 与无 canonical 正文节点时的旧记录兜底，绝不再用字符串相等/前缀关系切换分段渲染；投影一旦有 TextBlock，正文就全部由块流承载。流式：只有「最后一个正文块 && terminal='none' && run 活跃」走 `useTypewriterStreaming`（`TextSegmentView`），已封闭段静态渲染不重排。
- 折叠默认值（ADR-079 修订）：会话级默认 owner 唯一，属于当前最新 Agent 回合的最后一个 ActivityGroup；最终正文追加不改变 owner，同回合新 ActivityGroup 或新 Agent 回合产生 ActivityGroup 才转移 owner。旧 owner 柔和收起并卸载成员；组内 reasoning 完整换行，工具状态可见而原始 IN/OUT 仍懒加载；用户 override 粘性（`useDisclosureRegistry`）。

### 12.4 重复输出修复（三处加固）

1. **`resolveTerminalAssistantMarkdown` 分叉兜底**：current 与终态 reply 完全分叉且无后缀衔接时改为返回 reply（服务端 canonical，与刷新后投影一致）。旧实现把 reply 整段拼在 current 之后——流内任何一次偏差（重叠修剪误删、快照替换）都会让**整段正文显示两遍**（「消息重复输出」的直接来源）。
2. **打字机 stale-stable 守卫**（`useTypewriterStreaming`）：`stableTextRef` 镜像已提交 stable 前缀；`text` 不再以其开头（快照/终态改写）时整体重置 stable/live 游标，杜绝 stale stable 与新 live 拼接的同段双渲染。新增 `text` 收缩/替换重置用例。
3. **结构防重**：分段渲染本身消除了「中间文本既在时间线又沉底拼进整块正文」的重复形态（尾段独占正文，中间段独占时间线，一致性守卫兜底）。

### 12.5 测试与验收

- 前端新增/更新：投影器分段 5 例（切段/交错序/相邻合并/message.completed/空段过滤）、时间线 5 例（尾段判定×2、内联交错、未启用回退、DOM 序）、消费点 3 例（中间段内联+正文只承载尾段不重复、无尾段正文不渲染、分叉守卫回退整块）、终态 reply 不重复 2 例、打字机替换重置 1 例。chat 目录 106/109 套件通过（IntentConsole/InputArea/DevPanel 3 套件为 HEAD 存量失败，与本次无关）。
- 后端：`dotnet build` PuddingPlatform/PuddingHost 0 错误；TurnOutputChunker 7/7。
- 真实模型 smoke（多轮工具调用会话的交错视觉验证）待两段式外部验收。

## 13. AgentTurnCard 内容块流重构（2026-08-25）

验收报告（同日）确认旧双区域方案的四类缺陷后整轮重构，按 8 步施工顺序落地：

1. **projector 纪律**：`message.completed` 只给当前开放正文段落终态，绝不以 `reply` 覆盖段文本；reply 仅在整 turn 无任何 content delta 时创建唯一正文段（非流式兜底）。节点边界只由 canonical sequence 决定。
2. **后端硬切**（`AgentConversationProjectionService` / `AgentProjectionDtos`）：active run 不再取「最新任意 runId 事件」（子代理生命周期事件携带子 run_id 会整体抢占父快照）——根 run = 最新 `turn.started` 的 RunId，快照按其 TurnId 聚合父正文/思考/工具与子代理生命周期，正文按根 RunId 收敛；`ProcessSummaryItem.Sequence` 必填（前端删 `baseSequence+index` 合成与 `-1_000_000` 负段回退）；快照/明细携带 `TurnEventWindow`（throughSequence/min/max/hasMoreBefore，64 条窗口的截断边界显性化）。
3. **纯投影** `turnContentBlocks.ts`：`buildTurnContentBlocks`（正文 ⇄ 行为组块流）+ 组摘要（N 段思考/M 次工具/K 个子代理/时长）+ `deriveStatsFromProjection`；固定 11 事件验收基准（乱序重放等价 / T4 result 原位更新不改组 key / text D 到达组转历史）。
4. **组件**：`TurnContentStream`（块流编排 + 路径 A `buildActivityNodesFromProcessItems` 适配）、`ActivityGroup`（折叠=成员 DOM 卸载；默认披露策略后由 ADR-079 修订为“会话级唯一 owner = 最新 Agent 回合最后行为组”，最终正文不关闭 owner；reasoning 完整换行、工具原始详情懒加载）、`TextSegmentView`（封闭段静态 / 尾部开放段打字机；语义 memo 避免 append 时重渲染旧段）、`useDisclosureRegistry`（用户 override 粘性且引用稳定）。
5. **消费点**：AgentMessageBubble 删除 `segmentRendering/bodyContent/trailingMessageKey` 双区域逻辑；不再用 `answerMarkdown` 字符串关系关闭 TextBlock，投影一旦含正文节点就只有块流一个正文源；无 canonical 正文节点的旧记录才回退整块气泡。完成粒子在两种正文模式下都可达。`ExecutionFlowTimeline.tsx` 及其测试退役。
6. **受控折叠**：ExecutionDisclosureRow 家族（Reasoning/ToolCall/Delegation 行）补受控 props 透传。
7. **验收**：turnContentBlocks、TurnContentStream、消费点、projector/TurnSurfaceStore 定向用例覆盖交错、折叠、reply 不覆盖、sequence fail-closed 与历史正文段不重渲染；生产前端构建通过。真实运行时 smoke 必须在新 Core/新前端部署后执行，源码存在不等于当前进程已加载。
8. **遗留**（下轮）：TurnSurfaceStore 全量重投影改增量（revision 下沉单 turn）；SSE delta RAF 合帧（agent-client 轮询架构下暂无 SSE 路径）；最新组超 ~100 节点的外层滚动窗口化。

补充修复：历史明细懒水合的 `slice(0, 2)` 只表示并发窗口，不能成为总量上限。`useTurnSurfaceStore` 在每个槽位完成后继续调度下一条已注册可见消息；迟到的旧会话响应不写入新 store，同轮失败项不立即死循环，并在下一次服务端投影到达时重试。否则初始列表只会有最早两条消息得到投影，最新卡片仍退回“轨迹消失 + 底部整段正文”。
