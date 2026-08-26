# Agent 消息交错内容流与最新行为组披露完整实施方案

> 日期：2026-08-25  
> 状态：设计冻结；实施与生产验收尚未完成  
> 范围：`PuddingCore` canonical 投影合同、`PuddingPlatform` 会话投影、`PuddingPlatformAdmin` Chat 消息投影/渲染/滚动  
> ADR：[ADR-079 Agent 消息交错内容流与最新行为组披露](../07架构/93ADR-079Agent消息交错内容流与最新行为组披露ADR.md)  
> 关联：[消息卡对齐方案](../deepseek-harness-message-card-alignment-2026-08-14.md)、[Chat 行为链升级记录](../chat-ui-behavior-chain-quality-upgrade-2026-08-23.md)、[ADR-073](../07架构/87ADR-073任务看板优先的Agent工作台轨迹与实时指标施工ADR.md)

## 1. 目标与验收口径

同一个 Agent 回合必须以一个大卡片承载，并按 canonical 发生顺序 append：

```text
TextBlock 1
ActivityGroup 1 { Reasoning 1, Tool 1 }
TextBlock 2
ActivityGroup 2 { Reasoning 2, Tool 2, Tool 3 }
TextBlock 3
ActivityGroup 3 { Reasoning 3, Tool 4 }
```

目标不是把“行为轨迹”和“最终回答”排成两个区域，而是恢复一个真实、有序、可重放的内容流：

1. 正文、可披露思考、工具、委派严格按 `sequence` 交错，不按类型重新分区。
2. 正文只存在一个渲染源；不得在块流外再渲染一份 `answerMarkdown`。
3. 默认只展开**当前最新 Agent 回合的最后一个行为组**；其余行为组保留摘要行并折叠内容。
4. 最终正文到达后，最近行为组仍保持展开，方便用户理解最近一步；只有更新行为组或更新 Agent 回合出现时，默认披露所有权才转移。
5. 旧组以短促、柔和的收起动画退场，动画结束后卸载成员 DOM；不是 CSS 长期隐藏。
6. 最新组中完整显示 canonical 可披露思考正文和工具状态；工具原始 IN/OUT、JSON、日志仍按需加载。
7. 运行中工具在对应行显示动态状态；完成后以绿点表示成功、红点表示失败，并以图形/文字提供非颜色语义。
8. 长会话流式 append、滚动和历史水合不得导致旧正文反复 Markdown 解析、视口跳变或 hydration 永远停在前两条消息。

“思考”仅指服务端明确提供的 model-visible reasoning summary；本方案不推断、不生成、也不展示模型未披露的私有 chain-of-thought。

## 2. 产品信息架构

### 2.1 AgentTurnCard

一条 Agent 消息只有一个视觉卡片：

```text
AgentTurnCard
├─ Header：Agent 身份、时间、回合状态
├─ TurnStatus：唯一 aria-live 运行态
├─ TurnContentStream
│  ├─ TextBlock
│  ├─ ActivityGroup
│  ├─ TextBlock
│  └─ ActivityGroup
├─ Error / Retry（仅有事实才显示）
├─ Stats：思考段、工具数、委派数、耗时、tokens
└─ Actions：复制、重试、朗读、删除
```

宽屏下正文/行为内容列最大宽度为 720px，AgentTurnCard 外壳最大宽度为 750px（内容宽度 + 28px 横向 padding + 2px border）；窄屏继续受 82% 可用宽度约束。不得让外壳占满 82% 而内部内容仍固定 720px，否则会在卡片右侧制造大块无效空白。

不得恢复以下双区域结构：

```text
[思考 + 工具的大块]
[最终回答的大块]
```

### 2.2 TextBlock

- `MessageNode` 的连续正文增量构成一个 `TextBlock`。
- 任意 reasoning/tool/delegation/retry 事实切断当前正文段；后续正文创建新段。
- 封闭正文段静态渲染；只有当前回合最后一个开放正文段使用 append/typewriter。
- `message.completed.reply` 仅在整回合没有任何正文 delta 时创建唯一兜底正文；不得覆盖或复制已有正文段。
- `answerMarkdown` 只服务复制、TTS、持久正文兼容和无 canonical 正文节点时的最终文本兜底。

### 2.3 ActivityGroup

两个 TextBlock 之间最大连续非正文节点形成一个 `ActivityGroup`。它保留原始节点顺序，不把 reasoning、tool、delegation 再分别聚合到固定区域。

折叠标题示例：

```text
● 2 段思考 · 3 次工具 · 1 个子代理 · 34.3s                  ▸
```

折叠只隐藏成员内容，不删除摘要、状态、计量与失败事实。

### 2.4 最新披露所有权

默认展开由两个条件共同确定：

```text
isDefaultDisclosureOwner =
  messageId === newestAgentMessageId
  && groupKey === lastActivityGroupKeyOfMessage
```

规则冻结如下：

