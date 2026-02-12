import { render, screen } from '@testing-library/react';
import * as React from 'react';
import AgentMessageBubble from './AgentMessageBubble';

const mockUseTypewriterStreaming = jest.fn();
const mockMessageActions = jest.fn((_props: Record<string, unknown>) => (
  <div data-testid="message-actions" />
));
const mockMessageItem = jest.fn((props: Record<string, unknown>) => (
  <div
    data-testid="message-item"
    data-markdown={String(props.markdownText ?? '')}
    data-stable={String(props.stableMarkdown ?? '')}
    data-live={String(props.liveText ?? '')}
    data-visible={String(props.visibleLiveText ?? '')}
  />
));

jest.mock('antd', () => {
  const React = require('react');
  return {
    unstableSetRender: jest.fn(),
    Tooltip: ({ children, title, ...props }: any) => (
      <span
        data-testid="antd-tooltip"
        data-title={typeof title === 'string' ? title : ''}
        data-placement={props.placement ?? ''}
      >
        {children}
      </span>
    ),
  };
});

jest.mock('../styles', () => {
  const styles = new Proxy(
    {},
    {
      get: (_target, prop) => String(prop),
    },
  );
  return {
    useChatStyles: () => ({
      styles,
      cx: (...values: Array<string | false | undefined>) =>
        values.filter(Boolean).join(' '),
    }),
  };
});

jest.mock('../hooks/useTypewriterStreaming', () => ({
  useTypewriterStreaming: (...args: unknown[]) =>
    mockUseTypewriterStreaming(...args),
}));

jest.mock('../hooks/useTtsPlayer', () => ({
  useTtsPlayer: () => ({
    speak: jest.fn(),
    playing: false,
    loading: false,
  }),
}));

