import { render, screen } from '@testing-library/react';
import * as React from 'react';
import WorkspaceNavigationHeader, { headerStyles } from './index';

const mockHistoryPush = jest.fn();
const mockGlobalActionsProps = jest.fn();

jest.mock('@umijs/max', () => ({
  history: {
    push: (...args: unknown[]) => mockHistoryPush(...args),
  },
  useModel: () => ({
    initialState: {
      currentUser: { access: 'user' },
    },
  }),
}));

jest.mock('@/components/GlobalActions', () => ({
  PuddingGlobalActions: (props: { variant: string; hideHelp?: boolean }) => {
    mockGlobalActionsProps(props);
    return <div data-testid="global-actions" />;
  },
}));

describe('WorkspaceNavigationHeader theme tokens', () => {
  beforeEach(() => {
    mockHistoryPush.mockClear();
    mockGlobalActionsProps.mockClear();
  });

  it('uses semantic chat tokens for surfaces and borders so dark mode stays legible', () => {
    expect(headerStyles.header.background).toBe('var(--pudding-chat-header-bg)');
    expect(headerStyles.header.borderBottom).toBe('1px solid var(--pudding-chat-border)');

    render(<WorkspaceNavigationHeader crumbs={[{ label: '默认工作空间' }, { label: '默认助手' }]} />);

    expect(screen.getByRole('banner')).toBeTruthy();
  });

  it('IMG03：把「帮助已并入更多」的意图透传给 chat 全局操作区', () => {
    render(<WorkspaceNavigationHeader crumbs={[]} hideGlobalHelp />);

    expect(mockGlobalActionsProps).toHaveBeenCalledWith(
      expect.objectContaining({ variant: 'chat', hideHelp: true }),
    );
  });

  it('IMG03：未声明时保持内联帮助（其它消费方行为不变）', () => {
    render(<WorkspaceNavigationHeader crumbs={[]} />);

    expect(mockGlobalActionsProps).toHaveBeenCalledWith(
      expect.objectContaining({ variant: 'chat', hideHelp: undefined }),
    );
  });
});
