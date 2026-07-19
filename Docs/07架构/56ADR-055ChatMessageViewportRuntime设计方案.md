# ADR-055 Chat Message Viewport Runtime 设计方案

## 状态

Proposed

## 背景

当前聊天消息视图已经引入 `@tanstack/react-virtual`，但虚拟视图、消息投影、历史加载、发送后的滚动意图、贴底跟随和滚动按钮仍交织在 `MessageList.tsx` 与 `useChatState.ts` 中。

主要问题不是单点滚动 bug，而是控制权分裂：

- `MessageList.tsx` 同时承担消息投影、虚拟列表、滚动状态、贴底状态、历史加载入口和底部控件。
- `useChatState.ts` 直接监听 `messageListRef.current` 的滚动并触发历史加载，业务状态 hook 持有 DOM 滚动职责。
- `scrollToBottom` 同时使用 `virtualizer.scrollToIndex`、`scrollIntoView` 和直接写 `scrollTop`，多个滚动机制会竞争最终位置。
- 虚拟化粒度是合并后的 turns group，不是消息块级；历史 prepend 或 sub-agent 插入会改变 row key 和测量缓存。
- 发送消息只 append optimistic turn，没有向视图层声明“用户主动发送，需要跟随底部”的显式 intent。

## 决策

新增 `Chat Message Viewport Runtime` 作为消息视图的单一滚动控制层。它接管虚拟列表、滚动锚点、历史加载触发、贴底跟随和底部控件状态；`useChatState` 只保留数据加载和消息发送能力，不再监听 DOM scroll。

## 架构边界

```text
useChatState
  └─ 提供 turns / conversationView / loadMoreMessages / sendMessage

messageProjection
  └─ turns + conversationView + activeRun + subAgentCards -> VirtualMessageItem[]

useMessageViewportRuntime
  └─ virtualizer + scroll state + anchor restore + bottom follow + load-before intent

MessageViewport / MessageList
  └─ 渲染虚拟行、顶部加载器、底部控件和空状态
```

### MessageDataWindow

消息数据窗口只描述数据，不接触 DOM：

```ts
export interface MessageDataWindow {
  items: VirtualMessageItem[];
  sessionId?: string | null;
  agentId?: string;
  hasMoreBefore: boolean;
  loadingBefore: boolean;
  historyLoading: boolean;
}
```

### VirtualMessageItem

虚拟化粒度改为消息块级，而不是 turns group：

```ts
export type VirtualMessageItem =
  | {
      kind: 'message';
      id: string;
      createdAt: number;
      block: ChatMessageBlock;
      heightHint: 'compact' | 'normal' | 'rich' | 'streaming';
    }
  | {
      kind: 'subagent';
      id: string;
      createdAt: number;
      card: SubAgentCard;
      heightHint: 'compact' | 'normal' | 'rich';
    }
  | {
      kind: 'loader';
      id: string;
      createdAt: number;
      direction: 'before';
      heightHint: 'compact';
    };
```

稳定 id 规则：

- 用户消息：`message:user:${messageId}`
- Agent 消息：`message:agent:${messageId}`
- Active run：`run:${runId}`
- Sub-agent 卡片：`subagent:${subAgentId}`
- 顶部加载器：`loader:before:${sessionId}`

### MessageViewportRuntime

Runtime 是唯一滚动入口：

```ts
export type FollowMode = 'off' | 'auto' | 'pinned';

export type ScrollIntent =
  | { type: 'none' }
  | { type: 'user-send'; itemId: string; createdAt: number }
  | { type: 'manual-bottom'; behavior: ScrollBehavior }
  | { type: 'restore-anchor'; itemId: string; offset: number }
  | { type: 'load-before'; anchorItemId: string; anchorOffset: number };

export interface MessageViewportState {
  atBottom: boolean;
  nearTop: boolean;
  followMode: FollowMode;
  showBottomButton: boolean;
  anchorItemId?: string;
  pendingIntent: ScrollIntent;
}
```

状态机：

```text
IDLE
  -> USER_SCROLLING
  -> LOADING_BEFORE
  -> FOLLOWING_BOTTOM
  -> PINNED_BOTTOM
  -> RESTORING_ANCHOR
```

## 行为规则