| 事件 | 原最新组 | 新最新组 |
|---|---|---|
| 追加最终正文 | 保持展开 | 不变 |
| 同回合出现新 reasoning/tool/delegation 组 | 柔和收起 | 展开 |
| 新 Agent 回合开始产生行为组 | 柔和收起 | 展开 |
| 工具 result 更新同一个 `toolCallId` | 保持展开并原位更新 | 不新建组 |
| 用户手动折叠最新组 | 保持用户选择 | 后续普通 delta 不抢夺 |
| 用户手动展开历史组 | 保持用户选择 | 自动策略不强制关闭 |
| 刷新/首次历史水合 | 直接落到稳定状态 | 不回放入场/退场动画 |

因此默认情况下整个会话视图最多只有一个行为组展开；用户主动展开的历史组除外。

## 3. 展开组内部设计

### 3.1 Reasoning：组内直接完整换行

外层 ActivityGroup 已是披露边界，组内 reasoning 不再要求第二次点击：

```text
◇ 思考 · 776ms
  python -c 运行失败，可能是 python 不在 PATH 或者空格问题。
  让我改用 PowerShell 直接运行。
```

- 显示该 `ReasoningNode` 的完整 canonical 可披露文本。
- 元信息与正文分两行；正文 `white-space: pre-wrap`、`overflow-wrap: anywhere`、`word-break: break-word`。
- 不使用 `nowrap`、`text-overflow: ellipsis` 或仅靠 `title` 提供全文。
- 保留原始段落；普通 reasoning 不设置内层滚动容器。
- 运行中 reasoning 使用轻量扫光；完成后变为静态状态点和耗时。

### 3.2 Tool：状态直接可见，重载荷继续懒加载

展开组直接显示每次工具调用的状态和可读主参数：

```text
● 终端 · git status                                      1.2s
● 读取文件 · Source/.../ActivityGroup.tsx                86ms
● 搜索 · “latestActivityGroupKey”                        214ms
● shell · python -c ...                         失败 · exit 1
```

- 工具名、命令、路径、查询词允许自然换行；禁止把原始 JSON 当摘要。
- running：蓝色动态点 + 行扫光 + “执行中”。
- completed：绿色实心点 + 耗时。
- failed：红色实心点 + 错误首行 + 非零 exit code。
- 工具行点击后才创建 presentation、IN、OUT 和日志 DOM。
- 超长 OUT 默认只创建有界头尾预览；“查看完整输出”进入按需详情或检查器。
- `toolCallId` 是 call/result 配对的唯一依据；不得按工具名、数组相邻或时间猜测。

### 3.3 Delegation

- 组内显示子代理名称、任务摘要、running/completed/failed 状态和耗时。
- 子代理完整事件、回复和产物进入运行检查器；主卡不复制子代理内部工具轨迹。
- 相邻委派可在不改变 sequence 的前提下使用同一个视觉列表，但每项保留稳定 `runId`。

### 3.4 病理大组保护

- 折叠历史组不构建成员 DOM。
- 默认展开组内最多直接挂载最近 60 个 Activity 行；更早成员由“更早 N 条行为”按需挂载，不改变 canonical 组和统计。
- reasoning 文本本身是单个/少量文本节点，不按字符创建 span。
- 原始工具输出永远不因外层组自动展开而自动挂载。

## 4. 柔和折叠状态机

### 4.1 状态

```text
closed -> opening -> open -> closing -> closed
```

- `open`：成员 DOM 已挂载且可见。
- `closing`：保留 DOM 完成过渡，结束后卸载。
- `closed`：只保留 ActivityGroup header。
- 初始 hydration 直接进入 `open/closed`，不执行批量动画。

### 4.2 动画参数

- 高度：CSS grid `grid-template-rows: 1fr -> 0fr`，子层 `min-height:0; overflow:hidden`。
- 透明度：`1 -> 0`。
- 时长：高度 220ms，透明度 160ms。
- 曲线：`cubic-bezier(0.22, 1, 0.36, 1)`。
- 动画结束事件后卸载成员；另设 300ms fallback timer，避免 `transitionend` 丢失造成常驻 DOM。
- `prefers-reduced-motion: reduce` 时立即切换或仅做 ≤80ms 淡出。
- 同一次 canonical 更新最多触发“旧 owner 收起 + 新 owner 展开”一对动画。

ActivityGroup 不允许直接写 `scrollTop`。折叠引起的消息高度变化只上报既有 viewport 测量系统，由单一 scroll authority 决定吸底或锚点恢复。

## 5. Canonical 数据合同

### 5.1 最小事件字段

所有参与块流的事实必须携带：

```text
eventId, sequence, occurredAt, runId, turnId, type
```

工具额外要求 `toolCallId`；委派要求子 `runId`；presentation 使用 `kind/meta`。`sequence` 必须是服务端事实且单调，不得在 React 中用数组下标、负数区间或时间戳合成。

### 5.2 合流与重放

- bootstrap、历史 detail、active snapshot、gap recovery、live update 都进入同一个 `TurnSurfaceStore`。
- `eventId` 幂等去重；按 `sequence` 排序；终态单调，迟到 running 不能覆盖 completed/failed。
- Snapshot + Watch 与仅按同一窗口完整 replay 必须得到等价节点和块结构。
- `TurnEventWindow` 显式标记 `min/max/throughSequence/hasMoreBefore`；窗口被截断不能伪装为完整轨迹。
- 历史可见消息懒水合并发上限为 2，但队列必须持续排空；`slice(0, 2)` 不能成为总量上限。
- 会话切换后迟到响应不得写入新会话；失败项本轮跳过、下一服务端 revision 再试，防止死循环。

