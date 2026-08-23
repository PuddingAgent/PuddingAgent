// ── duration 计量格式化（行为链升级 §3.3：完成态计量 chip / 工具行尾部耗时 / StatsLine 共用）──
// <1s → 「123ms」；<60s → 「1.2s」；≥60s → 「1m03s」。
// 输入缺失 / 非有限 / 非正数 → null（调用方不渲染计量，不伪造数值）。
export function formatDurationMs(
  ms?: number | null,
): string | null {
  if (typeof ms !== 'number' || !Number.isFinite(ms) || ms < 0) return null;
  if (ms < 1000) return `${Math.round(ms)}ms`;
  const seconds = ms / 1000;
  if (seconds < 60) {
    const formatted = seconds.toFixed(1).replace(/\.0$/, '');
    return `${formatted}s`;
  }
  const totalSeconds = Math.floor(seconds);
  const minutes = Math.floor(totalSeconds / 60);
  const rest = String(totalSeconds % 60).padStart(2, '0');
  return `${minutes}m${rest}s`;
}

/** Token 数缩写：≥1000 → 「4.2k tokens」，否则原数（StatsLine 用）。 */
export function formatTokenCount(tokens?: number | null): string | null {
  if (typeof tokens !== 'number' || !Number.isFinite(tokens) || tokens <= 0) {
    return null;
  }
  if (tokens >= 1000) {
    const k = tokens / 1000;
    const formatted = k >= 100 ? Math.round(k).toString() : k.toFixed(1).replace(/\.0$/, '');
    return `${formatted}k tokens`;
  }
  return `${tokens} tokens`;
}
