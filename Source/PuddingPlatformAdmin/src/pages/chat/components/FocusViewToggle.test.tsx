// ── FocusViewToggle：P2#8 Focus view 开关 ────────────────────
import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import FocusViewToggle from './FocusViewToggle';

jest.mock('antd', () => ({
  Switch: ({ checked, onChange, ...props }: any) => (
    <button
      type="button"
      data-checked={String(checked)}
      onClick={() => onChange?.(!checked)}
      {...props}
    >
      {checked ? 'on' : 'off'}
    </button>
  ),
  Tooltip: ({ children }: any) => <span>{children}</span>,
}));

const switchTestId = 'focus-view-toggle-switch';

describe('FocusViewToggle', () => {
  it('renders the current value and the label', () => {
    render(<FocusViewToggle value={false} onChange={jest.fn()} />);
    expect(screen.getByTestId('focus-view-toggle')).toBeTruthy();
    expect(screen.getByText('专注')).toBeTruthy();
    expect(screen.getByTestId(switchTestId).dataset.checked).toBe('false');
  });

  it('renders true value as checked', () => {
    render(<FocusViewToggle value onChange={jest.fn()} />);
    expect(screen.getByTestId(switchTestId).dataset.checked).toBe('true');
  });

  it('fires onChange with the toggled value on click', () => {
    const onChange = jest.fn();
    render(<FocusViewToggle value={false} onChange={onChange} />);
    fireEvent.click(screen.getByTestId(switchTestId));
    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange).toHaveBeenCalledWith(true);
  });

  it('fires onChange with false when toggling off', () => {
    const onChange = jest.fn();
    render(<FocusViewToggle value onChange={onChange} />);
    fireEvent.click(screen.getByTestId(switchTestId));
    expect(onChange).toHaveBeenCalledWith(false);
  });
});