### 5.3 硬切与旧记录

开发阶段不为缺少 `sequence` 的轨迹制造兼容排序。旧记录若没有 canonical 明细，只显示最终正文和“轨迹不可用”，不得从 `processItems` 数组顺序伪造跨源行为链。完成数据重置/升级后删除临时 adapter 和本地 feature flag。

## 6. 前端投影模型

### 6.1 ExecutionFlowProjector

输入 canonical events，输出只读节点：

```text
MessageNode
ReasoningNode
ToolNode
DelegationNode
RetryNode
TerminalNode
```

投影器负责：

1. `eventId` 去重与 `sequence` 排序。
2. 连续正文分段。
3. reasoning 段边界与持续时间。
4. tool call/result 原位配对、父子工具树、终态单调。
5. delegation 生命周期聚合。
6. terminal 只更新状态，不复制正文。

投影器是纯函数，不持有 React 展开态、动画态或滚动态。

### 6.2 TurnContentBlocks

`buildTurnContentBlocks(nodes)` 只做确定性解释：

- MessageNode → TextBlock。
- 最大连续非正文节点 → ActivityGroupBlock。
- TerminalNode 不创建视觉行。
- ActivityGroup key 锚定首个 source event，后续 result 原位更新不改变 key。
- `lastActivityGroupKey` 从块数组逆向寻找最后一个 ActivityGroup；不得用 `blocks.at(-1)` 判断，因为最后一块可能是最终正文。

### 6.3 DisclosureRegistry

展开态采用“默认值 + 用户覆盖”：

```text
resolvedExpanded = userOverride[groupKey] ?? defaultExpanded(groupKey)
```

- override 是 tri-state：未设置、明确展开、明确折叠。
- 自动 owner 转移不能覆盖用户选择。
- key 必须稳定；不得把数组 index 用作 disclosure key。
- 初期只需保存在当前会话 UI 生命周期；除非后续有明确产品需求，不写数据库或 localStorage。

## 7. 组件与文件级施工

### 7.1 Core / Platform

| 文件 | 施工内容 |
|---|---|
| `Source/PuddingCore/Platform/AgentProjectionDtos.cs` | `ProcessSummaryItem.Sequence` 必填；窗口和 tool/delegation 稳定标识进入 DTO |
| `Source/PuddingPlatform/Services/AgentChat/TurnOutputChunker.cs` | 非 delta 事实到达前 flush 待提交正文/思考，保存跨类型真实顺序 |
| `Source/PuddingPlatform/Services/AgentChat/AgentConversationProjectionService.cs` | 根 turn 聚合、正文限定根 run、子代理生命周期并入；active/detail 输出同构 canonical 窗口 |
| `Source/PuddingPlatformTests/Services/TurnOutputChunkerPayloadOwnershipTests.cs` | 锁定 content/reasoning 在工具事实前 flush、后续正文不跨工具合并 |

### 7.2 Client store / projector

| 文件 | 施工内容 |
|---|---|
| `client/types.ts` | sequence、turnId、runId、toolCallId 强类型必填；删除 index 合成语义 |
| `projections/turnSurfaceStore.ts` | 四源 eventId 幂等合流、终态单调、缺 sequence fail-closed |
| `hooks/useTurnSurfaceStore.ts` | 并发 2 的可持续排空 hydration 队列、迟到响应隔离、失败重试门控 |
| `projections/executionFlowProjector.ts` | 连续正文分段、reply 只兜底、工具原位配对、多段 reasoning、委派投影 |
| `projections/turnContentBlocks.ts` | TextBlock/ActivityGroup 纯构建、稳定 key、统计、最后行为组派生 |

### 7.3 Chat UI

| 文件 | 施工内容 |
|---|---|
| `MessageList.tsx` | 派生 `newestAgentMessageId`；不在消息组件内猜当前 owner；把已水合 canonical 行为链成本计入 viewport render weight |
| `MessageRow.tsx` / `AgentMessageBubble.tsx` | 透传 `isDefaultDisclosureOwnerTurn`；大卡片只保留一个正文源；仅在进入视口一屏预取区后注册可见 turn |
| `execution-flow/TurnContentStream.tsx` | 按块顺序渲染；以“最后 ActivityGroup”而非“最后 block”选 owner |
| `execution-flow/ActivityGroup.tsx` | 摘要、默认披露、成员 DOM 生命周期、closing 动画、病理大组保护 |
| `execution-flow/ReasoningDisclosureRow.tsx` | 新增组内 `inline-full` 模式：完整 reasoning 自然换行、无二级 chevron |
| `execution-flow/ToolCallRow.tsx` | 组内可读主参数换行；状态直接可见；IN/OUT/presentation 保持显式展开 |
| `execution-flow/useDisclosureRegistry.ts` | tri-state 用户 override；默认值改变不抢夺用户选择 |
| `styles/execution-flow.styles.ts` | full reasoning 两行布局、折叠过渡、reduced-motion |
| `styles/toolcall.styles.ts` | 多行工具摘要、错误/运行/完成状态、长内容边界 |
| `viewport/useMessageViewportRuntime.ts` | 唯一 scroll writer；高度变化时吸底或恢复锚点，不由行为组件写 scrollTop；正常流保留真实行高，虚拟化决策包含 canonical render weight |
| `viewport/executionFlowRenderWeight.ts` | 按 reasoning/tool/delegation/message 节点计算结构与文本成本，避免只看消息正文而低估行为链 DOM |

