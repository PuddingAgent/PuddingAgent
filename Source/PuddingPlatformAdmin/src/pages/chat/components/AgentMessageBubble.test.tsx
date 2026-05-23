import { render, screen } from '@testing-library/react';
import * as React from 'react';
import AgentMessageBubble from './AgentMessageBubble';

const mockUseTypewriterStreaming = jest.fn();
const mockMessageItem = jest.fn((props: Record<string, unknown>) => (
  <div
    data-testid="message-item"
    data-markdown={String(props.markdownText ?? '')}
    data-stable={String(props.stableMarkdown ?? '')}
    data-live={String(props.liveText ?? '')}
    data-visible={String(props.visibleLiveText ?? '')}
  />
));

jest.mock('../styles', () => {
  const styles = new Proxy({}, {
    get: (_target, prop) => String(prop),
  });
  return {
    useChatStyles: () => ({
      styles,
      cx: (...values: Array<string | false | undefined>) => values.filter(Boolean).join(' '),
    }),
  };
});

jest.mock('../hooks/useTypewriterStreaming', () => ({
  useTypewriterStreaming: (...args: unknown[]) => mockUseTypewriterStreaming(...args),
}));

jest.mock('./AgentAvatar', () => () => <div data-testid="agent-avatar" />);
jest.mock('./MessageActions', () => () => <div data-testid="message-actions" />);
jest.mock('./MessageItem', () => (props: Record<string, unknown>) => mockMessageItem(props));

const baseProps = {
  id: 'assistant-1',
  status: 'streaming',
  createdAt: 1,
  agentName: 'Pudding',
  isStreaming: true,
  formatTime: () => '刚刚',
};

describe('AgentMessageBubble streaming presentation', () => {
  beforeEach(() => {
    mockUseTypewriterStreaming.mockReset();
    mockMessageItem.mockClear();
    mockUseTypewriterStreaming.mockReturnValue({
      stableMarkdown: '',
      liveText: '',
      visibleLiveText: '',
      visibleStartOffset: 0,
      isTyping: false,
      isSettling: false,
    });
  });

  it('shows the thinking preview before the first answer token without rendering an empty answer bubble', () => {
    render(
      <AgentMessageBubble
        {...baseProps}
        content=""
        processItems={[{
          id: 'thinking-1',
          type: 'thinking',
          text: '用户问的是商用密码应用安全性评估。',
          timestamp: 1,
          collapsed: true,
        }]}
      />,
    );

    expect(screen.queryByTestId('message-item')).toBeNull();
    expect(screen.getByText('正在思考...')).toBeTruthy();
    expect(screen.getByText('用户问的是商用密码应用安全性评估。')).toBeTruthy();
  });

  it('uses typewriter slices for streaming answers and hides live thinking text while printing', () => {
    mockUseTypewriterStreaming.mockReturnValue({
      stableMarkdown: '稳定段落',
      liveText: '尾段完整文本',
      visibleLiveText: '尾段',
      visibleStartOffset: 0,
      isTyping: true,
      isSettling: false,
    });

    render(
      <AgentMessageBubble
        {...baseProps}
        content="完整回答"
        processItems={[{
          id: 'thinking-1',
          type: 'thinking',
          text: '用户问的是商用密码应用安全性评估。',
          timestamp: 1,
          collapsed: true,
        }]}
      />,
    );

    const item = screen.getByTestId('message-item');
    expect(item.getAttribute('data-markdown')).toBe('完整回答');
    expect(item.getAttribute('data-stable')).toBe('稳定段落');
    expect(item.getAttribute('data-live')).toBe('尾段完整文本');
    expect(item.getAttribute('data-visible')).toBe('尾段');
    expect(screen.queryByText('用户问的是商用密码应用安全性评估。')).toBeNull();
    expect(screen.queryByText(/已思考/)).toBeNull();
  });
});
