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
const mockModelRetryRow = jest.fn((props: Record<string, unknown>) => (
  <div
    data-testid="model-retry-row"
    data-has-items={String(
      Boolean((props.items as Array<unknown> | undefined)?.length),
    )}
  />
));

jest.mock('antd', () => {
  return {
    unstableSetRender: jest.fn(),
    Segmented: ({ options, value, onChange }: any) => (
      <div data-testid="antd-segmented" data-value={String(value)}>
        {(options ?? []).map((option: any) => (
          <button
            key={option.value}
            type="button"
            onClick={() => onChange?.(option.value)}
          >
            {option.label}
          </button>
        ))}
      </div>
    ),
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
    useReasoningStyles: () => ({
      styles,
      cx: (...values: Array<string | false | undefined>) =>
        values.filter(Boolean).join(' '),
    }),
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

jest.mock('../styles/waiting.styles', () => {
  const styles = new Proxy(
    {},
    {
      get: (_target, prop) => String(prop),
    },
  );
  return {
    useWaitingStyles: () => ({ styles }),
  };
});

jest.mock('../styles/execution-flow.styles', () => {
  const styles = new Proxy(
    {},
    {
      get: (_target, prop) => String(prop),
    },
  );
  return {
    useExecutionFlowStyles: () => ({
      styles,
      cx: (...values: Array<string | false | undefined>) =>
        values.filter(Boolean).join(' '),
    }),
  };
});

jest.mock('../styles/toolcall.styles', () => {
  const styles = new Proxy(
    {},
    {
      get: (_target, prop) => String(prop),
    },
  );
  return {
    useToolCallStyles: () => ({
      styles,
      cx: (...v: unknown[]) => v.filter(Boolean).join(' '),
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

jest.mock('../client/agentChatApi', () => ({
  getAgentMessageProcessItems: jest.fn(),
}));

jest.mock('./AgentAvatar', () => () => <div data-testid="agent-avatar" />);
jest.mock('./StateDot', () => (props: { state: string; size?: number }) => (
  <span data-testid="state-dot" data-state={props.state} aria-hidden="true" />
));
jest.mock(
  './MessageActions',
  () => (props: Record<string, unknown>) => mockMessageActions(props),
);
jest.mock(
  './MessageItem',
  () => (props: Record<string, unknown>) => mockMessageItem(props),
);
// P1-2: ModelRetryRow 使用真实 antd-style createStyles，依赖 antd theme context；
// 本文件 mock 了 'antd'，因此需轻量 stub（真实条件渲染由 ModelRetryRow.test.tsx 覆盖）。
jest.mock(
  './ModelRetryRow',
  () => (props: Record<string, unknown>) => mockModelRetryRow(props),
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
    mockModelRetryRow.mockClear();
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
    expect(screen.getByText('思考')).toBeTruthy();
    expect(screen.getByText('用户问的是商用密码应用安全性评估。')).toBeTruthy();
    expect(screen.queryByText(/undefined/)).toBeNull();
    expect(screen.getByTestId('reasoning-disclosure-row')).toBeTruthy();
    expect(container.querySelector('.agentActiveOutputSurface')).toBeNull();
  });

  it('shows the latest reasoning line and expands the complete reasoning trajectory', () => {
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
    expect(screen.queryByText('思维链第二行')).toBeNull();
    expect(screen.queryByText('思维链第三行')).toBeNull();
    expect(screen.getByText('思维链第四行')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: '思考过程' }));
    const body = screen.getByTestId('reasoning-disclosure-body');
    expect(body.textContent).toContain('思维链第一行');
    expect(body.textContent).toContain('思维链第二行');
    expect(body.textContent).toContain('思维链第三行');
    expect(body.textContent).toContain('思维链第四行');
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
            toolCallId: 'call-1',
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

    expect(
      screen.getByTestId('toolcall-row').getAttribute('data-toolname'),
    ).toBe('list_dir');
    expect(screen.getByText('思考')).toBeTruthy();
    expect(screen.getByText('需要先查看项目结构。')).toBeTruthy();
    expect(screen.queryByText('正在调用工具：list_dir')).toBeNull();
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
            toolCallId: 'call-1',
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

    expect(screen.getByText('思考')).toBeTruthy();
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
    // P1-3 降噪：阶段文案折叠进 tooltip、轨道/开发者 hint 不再直出主行；
    // 容器仍保留气泡壳类（agentBubbleNew.agentBubbleStreaming），单行布局走 waiting.styles。
    expect(screen.queryByText('正在请求模型')).toBeNull();
    expect(screen.queryByText('等待首个可见事件')).toBeNull();
    expect(screen.queryByText(/这是主代理的等待占位/)).toBeNull();
    // CU-05：WaitingBubble 收敛为 TurnStatus（唯一 L0 状态行，单 aria-live）
    expect(screen.getByTestId('turn-status')).toBeTruthy();
    expect(container.querySelector('.turnStatusRow')).toBeTruthy();
    expect(screen.queryByTestId('reasoning-disclosure-row')).toBeNull();
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

      // CU-05：主行只显示单行 + ≥15s 时钟（Xm 格式）；不展示「复杂推理/深入分析」等推断文案。
      expect(screen.getByText('Pudding 正在运行')).toBeTruthy();
      expect(screen.getByText('· 已等待 10m')).toBeTruthy();
      expect(screen.queryByText('模型正在进行复杂推理')).toBeNull();
      expect(screen.queryByText('深入分析')).toBeNull();
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
            toolCallId: 'call-shell',
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
    expect(screen.getByTestId('toolcall-row').getAttribute('data-status')).toBe(
      'running',
    );
    expect(screen.getByText('shell')).toBeTruthy();
    // AgentTurnCard 重构：尾部组展开，但运行中工具详情保持折叠；行状态点与
    // 扫光已经表达“正在执行”，避免长 IN 自动撑开卡片。
    expect(
      screen.getAllByText('dotnet build Source/PuddingAgent/PuddingAgent.csproj'),
    ).toHaveLength(1);
    expect(screen.queryByTestId('toolcall-in')).toBeNull();
    fireEvent.click(screen.getByTestId('toolcall-row'));
    expect(screen.getByTestId('toolcall-in')).toBeTruthy();
    expect(container.querySelector('.agentActiveOutputSurface')).toBeNull();
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

    // 行为链升级：委派等待态由 TurnStatus delegating 阶段承载，
    // CurrentActivityPanel 委派大卡退役（不再三处重复）。
    expect(screen.getByText('正在等待子代理')).toBeTruthy();
    expect(screen.queryByText('正在调用 2 个子代理')).toBeNull();
    expect(screen.getByText('思考')).toBeTruthy();
    expect(screen.getByText('主代理继续整理已返回的信息。')).toBeTruthy();
    expect(
      screen.queryByText('主代理正在等待子代理返回；内部进度请查看右侧托盘坞'),
    ).toBeNull();
    expect(document.body.textContent).not.toContain('子代理任务详情');
    // CU-10：MessageProcessSummary 已退出主生产路径 → 不再渲染「查看过程」折叠摘要入口。
    expect(screen.queryByText('查看过程')).toBeNull();
    expect(
      screen.queryByText(/主代理过程可在当前消息的“查看过程”中展开/),
    ).toBeNull();
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
            toolCallId: 'call-subagent',
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

    expect(screen.getByText('spawn_sub_agent')).toBeTruthy();
    // 行摘要仍是 presenter 摘要（不带原始 JSON 字段名）；IN 详情只在用户
    // 显式展开后展示，两者职责分离。
    expect(screen.getByTestId('toolcall-summary').textContent).toContain(
      '对 PuddingAgent 项目进行代码 QA，重点检查注释是否完成。',
    );
    expect(screen.getByTestId('toolcall-summary').textContent).not.toContain(
      '"perspective"',
    );
    expect(screen.queryByTestId('toolcall-in')).toBeNull();
    fireEvent.click(screen.getByTestId('toolcall-row'));
    expect(screen.getByTestId('toolcall-in')).toBeTruthy();
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
            toolCallId: 'call-code-summary',
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

    expect(screen.getByText('参数已记录')).toBeTruthy();
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
            toolCallId: 'call-shell',
            type: 'tool_call',
            name: 'shell',
            status: 'tool_call',
            arguments: 'git diff --stat',
            timestamp: Date.now() - 2200,
            collapsed: false,
          },
          {
            id: 'tool-result-1',
            toolCallId: 'call-shell',
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

    const shellRow = screen.getByTestId('toolcall-row');
    expect(shellRow.getAttribute('data-status')).toBe('done');
    expect(screen.queryByText('工具调用完成：shell')).toBeNull();
    expect(document.body.textContent).not.toContain('正在处理结果');
    expect(document.body.textContent).not.toContain('已运行');
    expect(document.body.textContent).not.toContain('line 1');
    fireEvent.click(shellRow);
    const output = screen.getByTestId('toolcall-out');
    expect(output.textContent).toContain('line 1');
    expect(output.textContent).toContain('line 6');
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
            toolCallId: 'call-terminal',
            type: 'tool_call',
            name: 'terminal_execute',
            status: 'tool_call',
            arguments: 'dotnet test --no-build --verbosity quiet 2>&1',
            timestamp: Date.now() - 231 * 60 * 1000,
            collapsed: false,
          },
          {
            id: 'tool-result-1',
            toolCallId: 'call-terminal',
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

    const terminalRow = screen.getByTestId('toolcall-row');
    expect(terminalRow.getAttribute('data-status')).toBe('done');
    expect(screen.queryByText('工具调用完成：terminal_execute')).toBeNull();
    expect(document.body.textContent).toContain('bbc6acfb1bca');
    expect(document.body.textContent).not.toContain('正在处理结果');
    expect(document.body.textContent).not.toContain('已运行 231 分');
    expect(container.querySelector('.agentActiveOutputSurface')).toBeNull();
  });

  it('uses typewriter slices for streaming answers while keeping the reasoning row visible', () => {
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
        text: '完整回答',
      }),
    );
    expect(screen.queryByText(/已思考/)).toBeNull();
    expect(screen.queryByText('查看过程')).toBeNull();
    // CU-10：MessageProcessSummary 退出主路径后，typewriter 流式期间过程摘要不再渲染，
    // 但推理摘要行（ReasoningDisclosureRow）保持可见。
    expect(screen.getByText('思考')).toBeTruthy();
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

describe('AgentMessageBubble error summary row (P0-1)', () => {
  it('renders StateDot(error) + title + summarized message for error status', () => {
    render(
      <AgentMessageBubble
        {...baseProps}
        status="error"
        isStreaming={false}
        content='{"message":"模型超时，已自动重试","code":429}'
        onRerun={jest.fn()}
      />,
    );

    const dot = screen.getByTestId('state-dot');
    expect(dot.getAttribute('data-state')).toBe('error');
    expect(screen.getByText('本轮运行失败')).toBeTruthy();
    const summaryText = screen.getByTestId('agent-error-summary-text');
    expect(summaryText.textContent).toBe('模型超时，已自动重试');
    expect(summaryText.getAttribute('title')).toBe(
      '{"message":"模型超时，已自动重试","code":429}',
    );
    expect(screen.getByRole('button', { name: '重试' })).toBeTruthy();
  });

  it('renders warning state + 已取消 title for cancelled status', () => {
    render(
      <AgentMessageBubble
        {...baseProps}
        status="cancelled"
        isStreaming={false}
        content="用户取消"
        onRerun={jest.fn()}
      />,
    );

    expect(screen.getByTestId('state-dot').getAttribute('data-state')).toBe(
      'warning',
    );
    expect(screen.getByText('已取消')).toBeTruthy();
    expect(screen.getByTestId('agent-error-summary-text').textContent).toBe(
      '用户取消',
    );
  });

  it('prefers the failed timeline item message over answer content', () => {
    render(
      <AgentMessageBubble
        {...baseProps}
        status="error"
        isStreaming={false}
        content="部分输出"
        processItems={[
          {
            id: 'tool-1',
            type: 'tool_result',
            status: 'error',
            message: '命令执行失败：exit 1',
            timestamp: 1,
            collapsed: false,
          },
        ]}
      />,
    );

    expect(screen.getByTestId('agent-error-summary-text').textContent).toBe(
      '命令执行失败：exit 1',
    );
  });

  it('keeps dot + title when no error text is available', () => {
    render(
      <AgentMessageBubble
        {...baseProps}
        status="error"
        isStreaming={false}
        content=""
      />,
    );

    expect(screen.getByTestId('state-dot').getAttribute('data-state')).toBe(
      'error',
    );
    expect(screen.getByText('本轮运行失败')).toBeTruthy();
    expect(screen.queryByTestId('agent-error-summary-text')).toBeNull();
  });

  it('truncates a long plain-text error to 80 chars with an ellipsis', () => {
    const longError = 'e'.repeat(120);
    render(
      <AgentMessageBubble
        {...baseProps}
        status="error"
        isStreaming={false}
        content={longError}
      />,
    );

    expect(screen.getByTestId('agent-error-summary-text').textContent).toBe(
      `${'e'.repeat(80)}…`,
    );
  });
});

describe('AgentMessageBubble model retry row hook (P1-2)', () => {
  beforeEach(() => {
    mockModelRetryRow.mockClear();
  });

  it('passes processItems to ModelRetryRow when the message carries retry entries', () => {
    render(
      <AgentMessageBubble
        {...baseProps}
        status="executing"
        isStreaming={false}
        content=""
        processItems={[
          {
            id: 'retry-1',
            type: 'subconscious_step',
            text: 'LLM call retry 2/3.',
            message: 'connection reset',
            timestamp: 1,
            collapsed: false,
          },
        ]}
      />,
    );

    expect(mockModelRetryRow).toHaveBeenCalledWith(
      expect.objectContaining({
        items: expect.arrayContaining([
          expect.objectContaining({ id: 'retry-1' }),
        ]),
      }),
    );
  });

  it('renders the retry row area independent of the error summary row (no error status required)', () => {
    render(
      <AgentMessageBubble
        {...baseProps}
        status="success"
        isStreaming={false}
        content="已完成"
        processItems={[
          {
            id: 'retry-1',
            type: 'subconscious_step',
            text: 'LLM stream retry before first delta 1/3.',
            timestamp: 1,
            collapsed: false,
          },
        ]}
      />,
    );

    expect(mockModelRetryRow).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId('agent-error-summary-row')).toBeNull();
  });
});
