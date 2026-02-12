import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import WorkspaceStudioView from './WorkspaceStudioView';

jest.mock('./WorkspaceStudioGameCanvas', () => () => (
  <div data-testid="studio-canvas" />
));

const agent = (overrides: any): any => ({
  displayName: overrides.name,
  isEnabled: true,
  isFrozen: false,
  createdAt: '2026-05-25T00:00:00Z',
  updatedAt: '2026-05-25T00:00:00Z',
  ...overrides,
});

describe('WorkspaceStudioView agent HUD', () => {
  it('renders a dynamic top avatar button for each workspace agent', () => {
    const onAgentChange = jest.fn();
    render(
      <WorkspaceStudioView
        agents={[
          agent({
            agentId: 'default-agent',
            name: '默认助手',
            avatarUrl: '/avatars/default.png',
          }),
          agent({ agentId: 'consultant', name: '咨询专家', avatarEmoji: '咨' }),
        ]}
        selectedAgentId="default-agent"
        turns={[]}
        loading={false}
        subAgentCards={{}}
        onAgentChange={onAgentChange}
      />,
    );

    expect(
      screen.getByRole('button', { name: '打开 默认助手 角色面板' }),
    ).toBeTruthy();
    expect(
      screen.getByRole('button', { name: '切换到 咨询专家 角色面板' }),
    ).toBeTruthy();

    fireEvent.click(
      screen.getByRole('button', { name: '切换到 咨询专家 角色面板' }),
    );

    expect(onAgentChange).toHaveBeenCalledWith('consultant');
  });
});
