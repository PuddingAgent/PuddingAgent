import { useEffect, useRef, useCallback } from 'react';

/**
 * Hook that encapsulates the schedule/cancel/onVisibility polling pattern
 * extracted from DevPanel.tsx.
 *
 * - `schedule`: starts a setInterval calling `loadFn` every `delayMs` ms
 * - `cancel`: clears the interval
 * - `refresh`: immediately calls `loadFn` once
 *
 * Automatically pauses when the document is hidden and resumes when visible,
 * but only when `visible` (the caller's gate) is true.
 */
export function usePollingLoader(
  loadFn: () => void,
  visible: boolean,
  delayMs: number,
  deps: React.DependencyList,
): { schedule: () => void; cancel: () => void; refresh: () => void } {
  const timerRef = useRef<number | null>(null);
  const loadFnRef = useRef(loadFn);
  loadFnRef.current = loadFn;

  const cancel = useCallback(() => {
    if (timerRef.current != null) {
      window.clearInterval(timerRef.current);
      timerRef.current = null;
    }
  }, []);

  const schedule = useCallback(() => {
    if (timerRef.current != null) return;
    timerRef.current = window.setInterval(() => {
      loadFnRef.current();
    }, delayMs);
  }, [delayMs]);

  const refresh = useCallback(() => {
    loadFnRef.current();
  }, []);

  useEffect(() => {
    if (visible && document.visibilityState !== 'hidden') {
      schedule();
    }
    const onVisibility = () => {
      if (document.visibilityState === 'hidden') {
        cancel();
      } else if (visible) {
        schedule();
      }
    };
    document.addEventListener('visibilitychange', onVisibility);
    return () => {
      cancel();
      document.removeEventListener('visibilitychange', onVisibility);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [visible, schedule, cancel, ...deps]);

  return { schedule, cancel, refresh };
}
