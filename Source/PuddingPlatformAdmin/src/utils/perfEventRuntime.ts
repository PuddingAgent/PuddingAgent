export type PuddingDebugApi = {
  getSessionState(sessionId: string): any | null;
  getLastTraceId(): string | null;
  getLastSessionId(): string | null;
  getLastMessageId(): string | null;
  exportTimeline(): any | null;
  clearDebugEvents(): void;
};

export type PuddingPerfEvent = {
  name: string;
  at: number;
  payload?: Record<string, unknown>;
};

const PERF_DIAGNOSTICS_STORAGE_KEY = 'pudding_perf';
const MAX_PERF_EVENTS = 5000;
const perfEvents: PuddingPerfEvent[] = [];
const perfThrottle = new Map<string, number>();

/** 判断 debug mode 是否启用（通过 URL 参数 ?debug=1） */
export function isDebugMode(): boolean {
  try {
    return new URLSearchParams(window.location.search).get('debug') === '1';
  } catch {
    return false;
  }
}

/** 判断聊天性能诊断是否启用：?perf=1 / ?debug=1 / localStorage.pudding_perf=1 */
export function isPerfDiagnosticsEnabled(): boolean {
  try {
    const urlParams = new URLSearchParams(window.location.search);
    return (
      urlParams.get('perf') === '1' ||
      urlParams.get('debug') === '1' ||
      localStorage.getItem(PERF_DIAGNOSTICS_STORAGE_KEY) === '1'
    );
  } catch {
    return false;
  }
}

export function setPerfDiagnosticsEnabled(enabled: boolean): void {
  try {
    if (enabled) {
      localStorage.setItem(PERF_DIAGNOSTICS_STORAGE_KEY, '1');
    } else {
      localStorage.removeItem(PERF_DIAGNOSTICS_STORAGE_KEY);
    }
  } catch {
    // Storage can be unavailable in restricted browser contexts.
  }
}

/** Full diagnostics consumes the same mutable buffer without copying it. */
export function getPerfEventBuffer(): PuddingPerfEvent[] {
  return perfEvents;
}

export function getPerfEvents(): PuddingPerfEvent[] {
  return [...perfEvents];
}

export function clearPerfEventStore(): void {
  perfEvents.length = 0;
  perfThrottle.clear();
}

export function recordPerfEvent(
  name: string,
  payload?: Record<string, unknown>,
  options?: { throttleMs?: number },
): void {
  if (!isPerfDiagnosticsEnabled()) return;
  const now = performance.now();
  const throttleMs = options?.throttleMs ?? 0;
  if (throttleMs > 0) {
    const last = perfThrottle.get(name) ?? 0;
    if (now - last < throttleMs) return;
    perfThrottle.set(name, now);
  }

  perfEvents.push({ name, at: Math.round(now), payload });
  if (perfEvents.length > MAX_PERF_EVENTS) {
    perfEvents.splice(0, perfEvents.length - MAX_PERF_EVENTS);
  }
  if (localStorage.getItem('pudding_perf_console') === '1') {
    console.debug('[Pudding Perf]', name, payload ?? {});
  }
}

export function recordPerfStep(
  workflow: string,
  step: string,
  startedAt: number,
  payload: Record<string, unknown> = {},
): void {
  if (!isPerfDiagnosticsEnabled()) return;
  const durationMs = Math.max(0, Math.round(performance.now() - startedAt));
  const status = typeof payload.status === 'string' ? payload.status : 'ok';
  recordPerfEvent('chat.workflow.step', {
    ...payload,
    workflow,
    step,
    status,
    durationMs,
  });
}

export function markPerf(name: string): void {
  if (!isPerfDiagnosticsEnabled()) return;
  try {
    performance.mark(`pudding:${name}`);
  } catch {
    // performance.mark can fail in constrained test/browser contexts.
  }
}

export function measurePerf(
  name: string,
  startMark: string,
  endMark?: string,
): number | null {
  if (!isPerfDiagnosticsEnabled()) return null;
  try {
    const entryName = `pudding:${name}`;
    performance.measure(
      entryName,
      `pudding:${startMark}`,
      endMark ? `pudding:${endMark}` : undefined,
    );
    const entries = performance.getEntriesByName(entryName, 'measure');
    const duration = entries.length
      ? entries[entries.length - 1].duration
      : null;
    if (duration != null) {
      recordPerfEvent(name, { durationMs: Math.round(duration) });
    }
    return duration;
  } catch {
    return null;
  }
}

/** 写入 last session/message（仅 debug mode 下启用） */
export function writeDebugSessionState(
  sessionId: string,
  messageId: string,
): void {
  if (!isDebugMode()) return;
  sessionStorage.setItem('pudding_last_session_id', sessionId);
  sessionStorage.setItem('pudding_last_message_id', messageId);
  console.log('[Pudding Debug] Wrote session', sessionId, 'message', messageId);
}

/** 写入 last trace（仅 debug mode 下启用） */
export function writeDebugTrace(traceId: string): void {
  if (!isDebugMode()) return;
  sessionStorage.setItem('pudding_last_trace_id', traceId);
  console.log('[Pudding Debug] Wrote trace', traceId);
}

/** 注册 debug API 到 window.__PUDDING_DEBUG__ */
export function registerDebugApi(api: PuddingDebugApi): void {
  if (!isDebugMode()) return;
  (
    window as typeof window & { __PUDDING_DEBUG__?: PuddingDebugApi }
  ).__PUDDING_DEBUG__ = api;
  console.log('[Pudding Debug] Debug mode enabled');
}
