import { render, screen } from '@testing-library/react';
import * as React from 'react';
import WorkspaceNavigationHeader, { headerStyles } from './index';

const mockHistoryPush = jest.fn();

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
  PuddingGlobalActions: () => <div data-testid="global-actions" />,
}));

describe('WorkspaceNavigationHeader theme tokens', () => {
  beforeEach(() => {
    mockHistoryPush.mockClear();
  });

  it('uses semantic chat tokens for surfaces and borders so dark mode stays legible', () => {
    expect(headerStyles.header.background).toBe('var(--pudding-chat-header-bg)');
    expect(headerStyles.header.borderBottom).toBe('1px solid var(--pudding-chat-border)');

    render(<WorkspaceNavigationHeader crumbs={[{ label: '默认工作空间' }, { label: '默认助手' }]} />);

    expect(screen.getByRole('banner')).toBeTruthy();
  });
});
