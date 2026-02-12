/**
 * Pudding Agent 入站消息调试工具。
 * 在浏览器控制台执行 `window.__puddingDebugInbound = false` 关闭，
 * 执行 `window.__puddingDebugInbound = true` 重新开启。
 * 默认开启。
 */
const KEY = '__puddingDebugInbound';

export const inboundDebug = {
  get enabled(): boolean {
    const val = (window as Record<string, unknown>)[KEY];
    return val !== false; // 默认 true，只有显式设为 false 才关闭
  },
  /** 仅在开启时打日志，格式: [InboundDebug] module message */
  log(module: string, ...args: unknown[]): void {
    if (this.enabled) {
      console.log(`[InboundDebug] ${module}`, ...args);
    }
  },
  warn(module: string, ...args: unknown[]): void {
    if (this.enabled) {
      console.warn(`[InboundDebug] ${module}`, ...args);
    }
  },
};
