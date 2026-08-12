// ── P2#10 SandboxBoundaryIndicator 组件测试 ─────────────────
import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import { createDefaultSandboxBoundary } from '../sandbox/sandboxBoundary';
import SandboxBoundaryIndicator from './SandboxBoundaryIndicator';

describe('SandboxBoundaryIndicator', () => {
  it('renders placeholder chip when no boundary', () => {
    render(<SandboxBoundaryIndicator boundary={null} />);
    expect(screen.getByTestId('sandbox-boundary-chip').textContent).toContain(
      '未选择工作空间',
    );
  });

  it('renders chip with workspace short name and network mode tag', () => {
    const boundary = createDefaultSandboxBoundary('ws-42', 'allowlist');
    render(<SandboxBoundaryIndicator boundary={boundary} />);
    const chip = screen.getByTestId('sandbox-boundary-chip');
    expect(chip.textContent).toContain('ws-42');
    expect(screen.getByTestId('sandbox-network-mode').textContent).toContain(
      '白名单',
    );
  });

  it('opens popover showing root, protected paths and network description', () => {
    const boundary = createDefaultSandboxBoundary('ws-7', 'none');
    render(<SandboxBoundaryIndicator boundary={boundary} />);
    fireEvent.click(screen.getByTestId('sandbox-boundary-chip'));
    expect(screen.getByTestId('sandbox-boundary-popover')).toBeTruthy();
    expect(
      screen.getByTestId('sandbox-workspace-root').textContent,
    ).toContain('/workspaces/ws-7');
    expect(
      screen.getByTestId('sandbox-protected-paths').textContent,
    ).toContain('.git');
    expect(screen.getByTestId('sandbox-network-description').textContent).toContain(
      '禁止所有外联',
    );
  });

  it('calls onNetworkModeChange when an option is clicked', () => {
    const onNetworkModeChange = jest.fn();
    const boundary = createDefaultSandboxBoundary('ws-7', 'allowlist');
    render(
      <SandboxBoundaryIndicator
        boundary={boundary}
        onNetworkModeChange={onNetworkModeChange}
      />,
    );
    fireEvent.click(screen.getByTestId('sandbox-boundary-chip'));
    fireEvent.click(screen.getByTestId('sandbox-network-option-full'));
    expect(onNetworkModeChange).toHaveBeenCalledWith('full');
  });

  it('does not render mode options when no callback', () => {
    const boundary = createDefaultSandboxBoundary('ws-7', 'allowlist');
    render(<SandboxBoundaryIndicator boundary={boundary} />);
    fireEvent.click(screen.getByTestId('sandbox-boundary-chip'));
    expect(screen.queryByTestId('sandbox-network-option-full')).toBeNull();
  });

  it('respects disabled prop', () => {
    render(
      <SandboxBoundaryIndicator
        boundary={createDefaultSandboxBoundary('ws-1')}
        disabled
      />,
    );
    const chip = screen.getByTestId('sandbox-boundary-chip');
    expect(chip.style.opacity).toBe('0.45');
  });
});
