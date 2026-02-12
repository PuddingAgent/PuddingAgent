import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import type { AgentAvatarRenderState } from '../hooks/agentAvatarRuntime';
import AgentAvatarRuntimeView from './AgentAvatarRuntimeView';

const spriteRenderState: AgentAvatarRenderState = {
  runtimeKind: 'sprite',
  status: 'listening',
  expression: 'focused',
  motion: 'listen',
  visible: true,
  spriteSheetUrl: '/admin/assets/agent-sprites/neutral/spritesheet.png',
  spriteRow: 1,
  spriteFrameCount: 6,
  ariaLabel: 'Agent default-agent 状态：正在听',
};

describe('AgentAvatarRuntimeView', () => {
  it('renders a compact sprite avatar with accessible status and frame metadata', () => {
    render(
      <AgentAvatarRuntimeView
        renderState={spriteRenderState}
        agentName="默认助手"
      />,
    );

    const avatar = screen.getByRole('img', {
      name: 'Agent default-agent 状态：正在听',
    });

    expect(avatar.getAttribute('data-runtime-kind')).toBe('sprite');
    expect(avatar.getAttribute('data-avatar-status')).toBe('listening');
    expect(avatar.getAttribute('data-sprite-row')).toBe('1');
    expect(avatar.getAttribute('data-sprite-frame-count')).toBe('6');
    expect(screen.getByText('默认助手')).toBeTruthy();
    expect(screen.getByText('正在听')).toBeTruthy();
  });

  it('calls visibility change when the user closes the avatar view', () => {
    const onVisibilityChange = jest.fn();

    render(
      <AgentAvatarRuntimeView
        renderState={spriteRenderState}
        agentName="默认助手"
        onVisibilityChange={onVisibilityChange}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: '隐藏虚拟形象' }));

    expect(onVisibilityChange).toHaveBeenCalledWith(false);
  });

  it('does not render when the avatar runtime is hidden', () => {
    render(
      <AgentAvatarRuntimeView
        renderState={{ ...spriteRenderState, visible: false }}
        agentName="默认助手"
      />,
    );

    expect(screen.queryByRole('img')).toBeNull();
  });

  it('renders static avatars without sprite frame metadata', () => {
    render(
      <AgentAvatarRuntimeView
        agentName="默认助手"
        renderState={{
          runtimeKind: 'static',
          status: 'idle',
          expression: 'neutral',
          motion: 'idle',
          visible: true,
          staticAvatarUrl:
            '/admin/assets/agent-avatars/agent-avatar-neutral.png',
          ariaLabel: 'Agent default-agent 状态：待命',
        }}
      />,
    );

    const avatar = screen.getByRole('img', {
      name: 'Agent default-agent 状态：待命',
    });

    expect(avatar.getAttribute('src')).toBe(
      '/admin/assets/agent-avatars/agent-avatar-neutral.png',
    );
    expect(avatar.hasAttribute('data-sprite-row')).toBe(false);
    expect(screen.getByText('待命')).toBeTruthy();
  });

  it('shows the error text as a status detail without exposing provider payloads', () => {
    render(
      <AgentAvatarRuntimeView
        agentName="默认助手"
        statusDetail="tts playback failed"
        renderState={{
          ...spriteRenderState,
          status: 'error',
          expression: 'concerned',
          motion: 'error',
          spriteRow: 6,
          spriteFrameCount: 1,
          ariaLabel: 'Agent default-agent 状态：异常',
        }}
      />,
    );

    expect(screen.getByText('异常')).toBeTruthy();
    expect(screen.getByText('tts playback failed')).toBeTruthy();
    expect(screen.queryByText(/reasoning_content/)).toBeNull();
  });
});
