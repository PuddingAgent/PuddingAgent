import { useVirtualizer } from '@tanstack/react-virtual';
import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
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
const MESSAGE_VIEWPORT_BOTTOM_PADDING_PX = 32;
type MessageVirtualItem = Extract<VirtualMessageItem, { kind: 'message' }>;

const initialState: MessageViewportState = {
  atBottom: true,
  nearTop: false,
  followMode: 'auto',
  showBottomButton: false,
  pendingIntent: { type: 'none' },
};

const textLength = (value: unknown): number =>
  typeof value === 'string' || typeof value === 'number'
    ? String(value).length
    : 0;

const getProcessItemsFingerprint = (
  processItems: MessageVirtualItem['block']['processItems'],
): string => {
  if (!processItems?.length) return '0';
  const totalLength = processItems.reduce(
    (sum, item) =>
      sum +
      textLength(item.text) +
      textLength(item.name) +
      textLength(item.arguments) +
      textLength(item.output) +
      textLength(item.message),
    0,
  );
  const last = processItems[processItems.length - 1];
  return [
    processItems.length,
    totalLength,
    last?.id ?? '',
    last?.type ?? '',
    last?.status ?? '',
    last?.exitCode ?? '',
  ].join(':');
};

export const getVirtualMessageContentFingerprint = (
  items: VirtualMessageItem[],
): string => {
  const last = items[items.length - 1];
  if (!last) return '0:empty';

  if (last.kind === 'message') {
    return [
      items.length,
      last.id,
      last.block.status,
      last.block.isStreaming ? 'streaming' : 'settled',
      last.block.content.length,
      getProcessItemsFingerprint(last.block.processItems),
    ].join(':');
  }

  if (last.kind === 'subagent') {
    return [
      items.length,
      last.id,
      last.card.status ?? '',
      last.card.output?.length ?? 0,
    ].join(':');
  }

  return `${items.length}:${last.id}`;
};

