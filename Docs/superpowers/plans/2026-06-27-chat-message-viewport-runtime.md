# Chat Message Viewport Runtime Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild chat message virtualization, history loading, send-time scroll intent, viewport scrolling, scrollbar behavior, and bottom anchoring around a single Message Viewport Runtime.

**Architecture:** Extract message projection into pure functions, then introduce a viewport runtime hook that owns virtualizer state, scroll state, anchor restoration, and bottom-follow behavior. `useChatState` keeps data and network responsibilities only; `MessageList` becomes a render shell around projected virtual items.

**Tech Stack:** React 19, TypeScript, `@tanstack/react-virtual`, Jest + Testing Library, existing Pudding chat components and `pnpm`.

---

## File Structure

- Create: `Source/PuddingPlatformAdmin/src/pages/chat/viewport/types.ts`
  - Shared viewport item, scroll intent, state, and anchor types.
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/viewport/messageProjection.ts`
  - Pure projection from `ChatTurn[]`, `AgentConversationView`, active run, and sub-agent cards to `VirtualMessageItem[]`.
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/viewport/messageProjection.test.ts`
  - Projection behavior and key stability tests.
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/viewport/useMessageViewportRuntime.ts`
  - Runtime hook wrapping `useVirtualizer`, scroll state, anchor restore, bottom follow, and load-before callback.
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/viewport/useMessageViewportRuntime.test.tsx`
  - JSDOM tests for scroll intent, load-before, anchor restoration, and pinned bottom behavior.
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageList.tsx`
  - Remove inline projection and split scroll logic. Render `VirtualMessageItem[]` through viewport runtime.
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageStream.tsx`
  - Keep as compatibility renderer initially, then narrow to rendering already-built `ChatMessageBlock[]` if needed.
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.ts`
  - Remove DOM scroll listener. Add explicit send scroll intent output or callback contract.
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/ChatMain.tsx`
  - Pass viewport intent/callback props from state to message list.
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageList.test.tsx`
  - Replace patch-level scroll tests with runtime behavior tests.

## Public Interfaces

### Viewport Types

Add this shape to `viewport/types.ts`:

```ts
import type { ChatMessageBlock, SubAgentCard } from '../types';

export type VirtualMessageHeightHint = 'compact' | 'normal' | 'rich' | 'streaming';

export type VirtualMessageItem =
  | {
      kind: 'message';
      id: string;
      createdAt: number;
      block: ChatMessageBlock;
      heightHint: VirtualMessageHeightHint;
    }
  | {
      kind: 'subagent';
      id: string;
      createdAt: number;
      card: SubAgentCard;
      heightHint: VirtualMessageHeightHint;
    }
  | {
      kind: 'loader';
      id: string;
      createdAt: number;
      direction: 'before';
      heightHint: 'compact';
    };

export type FollowMode = 'off' | 'auto' | 'pinned';

export type ScrollIntent =
  | { type: 'none' }
  | { type: 'user-send'; itemId: string; createdAt: number }
  | { type: 'manual-bottom'; behavior: ScrollBehavior }
  | { type: 'restore-anchor'; itemId: string; offset: number }
  | { type: 'load-before'; anchorItemId: string; anchorOffset: number };

export interface ViewportAnchor {
  itemId: string;
  offset: number;
}

export interface MessageViewportState {
  atBottom: boolean;
  nearTop: boolean;
  followMode: FollowMode;
  showBottomButton: boolean;
  anchorItemId?: string;
  pendingIntent: ScrollIntent;
}

export interface LoadBeforeRequest {
  anchor: ViewportAnchor;
}
```

### Runtime Hook

`useMessageViewportRuntime` should expose one scrolling API:

```ts
export interface MessageViewportRuntime {
  parentRef: React.RefObject<HTMLDivElement | null>;
  virtualizer: ReturnType<typeof useVirtualizer<HTMLDivElement, HTMLDivElement>>;
  virtualRows: ReturnType<
    ReturnType<typeof useVirtualizer<HTMLDivElement, HTMLDivElement>>['getVirtualItems']
  >;
  totalSize: number;
  state: MessageViewportState;
  onScroll: () => void;
  scrollToBottom: (options: { behavior: ScrollBehavior; reason: string }) => void;
  setPinnedBottom: (enabled: boolean) => void;
  applyIntent: (intent: ScrollIntent) => void;
}
```

## Task 1: Add Viewport Types And Projection Tests

**Files:**
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/viewport/types.ts`
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/viewport/messageProjection.test.ts`
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/viewport/messageProjection.ts`

- [ ] **Step 1: Create the failing projection tests**

Add tests that prove item keys are message-block level, not grouped-turn level:

