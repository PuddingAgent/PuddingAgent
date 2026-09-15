import React from 'react';
import { act, fireEvent, render, screen } from '@testing-library/react';
import ExpandableMessageContent from './ExpandableMessageContent';

describe('long message reading disclosure', () => {
  let height: number;
  let resize: () => void;
  const disconnect = jest.fn();
  const originalObserver = global.ResizeObserver;
  beforeEach(() => {
    height = 700;
    jest.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(() => ({ height }) as DOMRect);
    global.ResizeObserver = class {
      constructor(callback: () => void) { resize = callback; }
      observe() {}
      disconnect = disconnect;
    } as unknown as typeof ResizeObserver;
  });
  afterEach(() => { jest.restoreAllMocks(); global.ResizeObserver = originalObserver; });

  it('keeps the full content and lets users expand and collapse long markdown', () => {
    render(<ExpandableMessageContent><p>完整正文</p></ExpandableMessageContent>);
    expect(screen.getByTestId('reading-disclosure').getAttribute('data-collapsed')).toBe('true');
    expect(screen.getByText('完整正文')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: '展开完整正文' }));
    expect(screen.getByTestId('reading-disclosure').getAttribute('data-collapsed')).toBe('false');
    fireEvent.click(screen.getAllByRole('button', { name: '收起正文' })[1]);
    expect(screen.getByTestId('reading-disclosure').getAttribute('data-collapsed')).toBe('true');
  });

  it('measures late layout changes, keeps explicit expansion, and removes unnecessary controls', () => {
    height = 100;
    const { unmount } = render(<ExpandableMessageContent><p>正文</p></ExpandableMessageContent>);
    expect(screen.queryByRole('button')).toBeNull();
    height = 900;
    act(() => resize());
    fireEvent.click(screen.getByRole('button', { name: '展开完整正文' }));
    height = 1000;
    act(() => resize());
    expect(screen.getByTestId('reading-disclosure').getAttribute('data-collapsed')).toBe('false');
    height = 200;
    act(() => resize());
    expect(screen.queryByRole('button')).toBeNull();
    unmount();
    expect(disconnect).toHaveBeenCalled();
  });

  it('does not clip live content and expands when keyboard focus enters a preview link', () => {
    const { rerender } = render(<ExpandableMessageContent disabled><a href="#result">结果链接</a></ExpandableMessageContent>);
    expect(screen.queryByRole('button')).toBeNull();
    rerender(<ExpandableMessageContent><a href="#result">结果链接</a></ExpandableMessageContent>);
    expect(screen.getByTestId('reading-disclosure').getAttribute('data-collapsed')).toBe('true');
    fireEvent.focus(screen.getByRole('link'));
    expect(screen.getByTestId('reading-disclosure').getAttribute('data-collapsed')).toBe('false');
  });
});