建议新增小型通用组件 `CollapsibleUnmountRegion.tsx`，只负责 open/opening/closing/closed 和 transitionend 后卸载，不承担业务展开策略。

### 7.4 必须采用的 TypeScript 接口

以下签名是施工合同；允许按仓库 lint 调整 import/命名，不允许改变职责：

```ts
// MessageList -> MessageRow -> AgentMessageBubble -> TurnContentStream
interface LatestActivityDisclosureProps {
  /** 当前 turn 是否拥有会话级“默认最新行为披露权”。 */
  ownsLatestActivityDisclosure: boolean;
}

interface TurnContentStreamProps extends LatestActivityDisclosureProps {
  projection?: ExecutionFlowProjection;
  processItems?: readonly TimelineItem[];
  isRunActive: boolean;
  workspaceId?: string;
  onAnswerContextMenu?: React.MouseEventHandler;
  onOpenInspector?: (runId: string) => void;
}

interface ActivityGroupProps {
  block: ActivityGroupBlock;
  /** 该组是否是本 turn 最后一个 ActivityGroup。 */
  isLatestGroupInTurn: boolean;
  /** turn 是否拥有会话级默认披露权。 */
  ownsLatestActivityDisclosure: boolean;
  /** 该组是否是整个块流最后一块；只参与 running tail 判断。 */
  isStreamTailGroup: boolean;
  isRunActive: boolean;
  registry: DisclosureRegistry;
  onOpenInspector?: (runId: string) => void;
}

type DisclosureOverride = 'expanded' | 'collapsed';

interface DisclosureRegistry {
  resolve(key: string, defaultExpanded: boolean): boolean;
  set(key: string, value: DisclosureOverride): void;
  toggle(key: string, resolvedValue: boolean): void;
  clear(): void;
}

type CollapsiblePhase = 'closed' | 'opening' | 'open' | 'closing';

interface CollapsibleUnmountRegionProps {
  open: boolean;
  animate: boolean;
  children: React.ReactNode;
  onPhaseChange?: (phase: CollapsiblePhase) => void;
}
```

`ActivityGroup` 的默认展开值只能按下式计算：

```ts
const defaultExpanded =
  ownsLatestActivityDisclosure && isLatestGroupInTurn;
const expanded = registry.resolve(block.key, defaultExpanded);
```

`isStreamTailGroup` 只能决定当前 reasoning 是否显示 running 视觉，不能决定默认展开。

### 7.5 会话级 owner 的确定算法

`MessageList` 已经持有完整 `projection.items` 和 `getTurnProjection(turnId)`，因此由它确定 owner，不新增全局 UI store：

```ts
function findLatestActivityDisclosureTurnId(
  items: readonly VirtualMessageItem[],
  getTurnProjection?: (turnId: string) => ExecutionFlowProjection | undefined,
): string | undefined {
  for (let index = items.length - 1; index >= 0; index -= 1) {
    const item = items[index];
    if (item.kind !== 'message' || item.block.role !== 'agent') continue;
    const turnId = item.block.turnId;
    const projection = getTurnProjection?.(turnId);
    if (!projection) continue;
    if (
      projection.nodes.some((node) =>
        node.kind === 'reasoning' ||
        node.kind === 'tool' ||
        node.kind === 'delegation',
      )
    ) {
      return turnId;
    }
  }
  return undefined;
}
```

约束：

- 从后向前找到第一个**已有 canonical ActivityNode** 的 Agent turn；最新 Agent 卡尚未产生行为节点时，上一段最近行为继续展开。
- owner 计算必须 `useMemo`；依赖 `projection.items`、`getTurnProjection` 及其 revision 语义。
- `renderProjectionItem` 传入 `ownsLatestActivityDisclosure={item.block.turnId === latestActivityDisclosureTurnId}`。
- `MessageRow.areMessageRowPropsEqual` 必须比较该布尔值，否则 owner 转移会被 memo 吞掉。
- 不以 DOM 挂载顺序、虚拟行 index、`activeItemId` 或当前滚动位置判断 owner。

### 7.6 TurnContentStream 的确定算法

```ts
const blocks = buildTurnContentBlocks(projection.nodes);
const latestActivityGroupKey = [...blocks]
  .reverse()
  .find((block) => block.kind === 'activity-group')
  ?.key;

return blocks.map((block, index) => {
  if (block.kind === 'text') {
    return <TextSegmentView /* canonical key + streaming tail */ />;
  }
  return (
    <ActivityGroup
      block={block}
      isLatestGroupInTurn={block.key === latestActivityGroupKey}
      ownsLatestActivityDisclosure={ownsLatestActivityDisclosure}
      isStreamTailGroup={index === blocks.length - 1}
      /* remaining props */
    />
  );
});
```

