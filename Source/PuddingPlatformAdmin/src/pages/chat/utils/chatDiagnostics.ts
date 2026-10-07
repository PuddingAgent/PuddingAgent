import {
  CHAT_DIAG_MAX_EVENTS,
  CHAT_DIAG_STORAGE_KEY,
  type ChatDiagPayload,
  type ChatDiagWindow,
} from '../types/chatStateTypes';

type ChatErrorDiagnosticEvent = Record<string, unknown> & {
  type?: unknown;
  message?: unknown;
  reply?: unknown;
};

type ChatErrorDiagnosticFallback = {
  sessionId?: string | null;
  turnId?: string | null;
};

function readDiagnosticText(
  event: ChatErrorDiagnosticEvent,
  ...keys: string[]
): string | undefined {
  for (const key of keys) {
    const value = event[key];
    if (typeof value === 'string' && value.trim()) return value.trim();
    if (typeof value === 'number' && Number.isFinite(value))
      return String(value);
  }
  return undefined;
}

function escapeDiagnosticCode(value: string): string {
  return value.replaceAll('`', '\\`');
}

export function looksLikePersistedErrorDiagnostic(markdown: unknown): boolean {
  if (typeof markdown !== 'string') return false;
  const normalized = markdown.trim().toLowerCase();
  if (!normalized) return false;

  const hasRequestFailureHeading =
    normalized.includes('## 请求失败') || normalized.includes('### 请求失败');
  const hasLookupField =
    normalized.includes('error id:') ||
    normalized.includes('error code:') ||
    normalized.includes('location:') ||
    normalized.includes('trace id:');
  const hasSessionFuseDiagnostic =
    normalized.includes('session fuse triggered') &&
    (normalized.includes('recovery:') || normalized.includes('/resume'));

  return (
    (hasRequestFailureHeading && hasLookupField) || hasSessionFuseDiagnostic
  );
}

export function isChatStreamErrorEvent(
  event: ChatErrorDiagnosticEvent,
): boolean {
  const type = readDiagnosticText(event, 'type')?.toLowerCase();
  // TR-01/CU-02：canonical 事件名（turn.failed/turn.completed）。
  if (type === 'turn.failed') return true;
  if (type !== 'turn.completed') return false;

  return (
    event.isError === true ||
    readDiagnosticText(event, 'status')?.toLowerCase() === 'error' ||
    Boolean(readDiagnosticText(event, 'errorId', 'error_id')) ||
    Boolean(readDiagnosticText(event, 'errorCode', 'error_code')) ||
    looksLikePersistedErrorDiagnostic(event.reply)
  );
}

