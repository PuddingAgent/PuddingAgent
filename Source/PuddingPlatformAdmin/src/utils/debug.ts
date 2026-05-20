export type PuddingDebugApi = {
  getSessionState(sessionId: string): any | null;
  getLastTraceId(): string | null;
  getLastSessionId(): string | null;
  exportTimeline(): any | null;
  clearDebugEvents(): void;
};

/** 判断 debug mode 是否启用（通过 URL 参数 ?debug=1） */
export function isDebugMode(): boolean {
  const urlParams = new URLSearchParams(window.location.search);
  return urlParams.get('debug') === '1';
}

/** 注册 debug API 到 window.__PUDDING_DEBUG__ */
export function registerDebugApi(api: PuddingDebugApi): void {
  if (isDebugMode()) {
    (window as any).__PUDDING_DEBUG__ = api;
    console.log('[Pudding Debug] Debug mode enabled');
  }
}