export function useMessageViewportRuntime(options: UseMessageViewportRuntimeOptions) {
  const parentRef = useRef<HTMLDivElement | null>(null);
  const contentRef = useRef<HTMLDivElement | null>(null);
  const [state, setState] = useState<MessageViewportState>(initialState);
  const lastLoadAnchorRef = useRef<string | null>(null);
  const followModeRef = useRef<MessageViewportState['followMode']>(
    initialState.followMode,
  );
  const suppressOnScrollRef = useRef(false);
  const settleBottomFrameRef = useRef<number | null>(null);
  const releaseScrollSuppressionFrameRef = useRef<number | null>(null);

  // Fingerprint tracks both item count and last item content length,
  // so streaming content growth also triggers the auto-follow effect.
  const contentFingerprint = useMemo(
    () => getVirtualMessageContentFingerprint(options.items),
    [options.items],
  );

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
    paddingEnd: MESSAGE_VIEWPORT_BOTTOM_PADDING_PX,
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

  const suppressProgrammaticScroll = useCallback(() => {
    suppressOnScrollRef.current = true;
    if (releaseScrollSuppressionFrameRef.current !== null) {
      cancelAnimationFrame(releaseScrollSuppressionFrameRef.current);
    }
    releaseScrollSuppressionFrameRef.current = requestAnimationFrame(() => {
      releaseScrollSuppressionFrameRef.current = requestAnimationFrame(() => {
        suppressOnScrollRef.current = false;
        releaseScrollSuppressionFrameRef.current = null;
      });
    });
  }, []);

  const writeBottomPosition = useCallback(
    (behavior: ScrollBehavior) => {
      const el = parentRef.current;
      if (!el) return;
      const top = Math.max(0, el.scrollHeight - el.clientHeight);
      suppressProgrammaticScroll();
      if (behavior === 'smooth' && typeof el.scrollTo === 'function') {
        el.scrollTo({ top, behavior });
        return;
      }
      el.scrollTop = top;
    },
    [suppressProgrammaticScroll],
  );

  const scheduleBottomSettlement = useCallback(() => {
    if (settleBottomFrameRef.current !== null) {
      cancelAnimationFrame(settleBottomFrameRef.current);
    }
    settleBottomFrameRef.current = requestAnimationFrame(() => {
      settleBottomFrameRef.current = null;
      writeBottomPosition('auto');
    });
  }, [writeBottomPosition]);

  const onScroll = useCallback(() => {
    if (suppressOnScrollRef.current) {
      return;
    }
    const next = readScroll();
    if (next.nearTop) requestLoadBefore();
    setState((current) => {
      if (current.followMode === 'pinned') {
        if (!next.atBottom) scheduleBottomSettlement();
        followModeRef.current = 'pinned';
        return {
          ...current,
          atBottom: true,
          nearTop: false,
          showBottomButton: false,
        };
      }
      const followMode =
        current.followMode === 'auto' && !next.atBottom
          ? 'off'
          : current.followMode === 'off' && next.atBottom
            ? 'auto'
            : current.followMode;
      followModeRef.current = followMode;
      return {
        ...current,
        atBottom: next.atBottom,
        nearTop: next.nearTop,
        showBottomButton: !next.atBottom,
        followMode,
      };
    });
  }, [readScroll, requestLoadBefore, scheduleBottomSettlement]);

  // Bottom following owns the real scroll container. The virtualizer only owns
  // item measurement and anchor restoration; it must not estimate the bottom.
  React.useLayoutEffect(() => {
    if (options.items.length === 0) return;
    if (state.followMode === 'auto' || state.followMode === 'pinned') {
      writeBottomPosition('auto');
      scheduleBottomSettlement();
    }
  }, [
    contentFingerprint,
    options.items.length,
    scheduleBottomSettlement,
    state.followMode,
    writeBottomPosition,
  ]);

  // Virtual rows, Markdown, images and tool panels can grow after React commits.
  // In pinned mode every measured layout change must converge to the actual
  // bottom; auto mode deliberately does not steal scroll from a reader.
  React.useLayoutEffect(() => {
    const parent = parentRef.current;
    const content = contentRef.current;
    if (!parent || !content || typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(() => {
      if (followModeRef.current === 'pinned') {
        scheduleBottomSettlement();
      }
    });
    observer.observe(parent);
    observer.observe(content);
    return () => observer.disconnect();
  }, [options.items.length, scheduleBottomSettlement]);

  React.useLayoutEffect(() => {
    followModeRef.current = state.followMode;
  }, [state.followMode]);

  useEffect(
    () => () => {
      if (settleBottomFrameRef.current !== null) {
        cancelAnimationFrame(settleBottomFrameRef.current);
      }
      if (releaseScrollSuppressionFrameRef.current !== null) {
        cancelAnimationFrame(releaseScrollSuppressionFrameRef.current);
      }
    },
    [],
  );

  const scrollToBottom = useCallback(
    ({ behavior }: { behavior: ScrollBehavior; reason: string }) => {
      if (options.items.length === 0) return;
      writeBottomPosition(behavior);
      if (behavior === 'auto') scheduleBottomSettlement();
      setState((current) => ({
        ...current,
        atBottom: true,
        nearTop: false,
        showBottomButton: false,
        followMode: current.followMode === 'pinned' ? 'pinned' : 'auto',
        pendingIntent: { type: 'none' },
      }));
      if (followModeRef.current !== 'pinned') followModeRef.current = 'auto';
    },
    [
      options.items.length,
      scheduleBottomSettlement,
      writeBottomPosition,
    ],
  );

  const setPinnedBottom = useCallback(
    (enabled: boolean) => {
      setState((current) => ({
        ...current,
        followMode: enabled
          ? 'pinned'
          : current.atBottom
            ? 'auto'
            : 'off',
        atBottom: enabled ? true : current.atBottom,
        showBottomButton: enabled ? false : current.showBottomButton,
      }));
      followModeRef.current = enabled
        ? 'pinned'
        : state.atBottom
          ? 'auto'
          : 'off';
      if (enabled) {
        scrollToBottom({ behavior: 'auto', reason: 'pin-bottom' });
      }
    },
    [scrollToBottom, state.atBottom],
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
      if (intent.type === 'restore-anchor') {
        followModeRef.current = 'off';
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
    },
    [scrollToBottom, options.items, virtualizer],
  );

  React.useLayoutEffect(() => {
    const intent = state.pendingIntent;
    if (intent.type !== 'restore-anchor') return;
    const index = options.items.findIndex((item) => item.id === intent.itemId);
    if (index < 0) return;
    virtualizer.scrollToIndex(index, { align: 'start', behavior: 'auto' });
  }, [options.items, state.pendingIntent, virtualizer]);

  return useMemo(
    () => ({
      parentRef,
      contentRef,
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
