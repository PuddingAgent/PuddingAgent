// ── PermissionModeSelector 单元测试（用户 2026-09-19：两档 + 5 分钟临时）──
import { fireEvent, render, screen, within } from '@testing-library/react';
import * as React from 'react';
import PermissionModeSelector from './PermissionModeSelector';

jest.mock('../styles', () => {
  const styles = new Proxy(
    {},
    {
      get: (_target, prop) => String(prop),
    },
  );
  return {
    useChatStyles: () => ({ styles }),
  };
});

const PERMISSION_LABELS = ['自动审批', '完全访问', '完全访问（5 分钟）'];
const PERMISSION_MODE_IDS = ['auto', 'full', 'fullTemporary'];

describe('PermissionModeSelector', () => {
  it('renders the current mode label on the trigger button', () => {
    render(<PermissionModeSelector value="auto" onChange={jest.fn()} />);

    const trigger = screen.getByTestId('permission-mode-selector');
    expect(trigger).toBeTruthy();
    expect(trigger.getAttribute('aria-label')).toBe('权限模式：自动审批');
    expect(trigger.textContent).toContain('自动审批');
  });

  it('opens the menu and lists the two levels plus the 5-minute option', () => {
    render(<PermissionModeSelector value="auto" onChange={jest.fn()} />);

    fireEvent.click(screen.getByTestId('permission-mode-selector'));

    const menu = screen.getByTestId('permission-mode-menu');
    expect(menu).toBeTruthy();
    for (const label of PERMISSION_LABELS) {
      expect(within(menu).getByText(label)).toBeTruthy();
    }
    for (const mode of PERMISSION_MODE_IDS) {
      expect(
        within(menu).getByTestId(`permission-mode-option-${mode}`),
      ).toBeTruthy();
    }
    // 旧的四档选项必须彻底消失（用户明确要求简化）。
    for (const removed of ['manual', 'acceptEdits', 'plan']) {
      expect(
        within(menu).queryByTestId(`permission-mode-option-${removed}`),
      ).toBeNull();
    }
  });

  it('describes the temporary option as auto-reverting', () => {
    render(<PermissionModeSelector value="auto" onChange={jest.fn()} />);

    fireEvent.click(screen.getByTestId('permission-mode-selector'));

    const menu = screen.getByTestId('permission-mode-menu');
    expect(within(menu).getByText('5 分钟后自动回到自动审批')).toBeTruthy();
  });

  it('marks the active mode with data-active and aria-selected', () => {
    render(<PermissionModeSelector value="full" onChange={jest.fn()} />);

    fireEvent.click(screen.getByTestId('permission-mode-selector'));

    const fullOption = screen.getByTestId('permission-mode-option-full');
    expect(fullOption.getAttribute('data-active')).toBe('true');
    expect(fullOption.getAttribute('aria-selected')).toBe('true');

    const autoOption = screen.getByTestId('permission-mode-option-auto');
    expect(autoOption.getAttribute('data-active')).toBeNull();
    expect(autoOption.getAttribute('aria-selected')).toBe('false');
  });

  it('invokes onChange with the selected mode and closes the menu', () => {
    const onChange = jest.fn();
    render(<PermissionModeSelector value="auto" onChange={onChange} />);

    fireEvent.click(screen.getByTestId('permission-mode-selector'));
    fireEvent.click(screen.getByTestId('permission-mode-option-fullTemporary'));

    expect(onChange).toHaveBeenCalledWith('fullTemporary');
    expect(onChange).toHaveBeenCalledTimes(1);
  });

  it('disables the trigger when disabled is set', () => {
    render(<PermissionModeSelector value="auto" onChange={jest.fn()} disabled />);

    const trigger = screen.getByTestId(
      'permission-mode-selector',
    ) as HTMLButtonElement;
    expect(trigger.disabled).toBe(true);
  });
});
