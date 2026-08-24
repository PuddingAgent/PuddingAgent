// ── CU-11 Phase 2: per-turn 投影消费点接线双路径对比测试 ───────────
// 验收锚点①：灰度关闭（不传 executionFlowProjection）走旧路径 A（processItems
// 构建 ToolNode/DelegationNode）；灰度开启（传 executionFlowProjection）走新路径 B
// （canonical 投影 nodes 过滤）。两条路径在同一组事实下渲染结构等价。
import { render, screen } from '@testing-library/react';
import * as React from 'react';
import { projectExecutionFlow } from '../projections/executionFlowProjector';
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
jest.mock(
  './ModelRetryRow',
  () => (props: Record<string, unknown>) => mockModelRetryRow(props),
);
jest.mock('./SessionBenchmarkDrawer', () => () => (
  <div data-testid="session-benchmark-drawer" />
));

// DelegationRow 真实渲染依赖 antd-style theme context（本项目测试环境未提供）；
// mock 渲染层但保留 buildDelegationNodesFromProcessItems 真实实现，保证路径 A/B 的
// 节点构建逻辑仍被真实覆盖，仅替换展示为 testid 结构。
jest.mock('./execution-flow/DelegationRow', () => {
  const actual = jest.requireActual('./execution-flow/DelegationRow');
  return {
    ...actual,
    DelegationRow: ({
      nodes,
    }: {
      nodes: Array<{ subAgentId: string }>;
    }) => (
      <div data-testid="delegation-list">
        {nodes.map((node) => (
          <div
            key={node.subAgentId}
            data-testid={`delegation-item-${node.subAgentId}`}
          />
        ))}
      </div>
    ),
  };
});

const baseProps = {
  id: 'assistant-1',
  status: 'success',
  createdAt: Date.now(),
  agentName: 'Pudding',
  isStreaming: false,
  formatTime: () => '刚刚',
};

// ── canonical fixture（turn-1：2 工具 + 1 父级委派；turn-2：1 工具用于过滤验证）──
const OCCURRED_AT = '2026-08-22T08:00:00.000Z';

function ev(
  type: string,
  seq: number,
  over: Record<string, unknown> = {},
): any {
  return {
    eventId: `e${seq}`,
    sequence: seq,
    occurredAt: OCCURRED_AT,
    runId: 'run-1',
    turnId: 'turn-1',
    type,
    ...over,
  } as ExecutionFlowEvent;
}

const canonicalEvents: ExecutionFlowEvent[] = [
  ev('message.thinking_summary.appended', 1, { delta: '分析' }),
  ev('tool.call.requested', 2, {
    toolCallId: 'call-search',
    name: 'search',
    arguments: '{"q":"x"}',
  }),
  ev('tool.call.completed', 3, {
    toolCallId: 'call-search',
    name: 'search',
    exitCode: 0,
    output: 'hit',
  }),
  ev('tool.call.requested', 4, {
    toolCallId: 'call-shell',
    name: 'shell',
    arguments: '{"cmd":"ls"}',
  }),
  ev('tool.call.failed', 5, {
    toolCallId: 'call-shell',
    name: 'shell',
    error: 'boom',
  }),
  ev('subagent.spawned', 6, {
    subAgentId: 'sa-1',
    template: 'deepseek-v4-flash',
    task: '做分析',
  }),
  ev('subagent.completed', 7, {
    subAgentId: 'sa-1',
    template: 'deepseek-v4-flash',
    success: true,
    resultSummary: '完成',
  }),
  ev('message.content.appended', 8, { delta: '答案' }),
  ev('turn.completed', 9, { reply: '答案' }),
  // turn-2 的工具调用：per-turn 投影必须将其排除在 turn-1 之外。
  ev('tool.call.requested', 10, {
    toolCallId: 'other-tool',
    name: 'grep',
    turnId: 'turn-2',
  }),
  ev('tool.call.completed', 11, {
    toolCallId: 'other-tool',
    name: 'grep',
    exitCode: 0,
    output: 'no',
    turnId: 'turn-2',
  }),
];

const turn1Projection = projectExecutionFlow(canonicalEvents, {
  turnId: 'turn-1',
});

// ── 旧路径 A 输入：与 canonical events 同一事实的 TimelineItem[] ──
const turn1ProcessItems = [
  {
    id: 'p1',
    type: 'tool_call',
    toolCallId: 'call-search',
    name: 'search',
    arguments: '{"q":"x"}',
    timestamp: 2,
    collapsed: true,
  },
  {
    id: 'p2',
    type: 'tool_result',
    toolCallId: 'call-search',
    status: 'success',
    output: 'hit',
    timestamp: 3,
    collapsed: true,
  },
  {
    id: 'p3',
    type: 'tool_call',
    toolCallId: 'call-shell',
    name: 'shell',
    arguments: '{"cmd":"ls"}',
    timestamp: 4,
    collapsed: true,
  },
  {
    id: 'p4',
    type: 'tool_result',
    toolCallId: 'call-shell',
    status: 'failed',
    message: 'boom',
    timestamp: 5,
    collapsed: true,
  },
  {
    id: 'p5',
    type: 'subagent_spawned',
    name: 'sa-1',
    text: '做分析',
    timestamp: 6,
    collapsed: true,
  },
  {
    id: 'p6',
    type: 'subagent_completed',
    name: 'sa-1',
    status: 'success',
    output: '完成',
    timestamp: 7,
    collapsed: true,
  },
];

