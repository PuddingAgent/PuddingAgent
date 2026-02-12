import { renderHook, act } from '@testing-library/react';
import {
  getVirtualMessageContentFingerprint,
  useMessageViewportRuntime,
} from './useMessageViewportRuntime';
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
  it('tracks active process item growth for bottom-follow updates', () => {
    const baseItem = {
      ...makeItem('m1', 1),
      block: {
        ...makeItem('m1', 1).block,
        isStreaming: true,
        content: '',
        processItems: [
          {
            id: 'tool-1',
            type: 'tool_result' as const,
            text: '工具调用完成',
            output: 'short output',
            timestamp: 1,
            collapsed: false,
          },
        ],
      },
    };
    const grownItem = {
      ...baseItem,
      block: {
        ...baseItem.block,
        processItems: [
          {
            ...baseItem.block.processItems[0],
            output: 'short output plus more streamed tool output',
          },
        ],
      },
    };

    expect(getVirtualMessageContentFingerprint([grownItem])).not.toBe(
      getVirtualMessageContentFingerprint([baseItem]),
    );
  });

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
    expect(result.current.state.followMode).toBe('auto'); // auto when still at bottom
  });

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
});