jest.mock('./AgentAvatar', () => () => <div data-testid="agent-avatar" />);
jest.mock(
  './MessageActions',
  () => (props: Record<string, unknown>) => mockMessageActions(props),
);
jest.mock(
  './MessageItem',
  () => (props: Record<string, unknown>) => mockMessageItem(props),
);
jest.mock('./SessionBenchmarkDrawer', () => () => (
  <div data-testid="session-benchmark-drawer" />
));

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
    mockMessageActions.mockClear();
    mockUseTypewriterStreaming.mockReturnValue({
      stableMarkdown: '',
      liveText: '',
      visibleLiveText: '',
      visibleStartOffset: 0,
      isTyping: false,
      isSettling: false,
    });
  });

  it('shows a sanitized runtime activity before the first answer token without rendering an empty answer bubble', () => {
    render(
      <AgentMessageBubble
        {...baseProps}
        content=""
        processItems={[
          {
            id: 'thinking-1',
            type: 'thinking',
            text: '用户问的是商用密码应用安全性评估。undefinedundefined',
            timestamp: 1,
            collapsed: true,
          },
        ]}
      />,
    );

    expect(screen.queryByTestId('message-item')).toBeNull();
    expect(screen.getByText('模型过程')).toBeTruthy();
    expect(screen.getByText('用户问的是商用密码应用安全性评估。')).toBeTruthy();
    expect(screen.queryByText(/undefined/)).toBeNull();
  });

  it('shows the thinking placeholder before metadata marks the answer as streaming', () => {
    render(
      <AgentMessageBubble
        {...baseProps}
        status="thinking"
        isStreaming={false}
        content=""
      />,
    );

    expect(screen.queryByTestId('message-item')).toBeNull();
    expect(screen.getByText('等待运行事件...')).toBeTruthy();
  });

  it('shows the current tool interaction as the default visible activity', () => {
    const { container } = render(
      <AgentMessageBubble
        {...baseProps}
        status="executing"
        isStreaming={false}
        content=""
        processItems={[
          {
            id: 'tool-1',
            type: 'tool_call',
            name: 'shell',
            status: 'tool_call',
            arguments: 'dotnet build Source/PuddingAgent/PuddingAgent.csproj',
            timestamp: Date.now() - 1200,
            collapsed: false,
          },
        ]}
      />,
    );

    expect(screen.queryByTestId('message-item')).toBeNull();
    expect(screen.getByText('正在调用工具：shell')).toBeTruthy();
    expect(screen.getByText('运行中')).toBeTruthy();
    expect(
      screen.getByText(
        '命令：dotnet build Source/PuddingAgent/PuddingAgent.csproj',
      ),
    ).toBeTruthy();
    expect(container.querySelector('.agentActiveOutputSurface')).toBeTruthy();
  });

  it('summarizes JSON tool arguments instead of showing raw JSON in the default activity panel', () => {
    const rawArguments = JSON.stringify({
      task: '对 PuddingAgent 项目进行代码 QA，重点检查注释是否完成。',
      perspective: 'reviewer',
    });

    render(
      <AgentMessageBubble
        {...baseProps}
        status="executing"
        isStreaming={false}
        content=""
        processItems={[
          {
            id: 'tool-1',
            type: 'tool_call',
            name: 'spawn_sub_agent',
            status: 'tool_call',
            arguments: rawArguments,
            timestamp: Date.now() - 1200,
            collapsed: false,
          },
        ]}
      />,
    );

    expect(screen.getByText('正在调用工具：spawn_sub_agent')).toBeTruthy();
    expect(
      screen.getByText(
        '任务：对 PuddingAgent 项目进行代码 QA，重点检查注释是否完成。',
      ),
    ).toBeTruthy();
    expect(document.body.textContent).not.toContain('"perspective"');
    expect(
      screen.queryByTestId('antd-tooltip')?.getAttribute('data-title') ?? '',
    ).not.toContain(rawArguments);
  });

  it('does not show raw JSON tool parameters in a hover tooltip for summarized argument rows', () => {
    const rawArguments = '{"symbol_name":"file_patch"}';

    render(
      <AgentMessageBubble
        {...baseProps}
        status="executing"
        isStreaming={false}
        content=""
        processItems={[
          {
            id: 'tool-1',
            type: 'tool_call',
            name: 'code_summary',
            status: 'tool_call',
            arguments: rawArguments,
            timestamp: Date.now() - 1200,
            collapsed: false,
          },
        ]}
      />,
    );

    expect(
      screen.getByText('参数：已记录，点击“查看过程”查看完整参数'),
    ).toBeTruthy();
    expect(
      screen.queryByTestId('antd-tooltip')?.getAttribute('data-title') ?? '',
    ).not.toContain(rawArguments);
  });

  it('shows a tail preview for long tool output and keeps the full output out of the default panel', () => {
    render(
      <AgentMessageBubble
        {...baseProps}
        status="executing"
        isStreaming={false}
        content=""
        processItems={[
          {
            id: 'tool-call-1',
            type: 'tool_call',
            name: 'shell',
            status: 'tool_call',
            arguments: 'git diff --stat',
            timestamp: Date.now() - 2200,
            collapsed: false,
          },
          {
            id: 'tool-result-1',
            type: 'tool_result',
            name: 'shell',
            status: 'success',
            output: [
              'line 1',
              'line 2',
              'line 3',
              'line 4',
              'line 5',
              'line 6',
            ].join('\n'),
            exitCode: 0,
            timestamp: Date.now() - 1200,
            collapsed: false,
          },
        ]}
      />,
    );

    expect(screen.getByText('工具调用完成：shell')).toBeTruthy();
    expect(screen.getByText('已完成')).toBeTruthy();
    expect(document.body.textContent).not.toContain('正在处理结果');
    expect(document.body.textContent).not.toContain('已运行');
    expect(document.body.textContent).toContain('line 2');
    expect(document.body.textContent).toContain('line 6');
    expect(document.body.textContent).toContain(
      '输出较长，已截取最近 5 行 · 查看过程',
    );
    expect(document.body.textContent).not.toContain('line 1');
  });

  it('does not show an old successful tool result as still running when the assistant status is stale', () => {
    const { container } = render(
      <AgentMessageBubble
        {...baseProps}
        status="executing"
        isStreaming={false}
        content="Now let me execute the self-improvement scan."
        processItems={[
          {
            id: 'tool-call-1',
            type: 'tool_call',
            name: 'terminal_execute',
            status: 'tool_call',
            arguments: 'dotnet test --no-build --verbosity quiet 2>&1',
            timestamp: Date.now() - 231 * 60 * 1000,
            collapsed: false,
          },
          {
            id: 'tool-result-1',
            type: 'tool_result',
            name: 'terminal_execute',
            output: 'bbc6acfb1bca',
            exitCode: 0,
            timestamp: Date.now() - 231 * 60 * 1000 + 20,
            collapsed: false,
          },
        ]}
      />,
    );

    expect(screen.getByText('工具调用完成：terminal_execute')).toBeTruthy();
    expect(screen.getByText('已完成')).toBeTruthy();
    expect(document.body.textContent).toContain('bbc6acfb1bca');
    expect(document.body.textContent).not.toContain('正在处理结果');
    expect(document.body.textContent).not.toContain('已运行 231 分');
    expect(container.querySelector('.agentActiveOutputSurface')).toBeNull();
  });

  it('uses typewriter slices for streaming answers and collapses the process timeline while printing', () => {
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
        processItems={[
          {
            id: 'thinking-1',
            type: 'thinking',
            text: '用户问的是商用密码应用安全性评估。',
            timestamp: 1,
            collapsed: true,
          },
        ]}
      />,
    );

    const item = screen.getByTestId('message-item');
    expect(item.getAttribute('data-markdown')).toBe('完整回答');
    expect(item.getAttribute('data-stable')).toBe('稳定段落');
    expect(item.getAttribute('data-live')).toBe('尾段完整文本');
    expect(item.getAttribute('data-visible')).toBe('尾段');
    expect(mockUseTypewriterStreaming).toHaveBeenCalledWith(
      expect.objectContaining({
        isStreaming: true,
        tickMs: 40,
        maxLagChars: 48,
      }),
    );
    expect(screen.getByText(/已思考/)).toBeTruthy();
    expect(screen.getByText('查看过程')).toBeTruthy();
  });

  it('marks the answer bubble as an active output surface while it is streaming', () => {
    mockUseTypewriterStreaming.mockReturnValue({
      stableMarkdown: '',
      liveText: '正在输出',
      visibleLiveText: '正在',
      visibleStartOffset: 0,
      isTyping: true,
      isSettling: false,
    });

    const { container } = render(
      <AgentMessageBubble
        {...baseProps}
        status="streaming"
        isStreaming
        content="正在输出"
      />,
    );

    expect(screen.getByTestId('message-item')).toBeTruthy();
    expect(
      container.querySelector('.agentBubbleNew.agentActiveOutputSurface'),
    ).toBeTruthy();
  });

  it('passes browser voice output to assistant message actions after answer content is available', () => {
    render(
      <AgentMessageBubble
        {...baseProps}
        status="success"
        isStreaming={false}
        content="整理今天的会议记录。"
      />,
    );

    expect(mockMessageActions).toHaveBeenCalledWith(
      expect.objectContaining({
        content: '整理今天的会议记录。',
        voiceOutputAdapter: expect.objectContaining({
          isSupported: expect.any(Function),
          speak: expect.any(Function),
        }),
      }),
    );
    expect(mockUseTypewriterStreaming).not.toHaveBeenCalled();
  });
});
