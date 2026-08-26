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
12. 采用自适应渲染：少于 80 个 timeline row 时使用正常文档流；80-199 个 row 仅在全部为 compact 稳定短行时启用 virtualizer，包含 normal/rich/streaming 动态行时继续使用正常流；200 个及以上才强制启用 virtualizer。这样中等长度富文本会话不会暴露估高到实测之间的短暂覆盖窗口。
13. 历史前插是 viewport transaction：更新前捕获第一条可见 row 的稳定 id、像素 offset 和 scrollHeight，更新后由 runtime 恢复。正常文档流使用新增高度差恢复，虚拟模式使用 `itemId + offset` 恢复；组件不得绕过 runtime 直接加载。
14. 正常文档流必须保留消息行真实高度，不得用 `content-visibility` + `contain-intrinsic-size` 代替离屏行高度。Agent 行在流式、行为组展开/收起后会让浏览器 remembered size 失真，用户滚回时会造成 `scrollHeight` 大幅跳变。
15. “可见 Turn”由消息滚动容器上的 `IntersectionObserver` 判定，并允许一屏预取；组件挂载不等于可见。已水合 canonical 行为链的文本与结构成本必须进入 viewport render weight，避免 `block.content/processItems` 低估实际 DOM 后错误停留在正常流。

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
- 正常流中离屏 Agent 行不因 React 挂载而批量水合；只水合当前视口及预取区。
- canonical 行为链水合后，虚拟化决策能感知 reasoning/tool/delegation 节点的结构成本。
- 历史 prepend 后当前第一条可见消息不跳动。
- 同一动画帧内多个 scroll event 只触发一次布局读取。
- 所有 viewport row 使用真实 user/assistant message id；同一 canonical Turn 的多条消息不得复用 React/virtualizer key，高度缓存也必须按 message id 而非数组下标索引。
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

## 2026-08-09 性能收敛补充

- `processScroll` 在 `atBottom`、`nearTop`、`followMode` 均未变化时复用原 state，避免每个 scroll animation frame 都触发 React commit。
- 首屏贴底的 100ms 轮询在布局稳定 3 次且经过 500ms 后停止；`ResizeObserver` 继续处理真正迟到的图片或 Markdown 高度变化。
- 折叠的过程摘要不构建 rounds、display items 或 trace chips；用户展开后才计算。
- active run 明细固定返回最近 64 条可见思考/工具事件，同时返回基于全量无 payload 元数据计算的 `processSummary`，避免长任务把整个事件 payload 注入首屏。
- bootstrap 子代理事件是有界快照（最近 500 条），前端仍通过终态状态对账修正截断边界。
- Agent conversation 首屏窗口与现有历史分页统一为最近 20 条；分页后的本地历史只在和投影窗口存在重叠时向前合并，避免旧快照覆盖权威投影。
- Markdown/KaTeX/HTML parser/Prism 从 Chat 首始 chunk 拆出；`MessageItem` 先显示保留换行的纯文本，再异步升级为完整 Markdown，ResizeObserver 负责增强后的高度收敛。
- 仅开发调试使用的 Pro Components `SettingDrawer` 采用动态加载，生产主包不再静态包含其依赖树。
- 子代理运行检查器只在当前会话存在子代理卡片或用户显式打开时加载；摄像头输入和会话基准诊断 Drawer 也采用首次使用加载，关闭状态不预挂载到每个消息节点。
- 高频埋点、mark/measure 和事件缓冲收敛到轻量 `perfEventRuntime`；完整浏览器观察器、诊断快照和建议生成保留在异步 `debug` chunk，普通 Chat 首屏不执行也不解析。
- bundle 评估以 HTML 的全部同步 script 与当前路由首始 chunk 合计为准；仅把 React framework 从 `umi.js` 移到同步 `framework.js` 不算首载优化。

## 2026-08-10 消息行热路径收敛补充

- `MessageList` 是消息树聚合样式的唯一注册边界；它通过 `ChatMessageStyleProvider` 向消息行共享样式结果。`AgentMessageBubble`、`MessageItem`、`MessageActions`、`MessageProcessSummary`、头像和用户气泡等叶子组件不得再次调用会同时订阅全部样式域的 `useChatStyles`。
- `useChatStyles` 必须稳定复用合并后的 styles/value；样式域没有变化时不能因为父组件普通重渲染而发布新的 Context value，避免所有可见消息越过 memo 边界。
- 虚拟投影已经生成 `ChatMessageBlock`，`MessageList` 必须直接交给 `MessageRow`。不得把单个 block 重新包装为一元素 `ChatTurn[]` 再执行一次 `buildMessageBlocks`；旧 `MessageStream` 和 `MessageGroup` 兼容层已删除。
- `MessageRow` 的 memo 比较覆盖正文、状态、视觉制品、Agent 信息、过程事件、过程摘要、usage 和引用消息。等价的服务端投影对象重建不得提交历史行；活动行正文或正文前的 thinking/tool 变化必须触发更新。
- 每条 Agent 消息的完整操作栏在首次 hover 前不实例化；不可见时不保留 button/Tooltip DOM。首次激活后允许保留组件状态，以免朗读状态因指针离开被强制销毁。
- 构建门禁除聚焦 Jest 外，还必须执行生产 `max build`，并阻断 Chat 样式模块的循环依赖警告。

## 2026-08-26 Chat 首载边界补充

- 会话投影、当前 Agent 与消息视口仍是首屏关键路径；余额、Goal 和用于推断会话的 `/api/sessions` 属于辅助数据，统一推迟到首帧后的浏览器空闲期。旧 WebView2 不支持 `requestIdleCallback` 时使用不超过 250ms 的退化计时器，不能让辅助功能永久饥饿。
- 任务看板、Checkpoint 时间线、历史搜索、开发面板、子代理检查器和消息右键菜单必须保持独立动态模块；关闭状态不下载、不解析也不挂载。工具栏入口允许在 pointer hover 或 keyboard focus 时预取，以缩短首次点击等待。
- `npm run build` 必须在生产构建后执行 `scripts/check-chat-bundle-budget.cjs`。当前门禁要求 HTML 同步脚本不超过 1536 KiB、Chat 路由 chunk 不超过 480 KiB，并验证任务看板源码不得回流 `common-async`，Checkpoint、ContextMenu 与任务看板入口不得回流 Chat 首始 chunk。
- 体积门禁是回归保护，不替代运行时验收。部署新静态资源后仍需采集实际 WebView2 的请求瀑布、首屏时间、长任务和长消息连续滚动帧率。