export function formatChatErrorDiagnostic(
  event: ChatErrorDiagnosticEvent,
  fallback: ChatErrorDiagnosticFallback = {},
): string {
  const persistedReply = readDiagnosticText(event, 'reply');
  if (persistedReply && looksLikePersistedErrorDiagnostic(persistedReply))
    return persistedReply;

  const message =
    readDiagnosticText(event, 'message', 'error', 'reply') ?? '请求处理失败。';
  // 后端契约（可诊断基础设施设计 §12）：标题 + 大概原因 + 稳定因果码 + 阶段 + 处置建议。
  const causeTitle = readDiagnosticText(event, 'causeTitle', 'cause_title');
  const causeShortCause = readDiagnosticText(
    event,
    'causeShortCause',
    'cause_short_cause',
  );
  const causeCode = readDiagnosticText(event, 'causeCode', 'cause_code');
  const causePhase = readDiagnosticText(event, 'causePhase', 'cause_phase');
  const retryable = readDiagnosticText(event, 'retryable');
  const remediationHint = readDiagnosticText(
    event,
    'remediationHint',
    'remediation_hint',
  );
  const sessionId =
    readDiagnosticText(event, 'sessionId', 'session_id') ??
    fallback.sessionId ??
    undefined;
  const messageOrTurnId =
    readDiagnosticText(event, 'messageId', 'message_id', 'turnId', 'turn_id') ??
    fallback.turnId ??
    undefined;
  const round = readDiagnosticText(event, 'round');
  const maxRounds = readDiagnosticText(event, 'maxRounds', 'max_rounds');
  const diagnosticFields: Array<[label: string, value: string | undefined]> = [
    ['Session ID', sessionId ?? undefined],
    ['Message ID / Turn ID', messageOrTurnId ?? undefined],
    ['Trace ID', readDiagnosticText(event, 'traceId', 'trace_id')],
    ['Error ID', readDiagnosticText(event, 'errorId', 'error_id')],
    ['Location', readDiagnosticText(event, 'location')],
    ['Error Code', readDiagnosticText(event, 'errorCode', 'error_code')],
    ['因果码', causeCode],
    ['失败阶段', causePhase],
    ['可重试', retryable === undefined ? undefined : retryable === 'true' ? '是' : '否'],
    [
      'Round',
      round ? (maxRounds ? `${round}/${maxRounds}` : round) : undefined,
    ],
    ['Model', readDiagnosticText(event, 'modelId', 'model_id', 'model')],
    [
      'Endpoint Host',
      readDiagnosticText(event, 'endpointHost', 'endpoint_host'),
    ],
    [
      'Timestamp UTC',
      readDiagnosticText(event, 'timestampUtc', 'timestamp_utc', 'recordedAt'),
    ],
  ];
  const lookupLines = diagnosticFields
    .filter((field): field is [string, string] => Boolean(field[1]))
    .map(([label, value]) => `- ${label}: \`${escapeDiagnosticCode(value)}\``);

  return [
    '## 请求失败',
    '',
    // 第一眼就要能认出「哪一类错误」，而不是一行英文异常。
    ...(causeTitle ? [`**${causeTitle}**`, ''] : []),
    ...(causeShortCause ? [causeShortCause, ''] : []),
    message,
    ...(lookupLines.length > 0 ? ['', '### 诊断信息', ...lookupLines] : []),
    ...(remediationHint ? ['', `- 处置建议: ${remediationHint}`] : []),
  ].join('\n');
}

/**
 * 「复制诊断信息」的载荷（可诊断基础设施设计 §12）。
 *
 * 优先使用后端给出的完整现场（`reportText` / `reportJson`，含时间、errorId、traceId、因果码、
 * 阶段、证据与日志定位提示）；后端未提供时，用本地可见字段拼出同样可定位的文本，
 * 保证用户点「复制」拿到的永远是**能贴给维护者的完整信息**，而不是一行英文异常。
 */