```ts
import { buildVirtualMessageItems } from './messageProjection';
import type { ChatTurn, SubAgentCardMap } from '../types';

const makeTurn = (turnId: string, timestamp: number, answer = 'answer'): ChatTurn => ({
  turnId,
  source: {
    sourceId: 'agent-1',
    sourceType: 'agent',
    displayName: 'Agent',
    avatarColor: '#7c3aed',
  },
  userMessage: {
    id: `user-${turnId}`,
    text: `Question ${turnId}`,
    timestamp,
    status: 'success',
  },
  assistant: {
    id: `assistant-${turnId}`,
    status: 'success',
    timelineItems: [],
    answerMarkdown: answer,
    isStreaming: false,
    renderMode: 'structured',
  },
});

describe('buildVirtualMessageItems', () => {
  it('creates stable message-level ids for user and agent blocks', () => {
    const result = buildVirtualMessageItems({
      turns: [makeTurn('t1', 1000)],
      agentName: 'Agent',
    });

    expect(result.items.map((item) => item.id)).toEqual([
      'message:user:user-t1',
      'message:agent:assistant-t1',
    ]);
  });

  it('adds loader before messages when older history exists', () => {
    const result = buildVirtualMessageItems({
      turns: [makeTurn('t1', 1000)],
      agentName: 'Agent',
      sessionId: 'session-1',
      hasMoreBefore: true,
    });

    expect(result.items[0]).toMatchObject({
      kind: 'loader',
      id: 'loader:before:session-1',
      direction: 'before',
    });
  });

  it('keeps sub-agent cards as independent virtual items ordered by time', () => {
    const subAgentCards: SubAgentCardMap = {
      sub1: {
        turnId: 'sub1',
        parentTurnId: 't1',
        parentMessageId: 'assistant-t1',
        status: 'running',
        title: 'Sub task',
        summary: 'Working',
        spawnedAt: 1500,
      },
    };

    const result = buildVirtualMessageItems({
      turns: [makeTurn('t1', 1000)],
      agentName: 'Agent',
      subAgentCards,
    });

    expect(result.items.map((item) => item.id)).toEqual([
      'message:user:user-t1',
      'message:agent:assistant-t1',
      'subagent:sub1',
    ]);
  });
});
```

- [ ] **Step 2: Run the tests and verify they fail**

Run:

```powershell
cd Source\PuddingPlatformAdmin
pnpm exec jest --runTestsByPath src/pages/chat/viewport/messageProjection.test.ts --runInBand
```

Expected: fail because `viewport/messageProjection.ts` does not exist or `buildVirtualMessageItems` is not implemented.

- [ ] **Step 3: Implement projection types and minimal projection**

Implement `types.ts` with the interfaces in the Public Interfaces section.

Implement `messageProjection.ts` using the existing `buildMessageBlocks` from `../types` instead of copying it:

```ts
import type { AgentConversationView } from '../client/types';
import type { ChatTurn, SubAgentCardMap } from '../types';
import { buildMessageBlocks } from '../types';
import type { VirtualMessageItem, VirtualMessageHeightHint } from './types';

export interface BuildVirtualMessageItemsInput {
  turns: ChatTurn[];
  conversationView?: AgentConversationView | null;
  subAgentCards?: SubAgentCardMap;
  agentName: string;
  sessionId?: string | null;
  hasMoreBefore?: boolean;
  currentUser?: { name?: string; avatar?: string };
}

export interface BuildVirtualMessageItemsOutput {
  items: VirtualMessageItem[];
  firstMessageItemId?: string;
  lastMessageItemId?: string;
  activeItemId?: string;
}

const getSubAgentCreatedAt = (card: SubAgentCardMap[string]): number =>
  card.spawnedAt ?? card.completedAt ?? 0;

const getHeightHint = (content: string, streaming?: boolean): VirtualMessageHeightHint => {
  if (streaming) return 'streaming';
  if (content.length > 1800 || content.includes('```')) return 'rich';
  if (content.length < 120) return 'compact';
  return 'normal';
};

export function buildVirtualMessageItems(
  input: BuildVirtualMessageItemsInput,
): BuildVirtualMessageItemsOutput {
  const items: VirtualMessageItem[] = [];

  if (input.hasMoreBefore) {
    items.push({
      kind: 'loader',
      id: `loader:before:${input.sessionId ?? '__no_session__'}`,
      createdAt: Number.NEGATIVE_INFINITY,
      direction: 'before',
      heightHint: 'compact',
    });
  }

  const blocks = buildMessageBlocks(input.turns, input.agentName, input.currentUser);
  for (const block of blocks) {
    const prefix = block.role === 'user' ? 'message:user' : 'message:agent';
    items.push({
      kind: 'message',
      id: `${prefix}:${block.id.includes(':') ? block.id : block.id}`,
      createdAt: block.createdAt,
      block,
      heightHint: getHeightHint(block.content, block.isStreaming),
    });
  }

  for (const card of Object.values(input.subAgentCards ?? {})) {
    items.push({
      kind: 'subagent',
      id: `subagent:${card.turnId}`,
      createdAt: getSubAgentCreatedAt(card),
      card,
      heightHint: card.output && card.output.length > 1200 ? 'rich' : 'normal',
    });
  }

  items.sort((a, b) => {
    const byTime = a.createdAt - b.createdAt;
    return byTime !== 0 ? byTime : a.id.localeCompare(b.id);
  });

  const messageItems = items.filter((item) => item.kind === 'message');
  const active = messageItems.find(
    (item) => item.kind === 'message' && item.block.isStreaming,
  );

  return {
    items,
    firstMessageItemId: messageItems[0]?.id,
    lastMessageItemId: messageItems[messageItems.length - 1]?.id,
    activeItemId: active?.id,
  };
}
```

- [ ] **Step 4: Run projection tests**

Run:

```powershell
cd Source\PuddingPlatformAdmin
pnpm exec jest --runTestsByPath src/pages/chat/viewport/messageProjection.test.ts --runInBand
```

Expected: pass.

- [ ] **Step 5: Commit projection extraction**

```powershell
git add Source/PuddingPlatformAdmin/src/pages/chat/viewport/types.ts Source/PuddingPlatformAdmin/src/pages/chat/viewport/messageProjection.ts Source/PuddingPlatformAdmin/src/pages/chat/viewport/messageProjection.test.ts
git commit -m "feat(chat): add message viewport projection"
```

## Task 2: Add Viewport Runtime Hook

**Files:**
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/viewport/useMessageViewportRuntime.ts`
- Create: `Source/PuddingPlatformAdmin/src/pages/chat/viewport/useMessageViewportRuntime.test.tsx`

