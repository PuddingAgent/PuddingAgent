import { useEffect, useState } from 'react';

type IdleWindow = Window &
  typeof globalThis & {
    requestIdleCallback?: (
      callback: () => void,
      options?: { timeout: number },
    ) => number;
    cancelIdleCallback?: (handle: number) => void;
  };

/**
 * 把余额、Goal 等辅助请求推迟到首帧之后的空闲期。
 * requestIdleCallback 不可用时只延迟一小段时间，保证旧 WebView2 不会饿死。
 */
export function scheduleAfterInitialPaint(
  callback: () => void,
  timeoutMs = 1200,
): () => void {
  const host = window as IdleWindow;
  let idleHandle: number | undefined;
  let timerHandle: number | undefined;
  let cancelled = false;

  const frameHandle = host.requestAnimationFrame(() => {
    if (cancelled) return;
    if (typeof host.requestIdleCallback === 'function') {
      idleHandle = host.requestIdleCallback(callback, { timeout: timeoutMs });
      return;
    }
    timerHandle = host.setTimeout(callback, Math.min(timeoutMs, 250));
  });

  return () => {
    cancelled = true;
    host.cancelAnimationFrame(frameHandle);
    if (idleHandle !== undefined) host.cancelIdleCallback?.(idleHandle);
    if (timerHandle !== undefined) host.clearTimeout(timerHandle);
  };
}

export function useInitialIdleReady(timeoutMs = 1200): boolean {
  const [ready, setReady] = useState(process.env.NODE_ENV === 'test');

  useEffect(() => {
    if (ready) return;
    return scheduleAfterInitialPaint(() => setReady(true), timeoutMs);
  }, [ready, timeoutMs]);

  return ready;
}
