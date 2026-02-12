import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import ChatMain from './ChatMain';

const mockHistoryPush = jest.fn();

jest.mock('@umijs/max', () => ({
  history: {
    push: (...args: unknown[]) => mockHistoryPush(...args),
  },
}));

jest.mock('@/components', () => ({
  WorkspaceNavigationHeader: ({ crumbs, controls, extraActions }: any) => (
    <header>
      <nav aria-label="工作台路径">
        {crumbs?.map((crumb: any) => (
          <span key={crumb.label}>{crumb.label}</span>
        ))}
      </nav>
      {controls}
      {extraActions}
    </header>
  ),
}));

let _mockLatestInputAreaProps: any;
let _mockLatestMessageListProps: any;
jest.mock('./InputArea', () => (props: any) => {
  _mockLatestInputAreaProps = props;
  return (
    <div role="tablist" aria-label="意图输入模式" data-testid="input-area" />
  );
});
jest.mock('./IntentConsole', () => (props: any) => {
  _mockLatestInputAreaProps = props;
  return (
    <div>
      <div role="tablist" aria-label="意图输入模式" data-testid="input-area">
        <button type="button" role="tab" aria-selected="true">
          键盘
        </button>
        <button type="button" role="tab" aria-selected="false">
          语音
        </button>
      </div>
      {props.status === 'thinking' && <div>正在思考</div>}
      <div data-testid="voice-conversation-panel">
        <button type="button" aria-label="开始语音会话">
          开始语音会话
        </button>
        <button type="button" aria-label="朗读最新回复">
          朗读最新回复
        </button>
        <button type="button" aria-label="发送语音内容">
          发送语音内容
        </button>
      </div>
    </div>
  );
});
jest.mock('./MessageList', () => (props: any) => {
  _mockLatestMessageListProps = props;
  return (
    <div
      data-testid="message-list"
      data-conversation-agent={props.conversationView?.agentId ?? ''}
    />
  );
});
jest.mock('./DevPanel', () => (props: any) => (
  <div data-testid="dev-panel">
    <button
      type="button"
      onClick={() =>
        props.onRunBenchmarkPrompt?.(
          '请创建一个 Markdown 摘要脚本并运行验证。',
          {
            source: 'benchmark_launcher',
            benchmarkCaseId: 'case-1',
          },
        )
      }
    >
      运行试题
    </button>
  </div>
));

const workspace = {
  workspaceId: 'default',
  name: '默认工作空间',
  isEnabled: true,
  isFrozen: false,
} as any;

const agents = [
  {
    agentId: 'agent-1',
    name: '默认助手',
    avatarId: 'thinking',
    isEnabled: true,
    isFrozen: false,
  },
  {
    agentId: 'agent-2',
    name: '分析助手',
    isEnabled: true,
    isFrozen: false,
  },
] as any[];

const createChatMainElement = ({
  onAgentChange = jest.fn(),
  selectedSessionId = null,
  turns = [],
  loading = false,
  chatInteractionRuntimeEvents = [],
  subAgentCards = {},
  onInputChange = jest.fn(),
  onSendWithMetadata,
  conversationView,
}: {
  onAgentChange?: jest.Mock;
  selectedSessionId?: string | null;
  turns?: any[];
  loading?: boolean;
  chatInteractionRuntimeEvents?: any[];
  subAgentCards?: any;
  onInputChange?: jest.Mock;
  onSendWithMetadata?: jest.Mock;
  conversationView?: any;
} = {}) => (
  <ChatMain
    sidebarOpen
    onToggleSidebar={jest.fn()}
    workspaces={[workspace]}
    workspaceId="default"
    workspaceLoading={false}
    wsOpts={[{ value: 'default', label: '默认工作空间', disabled: false }]}
    onWorkspaceChange={jest.fn()}
    agents={agents}
    agentId="agent-1"
    agentLoading={false}
    agOpts={agents.map((agent) => ({
      value: agent.agentId,
      label: agent.name,
      disabled: false,
    }))}
    selectedAgent={agents[0]}
    onAgentChange={onAgentChange}
    onCreateWorkspace={jest.fn()}
    selectedSessionId={selectedSessionId}
    turns={turns}
    conversationView={conversationView}
    chatInteractionRuntimeEvents={chatInteractionRuntimeEvents}
    subAgentCards={subAgentCards}
    historyLoading={false}
    loadingMore={false}
    hasMoreMessages={false}
    error={null}
    onClearError={jest.fn()}
    onLoadMore={jest.fn()}
    inputValue=""
    onInputChange={onInputChange}
    onKeyDown={jest.fn()}
    loading={loading}
    onSend={jest.fn()}
    onSendWithMetadata={onSendWithMetadata}
    onStop={jest.fn()}
    onExport={jest.fn()}
    disabled={false}
    tLimit={0}
    tUsed={0}
    tPct={0}
    formatTime={(ts) => String(ts)}
    onDeleteTurn={jest.fn()}
    onContextMenu={jest.fn()}
    onRerunTurn={jest.fn()}
    onPinTurn={jest.fn()}
    messageListRef={React.createRef()}
    listEndRef={React.createRef()}
  />
);