- [ ] **Step 1: Write runtime tests**

Create tests for bottom behavior and load-before intent:

```tsx
import { renderHook, act } from '@testing-library/react';
import { useMessageViewportRuntime } from './useMessageViewportRuntime';
import type { VirtualMessageItem } from './types';

const makeItem = (id: string, createdAt: number): VirtualMessageItem => ({
  kind: 'message',
  id,
  createdAt,
  heightHint: 'normal',
  block: {
    id,
    turnId: id,
    role: 'agent',
    content: 'hello',
    status: 'success',
    createdAt,
  },
});

describe('useMessageViewportRuntime', () => {
  it('requests older history with the first visible item anchor', () => {
    const onRequestLoadBefore = jest.fn();
    const items = [makeItem('m1', 1), makeItem('m2', 2)];
    const { result } = renderHook(() =>
      useMessageViewportRuntime({
        items,
        hasMoreBefore: true,
        loadingBefore: false,
        onRequestLoadBefore,
      }),
    );

    const node = document.createElement('div');
    Object.defineProperty(node, 'scrollTop', { value: 0, writable: true });
    Object.defineProperty(node, 'clientHeight', { value: 400, writable: true });
    Object.defineProperty(node, 'scrollHeight', { value: 1200, writable: true });
    result.current.parentRef.current = node;

    act(() => {
      result.current.onScroll();
    });

    expect(onRequestLoadBefore).toHaveBeenCalledWith({
      anchor: { itemId: 'm1', offset: 0 },
    });
  });

  it('manual bottom intent marks viewport as bottom-following', () => {
    const items = [makeItem('m1', 1), makeItem('m2', 2)];
    const { result } = renderHook(() =>
      useMessageViewportRuntime({
        items,
        hasMoreBefore: false,
        loadingBefore: false,
        onRequestLoadBefore: jest.fn(),
      }),
    );

    act(() => {
      result.current.scrollToBottom({ behavior: 'auto', reason: 'test' });
    });

    expect(result.current.state.atBottom).toBe(true);
    expect(result.current.state.showBottomButton).toBe(false);
  });

  it('pinned bottom remains enabled until explicitly disabled', () => {
    const items = [makeItem('m1', 1)];
    const { result } = renderHook(() =>
      useMessageViewportRuntime({
        items,
        hasMoreBefore: false,
        loadingBefore: false,
        onRequestLoadBefore: jest.fn(),
      }),
    );

    act(() => {
      result.current.setPinnedBottom(true);
    });
    expect(result.current.state.followMode).toBe('pinned');

    act(() => {
      result.current.setPinnedBottom(false);
    });
    expect(result.current.state.followMode).toBe('off');
  });
});
```

- [ ] **Step 2: Run runtime tests and verify they fail**

Run:

```powershell
cd Source\PuddingPlatformAdmin
pnpm exec jest --runTestsByPath src/pages/chat/viewport/useMessageViewportRuntime.test.tsx --runInBand
```

Expected: fail because hook is missing.

- [ ] **Step 3: Implement runtime hook**

Implement a first version that has a single bottom scroll path and emits load-before requests:

