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
const MESSAGE_VIEWPORT_VIRTUALIZATION_MIN_ITEMS = 40;
type MessageVirtualItem = Extract<VirtualMessageItem, { kind: 'message' }>;

interface PendingViewportAnchor {
  itemId: string;
  offset: number;
  scrollHeight: number;
  measurable: boolean;
}

const initialState: MessageViewportState = {
  atBottom: true,
  nearTop: false,
  // Opening an existing conversation is a reading action. Bottom following is
  // entered only after a user send, an explicit bottom action, or a real scroll
  // event confirms that the reader is already at the bottom.
  followMode: 'off',
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

  return `${items.length}:${last.id}`;
};

/**
 * Virtualization pays for itself when the timeline contains many independent
 * rows. Short conversations with very tall Markdown/tool rows are more stable
 * in normal document flow because there is no estimate -> measurement
 * correction while the user is scrolling.
 */
export const shouldVirtualizeMessageViewport = (
  items: VirtualMessageItem[],
): boolean => items.length >= MESSAGE_VIEWPORT_VIRTUALIZATION_MIN_ITEMS;

export function useMessageViewportRuntime(options: UseMessageViewportRuntimeOptions) {
  const parentRef = useRef<HTMLDivElement | null>(null);
  const contentRef = useRef<HTMLDivElement | null>(null);
  const [state, setState] = useState<MessageViewportState>(initialState);
  const lastLoadAnchorRef = useRef<string | null>(null);
  const followModeRef = useRef<MessageViewportState['followMode']>(
    initialState.followMode,
  );
  const suppressOnScrollRef = useRef(false);
  const scrollFrameRef = useRef<number | null>(null);
  const settleBottomFrameRef = useRef<number | null>(null);
  const releaseScrollSuppressionFrameRef = useRef<number | null>(null);
  const restoreAnchorFrameRef = useRef<number | null>(null);
  const pendingViewportAnchorRef = useRef<PendingViewportAnchor | null>(null);
  const preUpdateViewportAnchorRef = useRef<PendingViewportAnchor | null>(null);
  const firstTimelineItemIdRef = useRef<string | null>(null);
  const previousScrollHeightRef = useRef<number | null>(null);
  // C2: Suspend auto-follow while smooth scroll animation is in progress
  const smoothScrollActiveRef = useRef(false);
  const smoothScrollTimerRef = useRef<number | null>(null);
  // A3: Cache measured row heights to avoid estimate→measure correction jitter
  const measuredHeightsRef = useRef<Map<number, number>>(new Map());

  // Fingerprint tracks both item count and last item content length,
  // so streaming content growth also triggers the auto-follow effect.
  const contentFingerprint = useMemo(
    () => getVirtualMessageContentFingerprint(options.items),
    [options.items],
  );
  const virtualizationEnabled = shouldVirtualizeMessageViewport(options.items);

    const virtualizer = useVirtualizer({
    count: options.items.length,
    getScrollElement: () => parentRef.current,
    enabled: virtualizationEnabled,
    estimateSize: (index) => {
      // A3: Return cached measured height if available
      const cached = measuredHeightsRef.current.get(index);
      if (cached !== undefined) return cached;
      const hint = options.items[index]?.heightHint;
      if (hint === 'compact') return 96;
      if (hint === 'rich') return 520;
      if (hint === 'streaming') return 320;
      return 220;
    },
    overscan: 6,
    paddingEnd: MESSAGE_VIEWPORT_BOTTOM_PADDING_PX,
    getItemKey: (index) => options.items[index]?.id ?? index,
    // A3: Cache actual DOM measurements for future estimateSize calls
    measureElement: (element) => {
      const idx = Number((element as HTMLElement).dataset.index);
      const height = element.getBoundingClientRect().height;
      if (!Number.isNaN(idx) && height > 0) {
        measuredHeightsRef.current.set(idx, height);
      }
      return height;
    },
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

  const readVisibleAnchor = useCallback((): PendingViewportAnchor | null => {
    const parent = parentRef.current;
    if (!parent) return null;
    const parentRect = parent.getBoundingClientRect();
    const rows = Array.from(
      parent.querySelectorAll<HTMLElement>('[data-viewport-item-id]'),
    );
    const visible =
      rows.find((row) => {
        const rect = row.getBoundingClientRect();
        return rect.bottom > parentRect.top && rect.top < parentRect.bottom;
      }) ?? rows[0];
    if (!visible) return null;
    const itemId = visible.dataset.viewportItemId;
    if (!itemId) return null;
    return {
      itemId,
      offset: visible.getBoundingClientRect().top - parentRect.top,
      scrollHeight: parent.scrollHeight,
      measurable:
        parentRect.height > 0 || visible.getBoundingClientRect().height > 0,
    };
  }, []);

  const restoreVisibleAnchor = useCallback(
    (anchor: PendingViewportAnchor): boolean => {
      const parent = parentRef.current;
      if (!parent) return false;
      const row = Array.from(
        parent.querySelectorAll<HTMLElement>('[data-viewport-item-id]'),
      ).find((candidate) => candidate.dataset.viewportItemId === anchor.itemId);
      if (!row) return false;

      const currentOffset =
        row.getBoundingClientRect().top - parent.getBoundingClientRect().top;
      const delta = anchor.measurable
        ? currentOffset - anchor.offset
        : parent.scrollHeight - anchor.scrollHeight;
      if (Math.abs(delta) > 0.5) {
        suppressProgrammaticScroll();
        parent.scrollTop = Math.max(0, parent.scrollTop + delta);
      }
      return true;
    },
    [suppressProgrammaticScroll],
  );

  const requestLoadBefore = useCallback(() => {
    if (!options.hasMoreBefore || options.loadingBefore || options.items.length === 0) return;
    const fallbackItem =
      options.items.find((item) => item.kind !== 'loader') ?? options.items[0];
    const anchor =
      readVisibleAnchor() ??
      (fallbackItem
        ? {
            itemId: fallbackItem.id,
            offset: 0,
            scrollHeight: parentRef.current?.scrollHeight ?? 0,
            measurable: false,
          }
        : null);
    if (!anchor || lastLoadAnchorRef.current === anchor.itemId) return;
    lastLoadAnchorRef.current = anchor.itemId;
    pendingViewportAnchorRef.current = anchor;
    options.onRequestLoadBefore({
      anchor: { itemId: anchor.itemId, offset: anchor.offset },
    });
  }, [options, readVisibleAnchor]);

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

  const processScroll = useCallback(() => {
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

  const onScroll = useCallback(() => {
    if (suppressOnScrollRef.current || scrollFrameRef.current !== null) {
      return;
    }
    scrollFrameRef.current = requestAnimationFrame(() => {
      scrollFrameRef.current = null;
      processScroll();
    });
  }, [processScroll]);

  // Bottom following owns the real scroll container. The virtualizer only owns
  // item measurement and anchor restoration; it must not estimate the bottom.
  React.useLayoutEffect(() => {
    if (options.items.length === 0) return;
    // C2: Skip auto-follow while smooth scroll animation is in progress
    if (smoothScrollActiveRef.current) return;
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

  // Capture a DOM anchor in the cleanup phase, before React mutates the row
  // tree. This also protects programmatic history prepends and keeps the
  // invariant intact if a caller bypasses the visible loader button.
  React.useLayoutEffect(() => {
    const firstTimelineItemId =
      options.items.find((item) => item.kind !== 'loader')?.id ?? null;
    const previousFirstTimelineItemId = firstTimelineItemIdRef.current;
    const isPrepend =
      previousFirstTimelineItemId !== null &&
      firstTimelineItemId !== previousFirstTimelineItemId &&
      options.items.some((item) => item.id === previousFirstTimelineItemId);
    const parent = parentRef.current;
    const currentScrollHeight = parent?.scrollHeight ?? null;

    if (isPrepend && parent && !virtualizationEnabled) {
      const previousScrollHeight = previousScrollHeightRef.current;
      if (previousScrollHeight !== null && currentScrollHeight !== null) {
        const delta = currentScrollHeight - previousScrollHeight;
        if (Math.abs(delta) > 0.5) {
          suppressProgrammaticScroll();
          parent.scrollTop = Math.max(0, parent.scrollTop + delta);
        }
      }
      pendingViewportAnchorRef.current = null;
    } else if (
      isPrepend &&
      pendingViewportAnchorRef.current === null &&
      preUpdateViewportAnchorRef.current !== null
    ) {
      pendingViewportAnchorRef.current = preUpdateViewportAnchorRef.current;
    }
    firstTimelineItemIdRef.current = firstTimelineItemId;
    previousScrollHeightRef.current = currentScrollHeight;

    return () => {
      preUpdateViewportAnchorRef.current = readVisibleAnchor();
    };
  }, [
    options.items,
    readVisibleAnchor,
    suppressProgrammaticScroll,
    virtualizationEnabled,
  ]);

  // Historical prepend is a viewport transaction: keep the first visible row
  // at the same pixel offset after React inserts older items. Normal-flow rows
  // can be restored immediately. In virtual mode the anchor may first need to
  // be materialized, then the same DOM offset correction is applied next frame.
  // C3: Double-retry with scrollHeight delta fallback for robustness.
  React.useLayoutEffect(() => {
    const anchor = pendingViewportAnchorRef.current;
    if (!anchor) return;
    if (restoreVisibleAnchor(anchor)) {
      pendingViewportAnchorRef.current = null;
      return;
    }

    if (!virtualizationEnabled) return;
    const index = options.items.findIndex((item) => item.id === anchor.itemId);
    if (index < 0) return;

    // C3: Save scrollHeight before first attempt for delta fallback
    const scrollHeightBefore = parentRef.current?.scrollHeight ?? 0;

    virtualizer.scrollToIndex(index, { align: 'start', behavior: 'auto' });
    if (restoreAnchorFrameRef.current !== null) {
      cancelAnimationFrame(restoreAnchorFrameRef.current);
    }
    restoreAnchorFrameRef.current = requestAnimationFrame(() => {
      restoreAnchorFrameRef.current = null;
      if (restoreVisibleAnchor(anchor)) {
        pendingViewportAnchorRef.current = null;
        return;
      }
      // C3: Second retry — re-materialize and attempt restore again
      virtualizer.scrollToIndex(index, { align: 'start', behavior: 'auto' });
      requestAnimationFrame(() => {
        if (restoreVisibleAnchor(anchor)) {
          pendingViewportAnchorRef.current = null;
          return;
        }
        // C3: Final fallback — scrollHeight delta compensation
        const el = parentRef.current;
        if (el) {
          const delta = el.scrollHeight - scrollHeightBefore;
          if (Math.abs(delta) > 0.5) {
            suppressProgrammaticScroll();
            el.scrollTop = Math.max(0, el.scrollTop + delta);
          }
        }
        pendingViewportAnchorRef.current = null;
      });
    });
  }, [
    options.items,
    restoreVisibleAnchor,
    suppressProgrammaticScroll,
    totalSize,
    virtualizationEnabled,
    virtualizer,
  ]);

  React.useLayoutEffect(() => {
    followModeRef.current = state.followMode;
  }, [state.followMode]);

  useEffect(
    () => () => {
      if (settleBottomFrameRef.current !== null) {
        cancelAnimationFrame(settleBottomFrameRef.current);
      }
      if (scrollFrameRef.current !== null) {
        cancelAnimationFrame(scrollFrameRef.current);
      }
      if (releaseScrollSuppressionFrameRef.current !== null) {
        cancelAnimationFrame(releaseScrollSuppressionFrameRef.current);
      }
      if (restoreAnchorFrameRef.current !== null) {
        cancelAnimationFrame(restoreAnchorFrameRef.current);
      }
      // C2: Clean up smooth scroll safety timer
      if (smoothScrollTimerRef.current !== null) {
        clearTimeout(smoothScrollTimerRef.current);
      }
    },
    [],
  );

  const scrollToBottom = useCallback(
    ({ behavior }: { behavior: ScrollBehavior; reason: string }) => {
      if (options.items.length === 0) return;
      // C2: Suspend auto-follow during smooth scroll animation
      if (behavior === 'smooth') {
        smoothScrollActiveRef.current = true;
        if (smoothScrollTimerRef.current !== null) {
          clearTimeout(smoothScrollTimerRef.current);
        }
        smoothScrollTimerRef.current = window.setTimeout(() => {
          smoothScrollActiveRef.current = false;
          smoothScrollTimerRef.current = null;
        }, 500);
      }
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
        pendingViewportAnchorRef.current = {
          itemId: intent.itemId,
          offset: intent.offset,
          scrollHeight: parentRef.current?.scrollHeight ?? 0,
          measurable: false,
        };
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

  return useMemo(
    () => ({
      parentRef,
      contentRef,
      virtualizer,
      virtualRows,
      totalSize,
      virtualizationEnabled,
      state,
      onScroll,
      requestLoadBefore,
      scrollToBottom,
      setPinnedBottom,
      applyIntent,
    }),
    [
      applyIntent,
      onScroll,
      requestLoadBefore,
      scrollToBottom,
      setPinnedBottom,
      state,
      totalSize,
      virtualizationEnabled,
      virtualRows,
      virtualizer,
    ],
  );
}