禁止写成 `isLatestGroup={index === blocks.length - 1}`。该表达式在最终 TextBlock 到达后必然把最近行为隐藏。

### 7.7 CollapsibleUnmountRegion 施工算法

实现必须满足“动画期间保留、动画结束卸载”，推荐状态转换：

```ts
// 伪代码，Flash 施工时写成 reducer 或等价的显式状态机。
useLayoutEffect(() => {
  if (!animate) {
    setPhase(open ? 'open' : 'closed');
    return;
  }
  if (open) {
    setPhase('opening');       // 先挂载 children
    requestAnimationFrame(() => setPhase('open'));
    return;
  }
  if (phase === 'open' || phase === 'opening') {
    setPhase('closing');       // 保留 children，启动 1fr -> 0fr
  }
}, [animate, open]);

const mounted = phase !== 'closed';
const visuallyOpen = phase === 'open' || phase === 'opening';
```

- `closing` 的 `transitionend` 只接受目标容器的 `grid-template-rows`/`opacity` 事件；冒泡的子节点事件必须忽略。
- 300ms fallback timer 进入 `closed`；phase 变化或卸载时清理 timer/RAF。
- `open` 在 closing 中反转时直接进入 opening，不允许先卸载再重挂。
- 首次 mount、历史 hydration、虚拟行重新挂载使用 `animate=false`；只有已经提交过稳定状态后的 owner 转移使用 `animate=true`。
- 即将关闭且 `document.activeElement` 位于 body 内时，先 `headerRef.current?.focus()`。
- `closed` 时 `children` 不得被求值成完整 JSX 树。`ActivityGroup` 必须把 `renderNodes()` 放到 mounted 分支内部。

建议 DOM：

```tsx
<div className={styles.collapseGrid} data-phase={phase}>
  <div className={styles.collapseGridInner}>
    {mounted ? children : null}
  </div>
</div>
```

### 7.8 Reasoning 与 Tool 的精确改法

`ReasoningDisclosureRow` 新增 `mode?: 'disclosure' | 'inline-full'`，默认保持 standalone 使用方的旧行为；`ActivityGroup` 一律传 `mode="inline-full"`：

```tsx
if (mode === 'inline-full') {
  return (
    <div data-testid="reasoning-inline-full" className={styles.reasoningFullRow}>
      <StateDot state={isCurrent ? 'ongoing' : 'done'} />
      <div className={styles.reasoningFullContent}>
        <div className={styles.reasoningFullMeta}>思考 · {duration}</div>
        <div className={styles.reasoningFullText}>{fullText}</div>
      </div>
    </div>
  );
}
```

对应样式至少包含：

```ts
reasoningFullText: {
  whiteSpace: 'pre-wrap',
  overflowWrap: 'anywhere',
  wordBreak: 'break-word',
  minWidth: 0,
}
```

不得复用 `reasoningSummary` 的 `nowrap/ellipsis` 类名。

`ToolCallRow` 不新增自动打开详情的 prop。外层 ActivityGroup 展开只让工具行可见：

- title + `presentation.meta` 的主要 command/path/query/task 使用多行摘要类。
- 原始 arguments/output/presentation renderer 继续受工具行自己的 `expanded` 控制。
- 摘要生成仍需有界；不得为了“完整”把任意原始 JSON 直接放到摘要。
- `data-testid="toolcall-expanded"` 在外层组刚展开时必须为 0。

### 7.9 按任务卡施工

| ID | 修改 | 必须新增/修改的测试 | 依赖 |
|---|---|---|---|
| I-01 | 审计 `TurnOutputChunker` 与 canonical DTO，删除 sequence 合成入口 | chunker flush、DTO serialization | 无 |
| I-02 | 修复 `AgentConversationProjectionService` root turn/window/detail | active/detail 顺序等价、子 run 不抢 root | I-01 |
| I-03 | 固化 `TurnSurfaceStore` 幂等/终态/缺序 fail-closed | 乱序、重复、迟到终态 | I-01 |
| I-04 | 修复 `useTurnSurfaceStore` hydration 排空 | 3/10 条消息、并发≤2、切会话迟到响应 | I-03 |
| I-05 | 固化 `executionFlowProjector` 正文切段与 reply 兜底 | `T/A/T`、无重复正文、tool result 原位 | I-03 |
| I-06 | 固化 `turnContentBlocks` 最大连续组和稳定 key | 固定 11 事件、乱序 replay、last group | I-05 |
| I-07 | MessageList 计算并透传会话级 owner | 最新无 Activity 时保留旧 owner；新 Activity 后转移 | I-04/I-06 |
| I-08 | DisclosureRegistry tri-state + memo comparator | 用户 override 粘性、owner 布尔变化触发 render | I-07 |
| I-09 | CollapsibleUnmountRegion 与 ActivityGroup 接线 | closing 保留、结束卸载、反转、reduced-motion、焦点 | I-08 |
| I-10 | Reasoning `inline-full`、Tool 多行摘要 | 完整换行、无二级 reasoning disclosure、详情仍懒 | I-09 |
| I-11 | AgentMessageBubble 删除双正文源并完成统计/actions 接线 | 正文只一次、引用/粒子/actions 两路径可达 | I-06 |
| I-12 | viewport/性能收口与真实 smoke | render count、DOM budget、滚动锚点、浏览器证据 | I-07–I-11 |