```ts
import { useVirtualizer } from '@tanstack/react-virtual';
import React, { useCallback, useMemo, useRef, useState } from 'react';
import type {
  LoadBeforeRequest,
  MessageViewportState,
  ScrollIntent,
  VirtualMessageItem,
} from './types';

interface UseMessageViewportRuntimeOptions {
  items: VirtualMessageItem[];
  hasMoreBefore: boolean;
  loadingBefore: boolean;
  onRequestLoadBefore: (request: LoadBeforeRequest) => void;
}

const NEAR_TOP_PX = 64;
const BOTTOM_THRESHOLD_PX = 80;

const initialState: MessageViewportState = {
  atBottom: true,
  nearTop: false,
  followMode: 'off',
  showBottomButton: false,
  pendingIntent: { type: 'none' },
};

export function useMessageViewportRuntime(options: UseMessageViewportRuntimeOptions) {
  const parentRef = useRef<HTMLDivElement | null>(null);
  const [state, setState] = useState<MessageViewportState>(initialState);
  const lastLoadAnchorRef = useRef<string | null>(null);

  const virtualizer = useVirtualizer({
    count: options.items.length,
    getScrollElement: () => parentRef.current,
    estimateSize: (index) => {
      const hint = options.items[index]?.heightHint;
      if (hint === 'compact') return 96;
      if (hint === 'rich') return 520;
      if (hint === 'streaming') return 320;
      return 220;
    },
    overscan: 6,
    getItemKey: (index) => options.items[index]?.id ?? index,
  });

  const virtualRows = virtualizer.getVirtualItems();
  const totalSize = virtualizer.getTotalSize();

  const readScroll = useCallback(() => {
    const el = parentRef.current;
    if (!el) {
      return { nearTop: false, atBottom: true, scrollTop: 0 };
    }
    const nearTop = el.scrollTop <= NEAR_TOP_PX;
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight <= BOTTOM_THRESHOLD_PX;
    return { nearTop, atBottom, scrollTop: el.scrollTop };
  }, []);

  const requestLoadBefore = useCallback(() => {
    if (!options.hasMoreBefore || options.loadingBefore || options.items.length === 0) return;
    const anchorItem = options.items.find((item) => item.kind !== 'loader') ?? options.items[0];
    if (!anchorItem || lastLoadAnchorRef.current === anchorItem.id) return;
    lastLoadAnchorRef.current = anchorItem.id;
    options.onRequestLoadBefore({ anchor: { itemId: anchorItem.id, offset: 0 } });
  }, [options]);

  const onScroll = useCallback(() => {
    const next = readScroll();
    if (next.nearTop) requestLoadBefore();
    setState((current) => ({
      ...current,
      atBottom: next.atBottom,
      nearTop: next.nearTop,
      showBottomButton: !next.atBottom,
      followMode: current.followMode === 'pinned' ? 'pinned' : next.atBottom ? 'auto' : 'off',
    }));
  }, [readScroll, requestLoadBefore]);

  const scrollToBottom = useCallback(
    ({ behavior }: { behavior: ScrollBehavior; reason: string }) => {
      if (options.items.length === 0) return;
      virtualizer.scrollToIndex(options.items.length - 1, {
        align: 'end',
        behavior,
      });
      setState((current) => ({
        ...current,
        atBottom: true,
        nearTop: false,
        showBottomButton: false,
        followMode: current.followMode === 'pinned' ? 'pinned' : 'auto',
        pendingIntent: { type: 'none' },
      }));
    },
    [options.items.length, virtualizer],
  );

  const setPinnedBottom = useCallback(
    (enabled: boolean) => {
      setState((current) => ({
        ...current,
        followMode: enabled ? 'pinned' : 'off',
        atBottom: enabled ? true : current.atBottom,
        showBottomButton: enabled ? false : current.showBottomButton,
      }));
      if (enabled) {
        scrollToBottom({ behavior: 'auto', reason: 'pin-bottom' });
      }
    },
    [scrollToBottom],
  );

  const applyIntent = useCallback(
    (intent: ScrollIntent) => {
      setState((current) => ({ ...current, pendingIntent: intent }));
      if (intent.type === 'manual-bottom') {
        scrollToBottom({ behavior: intent.behavior, reason: 'manual-bottom' });
      }
      if (intent.type === 'user-send') {
        scrollToBottom({ behavior: 'auto', reason: 'user-send' });
      }
    },
    [scrollToBottom],
  );

  return useMemo(
    () => ({
      parentRef,
      virtualizer,
      virtualRows,
      totalSize,
      state,
      onScroll,
      scrollToBottom,
      setPinnedBottom,
      applyIntent,
    }),
    [applyIntent, onScroll, scrollToBottom, setPinnedBottom, state, totalSize, virtualRows, virtualizer],
  );
}
```

- [ ] **Step 4: Run runtime tests**

Run:

```powershell
cd Source\PuddingPlatformAdmin
pnpm exec jest --runTestsByPath src/pages/chat/viewport/useMessageViewportRuntime.test.tsx --runInBand
```

Expected: pass.

- [ ] **Step 5: Commit runtime hook**

```powershell
git add Source/PuddingPlatformAdmin/src/pages/chat/viewport/useMessageViewportRuntime.ts Source/PuddingPlatformAdmin/src/pages/chat/viewport/useMessageViewportRuntime.test.tsx
git commit -m "feat(chat): add message viewport runtime"
```

## Task 3: Wire MessageList To Projection And Runtime

