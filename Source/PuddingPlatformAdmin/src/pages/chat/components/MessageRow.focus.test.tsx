// ── MessageRow P2#8 Focus view 单行折叠模式测试 ─────────────
import { act, fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import MessageRow from './MessageRow';
import { makeAgentBlock, makeUserBlock } from './MessageRow.focus.fixtures';

jest.mock('./AgentMessageBubble', () => (props: any) => (
  <div data-testid="agent-bubble">
    {String(props.content ?? '')}
    {props.processItems?.length ? (
      <span data-testid="agent-bubble-has-process">has-process</span>
    ) : null}
  </div>
));
jest.mock('./UserMessageBubble', () => (props: any) => (
  <div data-testid="user-bubble">{String(props.content ?? '')}</div>
));
jest.mock('./MessageItem', () => (props: any) => (
  <div data-testid="message-item">{String(props.markdownText ?? '')}</div>
));

const formatTime = (timestamp: number) => `T:${timestamp}`;
const baseHandlers = {
  formatTime,
  onContextMenu: jest.fn(),
  onRerunTurn: jest.fn(),
  onPinTurn: jest.fn(),
  onDeleteTurn: jest.fn(),
};

describe('MessageRow Focus view', () => {
  it('renders agent row as a collapsed single line in focus view', () => {
    render(<MessageRow block={makeAgentBlock()} focusView {...baseHandlers} />);
    const row = screen.getByTestId('focus-view-row');
    expect(row.dataset.expanded).toBe('false');
    // 折叠时隐藏完整气泡与工具过程
    expect(screen.queryByTestId('agent-bubble')).toBeNull();
    expect(screen.queryByTestId('agent-bubble-has-process')).toBeNull();
    // 单行摘要可见
    expect(
      screen.getByText(
        '这是一段完整的 Agent 回复内容，用于验证展开后完整渲染。',
      ),
    ).toBeTruthy();
  });

  it('expands the full content when the focus row is clicked', () => {
    render(
      <MessageRow
        block={makeAgentBlock({
          content: '展开后应显示完整回复正文。',
        })}
        focusView
        {...baseHandlers}
      />,
    );
    fireEvent.click(screen.getByTestId('focus-view-row-header'));
    const row = screen.getByTestId('focus-view-row');
    expect(row.dataset.expanded).toBe('true');
    expect(screen.getByTestId('agent-bubble').textContent).toContain(
      '展开后应显示完整回复正文。',
    );
  });

  it('collapses back after a second click', () => {
    render(<MessageRow block={makeAgentBlock()} focusView {...baseHandlers} />);
    const header = screen.getByTestId('focus-view-row-header');
    fireEvent.click(header);
    fireEvent.click(screen.getByTestId('focus-view-row-header'));
    expect(screen.getByTestId('focus-view-row').dataset.expanded).toBe('false');
    expect(screen.queryByTestId('agent-bubble')).toBeNull();
  });

  it('renders user row collapsed and expands to the full user bubble', () => {
    render(<MessageRow block={makeUserBlock()} focusView {...baseHandlers} />);
    expect(screen.getByTestId('focus-view-row').dataset.expanded).toBe('false');
    expect(screen.queryByTestId('user-bubble')).toBeNull();
    expect(screen.getByText('用户提问内容预览')).toBeTruthy();
    fireEvent.click(screen.getByTestId('focus-view-row-header'));
    expect(screen.getByTestId('focus-view-row').dataset.expanded).toBe('true');
    expect(screen.getByTestId('user-bubble').textContent).toContain(
      '用户提问内容预览',
    );
  });

  it('shows the running tool as the collapsed summary while streaming', () => {
    render(
      <MessageRow
        block={makeAgentBlock({
          status: 'streaming',
          isStreaming: true,
          content: '',
          processItems: [
            {
              id: 'tool-1',
              type: 'tool_call',
              status: 'running',
              name: 'read_file',
              timestamp: 2000,
              collapsed: true,
            },
          ],
        })}
        focusView
        {...baseHandlers}
      />,
    );
    expect(screen.getByText('正在调用工具：read_file')).toBeTruthy();
    const header = screen.getByTestId('focus-view-row-header');
    expect(header.className).toContain('focusViewRowHeaderRunning');
    // 运行中不渲染任何工具明细（保持单行）
    expect(screen.queryByTestId('agent-bubble-has-process')).toBeNull();
  });

  it('marks error rows with the error tone and keeps the summary visible', () => {
    render(
      <MessageRow
        block={makeAgentBlock({
          status: 'error',
          content: '执行失败：网络超时',
        })}
        focusView
        {...baseHandlers}
      />,
    );
    const header = screen.getByTestId('focus-view-row-header');
    expect(header.className).toContain('focusViewRowHeaderError');
    expect(screen.getByText('执行失败：网络超时')).toBeTruthy();
  });

  it('renders the regular bubble directly when focus view is off', () => {
    render(
      <MessageRow
        block={makeAgentBlock({
          content: '普通模式正文',
          processItems: [
            {
              id: 'tool-1',
              type: 'tool_result',
              status: 'success',
              output: 'ok',
              timestamp: 2000,
              collapsed: true,
            },
          ],
        })}
        focusView={false}
        {...baseHandlers}
      />,
    );
    expect(screen.queryByTestId('focus-view-row')).toBeNull();
    expect(screen.getByTestId('agent-bubble').textContent).toContain(
      '普通模式正文',
    );
  });
});

describe('MessageRow visible-turn hydration', () => {
  const originalIntersectionObserver = window.IntersectionObserver;

  afterEach(() => {
    window.IntersectionObserver = originalIntersectionObserver;
  });

  it('registers an agent turn only after it enters the message viewport buffer', () => {
    let observerCallback: IntersectionObserverCallback | undefined;
    const observe = jest.fn();
    const disconnect = jest.fn();
    const observer = {
      root: null,
      rootMargin: '600px 0px',
      thresholds: [0],
      observe,
      unobserve: jest.fn(),
      disconnect,
      takeRecords: () => [],
    } as unknown as IntersectionObserver;
    window.IntersectionObserver = jest.fn((callback) => {
      observerCallback = callback;
      return observer;
    }) as unknown as typeof IntersectionObserver;
    const onTurnVisible = jest.fn();

    render(
      <div data-testid="chat-message-list">
        <MessageRow
          block={makeAgentBlock({ turnId: 'turn-visible' })}
          onTurnVisible={onTurnVisible}
          {...baseHandlers}
        />
      </div>,
    );

    expect(observe).toHaveBeenCalledTimes(1);
    expect(onTurnVisible).not.toHaveBeenCalled();

    act(() => {
      observerCallback?.(
        [{ isIntersecting: false } as IntersectionObserverEntry],
        observer,
      );
    });
    expect(onTurnVisible).not.toHaveBeenCalled();

    act(() => {
      observerCallback?.(
        [{ isIntersecting: true } as IntersectionObserverEntry],
        observer,
      );
    });
    expect(onTurnVisible).toHaveBeenCalledTimes(1);
    expect(onTurnVisible).toHaveBeenCalledWith('turn-visible');
    expect(disconnect).toHaveBeenCalled();
  });
});
