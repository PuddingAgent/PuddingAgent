// ── PermissionModeSelector 单元测试（P1#4）─────────────────
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

const PERMISSION_LABELS = ['每步需批', '只批编辑', '先计划后执行', '自动执行'];

describe('PermissionModeSelector', () => {
  it('renders the current mode label on the trigger button', () => {
    render(
      <PermissionModeSelector value="auto" onChange={jest.fn()} />,
    );

    const trigger = screen.getByTestId('permission-mode-selector');
    expect(trigger).toBeTruthy();
    expect(trigger.getAttribute('aria-label')).toBe('权限模式：自动执行');
    expect(trigger.textContent).toContain('自动执行');
  });

  it('opens the menu and lists all four permission modes', () => {
    render(
      <PermissionModeSelector value="manual" onChange={jest.fn()} />,
    );

    fireEvent.click(screen.getByTestId('permission-mode-selector'));

    const menu = screen.getByTestId('permission-mode-menu');
    expect(menu).toBeTruthy();
    for (const label of PERMISSION_LABELS) {
      expect(within(menu).getByText(label)).toBeTruthy();
    }
    for (const mode of ['manual', 'acceptEdits', 'plan', 'auto']) {
      expect(
        within(menu).getByTestId(`permission-mode-option-${mode}`),
      ).toBeTruthy();
    }
  });

  it('marks the active mode with data-active and aria-selected', () => {
    render(
      <PermissionModeSelector value="plan" onChange={jest.fn()} />,
    );

    fireEvent.click(screen.getByTestId('permission-mode-selector'));

    const planOption = screen.getByTestId('permission-mode-option-plan');
    expect(planOption.getAttribute('data-active')).toBe('true');
    expect(planOption.getAttribute('aria-selected')).toBe('true');

    const autoOption = screen.getByTestId('permission-mode-option-auto');
    expect(autoOption.getAttribute('data-active')).toBeNull();
    expect(autoOption.getAttribute('aria-selected')).toBe('false');
  });

  it('invokes onChange with the selected mode and closes the menu', () => {
    const onChange = jest.fn();
    render(
      <PermissionModeSelector value="auto" onChange={onChange} />,
    );

    fireEvent.click(screen.getByTestId('permission-mode-selector'));
    fireEvent.click(screen.getByTestId('permission-mode-option-acceptEdits'));

    expect(onChange).toHaveBeenCalledWith('acceptEdits');
    expect(onChange).toHaveBeenCalledTimes(1);
  });

  it('disables the trigger when disabled is set', () => {
    render(
      <PermissionModeSelector value="auto" onChange={jest.fn()} disabled />,
    );

    const trigger = screen.getByTestId(
      'permission-mode-selector',
    ) as HTMLButtonElement;
    expect(trigger.disabled).toBe(true);
  });
});
