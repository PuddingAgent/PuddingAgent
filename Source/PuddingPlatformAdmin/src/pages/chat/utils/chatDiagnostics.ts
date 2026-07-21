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
  if (type === 'error') return true;
  if (type !== 'done') return false;

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
    message,
    ...(lookupLines.length > 0 ? ['', '### 诊断信息', ...lookupLines] : []),
  ].join('\n');
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
