import { scheduleAfterInitialPaint } from './useInitialIdleReady';

describe('scheduleAfterInitialPaint', () => {
  const originalRequestAnimationFrame = window.requestAnimationFrame;
  const originalCancelAnimationFrame = window.cancelAnimationFrame;
  const originalRequestIdleCallback = (
    window as Window & { requestIdleCallback?: unknown }
  ).requestIdleCallback;
  const originalCancelIdleCallback = (
    window as Window & { cancelIdleCallback?: unknown }
  ).cancelIdleCallback;

  afterEach(() => {
    window.requestAnimationFrame = originalRequestAnimationFrame;
    window.cancelAnimationFrame = originalCancelAnimationFrame;
    Object.assign(window, {
      requestIdleCallback: originalRequestIdleCallback,
      cancelIdleCallback: originalCancelIdleCallback,
    });
    jest.useRealTimers();
  });

  it('waits for a frame and then uses the browser idle callback', () => {
    let frameCallback: FrameRequestCallback | undefined;
    let idleCallback: (() => void) | undefined;
    window.requestAnimationFrame = jest.fn((callback) => {
      frameCallback = callback;
      return 7;
    });
    window.cancelAnimationFrame = jest.fn();
    Object.assign(window, {
      requestIdleCallback: jest.fn((callback: () => void) => {
        idleCallback = callback;
        return 11;
      }),
      cancelIdleCallback: jest.fn(),
    });
    const callback = jest.fn();

    const cancel = scheduleAfterInitialPaint(callback, 900);
    expect(callback).not.toHaveBeenCalled();
    frameCallback?.(0);
    expect(window.requestIdleCallback).toHaveBeenCalledWith(callback, {
      timeout: 900,
    });
    idleCallback?.();
    expect(callback).toHaveBeenCalledTimes(1);

    cancel();
    expect(window.cancelAnimationFrame).toHaveBeenCalledWith(7);
    expect(window.cancelIdleCallback).toHaveBeenCalledWith(11);
  });

  it('falls back to a short timer when requestIdleCallback is unavailable', () => {
    jest.useFakeTimers();
    window.requestAnimationFrame = jest.fn((callback) => {
      callback(0);
      return 3;
    });
    window.cancelAnimationFrame = jest.fn();
    Object.assign(window, {
      requestIdleCallback: undefined,
      cancelIdleCallback: undefined,
    });
    const callback = jest.fn();

    scheduleAfterInitialPaint(callback, 1200);
    jest.advanceTimersByTime(249);
    expect(callback).not.toHaveBeenCalled();
    jest.advanceTimersByTime(1);
    expect(callback).toHaveBeenCalledTimes(1);
  });
});
