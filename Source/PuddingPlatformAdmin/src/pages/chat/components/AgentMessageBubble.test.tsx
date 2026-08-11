import { fireEvent, render, screen } from '@testing-library/react';
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

jest.mock('../styles/reasoning.styles', () => {
  const styles = new Proxy(
    {},
    {
      get: (_target, prop) => String(prop),
    },
  );
  return {
    useReasoningStyles: () => ({ styles }),
  };
});

jest.mock('../styles/agent.styles', () => {
  const styles = new Proxy(
    {},
    {
      get: (_target, prop) => String(prop),
    },
  );
  return {
    useAgentStyles: () => ({ styles }),
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

jest.mock('../client/agentChatApi', () => ({
  getAgentMessageProcessItems: jest.fn(),
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
  createdAt: Date.now(),
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

  it('does not instantiate message actions until the row is interacted with', () => {
    const { container } = render(
      <AgentMessageBubble
        {...baseProps}
        content="已经完成。"
        isStreaming={false}
        status="success"
      />,
    );

    expect(mockMessageActions).not.toHaveBeenCalled();

    const row = container.firstElementChild as HTMLElement;
    fireEvent.mouseEnter(row);
    expect(mockMessageActions).toHaveBeenLastCalledWith(
      expect.objectContaining({ visible: true }),
    );

    fireEvent.mouseLeave(row);
    expect(mockMessageActions).toHaveBeenLastCalledWith(
      expect.objectContaining({ visible: false }),
    );
  });

  it('shows a sanitized reasoning summary alongside the main-agent activity before the first answer token', () => {
    const { container } = render(
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
    expect(screen.getByText('推理摘要')).toBeTruthy();
    expect(screen.getByText('用户问的是商用密码应用安全性评估。')).toBeTruthy();
    expect(screen.queryByText(/undefined/)).toBeNull();
    expect(container.querySelector('.reasoningContainer')).toBeTruthy();
  });

  it('shows only the last three reasoning lines in the preview', () => {
    render(
      <AgentMessageBubble
        {...baseProps}
        content=""
        processItems={[
          {
            id: 't1',
            type: 'thinking',
            text: '思维链第一行',
            timestamp: 1,
            collapsed: true,
          },
          {
            id: 't2',
            type: 'thinking',
            text: '思维链第二行',
            timestamp: 2,
            collapsed: true,
          },
          {
            id: 't3',
            type: 'thinking',
            text: '思维链第三行',
            timestamp: 3,
            collapsed: true,
          },
          {
            id: 't4',
            type: 'thinking',
            text: '思维链第四行',
            timestamp: 4,
            collapsed: true,
          },
        ]}
      />,
    );

    expect(screen.queryByText('思维链第一行')).toBeNull();
    expect(screen.getByText('思维链第二行')).toBeTruthy();
    expect(screen.getByText('思维链第三行')).toBeTruthy();
    expect(screen.getByText('思维链第四行')).toBeTruthy();
    expect(screen.getByText('持续推理中...')).toBeTruthy();
  });

  it('keeps the latest reasoning summary visible when a tool call starts', () => {
    render(
      <AgentMessageBubble
        {...baseProps}
        status="executing"
        isStreaming={false}
        content=""
        processItems={[
          {
            id: 'thinking-1',
            type: 'thinking',
            text: '需要先查看项目结构。',
            timestamp: 1,
            collapsed: true,
          },
          {
            id: 'tool-1',
            type: 'tool_call',
            name: 'list_dir',
            status: 'tool_call',
            arguments: '.',
            timestamp: 2,
            collapsed: false,
          },
        ]}
      />,
    );

    expect(screen.getByText('正在调用工具：list_dir')).toBeTruthy();
    expect(screen.getByText('最近推理摘要')).toBeTruthy();
    expect(screen.getByText('需要先查看项目结构。')).toBeTruthy();
    expect(screen.getByText('当前：正在调用工具：list_dir')).toBeTruthy();
  });

  it('keeps the reasoning preview when older tool activity predates the latest thinking', () => {
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
            name: 'list_dir',
            status: 'tool_call',
            arguments: '.',
            timestamp: 1,
            collapsed: false,
          },
          {
            id: 'thinking-1',
            type: 'thinking',
            text: '目录结构已明确，开始分析。',
            timestamp: 2,
            collapsed: true,
          },
        ]}
      />,
    );

    expect(screen.getByText('模型过程')).toBeTruthy();
    expect(screen.getByText('推理摘要')).toBeTruthy();
    expect(screen.getByText('目录结构已明确，开始分析。')).toBeTruthy();
    expect(screen.queryByText('正在调用工具：list_dir')).toBeNull();
  });

  it('shows the thinking placeholder before metadata marks the answer as streaming', () => {
    const { container } = render(
      <AgentMessageBubble
        {...baseProps}
        status="thinking"
        isStreaming={false}
        content=""
      />,
    );

    expect(screen.queryByTestId('message-item')).toBeNull();
    expect(screen.getByText('Pudding 正在运行')).toBeTruthy();
    expect(screen.getByText('正在请求模型')).toBeTruthy();
    expect(screen.getByText('等待首个可见事件')).toBeTruthy();
    expect(screen.getByTestId('agent-waiting-monitor')).toBeTruthy();
    expect(
      container.querySelector(
        '.agentBubbleNew.agentBubbleStreaming.agentWaitingBubble',
      ),
    ).toBeTruthy();
    expect(container.querySelector('.reasoningContainer')).toBeNull();
  });

  it('keeps the server elapsed time after the bubble remounts', () => {
    jest.useFakeTimers();
    jest.setSystemTime(new Date('2026-07-24T12:10:00.000Z'));
    try {
      render(
        <AgentMessageBubble
          {...baseProps}
          createdAt={new Date('2026-07-24T12:00:00.000Z').getTime()}
          status="thinking"
          isStreaming={false}
          content=""
        />,
      );

      expect(screen.getByText('模型正在进行复杂推理')).toBeTruthy();
      expect(screen.getByText('已等待 10 分 0 秒')).toBeTruthy();
    } finally {
      jest.useRealTimers();
    }
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

  it('shows a bounded parent delegation summary without duplicating child internals', () => {
    render(
      <AgentMessageBubble
        {...baseProps}
        status="executing"
        isStreaming={false}
        content=""
        parentDelegationActivity={{
          activeCount: 2,
          label: 'reviewer',
          startedAt: Date.now() - 2_000,
          updatedAt: Date.now() - 200,
        }}
        processItems={[
          {
            id: 'thinking-after-delegation',
            type: 'thinking',
            text: '主代理继续整理已返回的信息。',
            timestamp: Date.now() - 100,
            collapsed: true,
          },
        ]}
      />,
    );

    expect(screen.getByText('正在调用 2 个子代理')).toBeTruthy();
    expect(screen.getByText('模型过程')).toBeTruthy();
    expect(screen.getByText('主代理继续整理已返回的信息。')).toBeTruthy();
    expect(
      screen.getByText('主代理正在等待子代理返回；内部进度请查看右侧托盘坞'),
    ).toBeTruthy();
    expect(document.body.textContent).not.toContain('子代理任务详情');
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
      container.querySelector(
        '.agentBubbleNew.agentBubbleEntrance.agentActiveOutputSurface',
      ),
    ).toBeTruthy();
  });

  it('does not replay the entrance animation for historical answers', () => {
    const { container } = render(
      <AgentMessageBubble
        {...baseProps}
        createdAt={Date.now() - 60_000}
        status="success"
        isStreaming={false}
        content="已经稳定展示的历史回答"
      />,
    );

    const bubble = container.querySelector('.agentBubbleNew');
    expect(bubble).toBeTruthy();
    expect(bubble?.classList.contains('agentBubbleEntrance')).toBe(false);
  });

  it('does not replay completion particles for historical answers on mount', () => {
    render(
      <AgentMessageBubble
        {...baseProps}
        status="success"
        isStreaming={false}
        content="已经完成的历史回答"
      />,
    );

    expect(screen.queryByTestId('answer-completion-particles')).toBeNull();
  });

  it('shows completion particles when a streaming answer finishes', () => {
    jest.useFakeTimers();
    try {
      const { rerender } = render(
        <AgentMessageBubble
          {...baseProps}
          status="streaming"
          isStreaming
          content="正在输出"
        />,
      );

      expect(screen.queryByTestId('answer-completion-particles')).toBeNull();

      rerender(
        <AgentMessageBubble
          {...baseProps}
          status="success"
          isStreaming={false}
          content="输出完成"
        />,
      );

      expect(
        screen.getByTestId('answer-completion-particles').children,
      ).toHaveLength(6);
      React.act(() => {
        jest.advanceTimersByTime(1_000);
      });
      expect(screen.queryByTestId('answer-completion-particles')).toBeNull();
    } finally {
      jest.useRealTimers();
    }
  });

  it('does not show completion particles when an answer enters an error state', () => {
    const { rerender } = render(
      <AgentMessageBubble
        {...baseProps}
        status="success"
        isStreaming={false}
        content=""
      />,
    );

    rerender(
      <AgentMessageBubble
        {...baseProps}
        status="error"
        isStreaming={false}
        content="输出失败"
      />,
    );

    expect(screen.queryByTestId('answer-completion-particles')).toBeNull();
  });

  it('passes browser voice output to assistant message actions after answer content is available', () => {
    const { container } = render(
      <AgentMessageBubble
        {...baseProps}
        status="success"
        isStreaming={false}
        content="整理今天的会议记录。"
      />,
    );
    fireEvent.mouseEnter(container.firstElementChild as HTMLElement);

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
