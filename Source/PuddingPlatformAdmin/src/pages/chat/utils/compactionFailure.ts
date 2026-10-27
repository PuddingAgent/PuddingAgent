/** The same allowlisted diagnostic envelope is used by HTTP and lifecycle replay. */
export function formatCompactionFailure(value: unknown): string | undefined {
  if (!value || typeof value !== 'object') return undefined;
  const envelope = value as Record<string, unknown>;
  const data = envelope.failure && typeof envelope.failure === 'object'
    ? envelope.failure as Record<string, unknown> : envelope;
  const message = [data.message, data.detail, data.error].find(v => typeof v === 'string' && v.trim());
  if (typeof message !== 'string') return undefined;
  const phases: Record<string, string> = {
    resolve_profile: '解析 Agent 配置', prepare_summary: '生成并保存摘要',
    session_handoff: '切换后续会话', publish_result: '发布整理结果',
  };
  const lines = [message];
  if (typeof data.phase === 'string') lines.push(`失败阶段：${phases[data.phase] ?? data.phase}`);
  if (data.summaryCommitted === true) lines.push('摘要状态：已保存');
  for (const [key, label] of [
    ['errorCode', '错误码'], ['nativeErrorCode', '系统错误码'], ['writeAttempts', '写入尝试次数'],
    ['errorId', 'errorId'], ['upstreamErrorId', '上游 errorId'], ['traceId', 'traceId'],
  ]) {
    const v = data[key];
    if (typeof v === 'string' || typeof v === 'number') lines.push(`${label}：${String(v).slice(0, 128)}`);
  }
  return lines.join('\n');
}

export function compactionHttpFailure(error: unknown): string | undefined {
  const value = error as { response?: { data?: unknown }; data?: unknown } | null;
  return formatCompactionFailure(value?.response?.data ?? value?.data);
}
