import { act, renderHook } from '@testing-library/react';
import { useHoldToTalk } from './useHoldToTalk';

it('ignores key repeat and sends once on release, even when startup is pending', () => {
  jest.useFakeTimers();
  const options = { blocked: false, start: jest.fn(), finish: jest.fn(), cancel: jest.fn(), shortPress: jest.fn() };
  const view = renderHook(() => useHoldToTalk(options));
  act(() => { window.dispatchEvent(new KeyboardEvent('keydown', { code: 'Space', ctrlKey: true, shiftKey: true })); window.dispatchEvent(new KeyboardEvent('keydown', { code: 'Space', ctrlKey: true, shiftKey: true, repeat: true })); });
  expect(options.start).toHaveBeenCalledTimes(1);
  act(() => { jest.advanceTimersByTime(300); window.dispatchEvent(new KeyboardEvent('keyup', { code: 'Space' })); window.dispatchEvent(new KeyboardEvent('keyup', { code: 'Space' })); });
  expect(options.finish).toHaveBeenCalledTimes(1); view.unmount(); jest.useRealTimers();
});

it('cancels a short tap and blur, never sends them', () => {
  const options = { blocked: false, start: jest.fn(), finish: jest.fn(), cancel: jest.fn(), shortPress: jest.fn() };
  const view = renderHook(() => useHoldToTalk(options));
  act(() => { window.dispatchEvent(new KeyboardEvent('keydown', { code: 'Space', ctrlKey: true, shiftKey: true })); window.dispatchEvent(new KeyboardEvent('keyup', { code: 'Space' })); });
  expect(options.shortPress).toHaveBeenCalledTimes(1); expect(options.finish).not.toHaveBeenCalled();
  act(() => { window.dispatchEvent(new KeyboardEvent('keydown', { code: 'Space', ctrlKey: true, shiftKey: true })); window.dispatchEvent(new Event('blur')); });
  expect(options.finish).not.toHaveBeenCalled(); view.unmount();
});
