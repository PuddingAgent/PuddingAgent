export type PuddingDebugApi = {
  getSessionState(sessionId: string): any | null;
  getLastTraceId(): string | null;
  getLastSessionId(): string | null;
  getLastMessageId(): string | null;
  exportTimeline(): any | null;
  clearDebugEvents(): void;
};

/** 判断 debug mode 是否启用（通过 URL 参数 ?debug=1） */
export function isDebugMode(): boolean {
  const urlParams = new URLSearchParams(window.location.search);
  return urlParams.get('debug') === '1';
}

/** 写入 last session/message（仅 debug mode 下启用） */
export function writeDebugSessionState(sessionId: string, messageId: string): void {
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
  if (isDebugMode()) {
    (window as any).__PUDDING_DEBUG__ = api;
    console.log('[Pudding Debug] Debug mode enabled');
  }
}