export function buildChatDiagnosticCopyPayload(input: {
  event?: ChatErrorDiagnosticEvent | null;
  sessionId?: string | null;
  turnId?: string | null;
  agentId?: string | null;
  errorMessage?: string | null;
  userAgent?: string | null;
  url?: string | null;
  recentPerfEvents?: unknown[];
}): { text: string; json: string } {
  const event = input.event ?? {};
  const reply = readDiagnosticText(event, 'reply');
  const backendReport =
    readDiagnosticText(event, 'reportText', 'report_text') ??
    (reply && looksLikePersistedErrorDiagnostic(reply) ? reply : undefined);
  const backendJson = readDiagnosticText(event, 'reportJson', 'report_json');
  const evidence = readDiagnosticText(
    event,
    'errorEvidence',
    'error_evidence',
    'evidenceJson',
    'evidence_json',
  );

  const structured = {
    exportedAt: new Date().toISOString(),
    reportVersion:
      readDiagnosticText(event, 'errorReportVersion', 'reportVersion') ?? null,
    sessionId: input.sessionId ?? readDiagnosticText(event, 'sessionId') ?? null,
    turnId: input.turnId ?? readDiagnosticText(event, 'turnId') ?? null,
    agentId: input.agentId ?? null,
    causeCode:
      readDiagnosticText(event, 'causeCode', 'cause_code') ?? null,
    causeTitle:
      readDiagnosticText(event, 'causeTitle', 'cause_title') ?? null,
    causeShortCause:
      readDiagnosticText(event, 'causeShortCause', 'cause_short_cause') ?? null,
    causePhase:
      readDiagnosticText(event, 'causePhase', 'cause_phase') ?? null,
    remediationHint:
      readDiagnosticText(event, 'remediationHint', 'remediation_hint') ?? null,
    errorId: readDiagnosticText(event, 'errorId', 'error_id') ?? null,
    traceId: readDiagnosticText(event, 'traceId', 'trace_id') ?? null,
    errorCode: readDiagnosticText(event, 'errorCode', 'error_code') ?? null,
    timestampUtc:
      readDiagnosticText(event, 'timestampUtc', 'timestamp_utc') ?? null,
    errorMessage:
      input.errorMessage ??
      readDiagnosticText(event, 'errorMessage', 'message') ??
      null,
    evidence: evidence ?? null,
    userAgent: input.userAgent ?? null,
    url: input.url ?? null,
    recentPerfEvents: input.recentPerfEvents ?? [],
  };

  const text =
    backendReport ??
    [
      '== Pudding 错误报告（前端导出） ==',
      `导出时间(本地): ${structured.exportedAt}`,
      `标题: ${structured.causeTitle ?? '(未提供)'}`,
      `大概原因: ${structured.causeShortCause ?? '(未提供)'}`,
      `因果码: ${structured.causeCode ?? '(未提供)'}`,
      `失败阶段: ${structured.causePhase ?? '(未提供)'}`,
      `errorId: ${structured.errorId ?? '(未提供)'}`,
      `traceId: ${structured.traceId ?? '(未提供)'}`,
      `会话 / 回合: ${structured.sessionId ?? '(未提供)'} / ${structured.turnId ?? '(未提供)'}`,
      `Error Code: ${structured.errorCode ?? '(未提供)'}`,
      `时间(UTC): ${structured.timestampUtc ?? '(未提供)'}`,
      `原始消息: ${structured.errorMessage ?? '(未提供)'}`,
      ...(structured.remediationHint ? [`处置建议: ${structured.remediationHint}`] : []),
      ...(structured.evidence ? ['', '-- 证据 --', structured.evidence] : []),
    ].join('\n');

  return {
    text,
    json: backendJson ?? JSON.stringify(structured, null, 2),
  };
}

export function toChatDiagValue(value: unknown, depth = 0): unknown {
  if (value == null) return value;
  if (typeof value === 'string')
    return value.length > 300 ? `${value.slice(0, 300)}...` : value;
  if (typeof value === 'number' || typeof value === 'boolean') return value;
  if (value instanceof Error)
    return { name: value.name, message: value.message };
  if (Array.isArray(value))
    return depth >= 2
      ? `[array:${value.length}]`
      : value.slice(0, 12).map((item) => toChatDiagValue(item, depth + 1));
  if (typeof value === 'object') {
    if (depth >= 2) return '[object]';
    return Object.fromEntries(
      Object.entries(value as Record<string, unknown>)
        .slice(0, 24)
        .map(([key, item]) => [key, toChatDiagValue(item, depth + 1)]),
    );
  }
  return String(value);
}

export function logChatDiag(label: string, payload: ChatDiagPayload = {}) {
  const entry = {
    at: new Date().toISOString(),
    label,
    payload: toChatDiagValue(payload),
  };
  const line = `[Pudding ChatDiag] ${JSON.stringify(entry)}`;
  console.warn(line);
  if (typeof window === 'undefined') return;
  try {
    const diagnosticWindow = window as ChatDiagWindow;
    const current = Array.isArray(diagnosticWindow.__PUDDING_CHAT_DIAG__)
      ? diagnosticWindow.__PUDDING_CHAT_DIAG__
      : [];
    const next = [...current, entry].slice(-CHAT_DIAG_MAX_EVENTS);
    diagnosticWindow.__PUDDING_CHAT_DIAG__ = next;
    window.sessionStorage.setItem(CHAT_DIAG_STORAGE_KEY, JSON.stringify(next));
  } catch {
    // Diagnostics must never affect chat behavior.
  }
}