每张任务卡独立提交测试证据；不得以最终视觉截图替代 P0–P2 的数据合同测试。

### 7.10 Flash 施工禁止项

- 禁止重写或回滚工作区中不属于本方案的未提交改动。
- 禁止恢复 `ExecutionFlowTimeline + bottom answer bubble` 双区域。
- 禁止用 `joined.startsWith(content)`、字符串相等或字符串后缀关系决定 canonical TextBlock 是否渲染。
- 禁止 `message.completed.reply` 覆盖已有分段正文。
- 禁止 `baseSequence + index`、负数 sequence、时间戳排序或数组 index key。
- 禁止每个 Agent 卡各自默认打开一个最后组。
- 禁止最终正文到达时关闭最近行为组。
- 禁止外层 ActivityGroup 展开时自动挂载全部工具 IN/OUT。
- 禁止折叠后只做 `display:none`。
- 禁止 ActivityGroup、ToolCallRow 或动画组件直接写 `scrollTop`。
- 禁止在 render 中创建不稳定 registry 对象或对全部历史节点做深 JSON stringify 比较。
- 禁止把源码存在、单测通过、构建通过写成真实产品验收完成。

## 8. 性能方案

1. canonical store 只更新受影响 turn 的 revision；不得每次 delta 重投影全部会话。
2. SSE/轮询增量按帧合并，单帧至多提交一次受影响 turn。
3. TextSegmentView、ActivityGroup、ToolCallRow 使用可见语义 memo；旧封闭正文和历史组不因尾部 append 重渲染。
4. 折叠组成员 DOM 为 0；不是 `display:none`。
5. Markdown 只解析新增/尾部块；旧稳定块 memo。
6. 工具 presentation、IN/OUT 和超长日志点击后创建。
7. 消息虚拟化以 message block 为单位；ActivityGroup 不引入第二个滚动容器。
8. ResizeObserver 只进入 viewport runtime；组件不得直接修正 scrollTop。
9. 正常文档流使用真实消息高度，不使用 `content-visibility` + remembered `contain-intrinsic-size` 估算动态 Agent 行。
10. 历史 Agent turn 仅在进入视口的一屏预取区后加入 hydration 队列；React 挂载不等于可见。
11. 已水合 canonical 行为节点的结构和文本成本必须计入 render weight，使重型短会话也能及时启用虚拟化。
12. 任务看板、Checkpoint、历史搜索、开发面板、子代理检查器和右键菜单是非首屏模块；关闭时不下载、不解析、不挂载，工具栏 hover/focus 仅做意图预取。
13. 余额、Goal 和会话推断请求在首帧后的 idle window 才启动；消息投影、Agent 选择和实时事件不得等待这些辅助请求。
14. 生产构建必须运行 Chat bundle budget，阻断非首屏模块重新泄漏到 `common-async` 或 Chat 路由首始 chunk。

性能门禁：

- 300 个 canonical 事件、20 个历史组、1 个最新组的卡片，默认 DOM 只包含历史 header + 最新组成员。
- 10 秒持续 append 期间不出现 >50ms 的重复主线程长任务；历史 TextBlock render 次数保持不变。
- 收起过渡只作用一个旧 owner，结束后成员 DOM 数归零。
- 自动跟随底部时折叠不跳离底部；用户上滚时保持锚点，不抢回底部。
- 正常流连续上下滚动时 `scrollHeight` 不因离屏 Agent 行进入视口而产生千像素级跳变。
- HTML 同步脚本不超过 1536 KiB，Chat 路由 chunk 不超过 480 KiB；任务看板源码不进入 `common-async`，Checkpoint、ContextMenu 和任务看板入口不进入 Chat 首始 chunk。

## 9. 无障碍与国际化

- ActivityGroup header 使用 `button` 语义或 `role=button`，携带 `aria-expanded` 和可达名称。
- TurnStatus 是唯一 `aria-live=polite`；每个工具状态变化不重复播报整个列表。
- 状态同时提供点形、文字和 aria label，不能只靠绿/红颜色。
- Enter/Space 控制折叠；焦点环可见。
- 自动收起不得把焦点所在元素卸载：若焦点位于即将收起的组内，先把焦点迁移到组 header，再开始 closing。
- 动画遵守 `prefers-reduced-motion`。
- 中文、英文、长路径、长 URL、无空格命令均必须自然换行。

## 10. 实施阶段与依赖

