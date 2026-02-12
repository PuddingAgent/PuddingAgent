import { act, fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import MessageList from './MessageList';

jest.mock('../styles', () => {
  const styles = new Proxy(
    {},
    {
      get: (_target, prop) => String(prop),
    },
  );
  return {
    useChatStyles: () => ({ styles }),
  };
});

jest.mock('./MessageRow', () => ({ block, onContextMenu }: any) => (
  <div
    data-testid="message-row"
    data-role={block.role}
    onContextMenu={(event) =>
      onContextMenu?.(
        event,
        block.turnId,
        block.role === 'agent' ? 'assistant' : 'user',
        block.content,
      )
    }
  >
    <span>{block.content}</span>
    {block.quotedMessage && (
      <span data-testid="quoted-message">
        {block.quotedMessage.sourceName}: {block.quotedMessage.content}
      </span>
    )}
    <span>
      {(block.processItems ?? []).map((item: any) => item.text).join('\n')}
    </span>
  </div>
));
jest.mock('./ChatEmptyState', () => () => <div data-testid="empty-state" />);

const baseProps = {
  turns: [],
  sessionId: 'session-1',
  agentId: 'agent-1',
  error: null,
  historyLoading: false,
  loadingMore: false,
  hasMoreMessages: false,
  onClearError: jest.fn(),
  onLoadMore: jest.fn(),
  formatTime: () => '刚刚',
  onDeleteTurn: jest.fn(),
  onContextMenu: jest.fn(),
  messageListRef: React.createRef<HTMLDivElement>(),
  listEndRef: React.createRef<HTMLDivElement>(),
};

const createTurn = (
  turnId: string,
  timestamp: number,
  userText: string,
  answerText: string,
) => ({
  turnId,
  userMessage: {
    id: `${turnId}:user`,
    text: userText,
    timestamp,
    status: 'success' as const,
  },
  assistant: {
    id: `${turnId}:assistant`,
    status: 'success' as const,
    timelineItems: [],
    answerMarkdown: answerText,
    isStreaming: false,
    renderMode: 'structured' as const,
  },
});

describe('MessageList scroll performance', () => {
  let rafCallbacks: FrameRequestCallback[];
  let originalRaf: typeof window.requestAnimationFrame;
  let originalCancelRaf: typeof window.cancelAnimationFrame;
  let originalResizeObserver: typeof ResizeObserver | undefined;
  let resizeObserverCallbacks: ResizeObserverCallback[];

  beforeEach(() => {
    rafCallbacks = [];
    resizeObserverCallbacks = [];
    originalRaf = window.requestAnimationFrame;
    originalCancelRaf = window.cancelAnimationFrame;
    originalResizeObserver = window.ResizeObserver;
    window.requestAnimationFrame = jest.fn((callback: FrameRequestCallback) => {
      rafCallbacks.push(callback);
      return rafCallbacks.length;
    });
    window.cancelAnimationFrame = jest.fn();
    window.ResizeObserver = jest.fn((callback: ResizeObserverCallback) => {
      resizeObserverCallbacks.push(callback);
      return {
        observe: jest.fn(),
        unobserve: jest.fn(),
        disconnect: jest.fn(),
      };
    }) as any;
  });

  afterEach(() => {
    window.requestAnimationFrame = originalRaf;
    window.cancelAnimationFrame = originalCancelRaf;
    if (originalResizeObserver) {
      window.ResizeObserver = originalResizeObserver;
    } else {
      delete (window as any).ResizeObserver;
    }
  });

  it('coalesces repeated scroll events into a single layout read per frame', () => {
    const props = {
      ...baseProps,
      messageListRef: React.createRef<HTMLDivElement>(),
      listEndRef: React.createRef<HTMLDivElement>(),
    };
    render(<MessageList {...props} />);

    const node = props.messageListRef.current!;
    let scrollHeightReads = 0;
    Object.defineProperty(node, 'scrollHeight', {
      configurable: true,
      get: () => {
        scrollHeightReads++;
        return 2000;
      },
    });
    Object.defineProperty(node, 'clientHeight', {
      configurable: true,
      value: 500,
    });
    Object.defineProperty(node, 'scrollTop', {
      configurable: true,
      writable: true,
      value: 100,
    });

    scrollHeightReads = 0;
    fireEvent.scroll(node);
    fireEvent.scroll(node);
    fireEvent.scroll(node);

    expect(scrollHeightReads).toBe(0);
    expect(rafCallbacks).toHaveLength(1);

    act(() => {
      rafCallbacks.shift()?.(performance.now());
    });

    expect(scrollHeightReads).toBe(1);
  });

  it('restores follow mode when the user scrolls back to the bottom', () => {
    const originalScrollIntoView = HTMLElement.prototype.scrollIntoView;
    HTMLElement.prototype.scrollIntoView = jest.fn();
    const props = {
      ...baseProps,
      sessionId: 'session-scroll-follow-recovery',
      messageListRef: React.createRef<HTMLDivElement>(),
      listEndRef: React.createRef<HTMLDivElement>(),
      turns: [
        createTurn('older-turn', 1_000, 'older user', 'older answer'),
        createTurn('latest-turn', 2_000, 'latest user', 'latest answer'),
      ],
    };
    try {
      render(<MessageList {...props} />);

      const node = props.messageListRef.current!;
      Object.defineProperty(node, 'scrollHeight', {
        configurable: true,
        get: () => 2_000,
      });
      Object.defineProperty(node, 'clientHeight', {
        configurable: true,
        value: 500,
      });
      Object.defineProperty(node, 'scrollTop', {
        configurable: true,
        writable: true,
        value: 900,
      });
      rafCallbacks = [];

      fireEvent.scroll(node);
      act(() => {
        while (rafCallbacks.length) rafCallbacks.shift()?.(performance.now());
      });
      expect(screen.getByText('回到底部 ↓')).toBeTruthy();

      node.scrollTop = 1_500;
      fireEvent.scroll(node);
      act(() => {
        while (rafCallbacks.length) rafCallbacks.shift()?.(performance.now());
      });

      expect(screen.queryByText('回到底部 ↓')).toBeNull();
    } finally {
      HTMLElement.prototype.scrollIntoView = originalScrollIntoView;
    }
  });

  it('keeps the viewport anchored when older history is prepended', () => {
    let scrollHeight = 2_000;
    const originalScrollHeight = Object.getOwnPropertyDescriptor(
      HTMLElement.prototype,
      'scrollHeight',
    );
    const originalClientHeight = Object.getOwnPropertyDescriptor(
      HTMLElement.prototype,
      'clientHeight',
    );
    Object.defineProperty(HTMLElement.prototype, 'scrollHeight', {
      configurable: true,
      get() {
        return this.getAttribute?.('data-testid') === 'chat-message-list'
          ? scrollHeight
          : 0;
      },
    });
    Object.defineProperty(HTMLElement.prototype, 'clientHeight', {
      configurable: true,
      get() {
        return this.getAttribute?.('data-testid') === 'chat-message-list'
          ? 500
          : 0;
      },
    });
    const props = {
      ...baseProps,
      messageListRef: React.createRef<HTMLDivElement>(),
      listEndRef: React.createRef<HTMLDivElement>(),
      turns: [
        createTurn('middle-turn', 2_000, 'middle user', 'middle answer'),
        createTurn('latest-turn', 3_000, 'latest user', 'latest answer'),
      ],
    };
    try {
      const { rerender } = render(<MessageList {...props} />);
      const node = props.messageListRef.current!;
      Object.defineProperty(node, 'scrollTop', {
        configurable: true,
        writable: true,
        value: 100,
      });
      const scrollIntoView = jest.fn();
      if (props.listEndRef.current) {
        props.listEndRef.current.scrollIntoView = scrollIntoView;
      }

      scrollHeight = 2_600;
      rerender(
        <MessageList
          {...props}
          turns={[
            createTurn('older-turn', 1_000, 'older user', 'older answer'),
            ...props.turns,
          ]}
        />,
      );

      expect(node.scrollTop).toBe(700);
      expect(scrollIntoView).not.toHaveBeenCalled();
    } finally {
      if (originalScrollHeight) {
        Object.defineProperty(
          HTMLElement.prototype,
          'scrollHeight',
          originalScrollHeight,
        );
      } else {
        delete (HTMLElement.prototype as any).scrollHeight;
      }
      if (originalClientHeight) {
        Object.defineProperty(
          HTMLElement.prototype,
          'clientHeight',
          originalClientHeight,
        );
      } else {
        delete (HTMLElement.prototype as any).clientHeight;
      }
    }
  });

  it('does not scroll to the latest message on first open', () => {
    const originalScrollIntoView = HTMLElement.prototype.scrollIntoView;
    const scrollIntoView = jest.fn();
    HTMLElement.prototype.scrollIntoView = scrollIntoView;
    const originalScrollHeight = Object.getOwnPropertyDescriptor(
      HTMLElement.prototype,
      'scrollHeight',
    );
    Object.defineProperty(HTMLElement.prototype, 'scrollHeight', {
      configurable: true,
      get() {
        return this.getAttribute?.('data-testid') === 'chat-message-list'
          ? 1_800
          : 0;
      },
    });
    const ref = React.createRef<HTMLDivElement>();
    try {
      render(
        <MessageList
          {...baseProps}
          sessionId="session-first-open-without-saved-position"
          messageListRef={ref}
          listEndRef={React.createRef<HTMLDivElement>()}
          turns={[
            createTurn('older-turn', 1_000, 'older user', 'older answer'),
            createTurn('latest-turn', 2_000, 'latest user', 'latest answer'),
          ]}
        />,
      );

      expect(scrollIntoView).not.toHaveBeenCalled();
    } finally {
      HTMLElement.prototype.scrollIntoView = originalScrollIntoView;
      if (originalScrollHeight) {
        Object.defineProperty(
          HTMLElement.prototype,
          'scrollHeight',
          originalScrollHeight,
        );
      } else {
        delete (HTMLElement.prototype as any).scrollHeight;
      }
    }
  });

  it('does not move the viewport after first-open virtual content measurement expands', () => {
    let scrollHeight = 300;
    const originalScrollIntoView = HTMLElement.prototype.scrollIntoView;
    delete (HTMLElement.prototype as any).scrollIntoView;
    const originalScrollHeight = Object.getOwnPropertyDescriptor(
      HTMLElement.prototype,
      'scrollHeight',
    );
    const originalClientHeight = Object.getOwnPropertyDescriptor(
      HTMLElement.prototype,
      'clientHeight',
    );
    Object.defineProperty(HTMLElement.prototype, 'scrollHeight', {
      configurable: true,
      get() {
        return this.getAttribute?.('data-testid') === 'chat-message-list'
          ? scrollHeight
          : 0;
      },
    });
    Object.defineProperty(HTMLElement.prototype, 'clientHeight', {
      configurable: true,
      get() {
        return this.getAttribute?.('data-testid') === 'chat-message-list'
          ? 500
          : 0;
      },
    });
    const ref = React.createRef<HTMLDivElement>();
    try {
      render(
        <MessageList
          {...baseProps}
          sessionId="session-first-open-expands-after-measure"
          messageListRef={ref}
          listEndRef={React.createRef<HTMLDivElement>()}
          turns={[
            createTurn('older-turn', 1_000, 'older user', 'older answer'),
            createTurn('latest-turn', 2_000, 'latest user', 'latest answer'),
          ]}
        />,
      );

      expect(ref.current?.scrollTop).toBe(0);
      scrollHeight = 2_000;
      act(() => {
        while (rafCallbacks.length) rafCallbacks.shift()?.(performance.now());
      });

      expect(ref.current?.scrollTop).toBe(0);
    } finally {
      HTMLElement.prototype.scrollIntoView = originalScrollIntoView;
      if (originalScrollHeight) {
        Object.defineProperty(
          HTMLElement.prototype,
          'scrollHeight',
          originalScrollHeight,
        );
      } else {
        delete (HTMLElement.prototype as any).scrollHeight;
      }
      if (originalClientHeight) {
        Object.defineProperty(
          HTMLElement.prototype,
          'clientHeight',
          originalClientHeight,
        );
      } else {
        delete (HTMLElement.prototype as any).clientHeight;
      }
    }
  });

  it('does not force follow mode when the user submits a new local turn from a saved history position', () => {
    let scrollHeight = 2_000;
    const originalScrollIntoView = HTMLElement.prototype.scrollIntoView;
    const scrollIntoView = jest.fn();
    HTMLElement.prototype.scrollIntoView = scrollIntoView;
    const originalScrollHeight = Object.getOwnPropertyDescriptor(
      HTMLElement.prototype,
      'scrollHeight',
    );
    const originalClientHeight = Object.getOwnPropertyDescriptor(
      HTMLElement.prototype,
      'clientHeight',
    );
    Object.defineProperty(HTMLElement.prototype, 'scrollHeight', {
      configurable: true,
      get() {
        return this.getAttribute?.('data-testid') === 'chat-message-list'
          ? scrollHeight
          : 0;
      },
    });
    Object.defineProperty(HTMLElement.prototype, 'clientHeight', {
      configurable: true,
      get() {
        return this.getAttribute?.('data-testid') === 'chat-message-list'
          ? 500
          : 0;
      },
    });
    const sessionId = 'session-local-submit-forces-follow';
    sessionStorage.setItem(`pudding-chat-scroll:agent-1::${sessionId}`, '240');
    const ref = React.createRef<HTMLDivElement>();
    const pendingTurn = {
      ...createTurn('pending-turn', 2_000, 'new user', ''),
      userMessage: {
        id: 'pending-turn:user',
        text: 'new user',
        timestamp: 2_000,
        status: 'sending' as const,
      },
      assistant: {
        ...createTurn('pending-turn', 2_000, 'new user', '').assistant,
        status: 'thinking' as const,
        isStreaming: true,
      },
    };
    try {
      const { rerender } = render(
        <MessageList
          {...baseProps}
          sessionId={sessionId}
          messageListRef={ref}
          listEndRef={React.createRef<HTMLDivElement>()}
          turns={[
            createTurn('older-turn', 1_000, 'older user', 'older answer'),
          ]}
        />,
      );

      expect(ref.current?.scrollTop).not.toBe(240);
      scrollIntoView.mockClear();
      scrollHeight = 2_400;
      rerender(
        <MessageList
          {...baseProps}
          sessionId={sessionId}
          messageListRef={ref}
          listEndRef={React.createRef<HTMLDivElement>()}
          turns={[
            createTurn('older-turn', 1_000, 'older user', 'older answer'),
            pendingTurn,
          ]}
        />,
      );

      expect(scrollIntoView).not.toHaveBeenCalled();
    } finally {
      HTMLElement.prototype.scrollIntoView = originalScrollIntoView;
      sessionStorage.removeItem(`pudding-chat-scroll:agent-1::${sessionId}`);
      if (originalScrollHeight) {
        Object.defineProperty(
          HTMLElement.prototype,
          'scrollHeight',
          originalScrollHeight,
        );
      } else {
        delete (HTMLElement.prototype as any).scrollHeight;
      }
      if (originalClientHeight) {
        Object.defineProperty(
          HTMLElement.prototype,
          'clientHeight',
          originalClientHeight,
        );
      } else {
        delete (HTMLElement.prototype as any).clientHeight;
      }
    }
  });

  it('does not restore a session scroll position persisted in sessionStorage', () => {
    const originalScrollIntoView = HTMLElement.prototype.scrollIntoView;
    HTMLElement.prototype.scrollIntoView = jest.fn();
    const sessionId = 'session-restores-persisted-scroll';
    sessionStorage.setItem(`pudding-chat-scroll:agent-1::${sessionId}`, '640');
    const props = {
      ...baseProps,
      sessionId,
      messageListRef: React.createRef<HTMLDivElement>(),
      listEndRef: React.createRef<HTMLDivElement>(),
      turns: [
        createTurn('older-turn', 1_000, 'older user', 'older answer'),
        createTurn('latest-turn', 2_000, 'latest user', 'latest answer'),
      ],
    };
    try {
      render(<MessageList {...props} />);
      expect(props.messageListRef.current?.scrollTop).not.toBe(640);
      expect(HTMLElement.prototype.scrollIntoView).not.toHaveBeenCalled();
    } finally {
      sessionStorage.removeItem(`pudding-chat-scroll:agent-1::${sessionId}`);
      HTMLElement.prototype.scrollIntoView = originalScrollIntoView;
    }
  });

  it('does not auto-scroll when a run is already streaming', () => {
    const originalScrollIntoView = HTMLElement.prototype.scrollIntoView;
    const scrollIntoView = jest.fn();
    HTMLElement.prototype.scrollIntoView = scrollIntoView;
    const sessionId = 'session-active-run-ignores-old-scroll';
    const props = {
      ...baseProps,
      sessionId,
      messageListRef: React.createRef<HTMLDivElement>(),
      listEndRef: React.createRef<HTMLDivElement>(),
      conversationView: {
        workspaceId: 'default',
        ownerUserId: 'single-user',
        agentId: 'agent-1',
        mainSessionId: sessionId,
        messages: [],
        activeRun: {
          runId: 'run-streaming',
          workspaceId: 'default',
          ownerUserId: 'single-user',
          agentId: 'agent-1',
          mainSessionId: sessionId,
          status: 'running' as const,
          statusText: '正在输出',
          summary: '',
          eventCursor: 1,
          outputSnapshot: {
            markdown: 'streaming answer',
            processItems: [],
          },
          startedAt: '2026-06-07T00:00:00.000Z',
          updatedAt: '2026-06-07T00:00:00.000Z',
        },
        eventCursor: 1,
        updatedAt: '2026-06-07T00:00:00.000Z',
      },
    };
    try {
      render(<MessageList {...props} />);

      expect(scrollIntoView).not.toHaveBeenCalled();
    } finally {
      HTMLElement.prototype.scrollIntoView = originalScrollIntoView;
    }
  });

  it('can toggle bottom anchor mode for streaming output', () => {
    const originalScrollIntoView = HTMLElement.prototype.scrollIntoView;
    const scrollIntoView = jest.fn();
    HTMLElement.prototype.scrollIntoView = scrollIntoView;
    const props = {
      ...baseProps,
      sessionId: 'session-anchor-streaming',
      messageListRef: React.createRef<HTMLDivElement>(),
      listEndRef: React.createRef<HTMLDivElement>(),
      conversationView: {
        workspaceId: 'default',
        ownerUserId: 'single-user',
        agentId: 'agent-1',
        mainSessionId: 'session-anchor-streaming',
        messages: [],
        activeRun: {
          runId: 'run-streaming',
          workspaceId: 'default',
          ownerUserId: 'single-user',
          agentId: 'agent-1',
          mainSessionId: 'session-anchor-streaming',
          status: 'running' as const,
          statusText: '正在输出',
          summary: '',
          eventCursor: 1,
          outputSnapshot: {
            markdown: 'streaming answer',
            processItems: [],
          },
          startedAt: '2026-06-07T00:00:00.000Z',
          updatedAt: '2026-06-07T00:00:00.000Z',
        },
        eventCursor: 1,
        updatedAt: '2026-06-07T00:00:00.000Z',
      },
    };
    try {
      const { rerender } = render(<MessageList {...props} />);
      expect(scrollIntoView).not.toHaveBeenCalled();

      fireEvent.click(screen.getByRole('button', { name: '开启贴底跟随' }));
      expect(scrollIntoView).toHaveBeenCalledWith({
        behavior: 'smooth',
        block: 'end',
      });
      scrollIntoView.mockClear();

      rerender(
        <MessageList
          {...props}
          conversationView={{
            ...props.conversationView,
            activeRun: {
              ...props.conversationView.activeRun,
              outputSnapshot: {
                markdown: 'streaming answer grows',
                processItems: [],
              },
            },
          }}
        />,
      );
      act(() => {
        while (rafCallbacks.length) rafCallbacks.shift()?.(performance.now());
      });
      expect(scrollIntoView).toHaveBeenCalledWith({
        behavior: 'auto',
        block: 'end',
      });

      scrollIntoView.mockClear();
      fireEvent.click(screen.getByRole('button', { name: '取消贴底跟随' }));
      rerender(
        <MessageList
          {...props}
          conversationView={{
            ...props.conversationView,
            activeRun: {
              ...props.conversationView.activeRun,
              outputSnapshot: {
                markdown: 'streaming answer grows again',
                processItems: [],
              },
            },
          }}
        />,
      );
      act(() => {
        while (rafCallbacks.length) rafCallbacks.shift()?.(performance.now());
      });
      expect(scrollIntoView).not.toHaveBeenCalled();
    } finally {
      HTMLElement.prototype.scrollIntoView = originalScrollIntoView;
    }
  });

  it('pins bottom controls to the browser viewport', () => {
    render(
      <MessageList
        {...baseProps}
        sessionId="session-fixed-bottom-controls"
        messageListRef={React.createRef<HTMLDivElement>()}
        listEndRef={React.createRef<HTMLDivElement>()}
        turns={[
          createTurn('older-turn', 1_000, 'older user', 'older answer'),
          createTurn('latest-turn', 2_000, 'latest user', 'latest answer'),
        ]}
      />,
    );

    screen.getByRole('button', { name: '开启贴底跟随' });
    const controls = screen.getByTestId('chat-bottom-scroll-controls');

    expect(controls.style.position).toBe('fixed');
  });

  it('keeps bottom anchor mode on for newly appended local turns', () => {
    const originalScrollIntoView = HTMLElement.prototype.scrollIntoView;
    const scrollIntoView = jest.fn();
    HTMLElement.prototype.scrollIntoView = scrollIntoView;
    const ref = React.createRef<HTMLDivElement>();
    const props = {
      ...baseProps,
      sessionId: 'session-anchor-appended-turn',
      messageListRef: ref,
      listEndRef: React.createRef<HTMLDivElement>(),
      turns: [createTurn('older-turn', 1_000, 'older user', 'older answer')],
    };
    const pendingTurn = {
      ...createTurn('pending-turn', 2_000, 'new user', ''),
      userMessage: {
        id: 'pending-turn:user',
        text: 'new user',
        timestamp: 2_000,
        status: 'sending' as const,
      },
      assistant: {
        ...createTurn('pending-turn', 2_000, 'new user', '').assistant,
        status: 'thinking' as const,
        isStreaming: true,
      },
    };
    try {
      const { rerender } = render(<MessageList {...props} />);
      fireEvent.click(screen.getByRole('button', { name: '开启贴底跟随' }));
      scrollIntoView.mockClear();

      rerender(
        <MessageList {...props} turns={[...props.turns, pendingTurn]} />,
      );
      act(() => {
        while (rafCallbacks.length) rafCallbacks.shift()?.(performance.now());
      });

      expect(scrollIntoView).toHaveBeenCalledWith({
        behavior: 'auto',
        block: 'end',
      });
    } finally {
      HTMLElement.prototype.scrollIntoView = originalScrollIntoView;
    }
  });

  it('settles bottom anchor across delayed layout growth', () => {
    let scrollHeight = 1_000;
    const originalScrollIntoView = HTMLElement.prototype.scrollIntoView;
    const originalScrollHeight = Object.getOwnPropertyDescriptor(
      HTMLElement.prototype,
      'scrollHeight',
    );
    const originalClientHeight = Object.getOwnPropertyDescriptor(
      HTMLElement.prototype,
      'clientHeight',
    );
    delete (HTMLElement.prototype as any).scrollIntoView;
    Object.defineProperty(HTMLElement.prototype, 'scrollHeight', {
      configurable: true,
      get() {
        return this.getAttribute?.('data-testid') === 'chat-message-list'
          ? scrollHeight
          : 0;
      },
    });
    Object.defineProperty(HTMLElement.prototype, 'clientHeight', {
      configurable: true,
      get() {
        return this.getAttribute?.('data-testid') === 'chat-message-list'
          ? 500
          : 0;
      },
    });
    const ref = React.createRef<HTMLDivElement>();
    const props = {
      ...baseProps,
      sessionId: 'session-anchor-delayed-layout',
      messageListRef: ref,
      listEndRef: React.createRef<HTMLDivElement>(),
      conversationView: {
        workspaceId: 'default',
        ownerUserId: 'single-user',
        agentId: 'agent-1',
        mainSessionId: 'session-anchor-delayed-layout',
        messages: [],
        activeRun: {
          runId: 'run-streaming',
          workspaceId: 'default',
          ownerUserId: 'single-user',
          agentId: 'agent-1',
          mainSessionId: 'session-anchor-delayed-layout',
          status: 'running' as const,
          statusText: '正在输出',
          summary: '',
          eventCursor: 1,
          outputSnapshot: {
            markdown: 'streaming answer',
            processItems: [],
          },
          startedAt: '2026-06-07T00:00:00.000Z',
          updatedAt: '2026-06-07T00:00:00.000Z',
        },
        eventCursor: 1,
        updatedAt: '2026-06-07T00:00:00.000Z',
      },
    };
    try {
      const { rerender } = render(<MessageList {...props} />);
      fireEvent.click(screen.getByRole('button', { name: '开启贴底跟随' }));

      expect(ref.current?.scrollTop).toBe(500);

      rerender(
        <MessageList
          {...props}
          conversationView={{
            ...props.conversationView,
            activeRun: {
              ...props.conversationView.activeRun,
              outputSnapshot: {
                markdown: 'streaming answer with a later expanding block',
                processItems: [],
              },
            },
          }}
        />,
      );
      act(() => {
        rafCallbacks.shift()?.(performance.now());
        rafCallbacks.shift()?.(performance.now());
      });
      expect(ref.current?.scrollTop).toBe(500);
      scrollHeight = 1_800;
      act(() => {
        resizeObserverCallbacks.forEach((callback) => {
          callback(
            [] as unknown as ResizeObserverEntry[],
            {} as ResizeObserver,
          );
        });
        while (rafCallbacks.length) rafCallbacks.shift()?.(performance.now());
      });

      expect(ref.current?.scrollTop).toBe(1_300);
    } finally {
      HTMLElement.prototype.scrollIntoView = originalScrollIntoView;
      if (originalScrollHeight) {
        Object.defineProperty(
          HTMLElement.prototype,
          'scrollHeight',
          originalScrollHeight,
        );
      } else {
        delete (HTMLElement.prototype as any).scrollHeight;
      }
      if (originalClientHeight) {
        Object.defineProperty(
          HTMLElement.prototype,
          'clientHeight',
          originalClientHeight,
        );
      } else {
        delete (HTMLElement.prototype as any).clientHeight;
      }
    }
  });

  it('does not auto-scroll when opening a populated session with a zero persisted position', () => {
    const originalScrollIntoView = HTMLElement.prototype.scrollIntoView;
    const scrollIntoView = jest.fn();
    HTMLElement.prototype.scrollIntoView = scrollIntoView;
    const sessionId = 'session-ignores-stale-zero-scroll';
    sessionStorage.setItem(`pudding-chat-scroll:agent-1::${sessionId}`, '0');
    const props = {
      ...baseProps,
      sessionId,
      messageListRef: React.createRef<HTMLDivElement>(),
      listEndRef: React.createRef<HTMLDivElement>(),
      turns: [
        createTurn('older-turn', 1_000, 'older user', 'older answer'),
        createTurn('latest-turn', 2_000, 'latest user', 'latest answer'),
      ],
    };
    try {
      render(<MessageList {...props} />);

      expect(scrollIntoView).not.toHaveBeenCalled();
    } finally {
      sessionStorage.removeItem(`pudding-chat-scroll:agent-1::${sessionId}`);
      HTMLElement.prototype.scrollIntoView = originalScrollIntoView;
    }
  });

  it('does not restore the recorded session scroll position when returning to a session', () => {
    const originalScrollIntoView = HTMLElement.prototype.scrollIntoView;
    HTMLElement.prototype.scrollIntoView = jest.fn();
    const props = {
      ...baseProps,
      sessionId: 'session-a',
      messageListRef: React.createRef<HTMLDivElement>(),
      listEndRef: React.createRef<HTMLDivElement>(),
      turns: [
        createTurn('older-turn', 1_000, 'older user', 'older answer'),
        createTurn('latest-turn', 2_000, 'latest user', 'latest answer'),
      ],
    };
    try {
      const { rerender } = render(<MessageList {...props} />);
      const node = props.messageListRef.current!;
      Object.defineProperty(node, 'scrollHeight', {
        configurable: true,
        get: () => 2_000,
      });
      Object.defineProperty(node, 'clientHeight', {
        configurable: true,
        value: 500,
      });
      Object.defineProperty(node, 'scrollTop', {
        configurable: true,
        writable: true,
        value: 320,
      });

      fireEvent.scroll(node);

      rerender(
        <MessageList
          {...props}
          sessionId="session-b"
          turns={[
            createTurn('session-b-turn', 3_000, 'other user', 'other answer'),
          ]}
        />,
      );
      node.scrollTop = 0;

      rerender(<MessageList {...props} />);

      expect(node.scrollTop).not.toBe(320);
    } finally {
      HTMLElement.prototype.scrollIntoView = originalScrollIntoView;
    }
  });

  it('renders active run output through the normal message stream with process items', () => {
    render(
      <MessageList
        {...baseProps}
        conversationView={{
          workspaceId: 'default',
          ownerUserId: 'single-user',
          agentId: 'agent-a',
          mainSessionId: 'session-a',
          messages: [],
          activeRun: {
            runId: 'run-a',
            workspaceId: 'default',
            ownerUserId: 'single-user',
            agentId: 'agent-a',
            mainSessionId: 'session-a',
            status: 'running',
            statusText: '正在输出',
            summary: '',
            eventCursor: 1,
            outputSnapshot: {
              markdown: 'active output',
              processItems: [
                {
                  id: 'thinking-1',
                  kind: 'thinking',
                  status: 'running',
                  text: 'active thinking process',
                  timestamp: '2026-06-07T00:00:00.000Z',
                },
              ],
            },
            startedAt: '2026-06-07T00:00:00.000Z',
            updatedAt: '2026-06-07T00:00:00.000Z',
          },
          eventCursor: 1,
          updatedAt: '2026-06-07T00:00:00.000Z',
        }}
      />,
    );

    expect(screen.getByText('active output')).toBeTruthy();
    expect(screen.getByText('active thinking process')).toBeTruthy();
    expect(screen.getAllByTestId('message-row').length).toBeGreaterThan(0);
    expect(screen.queryByLabelText('当前输出')).toBeNull();
    expect(screen.queryByTestId('empty-state')).toBeNull();
  });

  it('keeps active run markdown visible when an intermediate stream snapshot is shorter', () => {
    const activeRun = {
      runId: 'run-a',
      workspaceId: 'default',
      ownerUserId: 'single-user',
      agentId: 'agent-a',
      mainSessionId: 'session-a',
      status: 'running' as const,
      statusText: '正在输出',
      summary: '',
      eventCursor: 1,
      outputSnapshot: {
        markdown: '第一段已经输出',
        processItems: [],
      },
      startedAt: '2026-06-07T00:00:00.000Z',
      updatedAt: '2026-06-07T00:00:00.000Z',
    };
    const conversationView = {
      workspaceId: 'default',
      ownerUserId: 'single-user',
      agentId: 'agent-a',
      mainSessionId: 'session-a',
      messages: [],
      activeRun,
      eventCursor: 1,
      updatedAt: '2026-06-07T00:00:00.000Z',
    };
    const { rerender } = render(
      <MessageList {...baseProps} conversationView={conversationView} />,
    );

    expect(screen.getByText('第一段已经输出')).toBeTruthy();

    rerender(
      <MessageList
        {...baseProps}
        conversationView={{
          ...conversationView,
          activeRun: {
            ...activeRun,
            eventCursor: 2,
            outputSnapshot: {
              markdown: '',
              processItems: [],
            },
          },
          eventCursor: 2,
        }}
      />,
    );

    expect(screen.getByText('第一段已经输出')).toBeTruthy();

    rerender(
      <MessageList
        {...baseProps}
        conversationView={{
          ...conversationView,
          activeRun: {
            ...activeRun,
            eventCursor: 3,
            outputSnapshot: {
              markdown: '第一段已经输出，第二段继续输出',
              processItems: [],
            },
          },
          eventCursor: 3,
        }}
      />,
    );

    expect(screen.getByText('第一段已经输出，第二段继续输出')).toBeTruthy();
  });

  it('renders projected conversation history when legacy turns are empty', () => {
    render(
      <MessageList
        {...baseProps}
        turns={[]}
        conversationView={{
          workspaceId: 'default',
          ownerUserId: 'single-user',
          agentId: 'agent-a',
          mainSessionId: 'session-a',
          messages: [
            {
              messageId: 'user-1',
              runId: 'run-1',
              role: 'user',
              sourceId: 'admin',
              sourceName: 'Pudding Admin',
              createdAt: '2026-06-07T00:00:00.000Z',
              content: 'persisted user prompt',
              status: 'sent',
              processItems: [],
            },
            {
              messageId: 'agent-1',
              runId: 'run-1',
              role: 'agent',
              sourceId: 'agent-a',
              sourceName: 'Agent A',
              createdAt: '2026-06-07T00:00:01.000Z',
              content: 'persisted agent answer',
              status: 'succeeded',
              processItems: [],
            },
          ],
          activeRun: null,
          eventCursor: 2,
          updatedAt: '2026-06-07T00:00:02.000Z',
        }}
      />,
    );

    expect(screen.getByText('persisted user prompt')).toBeTruthy();
    expect(screen.getByText('persisted agent answer')).toBeTruthy();
    expect(screen.queryByTestId('empty-state')).toBeNull();
  });

  it('renders agent-origin inbound messages as a quote on the agent reply', () => {
    render(
      <MessageList
        {...baseProps}
        turns={[]}
        conversationView={{
          workspaceId: 'default',
          ownerUserId: 'single-user',
          agentId: 'agent-b',
          mainSessionId: 'session-b',
          messages: [
            {
              messageId: 'inbound-1',
              runId: 'run-1',
              role: 'agent',
              sourceKind: 'agent',
              sourceId: 'agent-a',
              sourceName: 'Agent A',
              messageType: 'agent_message',
              llmRole: 'user',
              createdAt: '2026-06-07T00:00:00.000Z',
              content: `<pudding-message version="1">
                <message-type>agent_message</message-type>
                <from kind="agent" id="agent-a" display-name="Agent A" />
                <context content-type="text/markdown"><![CDATA[请确认跨 Agent 消息。]]></context>
              </pudding-message>`,
              status: 'sent',
              processItems: [],
            },
            {
              messageId: 'agent-1',
              runId: 'run-1',
              role: 'agent',
              sourceId: 'agent-b',
              sourceName: 'Agent B',
              createdAt: '2026-06-07T00:00:01.000Z',
              content: '已收到 Agent A 的消息。',
              status: 'succeeded',
              processItems: [],
            },
          ],
          activeRun: null,
          eventCursor: 2,
          updatedAt: '2026-06-07T00:00:02.000Z',
        }}
      />,
    );

    const rows = screen.getAllByTestId('message-row');
    expect(rows).toHaveLength(1);
    expect(rows[0].getAttribute('data-role')).toBe('agent');
    expect(screen.getByText('已收到 Agent A 的消息。')).toBeTruthy();
    expect(screen.getByTestId('quoted-message').textContent).toContain(
      'Agent A: 请确认跨 Agent 消息。',
    );
    expect(screen.queryByText(/<pudding-message/)).toBeNull();
  });

  it('passes projected message content to the context menu callback', () => {
    const onContextMenu = jest.fn();
    render(
      <MessageList
        {...baseProps}
        turns={[]}
        onContextMenu={onContextMenu}
        conversationView={{
          workspaceId: 'default',
          ownerUserId: 'single-user',
          agentId: 'agent-a',
          mainSessionId: 'session-a',
          messages: [
            {
              messageId: 'user-1',
              runId: 'run-1',
              role: 'user',
              sourceId: 'admin',
              sourceName: 'Pudding Admin',
              createdAt: '2026-06-07T00:00:00.000Z',
              content: 'projected user prompt for copy',
              status: 'sent',
              processItems: [],
            },
            {
              messageId: 'agent-1',
              runId: 'run-1',
              role: 'agent',
              sourceId: 'agent-a',
              sourceName: 'Agent A',
              createdAt: '2026-06-07T00:00:01.000Z',
              content: 'projected agent answer for copy',
              status: 'succeeded',
              processItems: [],
            },
          ],
          activeRun: null,
          eventCursor: 2,
          updatedAt: '2026-06-07T00:00:02.000Z',
        }}
      />,
    );

    fireEvent.contextMenu(screen.getByText('projected user prompt for copy'));
    fireEvent.contextMenu(screen.getByText('projected agent answer for copy'));

    expect(onContextMenu).toHaveBeenNthCalledWith(
      1,
      expect.anything(),
      'run-1',
      'user',
      'projected user prompt for copy',
    );
    expect(onContextMenu).toHaveBeenNthCalledWith(
      2,
      expect.anything(),
      'run-1',
      'assistant',
      'projected agent answer for copy',
    );
  });

  it('prefers projected conversation history over stale legacy turns', () => {
    render(
      <MessageList
        {...baseProps}
        turns={[
          {
            turnId: 'stale-turn',
            userMessage: {
              id: 'stale-user',
              text: 'stale user prompt',
              timestamp: 1,
              status: 'success',
            },
            assistant: {
              id: 'stale-agent',
              status: 'success',
              timelineItems: [],
              answerMarkdown: 'stale legacy answer',
              isStreaming: false,
              renderMode: 'structured',
            },
          },
        ]}
        conversationView={{
          workspaceId: 'default',
          ownerUserId: 'single-user',
          agentId: 'agent-a',
          mainSessionId: 'session-a',
          messages: [
            {
              messageId: 'user-1',
              runId: 'run-1',
              role: 'user',
              sourceId: 'admin',
              sourceName: 'Pudding Admin',
              createdAt: '2026-06-07T00:00:00.000Z',
              content: 'projected user prompt',
              status: 'sent',
              processItems: [],
            },
            {
              messageId: 'agent-1',
              runId: 'run-1',
              role: 'agent',
              sourceId: 'agent-a',
              sourceName: 'Agent A',
              createdAt: '2026-06-07T00:00:01.000Z',
              content: 'projected agent answer',
              status: 'succeeded',
              processItems: [],
            },
          ],
          activeRun: null,
          eventCursor: 2,
          updatedAt: '2026-06-07T00:00:02.000Z',
        }}
      />,
    );

    expect(screen.getByText('projected user prompt')).toBeTruthy();
    expect(screen.getByText('projected agent answer')).toBeTruthy();
    expect(screen.queryByText('stale user prompt')).toBeNull();
    expect(screen.queryByText('stale legacy answer')).toBeNull();
  });

  it('keeps a local pending turn visible until projection materializes it', () => {
    render(
      <MessageList
        {...baseProps}
        turns={[
          {
            turnId: 'pending-turn',
            userMessage: {
              id: 'pending-user',
              text: 'new local prompt',
              timestamp: 3,
              status: 'sending',
            },
            assistant: {
              id: 'pending-agent',
              status: 'thinking',
              timelineItems: [],
              answerMarkdown: '',
              isStreaming: true,
              renderMode: 'structured',
            },
          },
        ]}
        conversationView={{
          workspaceId: 'default',
          ownerUserId: 'single-user',
          agentId: 'agent-a',
          mainSessionId: 'session-a',
          messages: [
            {
              messageId: 'user-1',
              runId: 'run-1',
              role: 'user',
              sourceId: 'admin',
              sourceName: 'Pudding Admin',
              createdAt: '2026-06-07T00:00:00.000Z',
              content: 'projected user prompt',
              status: 'sent',
              processItems: [],
            },
            {
              messageId: 'agent-1',
              runId: 'run-1',
              role: 'agent',
              sourceId: 'agent-a',
              sourceName: 'Agent A',
              createdAt: '2026-06-07T00:00:01.000Z',
              content: 'projected agent answer',
              status: 'succeeded',
              processItems: [],
            },
          ],
          activeRun: null,
          eventCursor: 2,
          updatedAt: '2026-06-07T00:00:02.000Z',
        }}
      />,
    );

    expect(screen.getByText('projected user prompt')).toBeTruthy();
    expect(screen.getByText('projected agent answer')).toBeTruthy();
    expect(screen.getByText('new local prompt')).toBeTruthy();
  });

  it('keeps a repeated pending prompt visible when older projection has the same text', () => {
    render(
      <MessageList
        {...baseProps}
        turns={[
          {
            turnId: 'pending-repeat-turn',
            userMessage: {
              id: 'pending-repeat-user',
              text: '你好',
              timestamp: 30_000,
              status: 'sending',
            },
            assistant: {
              id: 'pending-repeat-agent',
              status: 'thinking',
              timelineItems: [],
              answerMarkdown: '',
              isStreaming: true,
              renderMode: 'structured',
            },
          },
        ]}
        conversationView={{
          workspaceId: 'default',
          ownerUserId: 'single-user',
          agentId: 'agent-a',
          mainSessionId: 'session-a',
          messages: [
            {
              messageId: 'user-old',
              runId: 'run-old',
              role: 'user',
              sourceId: 'admin',
              sourceName: 'Pudding Admin',
              createdAt: '1970-01-01T00:00:10.000Z',
              content: '你好',
              status: 'sent',
              processItems: [],
            },
            {
              messageId: 'agent-old',
              runId: 'run-old',
              role: 'agent',
              sourceId: 'agent-a',
              sourceName: 'Agent A',
              createdAt: '1970-01-01T00:00:11.000Z',
              content: '旧回复',
              status: 'succeeded',
              processItems: [],
            },
          ],
          activeRun: null,
          eventCursor: 2,
          updatedAt: '1970-01-01T00:00:11.000Z',
        }}
      />,
    );

    expect(screen.getAllByText('你好')).toHaveLength(2);
    expect(screen.getByText('旧回复')).toBeTruthy();
    expect(screen.getAllByTestId('message-row')).toHaveLength(4);
  });

  it('does not attach an older active run output to a newer pending prompt', () => {
    render(
      <MessageList
        {...baseProps}
        turns={[
          {
            turnId: 'pending-new-question',
            userMessage: {
              id: 'pending-new-user',
              text: 'new current question',
              timestamp: 30_000,
              status: 'sending',
            },
            assistant: {
              id: 'pending-new-agent',
              status: 'thinking',
              timelineItems: [],
              answerMarkdown: '',
              isStreaming: true,
              renderMode: 'structured',
            },
          },
        ]}
        conversationView={{
          workspaceId: 'default',
          ownerUserId: 'single-user',
          agentId: 'agent-a',
          mainSessionId: 'session-a',
          messages: [],
          activeRun: {
            runId: 'older-run',
            workspaceId: 'default',
            ownerUserId: 'single-user',
            agentId: 'agent-a',
            mainSessionId: 'session-a',
            status: 'running',
            statusText: '正在输出',
            summary: '',
            eventCursor: 1,
            outputSnapshot: {
              markdown: 'older run output',
              processItems: [],
            },
            startedAt: '1970-01-01T00:00:10.000Z',
            updatedAt: '1970-01-01T00:00:10.000Z',
          },
          eventCursor: 1,
          updatedAt: '1970-01-01T00:00:10.000Z',
        }}
      />,
    );

    const rows = screen.getAllByTestId('message-row');
    expect(rows.map((row) => row.getAttribute('data-role'))).toEqual([
      'agent',
      'user',
      'agent',
    ]);
    expect(rows[0].textContent).toContain('older run output');
    expect(rows[1].textContent).toContain('new current question');
    expect(rows[2].textContent).not.toContain('older run output');
  });

  it('attaches active run output to the pending prompt with the same server message id', () => {
    render(
      <MessageList
        {...baseProps}
        turns={[
          {
            turnId: 'pending-current-question',
            userMessage: {
              id: 'message-current',
              text: 'current question',
              timestamp: 30_000,
              status: 'sending',
            },
            assistant: {
              id: 'pending-current-agent',
              status: 'thinking',
              timelineItems: [],
              answerMarkdown: '',
              isStreaming: true,
              renderMode: 'structured',
            },
          },
        ]}
        conversationView={{
          workspaceId: 'default',
          ownerUserId: 'single-user',
          agentId: 'agent-a',
          mainSessionId: 'session-a',
          messages: [],
          activeRun: {
            runId: 'run-current',
            commandClientId: 'message-current',
            workspaceId: 'default',
            ownerUserId: 'single-user',
            agentId: 'agent-a',
            mainSessionId: 'session-a',
            status: 'running',
            statusText: '正在输出',
            summary: '',
            eventCursor: 1,
            outputSnapshot: {
              markdown: 'current answer fragment',
              processItems: [],
            },
            startedAt: '1970-01-01T00:00:10.000Z',
            updatedAt: '1970-01-01T00:00:10.000Z',
          },
          eventCursor: 1,
          updatedAt: '1970-01-01T00:00:10.000Z',
        }}
      />,
    );

    const rows = screen.getAllByTestId('message-row');
    expect(rows.map((row) => row.getAttribute('data-role'))).toEqual([
      'user',
      'agent',
    ]);
    expect(rows[0].textContent).toContain('current question');
    expect(rows[1].textContent).toContain('current answer fragment');
  });

  it('renders completed sub-agent cards at their chronological position instead of appending them after later messages', () => {
    render(
      <MessageList
        {...baseProps}
        turns={[
          {
            turnId: 'turn-before',
            userMessage: {
              id: 'user-before',
              text: 'message before sub-agent',
              timestamp: 1_000,
              status: 'success',
            },
            assistant: {
              id: 'agent-before',
              status: 'success',
              timelineItems: [],
              answerMarkdown: 'answer before sub-agent',
              isStreaming: false,
              renderMode: 'structured',
            },
          },
          {
            turnId: 'turn-after',
            userMessage: {
              id: 'user-after',
              text: 'message after sub-agent',
              timestamp: 3_000,
              status: 'success',
            },
            assistant: {
              id: 'agent-after',
              status: 'success',
              timelineItems: [],
              answerMarkdown: 'answer after sub-agent',
              isStreaming: false,
              renderMode: 'structured',
            },
          },
        ]}
        subAgentCards={{
          'sa-child': {
            turnId: 'sa-child',
            subSessionId: 'session-1-sub-child',
            parentSessionId: 'session-1',
            taskSummary: 'child task',
            status: 'completed',
            spawnedAt: 1_500,
            completedAt: 2_000,
            output: 'child done',
            success: true,
          },
        }}
      />,
    );

    const before = screen.getByText('answer before sub-agent');
    const card = screen.getByText('child done');
    const after = screen.getByText('message after sub-agent');

    expect(
      Boolean(
        before.compareDocumentPosition(card) & Node.DOCUMENT_POSITION_FOLLOWING,
      ),
    ).toBe(true);
    expect(
      Boolean(
        card.compareDocumentPosition(after) & Node.DOCUMENT_POSITION_FOLLOWING,
      ),
    ).toBe(true);
  });

  it('anchors completed sub-agent cards by spawn time instead of completion time', () => {
    render(
      <MessageList
        {...baseProps}
        turns={[
          {
            turnId: 'turn-before',
            userMessage: {
              id: 'user-before',
              text: 'message before sub-agent',
              timestamp: 1_000,
              status: 'success',
            },
            assistant: {
              id: 'agent-before',
              status: 'success',
              timelineItems: [],
              answerMarkdown: 'answer before sub-agent',
              isStreaming: false,
              renderMode: 'structured',
            },
          },
          {
            turnId: 'turn-after',
            userMessage: {
              id: 'user-after',
              text: 'message after sub-agent',
              timestamp: 3_000,
              status: 'success',
            },
            assistant: {
              id: 'agent-after',
              status: 'success',
              timelineItems: [],
              answerMarkdown: 'answer after sub-agent',
              isStreaming: false,
              renderMode: 'structured',
            },
          },
        ]}
        subAgentCards={{
          'sa-child': {
            turnId: 'sa-child',
            subSessionId: 'session-1-sub-child',
            parentSessionId: 'session-1',
            taskSummary: 'child task',
            status: 'completed',
            spawnedAt: 1_500,
            completedAt: 4_000,
            output: 'late child done',
            success: true,
          },
        }}
      />,
    );

    const before = screen.getByText('answer before sub-agent');
    const card = screen.getByText('late child done');
    const after = screen.getByText('message after sub-agent');

    expect(
      Boolean(
        before.compareDocumentPosition(card) & Node.DOCUMENT_POSITION_FOLLOWING,
      ),
    ).toBe(true);
    expect(
      Boolean(
        card.compareDocumentPosition(after) & Node.DOCUMENT_POSITION_FOLLOWING,
      ),
    ).toBe(true);
  });
});