**Files:**
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageList.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageList.test.tsx`

- [ ] **Step 1: Add integration tests for single scroll path**

In `MessageList.test.tsx`, add a test that mocks `scrollIntoView` and verifies the bottom button does not call it:

```tsx
it('uses viewport runtime for bottom scrolling without scrollIntoView fallback', () => {
  const originalScrollIntoView = HTMLElement.prototype.scrollIntoView;
  const scrollIntoView = jest.fn();
  HTMLElement.prototype.scrollIntoView = scrollIntoView;

  renderMessageList({
    turns: [makeTurn('t1', 'hello', 'answer')],
    messageListRef: React.createRef<HTMLDivElement>(),
    listEndRef: React.createRef<HTMLDivElement>(),
  });

  fireEvent.click(screen.getByRole('button', { name: '回到底部' }));

  expect(scrollIntoView).not.toHaveBeenCalled();
  HTMLElement.prototype.scrollIntoView = originalScrollIntoView;
});
```

- [ ] **Step 2: Run the integration test and verify it fails or is not applicable**

Run:

```powershell
cd Source\PuddingPlatformAdmin
pnpm exec jest --runTestsByPath src/pages/chat/components/MessageList.test.tsx --runInBand
```

Expected before wiring: either fail because current code calls `scrollIntoView`, or fail because the queried button behavior still uses old conditions.

- [ ] **Step 3: Replace inline projection with `buildVirtualMessageItems`**

In `MessageList.tsx`:

- Remove local `buildChronologicalRenderItems` usage.
- Keep `buildProjectedTurns`, `mergePendingLocalTurns`, and `mergeActiveRunIntoTurns` temporarily if they are still required to produce `visibleTurns`.
- Build virtual items with:

```ts
const projection = useMemo(
  () =>
    buildVirtualMessageItems({
      turns: visibleTurns,
      subAgentCards,
      agentName: selectedAgent?.name || 'Pudding',
      sessionId,
      hasMoreBefore: hasMoreMessages,
      currentUser,
    }),
  [visibleTurns, subAgentCards, selectedAgent?.name, sessionId, hasMoreMessages, currentUser],
);

const viewport = useMessageViewportRuntime({
  items: projection.items,
  hasMoreBefore: hasMoreMessages,
  loadingBefore: loadingMore,
  onRequestLoadBefore: () => onLoadMore(),
});
```

- [ ] **Step 4: Replace render loop with viewport rows**

Render with one virtual row per `VirtualMessageItem`:

```tsx
<div
  className={styles.messageList}
  ref={(node) => {
    viewport.parentRef.current = node;
    if (typeof messageListRef === 'object') {
      messageListRef.current = node;
    }
  }}
  onScroll={viewport.onScroll}
  data-testid="chat-message-list"
>
  <div style={{ height: `${viewport.totalSize}px`, width: '100%', position: 'relative' }}>
    {viewport.virtualRows.map((virtualRow) => {
      const item = projection.items[virtualRow.index];
      if (!item) return null;
      return (
        <div
          key={item.id}
          data-index={virtualRow.index}
          ref={viewport.virtualizer.measureElement}
          style={{
            position: 'absolute',
            top: 0,
            left: 0,
            width: '100%',
            transform: `translateY(${virtualRow.start}px)`,
          }}
        >
          {item.kind === 'loader' ? (
            <TopHistoryLoader loading={loadingMore} onClick={onLoadMore} />
          ) : item.kind === 'message' ? (
            <MessageRow
              block={item.block}
              sessionId={sessionId}
              defaultAvatarUrl={selectedAgent?.avatarUrl}
              formatTime={formatTime}
              onContextMenu={onContextMenu}
              onRerunTurn={onRerunTurn}
              onPinTurn={onPinTurn}
              onDeleteTurn={onDeleteTurn}
            />
          ) : (
            <SubAgentCard card={item.card} />
          )}
        </div>
      );
    })}
  </div>
</div>
```

- [ ] **Step 5: Replace bottom controls**

Use runtime state:

```tsx
<div data-testid="chat-bottom-scroll-controls" className={styles.messageViewportControls}>
  <Tooltip title={viewport.state.followMode === 'pinned' ? '取消贴底跟随' : '开启贴底跟随'}>
    <Button
      type={viewport.state.followMode === 'pinned' ? 'primary' : 'default'}
      icon={<PushpinOutlined />}
      onClick={() => viewport.setPinnedBottom(viewport.state.followMode !== 'pinned')}
      aria-label={viewport.state.followMode === 'pinned' ? '取消贴底跟随' : '开启贴底跟随'}
    />
  </Tooltip>
  {viewport.state.showBottomButton && (
    <Tooltip title="回到底部">
      <Button
        type="default"
        icon={<ArrowDownOutlined />}
        onClick={() => viewport.scrollToBottom({ behavior: 'smooth', reason: 'manual-bottom' })}
        aria-label="回到底部"
      />
    </Tooltip>
  )}
