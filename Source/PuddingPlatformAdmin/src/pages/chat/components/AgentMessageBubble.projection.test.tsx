// ── CU-11 Phase 2 → AgentTurnCard 重构（2026-08-25）：内容块流消费点测试 ────
// 验收锚点：
//  - 灰度关闭（无 executionFlowProjection）走路径 A（processItems 适配）；
//    灰度开启走路径 B（canonical 投影）；两路径同一组事实渲染结构等价。
//  - 正文全部由 TurnContentStream 的 TextBlock 承载且只渲染一次——卡片底部
//    不存在第二个 answer bubble。
//  - projection 一旦有 TextBlock 就始终是正文渲染权威，answerMarkdown 不再
//    通过字符串关系把正文切回卡片底部。
import { fireEvent, render, screen } from '@testing-library/react';
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
    useAgentStyles: () => ({
      styles,
      cx: (...values: Array<string | false | undefined>) =>
        values.filter(Boolean).join(' '),
    }),
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
  } as any;
}

const canonicalEvents: any[] = [
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

  it('path A (gray off): processItems 适配为行为组；展开后工具/委派可见', () => {
    render(
      <AgentMessageBubble
        {...baseProps}
        content="答案"
        processItems={turn1ProcessItems as never}
      />,
    );
    // 单一尾部组默认展开；无投影时正文仍由整块气泡兜底。
    expect(screen.getAllByTestId('activity-group-header')).toHaveLength(1);
    expect(screen.getAllByTestId('toolcall-row')).toHaveLength(2);
    expect(screen.getByTestId('delegation-list')).toBeTruthy();
    expect(screen.getByTestId('delegation-item-sa-1')).toBeTruthy();
  });

  it('path B (gray on): 投影正文段直渲 + 行为组等价结构', () => {
    render(
      <AgentMessageBubble
        {...baseProps}
        content="答案"
        executionFlowProjection={turn1Projection}
      />,
    );
    expect(screen.getAllByTestId('turn-text-segment')).toHaveLength(1);
    expect(screen.getAllByTestId('activity-group-header')).toHaveLength(1);
    // I-09 权威语义：唯一行为组 = 最新组，默认展开（成员 DOM 挂载），与 path A 等价。
    // （旧断言按 CU-11 语义「组后还有正文即历史组默认折叠」点击 header，实际会把
    // 已展开的组折叠，与「折叠历史组卸载成员 DOM」的 I-09 设计直接冲突。）
    expect(screen.getAllByTestId('toolcall-row')).toHaveLength(2);
    expect(screen.getByTestId('delegation-list')).toBeTruthy();
    expect(screen.getByTestId('delegation-item-sa-1')).toBeTruthy();
    // 折叠态：成员 DOM 完全卸载（不是 CSS 隐藏），即 CollapsibleUnmountRegion 关闭语义。
    fireEvent.click(screen.getByTestId('activity-group-header'));
    expect(screen.queryByTestId('toolcall-row')).toBeNull();
    expect(screen.queryByTestId('delegation-list')).toBeNull();
    // 再次展开：成员 DOM 重新挂载。
    fireEvent.click(screen.getByTestId('activity-group-header'));
    expect(screen.getAllByTestId('toolcall-row')).toHaveLength(2);
    expect(screen.getByTestId('delegation-item-sa-1')).toBeTruthy();
  });

  it('dual-path equivalence: same key structure (tool count + delegation) on both paths', () => {
    const { unmount } = render(
      <AgentMessageBubble
        {...baseProps}
        content="答案"
        processItems={turn1ProcessItems as never}
      />,
    );
    const pathAToolRows = screen.getAllByTestId('toolcall-row').length;
    const pathADelegation = Boolean(screen.queryByTestId('delegation-list'));
    // 路径 A 折叠态：成员 DOM 完全卸载（I-09 语义）。
    fireEvent.click(screen.getByTestId('activity-group-header'));
    const pathACollapsedToolRows = screen.queryAllByTestId('toolcall-row').length;
    const pathACollapsedDelegation = Boolean(
      screen.queryByTestId('delegation-list'),
    );
    unmount();

    render(
      <AgentMessageBubble
        {...baseProps}
        content="答案"
        executionFlowProjection={turn1Projection}
      />,
    );
    // I-09 语义：唯一行为组 = 最新组，默认展开——与 path A 相同，无需点击。
    const pathBToolRows = screen.getAllByTestId('toolcall-row').length;
    const pathBDelegation = Boolean(screen.queryByTestId('delegation-list'));
    fireEvent.click(screen.getByTestId('activity-group-header'));
    const pathBCollapsedToolRows = screen.queryAllByTestId('toolcall-row').length;
    const pathBCollapsedDelegation = Boolean(
      screen.queryByTestId('delegation-list'),
    );

    expect(pathBToolRows).toBe(pathAToolRows);
    expect(pathBDelegation).toBe(pathADelegation);
    expect(pathBCollapsedToolRows).toBe(pathACollapsedToolRows);
    expect(pathBCollapsedDelegation).toBe(pathACollapsedDelegation);
  });
});

// ── 行为链 §3.4+ → AgentTurnCard：正文分段内容块流 ─────────────────────────
describe('AgentMessageBubble message-segment interleaving', () => {
  const segmentEvents: any[] = [
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

  it('每个正文段各渲染一次；卡片底部不存在第二个 answer bubble（不重复）', () => {
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
    // 两段正文按序各渲染一次（TurnContentStream TextBlock）
    const segments = screen.getAllByTestId('turn-text-segment');
    expect(segments).toHaveLength(2);
    // MessageItem 恰好两次调用（每段一次）；绝无整段全文的第三次调用
    expect(mockMessageItem).toHaveBeenCalledTimes(2);
    const fullTextItem = mockMessageItem.mock.calls.find(
      (call) =>
        (call[0] as Record<string, unknown>).markdownText ===
        '先说明一下最终回答',
    );
    expect(fullTextItem).toBeUndefined();
  });

  it('尾段后仍有工具（run 进行中）：正文段照常渲染，行为组在正文之后', () => {
    const runningEvents: any[] = [
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
    expect(screen.getAllByTestId('turn-text-segment')).toHaveLength(1);
    expect(mockMessageItem).toHaveBeenCalledTimes(1);
    // run 活跃：尾部组默认展开，运行中工具行可见
    expect(screen.getAllByTestId('toolcall-row')).toHaveLength(1);
  });

  it('answerMarkdown 与分段文本不同：canonical TextBlock 仍按序渲染，不切回底部整块正文', () => {
    const projection = projectExecutionFlow(segmentEvents, {
      turnId: 'turn-1',
    });
    // content 是完成态物化文本，不再参与分段路径选择。
    render(
      <AgentMessageBubble
        {...baseProps}
        content="完全不同的诊断文本"
        executionFlowProjection={projection}
      />,
    );
    expect(screen.getAllByTestId('turn-text-segment')).toHaveLength(2);
    const fullTextItem = mockMessageItem.mock.calls.find(
      (call) =>
        (call[0] as Record<string, unknown>).markdownText ===
        '完全不同的诊断文本',
    );
    expect(fullTextItem).toBeUndefined();
  });
});