| 阶段 | 内容 | 依赖 | 完成证据 |
|---|---|---|---|
| P0 合同硬切 | sequence、窗口、根 turn、toolCallId、chunk flush | 无 | Core/Platform 合同测试 |
| P1 Store | 四源合流、hydration 排空、迟到响应隔离 | P0 | store/hook 测试 |
| P2 纯投影 | nodes、正文切段、ActivityGroup、稳定 key | P1 | projector/block property tests |
| P3 单卡交错 | 删除双正文源，接入 TurnContentStream | P2 | DOM 顺序/不重复测试 |
| P4 最新披露 | 全局 owner、完整 reasoning、工具状态、柔和收起 | P3 | disclosure/animation/a11y 测试 |
| P5 性能滚动 | memo、懒 DOM、单 scroll authority、基准 | P3/P4 | profiler + viewport 测试 |
| P6 双阶段验收 | 外部重启部署；新 Pudding 会话真实 smoke | P0–P5 | 新构建 hash、真实会话截图、日志与指标 |

施工顺序不可倒置。先修 UI 再让 React 猜 sequence，只会再次得到“轨迹块 + 正文块”或重复正文。

## 11. 测试矩阵

### 11.1 纯投影

- `T1 → R1 → Tool1 → T2 → R2 → Tool2 → Tool3 → T3` 输出 `T/A/T/A/T`。
- 同 `toolCallId` result 原位更新，group key 和顺序不变。
- `message.completed.reply` 不覆盖已有 TextBlock；无 delta 时只创建一个兜底 TextBlock。
- 乱序 replay、重复 eventId、Snapshot+Watch 得到相同结果。
- 缺 sequence fail-closed，不合成顺序。

### 11.2 披露策略

- 最新 Agent 回合最后 ActivityGroup 默认展开。
- 最终 TextBlock 到达后该组仍展开。
- 新 ActivityGroup 到达后旧组 closing、新组 open；过渡后旧成员 DOM 卸载。
- 新 Agent 回合获得组后，上一回合 owner 收起。
- 用户手动折叠最新组后，普通 delta 不重新打开。
- 用户手动展开历史组后，owner 转移不强制关闭。
- 首次 hydration 不批量播放动画。

### 11.3 展开内容

- reasoning 完整文本包含换行，DOM 无 ellipsis 样式、无二级 disclosure。
- 最新工具行全部可见，running/success/failure 与耗时/exit code 正确。
- 外层组自动展开不创建 `toolcall-in/out/presentation` DOM。
- 点击工具后才创建详情，长 OUT 默认有界预览。

### 11.4 性能与滚动

- append 新事件不重渲染已封闭 TextBlock。
- 折叠历史组无成员 DOM。
- hydration 三条以上消息时队列持续排空且并发不超过 2。
- 离屏 Agent 行不因组件初次挂载而批量加入 hydration 队列。
- canonical reasoning/tool/delegation 节点较多时，即使消息数少于 80 条也能按 render weight 启用虚拟化。
- 自动吸底、用户上滚、加载更早历史、折叠高度变化分别保持正确锚点。
- 正常流上下滚动时 `scrollHeight` 保持稳定，不依赖刷新恢复。
- 首屏消息可见后才启动余额、Goal 和会话推断辅助请求；关闭的任务看板、Checkpoint、历史搜索、开发面板与右键菜单无对应资源请求和 DOM。
- hover/focus 对非首屏入口做预取后，点击仍保持键盘可达且只挂载一次目标模块。
- reduced-motion 下无高度动画。

### 11.5 真实 smoke

至少执行一个包含三轮 reasoning/tool/text 的真实模型任务，并记录：

1. 网络事件 sequence。
2. 页面块序 `T/A/T/A/T/A`。
3. 默认仅最新行为组展开。
4. 最终正文出现后最新组仍展开。
5. 新调用开始时旧组柔和收起，新组展开。
6. 工具成功/失败状态点、耗时和详情懒加载。
7. 长卡滚动无明显掉帧和跳变。

## 12. 交付与回滚

- 设计完成不等于实现完成；源码存在、单测通过、构建通过、进程已启动、真实 smoke 是五个独立门禁。
- 内部 Agent 只能交付 `ready-for-external-deploy`；外部控制器必须重启到明确新构建；新 Pudding 会话再执行 `in-product-functional-complete` smoke。
- 临时 rollout flag 只能存在一个发布窗口；稳定后删除旧双路径和 flag，不长期维护兼容状态机。
- 回滚只回到“单卡最终正文 + 轨迹不可用提示”，不得回到会重复正文或伪造顺序的双区域实现。

## 13. 明确不做

- 不展示未由模型/API 明确披露的私有 chain-of-thought。
- 不把完整 Trajectory 审计页塞进消息卡。
- 不在每个历史消息中默认展开一个行为组；默认全局只展开当前 owner。
- 不因外层展开自动展开全部工具 IN/OUT。
- 不增加第二个消息滚动容器或组件级 scrollTop 写入。
- 不按 `toolName`、数组下标或时间戳猜测事件关系。
- 不以长期兼容层掩盖缺失 canonical sequence 的旧数据。

## 14. Flash 执行命令与交付清单

### 14.1 开工前