</div>
```

- [ ] **Step 6: Run MessageList tests**

Run:

```powershell
cd Source\PuddingPlatformAdmin
pnpm exec jest --runTestsByPath src/pages/chat/components/MessageList.test.tsx src/pages/chat/viewport/messageProjection.test.ts src/pages/chat/viewport/useMessageViewportRuntime.test.tsx --runInBand
```

Expected: pass after updating tests that asserted old grouped-row behavior.

- [ ] **Step 7: Commit MessageList wiring**

```powershell
git add Source/PuddingPlatformAdmin/src/pages/chat/components/MessageList.tsx Source/PuddingPlatformAdmin/src/pages/chat/components/MessageList.test.tsx
git commit -m "refactor(chat): route message list through viewport runtime"
```

## Task 4: Remove DOM Scroll Listener From useChatState

**Files:**
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.ts`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.selection.test.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageList.tsx`

- [ ] **Step 1: Add a test that useChatState does not attach scroll listener**

In `useChatState.selection.test.tsx`, spy on `addEventListener` for a mock message list node and assert no direct scroll listener is registered by the hook:

```tsx
it('does not attach message list scroll listeners from useChatState', async () => {
  const addEventListener = jest.spyOn(HTMLDivElement.prototype, 'addEventListener');

  const { result } = renderUseChatState();
  const node = document.createElement('div');
  result.current.messageListRef.current = node;

  await act(async () => {
    await Promise.resolve();
  });

  const scrollRegistrations = addEventListener.mock.calls.filter(
    ([eventName]) => eventName === 'scroll',
  );
  expect(scrollRegistrations).toHaveLength(0);

  addEventListener.mockRestore();
});
```

- [ ] **Step 2: Run the test and verify it fails on current code**

Run:

```powershell
cd Source\PuddingPlatformAdmin
pnpm exec jest --runTestsByPath src/pages/chat/hooks/useChatState.selection.test.tsx --runInBand
```

Expected: fail while `useChatState.ts` still has the scroll listener effect.

- [ ] **Step 3: Remove the scroll effect from useChatState**

Delete the effect currently shaped like:

```ts
useEffect(() => {
  const el = messageListRef.current;
  if (!el) return;
  const handleScroll = () => {
    if (el.scrollTop < 50 && hasMoreMessages && !loadingMore) {
      loadMoreMessages();
    }
  };
  el.addEventListener('scroll', handleScroll, { passive: true });
  return () => el.removeEventListener('scroll', handleScroll);
}, [hasMoreMessages, loadingMore, loadMoreMessages]);
```

Keep `loadMoreMessages` public in the hook return value.

- [ ] **Step 4: Ensure MessageList triggers history loading**

The viewport runtime callback from Task 3 should be the only top-loading trigger:

```ts
const viewport = useMessageViewportRuntime({
  items: projection.items,
  hasMoreBefore: hasMoreMessages,
  loadingBefore: loadingMore,
  onRequestLoadBefore: () => onLoadMore(),
});
```

- [ ] **Step 5: Run targeted tests**

Run:

```powershell
cd Source\PuddingPlatformAdmin
pnpm exec jest --runTestsByPath src/pages/chat/hooks/useChatState.selection.test.tsx src/pages/chat/components/MessageList.test.tsx --runInBand
```

Expected: pass.

- [ ] **Step 6: Commit scroll ownership cleanup**

```powershell
git add Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.ts Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.selection.test.tsx Source/PuddingPlatformAdmin/src/pages/chat/components/MessageList.tsx
git commit -m "refactor(chat): move history scroll trigger to viewport runtime"
```

## Task 5: Add Send-Time Scroll Intent

**Files:**
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.ts`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/ChatMain.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageList.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.recovery.test.ts`

- [ ] **Step 1: Add `scrollIntent` to chat state return type**

Add this field to the useChatState return interface:

```ts
viewportScrollIntent: ScrollIntent;
clearViewportScrollIntent: () => void;
```

Import `ScrollIntent` from `../viewport/types`.

- [ ] **Step 2: Add a send intent test**

In `useChatState.recovery.test.ts`, assert sending creates a `user-send` intent:

```ts
it('emits user-send viewport scroll intent after optimistic append', async () => {
  const { result } = renderUseChatStateWithAgent();

  await act(async () => {
    await result.current.sendMessage('hello');
  });

  expect(result.current.viewportScrollIntent).toMatchObject({
    type: 'user-send',
  });
});
```

- [ ] **Step 3: Run the test and verify it fails**

Run:

```powershell
cd Source\PuddingPlatformAdmin
pnpm exec jest --runTestsByPath src/pages/chat/hooks/useChatState.recovery.test.ts --runInBand
```

Expected: fail because intent is not exposed.

- [ ] **Step 4: Implement intent in useChatState**

Add state:

```ts
const [viewportScrollIntent, setViewportScrollIntent] = useState<ScrollIntent>({ type: 'none' });

