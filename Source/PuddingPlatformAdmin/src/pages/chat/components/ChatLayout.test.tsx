import { render, screen } from '@testing-library/react';
import * as React from 'react';
import ChatLayout from './ChatLayout';

jest.mock('../styles', () => ({
  useChatStyles: () => ({
    styles: new Proxy({}, { get: (_target, prop) => String(prop) }),
  }),
}));

jest.mock('./ChatMain', () => (props: any) => (
  <main
    data-testid="chat-main"
    data-conversation-agent={props.conversationView?.agentId ?? ''}
  />
));
jest.mock('./SessionSidebar', () => (props: any) => (
  <aside
    data-testid="session-sidebar"
    data-working-agent-ids={props.workingAgentIds.join(',')}
    data-agent-status={props.agentStatuses?.['agent-1']?.status ?? ''}
  />
));

const baseProps = {
  sidebarOpen: true,
  onToggleSidebar: jest.fn(),
  sessionsLoading: false,
  groups: [],
  selectedSessionId: 'session-1',
  creatingSession: false,
  onNewSession: jest.fn(),
  onSelectSession: jest.fn(),
  onRenameStart: jest.fn(),
  onArchiveSession: jest.fn(),
  onDeleteSession: jest.fn(),
  workspaces: [],
  workspaceId: 'default',
  workspaceLoading: false,
  wsOpts: [],
  onWorkspaceChange: jest.fn(),
  agents: [
    { agentId: 'agent-1', name: 'Agent 1', isEnabled: true, isFrozen: false },
  ],
  agentId: 'agent-1',
  agentLoading: false,
  agOpts: [],
  selectedAgent: {
    agentId: 'agent-1',
    name: 'Agent 1',
    isEnabled: true,
    isFrozen: false,
  },
  onAgentChange: jest.fn(),
  onCreateWorkspace: jest.fn(),
  turns: [],
  chatInteractionRuntimeEvents: [],
  historyLoading: false,
  loadingMore: false,
  hasMoreMessages: false,
  error: null,
  onClearError: jest.fn(),
  onLoadMore: jest.fn(),
  inputValue: '',
  onInputChange: jest.fn(),
  onKeyDown: jest.fn(),
  loading: false,
  workingAgentIds: [],
  interactionQueue: [],
  onUpdateQueuedInteraction: jest.fn(),
  onDeleteQueuedInteraction: jest.fn(),
  onSendQueuedInteractionNow: jest.fn(),
  onSteerQueuedInteraction: jest.fn(),
  onSend: jest.fn(),
  onStop: jest.fn(),
  onExport: jest.fn(),
  disabled: false,
  tLimit: 0,
  tUsed: 0,
  tPct: 0,
  formatTime: jest.fn(),
  onDeleteTurn: jest.fn(),
  onContextMenu: jest.fn(),
  onRerunTurn: jest.fn(),
  onPinTurn: jest.fn(),
  messageListRef: React.createRef<HTMLDivElement>(),
  listEndRef: React.createRef<HTMLDivElement>(),
  subAgentCards: {},
};

describe('ChatLayout agent status projection', () => {
  it('passes through real working agent ids instead of deriving them from the selected active turn', () => {
    render(
      <ChatLayout
        {...(baseProps as any)}
        turns={[
          {
            turnId: 'turn-1',
            userMessage: {
              id: 'user-1',
              text: 'hello',
              timestamp: 1,
              status: 'success',
            },
            assistant: {
              id: 'assistant-1',
              status: 'thinking',
              isStreaming: true,
              answerMarkdown: '',
              timelineItems: [],
              renderMode: 'structured',
            },
          },
        ]}
        workingAgentIds={[]}
      />,
    );

    expect(
      screen
        .getByTestId('session-sidebar')
        .getAttribute('data-working-agent-ids'),
    ).toBe('');
  });

  it('passes Agent status projection through to the contact list', () => {
    render(
      <ChatLayout
        {...(baseProps as any)}
        agentStatuses={{
          'agent-1': { status: 'running', summary: '正在执行' },
        }}
      />,
    );

    expect(
      screen.getByTestId('session-sidebar').getAttribute('data-agent-status'),
    ).toBe('running');
  });

  it('passes Agent conversation projection through to the main message area', () => {
    render(
      <ChatLayout
        {...(baseProps as any)}
        conversationView={{
          workspaceId: 'default',
          ownerUserId: 'single-user',
          agentId: 'agent-1',
          mainSessionId: 'session-agent-1',
          messages: [],
          eventCursor: 1,
          updatedAt: '2026-06-07T00:00:00.000Z',
        }}
      />,
    );

    expect(
      screen.getByTestId('chat-main').getAttribute('data-conversation-agent'),
    ).toBe('agent-1');
  });
});