describe('AgentMessageBubble projection dual-path equivalence (CU-11 Phase 2)', () => {
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

  it('fixture sanity: per-turn projection contains 2 tools + 1 delegation and excludes turn-2', () => {
    const toolNodes = turn1Projection.nodes.filter(
      (node) => node.kind === 'tool',
    );
    const delegationNodes = turn1Projection.nodes.filter(
      (node) => node.kind === 'delegation',
    );
    expect(toolNodes).toHaveLength(2);
    expect(delegationNodes).toHaveLength(1);
    const toolCallIds = toolNodes
      .map((node) => (node.kind === 'tool' ? node.toolCallId : ''))
      .sort();
    expect(toolCallIds).toEqual(['call-search', 'call-shell']);
    expect(toolCallIds).not.toContain('other-tool');
  });

  it('path A (gray off): renders tool rows + delegation list from processItems', () => {
    render(
      <AgentMessageBubble
        {...baseProps}
        content="答案"
        processItems={turn1ProcessItems}
      />,
    );
    expect(screen.getAllByTestId('toolcall-row')).toHaveLength(2);
    expect(screen.getByTestId('delegation-list')).toBeTruthy();
    expect(screen.getByTestId('delegation-item-sa-1')).toBeTruthy();
  });

  it('path B (gray on): renders equivalent structure from canonical projection', () => {
    render(
      <AgentMessageBubble
        {...baseProps}
        content="答案"
        executionFlowProjection={turn1Projection}
      />,
    );
    expect(screen.getAllByTestId('toolcall-row')).toHaveLength(2);
    expect(screen.getByTestId('delegation-list')).toBeTruthy();
    expect(screen.getByTestId('delegation-item-sa-1')).toBeTruthy();
  });

  it('dual-path equivalence: same key structure (tool count + delegation) on both paths', () => {
    const { unmount } = render(
      <AgentMessageBubble
        {...baseProps}
        content="答案"
        processItems={turn1ProcessItems}
      />,
    );
    const pathAToolRows = screen.getAllByTestId('toolcall-row').length;
    const pathADelegation = Boolean(screen.queryByTestId('delegation-list'));
    unmount();

    render(
      <AgentMessageBubble
        {...baseProps}
        content="答案"
        executionFlowProjection={turn1Projection}
      />,
    );
    const pathBToolRows = screen.getAllByTestId('toolcall-row').length;
    const pathBDelegation = Boolean(screen.queryByTestId('delegation-list'));

    expect(pathBToolRows).toBe(pathAToolRows);
    expect(pathBDelegation).toBe(pathADelegation);
  });
});

// ── 行为链 §3.4+：正文分段交错消费点 ─────────────────────────────────────
describe('AgentMessageBubble message-segment interleaving', () => {
  const segmentEvents: ExecutionFlowEvent[] = [
    ev('message.content.appended', 1, { delta: '先说明一下' }),
    ev('message.thinking_summary.appended', 2, { delta: '思考' }),
    ev('tool.call.requested', 3, {
      toolCallId: 'call-1',
      name: 'shell',
      arguments: '{"command":"ls"}',
    }),
    ev('tool.call.completed', 4, {
      toolCallId: 'call-1',
      name: 'shell',
      exitCode: 0,
      output: 'ok',
    }),
    ev('message.content.appended', 5, { delta: '最终回答' }),
    ev('turn.completed', 6, { reply: '先说明一下最终回答' }),
  ];

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

  it('中间文本段内联进时间线，正文只承载尾段（不重复）', () => {
    const projection = projectExecutionFlow(segmentEvents, {
      turnId: 'turn-1',
    });
    render(
      <AgentMessageBubble
        {...baseProps}
        content="先说明一下最终回答"
        executionFlowProjection={projection}
      />,
    );
    // 中间段（text1）由时间线内联渲染
    const segments = screen.getAllByTestId('timeline-message-segment');
    expect(segments).toHaveLength(1);
    // 正文气泡的 MessageItem 只承载尾段文本，绝不再包含整段全文
    const bubbleItem = mockMessageItem.mock.calls.find(
      (call) => (call[0] as Record<string, unknown>).markdownText === '最终回答',
    );
    expect(bubbleItem).toBeTruthy();
    const fullTextItem = mockMessageItem.mock.calls.find(
      (call) =>
        (call[0] as Record<string, unknown>).markdownText ===
        '先说明一下最终回答',
    );
    expect(fullTextItem).toBeUndefined();
  });

  it('尾段后仍有工具（run 进行中）：正文不渲染，全部文本进时间线', () => {
    const runningEvents: ExecutionFlowEvent[] = [
      ev('message.content.appended', 1, { delta: '文本1' }),
      ev('tool.call.requested', 3, {
        toolCallId: 'call-1',
        name: 'shell',
      }),
    ];
    const projection = projectExecutionFlow(runningEvents, {
      turnId: 'turn-1',
    });
    render(
      <AgentMessageBubble
        {...baseProps}
        status="executing"
        isStreaming
        content="文本1"
        executionFlowProjection={projection}
      />,
    );
    expect(screen.getAllByTestId('timeline-message-segment')).toHaveLength(1);
    // 无尾段 → 正文气泡不渲染（MessageItem 仅时间线段一个调用）
    expect(mockMessageItem).toHaveBeenCalledTimes(1);
  });

  it('分段并集与 answerMarkdown 分叉时回退整块正文（守卫）', () => {
    const projection = projectExecutionFlow(segmentEvents, {
      turnId: 'turn-1',
    });
    // content 与投影分段不一致（模拟 envelope 缺事件）：不得启用分段渲染
    render(
      <AgentMessageBubble
        {...baseProps}
        content="完全不同的诊断文本"
        executionFlowProjection={projection}
      />,
    );
    expect(screen.queryByTestId('timeline-message-segment')).toBeNull();
    const fullTextItem = mockMessageItem.mock.calls.find(
      (call) =>
        (call[0] as Record<string, unknown>).markdownText ===
        '完全不同的诊断文本',
    );
    expect(fullTextItem).toBeTruthy();
  });
});