const clearViewportScrollIntent = useCallback(() => {
  setViewportScrollIntent({ type: 'none' });
}, []);
```

After optimistic turn append:

```ts
setViewportScrollIntent({
  type: 'user-send',
  itemId: `message:user:${optimisticTurn.userMessage.id}`,
  createdAt: now,
});
```

Return both fields from `useChatState`.

- [ ] **Step 5: Apply intent in MessageList**

Add props:

```ts
viewportScrollIntent: ScrollIntent;
onViewportScrollIntentHandled: () => void;
```

In `MessageList.tsx`:

```ts
useEffect(() => {
  if (viewportScrollIntent.type === 'none') return;
  viewport.applyIntent(viewportScrollIntent);
  onViewportScrollIntentHandled();
}, [viewport, viewportScrollIntent, onViewportScrollIntentHandled]);
```

- [ ] **Step 6: Pass props through ChatMain**

From `ChatMain.tsx`, pass:

```tsx
viewportScrollIntent={viewportScrollIntent}
onViewportScrollIntentHandled={clearViewportScrollIntent}
```

- [ ] **Step 7: Run targeted tests**

Run:

```powershell
cd Source\PuddingPlatformAdmin
pnpm exec jest --runTestsByPath src/pages/chat/hooks/useChatState.recovery.test.ts src/pages/chat/components/ChatMain.test.tsx src/pages/chat/components/MessageList.test.tsx --runInBand
```

Expected: pass.

- [ ] **Step 8: Commit send intent**

```powershell
git add Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.ts Source/PuddingPlatformAdmin/src/pages/chat/components/ChatMain.tsx Source/PuddingPlatformAdmin/src/pages/chat/components/MessageList.tsx Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.recovery.test.ts
git commit -m "feat(chat): add explicit send scroll intent"
```

## Task 6: Anchor Restoration For History Prepend

**Files:**
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/viewport/useMessageViewportRuntime.ts`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/viewport/useMessageViewportRuntime.test.tsx`

- [ ] **Step 1: Add anchor restoration test**

Add:

```tsx
it('restores anchor after older items are prepended', () => {
  const onRequestLoadBefore = jest.fn();
  const initial = [makeItem('m3', 3), makeItem('m4', 4)];
  const { result, rerender } = renderHook(
    ({ items }) =>
      useMessageViewportRuntime({
        items,
        hasMoreBefore: true,
        loadingBefore: false,
        onRequestLoadBefore,
      }),
    { initialProps: { items: initial } },
  );

  act(() => {
    result.current.applyIntent({
      type: 'restore-anchor',
      itemId: 'm3',
      offset: 12,
    });
  });

  rerender({ items: [makeItem('m1', 1), makeItem('m2', 2), ...initial] });

  expect(result.current.state.anchorItemId).toBe('m3');
});
```

- [ ] **Step 2: Run runtime tests and verify failure**

Run:

```powershell
cd Source\PuddingPlatformAdmin
pnpm exec jest --runTestsByPath src/pages/chat/viewport/useMessageViewportRuntime.test.tsx --runInBand
```

Expected: fail until anchor state is tracked.

- [ ] **Step 3: Implement anchor state**

In `applyIntent`, handle restore anchor:

```ts
if (intent.type === 'restore-anchor') {
  setState((current) => ({
    ...current,
    anchorItemId: intent.itemId,
    pendingIntent: intent,
    followMode: 'off',
  }));
  const index = options.items.findIndex((item) => item.id === intent.itemId);
  if (index >= 0) {
    virtualizer.scrollToIndex(index, { align: 'start', behavior: 'auto' });
  }
}
```

After items change, if pending intent is `restore-anchor`, find the anchor index and keep it aligned. Use a layout effect:

```ts
React.useLayoutEffect(() => {
  if (state.pendingIntent.type !== 'restore-anchor') return;
  const index = options.items.findIndex((item) => item.id === state.pendingIntent.itemId);
  if (index < 0) return;
  virtualizer.scrollToIndex(index, { align: 'start', behavior: 'auto' });
}, [options.items, state.pendingIntent, virtualizer]);
```

- [ ] **Step 4: Run runtime tests**

Run:

```powershell
cd Source\PuddingPlatformAdmin
pnpm exec jest --runTestsByPath src/pages/chat/viewport/useMessageViewportRuntime.test.tsx --runInBand
```

Expected: pass.

- [ ] **Step 5: Commit anchor restoration**

```powershell
git add Source/PuddingPlatformAdmin/src/pages/chat/viewport/useMessageViewportRuntime.ts Source/PuddingPlatformAdmin/src/pages/chat/viewport/useMessageViewportRuntime.test.tsx
git commit -m "feat(chat): restore viewport anchor after history prepend"
```

## Task 7: Style Bottom Controls And Scrollbar

**Files:**
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/styles.ts`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageList.tsx`
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageList.test.tsx`

- [ ] **Step 1: Add style assertions**

In `MessageList.test.tsx`, assert controls are inside message list rather than fixed to browser viewport:

```tsx
it('renders bottom controls as a message viewport overlay', () => {
  renderMessageList({
    turns: [makeTurn('t1', 'hello', 'answer')],
    messageListRef: React.createRef<HTMLDivElement>(),
    listEndRef: React.createRef<HTMLDivElement>(),
  });

  const controls = screen.getByTestId('chat-bottom-scroll-controls');
  expect(controls.className).toContain('messageViewportControls');
});
```

- [ ] **Step 2: Add styles**

In `styles.ts`, add:

```ts
messageViewportControls: {
  position: 'sticky',
  bottom: 16,
  display: 'flex',
  justifyContent: 'flex-end',
  gap: 8,
  paddingInline: 16,
  pointerEvents: 'none',
  zIndex: 20,
},
messageViewportControlButton: {
  pointerEvents: 'auto',
  width: 40,
  height: 40,
  borderRadius: 8,
},
messageViewportScrollArea: {
  scrollbarGutter: 'stable',
  overscrollBehavior: 'contain',
  scrollbarWidth: 'thin',
},
```

