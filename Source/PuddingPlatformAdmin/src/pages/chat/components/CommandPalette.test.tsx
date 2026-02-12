import { fireEvent, render, screen } from '@testing-library/react';
import React from 'react';
import CommandPalette, { COMMANDS, filterCommands } from './CommandPalette';

describe('CommandPalette', () => {
  it('contains real system authorization commands instead of old placeholders', () => {
    expect(
      COMMANDS.some((cmd) => cmd.shortcut === '/authorize shell 10m'),
    ).toBe(true);
    expect(
      COMMANDS.some((cmd) => cmd.shortcut === '/authorize shell session'),
    ).toBe(true);
    expect(
      COMMANDS.some((cmd) => cmd.shortcut === '/authorize file_write session'),
    ).toBe(true);
    expect(
      COMMANDS.some((cmd) => cmd.shortcut === '/authorize file_patch once'),
    ).toBe(true);
    expect(COMMANDS.some((cmd) => cmd.shortcut === '/status')).toBe(true);
    expect(COMMANDS.some((cmd) => cmd.shortcut === '/stop')).toBe(true);
    expect(COMMANDS.some((cmd) => cmd.shortcut === '/stop all')).toBe(true);
    expect(COMMANDS.some((cmd) => cmd.shortcut === '/mode')).toBe(true);
    expect(COMMANDS.some((cmd) => cmd.shortcut === '/mode safe')).toBe(true);
    expect(COMMANDS.some((cmd) => cmd.shortcut === '/mode normal')).toBe(true);
    expect(COMMANDS.some((cmd) => cmd.shortcut === '/yolo')).toBe(true);
    expect(COMMANDS.some((cmd) => cmd.shortcut === '/mode list')).toBe(true);
    expect(COMMANDS.some((cmd) => cmd.shortcut === '/estop')).toBe(false);
    expect(COMMANDS.some((cmd) => cmd.shortcut === '/deny shell')).toBe(true);
    expect(COMMANDS.some((cmd) => cmd.shortcut === '/revoke shell')).toBe(true);
    expect(COMMANDS.some((cmd) => cmd.shortcut === '/revoke file_write')).toBe(
      true,
    );
    expect(COMMANDS.some((cmd) => cmd.shortcut === '/revoke file_patch')).toBe(
      true,
    );
    expect(COMMANDS.some((cmd) => cmd.shortcut === '/compact')).toBe(true);
    expect(COMMANDS.some((cmd) => cmd.shortcut === '/memory')).toBe(true);
    expect(COMMANDS.some((cmd) => cmd.shortcut === '/api')).toBe(false);
    expect(COMMANDS.some((cmd) => cmd.shortcut === '/log')).toBe(false);
  });

  it('filters by command text and selects the full command', () => {
    const onSelect = jest.fn();
    render(
      <CommandPalette
        visible
        filterText="session"
        selectedIdx={0}
        onSelectIndex={jest.fn()}
        onSelect={onSelect}
        onClose={jest.fn()}
      />,
    );

    expect(screen.getByText('/authorize shell session')).toBeTruthy();
    expect(screen.queryByText('/authorize shell 10m')).toBeNull();

    fireEvent.click(screen.getByText('/authorize shell session'));

    expect(onSelect).toHaveBeenCalledWith(
      expect.objectContaining({
        shortcut: '/authorize shell session',
      }),
    );
  });

  it('shares the same filtering behavior with the composer', () => {
    expect(filterCommands('revoke').map((cmd) => cmd.shortcut)).toEqual([
      '/revoke shell',
      '/revoke file_write',
      '/revoke file_patch',
    ]);
    expect(filterCommands('安全').map((cmd) => cmd.shortcut)).toContain(
      '/mode safe',
    );
  });
});
