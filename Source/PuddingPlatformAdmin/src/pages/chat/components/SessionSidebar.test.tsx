import { fireEvent, render, screen, within } from '@testing-library/react';
import * as React from 'react';
import SessionSidebar, { getAgentContactStatus } from './SessionSidebar';

jest.mock('../styles', () => ({
  useChatStyles: () => ({
    cx: (...items: Array<string | false | undefined>) =>
      items.filter(Boolean).join(' '),
    styles: new Proxy({}, { get: (_target, prop) => String(prop) }),
  }),
}));

const agents = [
  {
    agentId: 'planner',
    name: '规划 Agent',
    isEnabled: true,
    isFrozen: false,
    avatarEmoji: '计',
  },
  { agentId: 'reviewer', name: '审查 Agent', isEnabled: true, isFrozen: false },
  { agentId: 'frozen', name: '冻结 Agent', isEnabled: true, isFrozen: true },
] as any[];

const baseProps = {
  sidebarOpen: true,
  onToggleSidebar: jest.fn(),
  sessionsLoading: false,
  groups: [
    {
      label: '今天',
      items: [{ sessionId: 'session-1', title: '实现主会话', timestamp: 1 }],
    },
  ],
  selectedSessionId: 'session-1',
  creatingSession: false,
  onNewSession: jest.fn(),
  onSelectSession: jest.fn(),
  onRenameStart: jest.fn(),
  onArchiveSession: jest.fn(),
  onDeleteSession: jest.fn(),
  agents,
  agentId: 'planner',
  agentLoading: false,
  onAgentChange: jest.fn(),
  workingAgentIds: ['planner'],
};

describe('SessionSidebar agent contacts', () => {
  it('renders agents as the primary contact list and switches by contact', () => {
    const onAgentChange = jest.fn();
    render(<SessionSidebar {...baseProps} onAgentChange={onAgentChange} />);

    expect(
      screen.getByRole('navigation', { name: 'Agent 通讯录' }),
    ).toBeTruthy();
    expect(
      screen.getByRole('button', { name: /规划 Agent/ }).getAttribute('title'),
    ).toBeNull();
    expect(screen.getByText('工作中')).toBeTruthy();
    expect(screen.getByText('Groups')).toBeTruthy();
    expect(screen.getByText('群组即将接入')).toBeTruthy();
    expect(screen.queryByRole('region', { name: '会话细节' })).toBeNull();
    expect(screen.queryByText('最近会话')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: /审查 Agent/ }));
    expect(onAgentChange).toHaveBeenCalledWith('reviewer');
  });

  it('keeps session history behind a secondary details toggle', () => {
    render(<SessionSidebar {...baseProps} />);

    const historyToggle = screen.getByRole('button', { name: '查看历史会话' });
    expect(historyToggle.getAttribute('aria-expanded')).toBe('false');
    fireEvent.click(historyToggle);

    const details = screen.getByRole('region', { name: '会话细节' });
    expect(
      screen
        .getByRole('button', { name: '隐藏历史会话' })
        .getAttribute('aria-expanded'),
    ).toBe('true');
    expect(screen.getByText('最近会话')).toBeTruthy();
    expect(within(details).getByText('实现主会话')).toBeTruthy();
  });

  it('derives concise agent contact statuses', () => {
    expect(getAgentContactStatus(agents[0], true).label).toBe('工作中');
    expect(getAgentContactStatus(agents[1], false).label).toBe('在线');
    expect(getAgentContactStatus(agents[2], false).label).toBe('冻结');
  });

  it('does not turn selected contacts into working status', () => {
    render(<SessionSidebar {...baseProps} workingAgentIds={[]} />);

    expect(
      screen.getByRole('button', { name: '规划 Agent 当前 在线' }),
    ).toBeTruthy();
    expect(screen.queryByText('工作中')).toBeNull();
    expect(screen.queryByText('当前')).toBeNull();
  });

  it('prefers Agent status projection over legacy working ids', () => {
    render(
      <SessionSidebar
        {...baseProps}
        workingAgentIds={[]}
        agentStatuses={{
          planner: { status: 'running', summary: '正在整理日志' },
        }}
      />,
    );

    expect(
      screen.getByRole('button', { name: '规划 Agent 当前 工作中' }),
    ).toBeTruthy();
    expect(screen.getByText('工作中')).toBeTruthy();
  });
});