1. 打开已有长会话不自动跳到底部。
2. 用户在中部阅读时，streaming 和图片/code block 延迟测量不抢滚动。
3. 用户发送新消息后，视图进入 `FOLLOWING_BOTTOM`，直到该消息的首个 assistant 输出稳定。
4. 用户点击贴底后进入 `PINNED_BOTTOM`，所有 streaming 和布局增长都跟随底部。
5. 历史加载由 viewport 发起 `onRequestLoadBefore(anchor)`，加载完成后按 `anchorItemId + offset` 恢复视口。
6. 所有滚动到底部操作只能调用 runtime 的 `scrollToBottom`，禁止同一路径同时调用 `scrollIntoView`、`scrollTop` 和 virtualizer API。
7. `useChatState` 不注册 message list 的 scroll listener。
8. `PINNED_BOTTOM` 只能由用户显式关闭；普通/程序化 `scroll`、`user-send` 和 streaming 更新都不能把它降级为 `auto/off`。
9. 底部跟随以滚动容器的实际 `scrollHeight - clientHeight` 为准；virtualizer 只负责 item 测量和 anchor 恢复。虚拟内容、Markdown、图片或工具面板延迟增高时，由 ResizeObserver 触发下一帧底部收敛。
10. 打开会话的初始 `followMode` 必须为 `off`；只有用户发送、显式回到底部或真实滚动确认已经位于底部后，才能进入自动跟随。
11. `scroll` 事件按 animation frame 合并；同一帧只允许读取一次 `scrollTop/scrollHeight/clientHeight` 并执行一次状态迁移。
12. 采用自适应渲染：少于 40 个 timeline row 时使用正常文档流，避免高 Markdown/tool row 的估高与实测差触发滚动校正；40 个及以上才启用 virtualizer。
13. 历史前插是 viewport transaction：更新前捕获第一条可见 row 的稳定 id、像素 offset 和 scrollHeight，更新后由 runtime 恢复。正常文档流使用新增高度差恢复，虚拟模式使用 `itemId + offset` 恢复；组件不得绕过 runtime 直接加载。

## 视觉与交互

- 底部控件属于消息 viewport 的 overlay layer，不使用相对浏览器窗口的 magic fixed offset。
- 默认只显示“回到底部”图标按钮；贴底为 pin toggle，状态通过 tooltip 和 active 样式表达。
- 滚动条使用稳定 gutter，避免出现/隐藏时挤压内容。
- 移动端底部控件避开 composer safe area，触控目标不小于 44px。
- `prefers-reduced-motion` 下所有底部滚动使用 `auto`，不使用 smooth。

## 迁移策略

分五阶段迁移：

1. 抽出 `messageProjection`，保持渲染行为不变。
2. 新增 `useMessageViewportRuntime`，并让 `MessageList` 只通过 runtime 滚动。
3. 移除 `useChatState` 中的 DOM scroll listener，历史加载改成 viewport callback。
4. 将虚拟化粒度从 turns group 改为 message block item。
5. 发送事务接入 scroll intent，让用户发送、手动回到底部、贴底跟随成为显式状态。

## 验收条件

- 1000 条消息下只渲染视口附近 rows，滚动无明显掉帧。
- 短会话中的超高 Markdown、表格和工具输出使用正常文档流，连续上下滚动时 `scrollHeight` 不因 row 进入视口而变化。
- 历史 prepend 后当前第一条可见消息不跳动。
- 同一动画帧内多个 scroll event 只触发一次布局读取。
- 打开已有长会话不自动贴底。
- 用户主动发送后自动跟随到底部。
- 用户手动向上阅读时 streaming 不抢滚动。
- 贴底模式开启时，streaming 和延迟测量持续保持底部。
- `useChatState.ts` 中不存在针对 message list DOM 的 scroll listener。
- `MessageList.tsx` 不再直接组合 `scrollToIndex + scrollIntoView + scrollTop`。

## 影响

正向影响：

- 消息视图行为可测试、可解释、可维护。
- 后续 ChatRuntime 状态拆分可以和 viewport runtime 对接。
- 用户体验从“偶发跳动”变成明确的阅读/跟随模式。

代价：

- 初期会引入新的 viewport runtime 抽象。
- 需要补充 JSDOM 单测和 Playwright 视图验收。
- 第一阶段必须保持现有视觉行为，避免架构迁移和 UI 重设计混在一起。