const renderChatMain = (
  options: Parameters<typeof createChatMainElement>[0] = {},
) => render(createChatMainElement(options));

describe('ChatMain workbench header', () => {
  beforeEach(() => {
    mockHistoryPush.mockClear();
    _mockLatestInputAreaProps = undefined;
    _mockLatestMessageListProps = undefined;
    localStorage.removeItem('pudding-dev-mode');
    localStorage.removeItem('pudding_token');
    localStorage.removeItem('pudding_pinned_message');
  });

  it('uses the header for workspace context without duplicating the selected agent contact', () => {
    renderChatMain({ selectedSessionId: 'session-visible-123456' });

    expect(screen.getByRole('navigation', { name: '工作台路径' })).toBeTruthy();
    expect(screen.getAllByText('默认工作空间').length).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: '工作空间视图' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: '切换助手' })).toBeNull();
    expect(screen.queryByRole('menu', { name: '助手列表' })).toBeNull();
    expect(screen.queryByText('默认助手')).toBeNull();
    expect(screen.queryByText(/session-visible/)).toBeNull();
  });

  it('renders the Agent Workbench with timeline and intent console without a side presence rail', () => {
    renderChatMain();

    expect(screen.getByRole('main', { name: 'Agent 工作台' })).toBeTruthy();
    expect(screen.getByRole('region', { name: '会话时间线' })).toBeTruthy();
    expect(screen.getByRole('tablist', { name: '意图输入模式' })).toBeTruthy();
    expect(
      screen.queryByRole('complementary', { name: 'Agent 感知栏' }),
    ).toBeNull();
    expect(screen.getByRole('tab', { name: '语音' })).toBeTruthy();
  });

  it('passes Agent conversation projection to MessageList', () => {
    renderChatMain({
      conversationView: {
        workspaceId: 'default',
        ownerUserId: 'single-user',
        agentId: 'agent-1',
        mainSessionId: 'session-agent-1',
        messages: [],
        eventCursor: 1,
        updatedAt: '2026-06-07T00:00:00.000Z',
      },
    });

    expect(
      screen
        .getByTestId('message-list')
        .getAttribute('data-conversation-agent'),
    ).toBe('agent-1');
  });

  it('does not scan raw timeline events while developer mode is off', () => {
    const readTimelineItems = jest.fn(() => []);
    const turn = {
      turnId: 'turn-1',
      userMessage: {
        id: 'user-1',
        text: 'hello',
        timestamp: 1,
        status: 'success',
      },
      assistant: {
        id: 'assistant-1',
        status: 'streaming',
        get timelineItems() {
          return readTimelineItems();
        },
        answerMarkdown: 'partial answer',
        isStreaming: true,
        renderMode: 'structured',
      },
    } as any;

    renderChatMain({ turns: [turn] });

    expect(readTimelineItems).not.toHaveBeenCalled();
  });

  it('does not refetch inferred sessions when only the turn count changes', async () => {
    localStorage.setItem('pudding_token', 'token');
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => [{ sessionId: 'session-1' }],
    });
    (globalThis as any).fetch = fetchMock;

    const turn = {
      turnId: 'turn-1',
      userMessage: {
        id: 'user-1',
        text: 'hello',
        timestamp: 1,
        status: 'success',
      },
      assistant: {
        id: 'assistant-1',
        status: 'success',
        timelineItems: [],
        answerMarkdown: 'answer',
        isStreaming: false,
        renderMode: 'structured',
      },
    } as any;

    const { rerender } = renderChatMain({ turns: [] });

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledTimes(1);
    });

    rerender(createChatMainElement({ turns: [turn] }));

    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('does not render the selected agent virtual avatar or standalone presence rail', () => {
    renderChatMain();

    expect(screen.queryByLabelText('默认助手 虚拟形象')).toBeNull();
    expect(
      screen.queryByRole('img', { name: /Agent agent-1 状态/ }),
    ).toBeNull();
    expect(
      screen.queryByRole('complementary', { name: 'Agent 感知栏' }),
    ).toBeNull();
  });

  it('projects assistant response status into text-only presence state without a virtual avatar', () => {
    const turn = {
      turnId: 'turn-thinking',
      userMessage: {
        id: 'user-1',
        text: '帮我分析',
        timestamp: 1,
        status: 'success',
      },
      assistant: {
        id: 'assistant-1',
        status: 'thinking',
        timelineItems: [],
        answerMarkdown: '',
        isStreaming: true,
        renderMode: 'structured',
      },
    } as any;

    renderChatMain({ loading: true, turns: [turn] });

    expect(screen.getByText('正在思考')).toBeTruthy();
    expect(screen.queryByLabelText('默认助手 虚拟形象')).toBeNull();
    expect(
      screen.queryByRole('complementary', { name: 'Agent 感知栏' }),
    ).toBeNull();
  });

  it('counts only active sub-agents in the composer status summary', () => {
    renderChatMain({
      subAgentCards: {
        'sa-running': {
          turnId: 'sa-running',
          subSessionId: 'session-1-sub-running',
          status: 'running',
          taskSummary: 'running task',
          spawnedAt: 1_000,
        },
        'sa-completed': {
          turnId: 'sa-completed',
          subSessionId: 'session-1-sub-completed',
          status: 'completed',
          taskSummary: 'completed task',
          spawnedAt: 1_000,
          completedAt: 2_000,
          output: 'done',
        },
      },
    });

    expect(_mockLatestInputAreaProps.subAgentsRunning).toBe(1);
  });

  it('keeps voice interaction inside the main input surface without a side rail', () => {
    const { rerender } = renderChatMain({
      chatInteractionRuntimeEvents: [
        {
          type: 'camera_capture_status',
          agentId: 'agent-1',
          status: 'capturing',
          sessionId: 'camera-1',
          artifactId: 'vision-frame-1',
          now: 100,
        },
      ],
    });

    expect(
      screen.queryByRole('complementary', { name: 'Agent 感知栏' }),
    ).toBeNull();
    fireEvent.click(screen.getByRole('tab', { name: '语音' }));
    expect(screen.getByTestId('voice-conversation-panel')).toBeTruthy();
    expect(screen.getByRole('button', { name: '开始语音会话' })).toBeTruthy();

    rerender(
      createChatMainElement({
        chatInteractionRuntimeEvents: [
          {
            type: 'voice_capture_status',
            agentId: 'agent-1',
            status: 'recording',
            sessionId: 'voice-1',
            now: 110,
          },
        ],
      }),
    );

    expect(screen.getByTestId('voice-conversation-panel')).toBeTruthy();
    expect(screen.queryByLabelText('默认助手 虚拟形象')).toBeNull();
    expect(
      screen.queryByRole('complementary', { name: 'Agent 感知栏' }),
    ).toBeNull();
  });

  it('keeps local voice input and output controls in the main voice panel', () => {
    renderChatMain();
    fireEvent.click(screen.getByRole('tab', { name: '语音' }));

    expect(screen.getByRole('button', { name: '开始语音会话' })).toBeTruthy();
    expect(screen.getByRole('button', { name: '朗读最新回复' })).toBeTruthy();
    expect(screen.getByRole('button', { name: '发送语音内容' })).toBeTruthy();
    expect(
      screen.queryByRole('img', { name: /Agent agent-1 状态/ }),
    ).toBeNull();
    expect(
      screen.queryByRole('complementary', { name: 'Agent 感知栏' }),
    ).toBeNull();
  });

  it('sends a benchmark prompt through metadata without appending runner text', async () => {
    localStorage.setItem('pudding-dev-mode', '1');
    const onInputChange = jest.fn();
    const onSendWithMetadata = jest.fn();

    renderChatMain({ onInputChange, onSendWithMetadata });

    fireEvent.click(screen.getByRole('button', { name: '运行试题' }));

    await waitFor(() => {
      expect(onSendWithMetadata).toHaveBeenCalledWith(
        '请创建一个 Markdown 摘要脚本并运行验证。',
        {
          source: 'benchmark_launcher',
          benchmarkCaseId: 'case-1',
        },
      );
    });
    expect(onInputChange).toHaveBeenCalledWith(
      '请创建一个 Markdown 摘要脚本并运行验证。',
    );
  });

  it('wires pinned quote insertion from MessageList into the composer', () => {
    const onInputChange = jest.fn();

    renderChatMain({ onInputChange });

    _mockLatestMessageListProps.onPinnedQuote('> 摘要：已钉住\n');

    expect(onInputChange).toHaveBeenCalledWith('> 摘要：已钉住\n');
  });
});