```powershell
Set-Location E:\github\AgentNetworkPlan\PuddingAgent
git status --short
git diff -- Source/PuddingPlatformAdmin/src/pages/chat Source/PuddingCore/Platform/AgentProjectionDtos.cs Source/PuddingPlatform/Services/AgentChat
```

- 记录开工前 dirty 文件；只在本方案列出的文件中增量施工。
- 先读根 `AGENTS.md`、`code_map.md`、相关子项目 `code_map.md` 和本 ADR。
- 不清理、reset、checkout 或覆盖其他 Goal/Storage/LLM/Runtime 改动。

### 14.2 前端定向测试

```powershell
Set-Location E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin
npm test -- --runInBand `
  src/pages/chat/projections/executionFlowProjector.test.ts `
  src/pages/chat/projections/turnContentBlocks.test.ts `
  src/pages/chat/projections/turnSurfaceStore.test.ts `
  src/pages/chat/hooks/__tests__/turnSurfaceStore.hydration.test.ts `
  src/pages/chat/components/execution-flow/TurnContentStream.test.tsx `
  src/pages/chat/components/execution-flow/ReasoningDisclosureRow.test.tsx `
  src/pages/chat/components/execution-flow/ToolCallRow.test.tsx `
  src/pages/chat/components/MessageRow.memo.test.ts `
  src/pages/chat/components/MessageList.test.tsx `
  src/pages/chat/viewport/useMessageViewportRuntime.test.tsx
npm run build
```

`npm run build` 已包含 `scripts/check-chat-bundle-budget.cjs`，不得用单独执行 `max build` 绕过 Chat 首载体积与模块归属门禁。

若仓库全量 `tsc` 存在基线错误，必须同时提供：

1. 完整命令和退出码；
2. 与本方案触及文件相关的错误过滤结果；
3. 证明新增文件没有引入新类型错误的定向证据；

不得只写“tsc 有历史问题”而不给证据。

### 14.3 后端定向验证

```powershell
Set-Location E:\github\AgentNetworkPlan\PuddingAgent
dotnet test Source\PuddingPlatformTests\PuddingPlatformTests.csproj --no-restore --nologo `
  --filter "FullyQualifiedName~TurnOutputChunkerPayloadOwnershipTests|FullyQualifiedName~AgentConversationProjection"
dotnet build Source\PuddingPlatform\PuddingPlatform.csproj --no-restore --nologo
dotnet build PuddingRuntime --no-restore
```

若 filter 名称与实际测试类不一致，Flash 必须先用 `rg` 定位真实类名并记录调整，不能静默跳过测试。

### 14.4 静态检查

```powershell
Set-Location E:\github\AgentNetworkPlan\PuddingAgent
git diff --check
git status --short
```

任务结束必须同步维护：

- `Docs/Features/Agent消息交错内容流与最新行为组披露完整实施方案.md` 的实施状态与证据；
- `Docs/07架构/93ADR-079Agent消息交错内容流与最新行为组披露ADR.md` 的状态（只有决策变化才改 ADR）；
- 根 `code_map.md`；
- `Source/PuddingPlatformAdmin/code_map.md`；
- 若改动后端合同，对应 `Source/PuddingCore/code_map.md`、`Source/PuddingPlatform/code_map.md`。

### 14.5 外部部署与真实验收

1. 内部开发 Agent 完成构建和定向测试，输出 `ready-for-external-deploy`，不得自行声明新代码已被当前 Pudding 进程加载。
2. 外部控制器停止 dev-up/Desktop 管理的旧 Core，确认无双 Core 访问同一 DataRoot。
3. 部署明确 hash 的新前端与 Core，重启 Desktop/Core，记录 PID、静态资源 hash 和 `/health`。
4. 在新 Pudding 会话发送一个至少三轮 reasoning/tool/text 的测试任务。
5. 浏览器 DOM 验证 TextBlock/ActivityGroup 顺序、唯一 owner、最终正文后仍展开、owner 转移动画、折叠后 DOM 卸载和工具详情懒加载。
6. 记录截图、事件窗口、失败日志、性能采样；通过后才可标记 `in-product-functional-complete`。
7. Desktop/Core 启动、重启、退出回收仍由外部控制器单独判定。

### 14.6 Definition of Done

只有以下全部为真，施工才算完成：

- canonical sequence、正文切段、toolCallId 配对和窗口合同通过测试；
- 单卡 DOM 呈现真实 `T/A/T/A` 顺序且正文只出现一次；
- 默认 owner 在会话内唯一；最终正文不会关闭它；新行为组/新回合才转移 owner；
- owner 转移柔和收起，结束后旧成员 DOM 为 0；
- 最新 reasoning 完整换行显示，工具状态可见，原始详情仍懒加载；
- 用户 override、焦点、reduced-motion 和屏幕阅读器语义通过；
- 历史 hydration 队列持续排空；切会话迟到响应被丢弃；
- 长消息 append 不重渲染历史 TextBlock，滚动无可复现跳变；
- 前端生产构建、相关 .NET 构建与定向测试通过；
- 新构建经外部部署，并由新 Pudding 会话完成真实 smoke；
- 文档、ADR、code_map 与实际实现一致，且未破坏无关 dirty worktree。
