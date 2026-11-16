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

it('ignores the shortcut while a text field has focus, so typing is never sent', () => {
  const options = { blocked: false, start: jest.fn(), finish: jest.fn(), cancel: jest.fn(), shortPress: jest.fn() };
  const view = renderHook(() => useHoldToTalk(options));
  const input = document.createElement('textarea');
  document.body.appendChild(input);
  input.focus();
  act(() => {
    window.dispatchEvent(new KeyboardEvent('keydown', { code: 'Space', ctrlKey: true, shiftKey: true }));
    window.dispatchEvent(new KeyboardEvent('keyup', { code: 'Space' }));
  });
  expect(options.start).not.toHaveBeenCalled();
  expect(options.finish).not.toHaveBeenCalled();
  input.remove(); view.unmount();
});

it('also ignores the shortcut inside a contenteditable block', () => {
  const options = { blocked: false, start: jest.fn(), finish: jest.fn(), cancel: jest.fn(), shortPress: jest.fn() };
  const view = renderHook(() => useHoldToTalk(options));
  const editable = document.createElement('div');
  editable.setAttribute('contenteditable', 'true');
  Object.defineProperty(editable, 'isContentEditable', { value: true });
  document.body.appendChild(editable);
  editable.focus();
  act(() => {
    window.dispatchEvent(new KeyboardEvent('keydown', { code: 'Space', ctrlKey: true, shiftKey: true }));
  });
  expect(options.start).not.toHaveBeenCalled();
  editable.remove(); view.unmount();
});

it('works again once focus leaves the text field', () => {
  jest.useFakeTimers();
  const options = { blocked: false, start: jest.fn(), finish: jest.fn(), cancel: jest.fn(), shortPress: jest.fn() };
  const view = renderHook(() => useHoldToTalk(options));
  const input = document.createElement('textarea');
  document.body.appendChild(input);
  input.focus();
  act(() => {
    window.dispatchEvent(new KeyboardEvent('keydown', { code: 'Space', ctrlKey: true, shiftKey: true }));
  });
  expect(options.start).not.toHaveBeenCalled();
  input.blur();
  act(() => {
    window.dispatchEvent(new KeyboardEvent('keydown', { code: 'Space', ctrlKey: true, shiftKey: true }));
  });
  expect(options.start).toHaveBeenCalledTimes(1);
  act(() => { jest.advanceTimersByTime(300); window.dispatchEvent(new KeyboardEvent('keyup', { code: 'Space' })); });
  expect(options.finish).toHaveBeenCalledTimes(1);
  input.remove(); view.unmount(); jest.useRealTimers();
});

/** jsdom 没有 PointerEvent 构造器；手动派发足以覆盖这里的事件处理契约。 */
const firePointer = (type: string, props: Record<string, unknown>) => {
  const event = new Event(type, { bubbles: true }) as unknown as Record<string, unknown>;
  Object.assign(event, props);
  window.dispatchEvent(event as unknown as Event);
};

const pointerDown = (button: number, pointerId: number) =>
  ({
    button,
    pointerId,
    preventDefault: jest.fn(),
    currentTarget: { setPointerCapture: jest.fn() },
  }) as never;

it('keeps recording when a different mouse button is released', () => {
  jest.useFakeTimers();
  const options = { blocked: false, start: jest.fn(), finish: jest.fn(), cancel: jest.fn(), shortPress: jest.fn() };
  const view = renderHook(() => useHoldToTalk(options));
  act(() => { view.result.current.beginPointer(pointerDown(1, 1)); });
  expect(options.start).toHaveBeenCalledTimes(1);
  act(() => {
    jest.advanceTimersByTime(400);
    firePointer('pointerup', { pointerId: 1, button: 0, pointerType: 'mouse' });
  });
  expect(options.finish).not.toHaveBeenCalled();
  expect(options.cancel).not.toHaveBeenCalled();
  act(() => { firePointer('pointerup', { pointerId: 1, button: 1, pointerType: 'mouse' }); });
  expect(options.finish).toHaveBeenCalledTimes(1);
  view.unmount(); jest.useRealTimers();
});

it('ignores a mouse pointercancel so an autoscroll does not swallow the recording', () => {
  jest.useFakeTimers();
  const options = { blocked: false, start: jest.fn(), finish: jest.fn(), cancel: jest.fn(), shortPress: jest.fn() };
  const view = renderHook(() => useHoldToTalk(options));
  act(() => { view.result.current.beginPointer(pointerDown(1, 1)); });
  act(() => {
    jest.advanceTimersByTime(400);
    firePointer('pointercancel', { pointerId: 1, pointerType: 'mouse' });
  });
  expect(options.cancel).not.toHaveBeenCalled();
  act(() => { firePointer('pointerup', { pointerId: 1, button: 1, pointerType: 'mouse' }); });
  expect(options.finish).toHaveBeenCalledTimes(1);
  view.unmount(); jest.useRealTimers();
});

it('still cancels on a touch pointercancel', () => {
  const options = { blocked: false, start: jest.fn(), finish: jest.fn(), cancel: jest.fn(), shortPress: jest.fn() };
  const view = renderHook(() => useHoldToTalk(options));
  act(() => { view.result.current.beginPointer(pointerDown(0, 7)); });
  act(() => { firePointer('pointercancel', { pointerId: 7, pointerType: 'touch' }); });
  expect(options.cancel).toHaveBeenCalledTimes(1);
  view.unmount();
});