Apply `styles.messageViewportScrollArea` to the message list root.

- [ ] **Step 3: Run tests**

Run:

```powershell
cd Source\PuddingPlatformAdmin
pnpm exec jest --runTestsByPath src/pages/chat/components/MessageList.test.tsx --runInBand
```

Expected: pass.

- [ ] **Step 4: Commit style cleanup**

```powershell
git add Source/PuddingPlatformAdmin/src/pages/chat/styles.ts Source/PuddingPlatformAdmin/src/pages/chat/components/MessageList.tsx Source/PuddingPlatformAdmin/src/pages/chat/components/MessageList.test.tsx
git commit -m "style(chat): stabilize viewport controls and scrollbar"
```

## Task 8: Verification And QA

**Files:**
- Modify: `Source/PuddingPlatformAdmin/src/pages/chat/perf/chatPerfScenario.test.tsx`
- Modify: `Docs/07架构/56ADR-055ChatMessageViewportRuntime设计方案.md`

- [ ] **Step 1: Fix perf test data shape**

Ensure `chatPerfScenario.test.tsx` uses real `ChatTurn` objects:

```ts
const defaultTurns: ChatTurn[] = [
  {
    turnId: 't1',
    userMessage: {
      id: 'u1',
      text: 'hello',
      timestamp: 1000,
      status: 'success',
    },
    assistant: {
      id: 'a1',
      status: 'success',
      answerMarkdown: 'Hi there!',
      timelineItems: [],
      usage: { totalTokens: 10 } as any,
      isStreaming: false,
      renderMode: 'structured',
    },
  },
];
```

- [ ] **Step 2: Run frontend focused tests**

Run:

```powershell
cd Source\PuddingPlatformAdmin
pnpm exec jest --runTestsByPath src/pages/chat/viewport/messageProjection.test.ts src/pages/chat/viewport/useMessageViewportRuntime.test.tsx src/pages/chat/components/MessageList.test.tsx src/pages/chat/components/ChatMain.test.tsx src/pages/chat/hooks/useChatState.selection.test.tsx src/pages/chat/hooks/useChatState.recovery.test.ts src/pages/chat/perf/chatPerfScenario.test.tsx --runInBand --detectOpenHandles --forceExit
```

Expected: all selected suites pass.

- [ ] **Step 3: Run TypeScript and Biome for touched paths**

Run:

```powershell
cd Source\PuddingPlatformAdmin
pnpm tsc --noEmit --pretty false
pnpm exec biome check src/pages/chat/viewport src/pages/chat/components/MessageList.tsx src/pages/chat/components/ChatMain.tsx src/pages/chat/hooks/useChatState.ts src/pages/chat/perf/chatPerfScenario.test.tsx
```

Expected: no new errors in touched files. If repo-wide `tsc` still fails because of pre-existing non-chat files, record the failing file list in the QA note and prove touched viewport files are clean through the focused Biome command.

- [ ] **Step 4: Manual browser QA**

Run local frontend through the existing dev stack:

```powershell
python dev-up.py --status
.\dev-up.ps1
```

Manual checks:

- Open an existing long session: viewport remains near the opening position and does not jump to bottom.
- Scroll to middle, start or observe streaming: viewport does not move.
- Send a new message: viewport follows bottom.
- Toggle pinned bottom: streaming remains glued to bottom.
- Scroll to top: exactly one older-page request fires per anchor.
- Load older messages: first visible message stays in place.
- Resize browser to 375, 768, 1024, 1440 widths: controls do not overlap composer or message text.

- [ ] **Step 5: Commit QA fixes**

```powershell
git add Source/PuddingPlatformAdmin/src/pages/chat/perf/chatPerfScenario.test.tsx Docs/07架构/56ADR-055ChatMessageViewportRuntime设计方案.md
git commit -m "test(chat): verify message viewport runtime scenarios"
```

## Rollback Strategy

If viewport runtime causes instability during rollout:

1. Keep `viewport/messageProjection.ts` because it is pure and reusable.
2. Revert only the `MessageList.tsx`, `ChatMain.tsx`, and `useChatState.ts` wiring commits.
3. Leave ADR and tests in place with a note that runtime integration was reverted.
4. Re-run:

```powershell
cd Source\PuddingPlatformAdmin
pnpm exec jest --runTestsByPath src/pages/chat/components/MessageList.test.tsx src/pages/chat/hooks/useChatState.selection.test.tsx --runInBand
```

## Self-Review

- Spec coverage: ADR-055 requirements map to projection extraction, viewport runtime, scroll ownership cleanup, send intent, anchor restoration, visual controls, and QA tasks.
- Placeholder scan: no open-ended implementation steps are left; each task names files, code shape, commands, and expected results.
- Type consistency: `VirtualMessageItem`, `ScrollIntent`, `LoadBeforeRequest`, and `MessageViewportState` are defined once in `viewport/types.ts` and reused by projection, runtime, and component wiring.
