export const PINNED_MESSAGE_STORAGE_KEY = 'pudding_pinned_message';
export const PINNED_MESSAGE_CHANGED_EVENT = 'pudding:pinned-message-changed';

export interface PinnedMessage {
  messageId?: number;
  turnId: string;
  /** 前三行摘要 */
  preview: string;
  /** 完整文本，用于悬浮预览和后续扩展。 */
  fullText: string;
  pinnedAt: number;
}

export function summarizePinnedMessage(text: string): string {
  const preview = text
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean)
    .slice(0, 3)
    .join('\n');

  return preview.length > 120 ? `${preview.substring(0, 120)}…` : preview;
}

function buildPinnedPreviewLines(preview: string): string[] {
  const lines = preview.split(/\r?\n/);
  if (lines.length === 0) return ['> 摘要：'];
  const [first, ...rest] = lines;
  return [`> 摘要：${first ?? ''}`, ...rest.map((line) => `> ${line}`)];
}

export function buildPinnedMessageQuote(pinned: PinnedMessage): string {
  return [
    pinned.messageId
      ? `> 消息ID：${pinned.messageId}`
      : `> 消息ID：请通过Query Session Log查询 turnId=${pinned.turnId}`,
    '> 请通过Query Session Log工具获取原始信息',
    ...buildPinnedPreviewLines(pinned.preview),
    '',
  ].join('\n');
}

export function loadPinnedMessage(): PinnedMessage | null {
  try {
    const raw = window.localStorage.getItem(PINNED_MESSAGE_STORAGE_KEY);
    return raw ? (JSON.parse(raw) as PinnedMessage) : null;
  } catch {
    return null;
  }
}

function notifyPinnedMessageChanged(): void {
  window.dispatchEvent(new Event(PINNED_MESSAGE_CHANGED_EVENT));
}

export function savePinnedMessage(msg: PinnedMessage): void {
  window.localStorage.setItem(PINNED_MESSAGE_STORAGE_KEY, JSON.stringify(msg));
  notifyPinnedMessageChanged();
}

export function clearPinnedMessage(): void {
  window.localStorage.removeItem(PINNED_MESSAGE_STORAGE_KEY);
  notifyPinnedMessageChanged();
}

export function subscribePinnedMessageChange(listener: () => void): () => void {
  const handleChange = () => listener();
  window.addEventListener('storage', handleChange);
  window.addEventListener(PINNED_MESSAGE_CHANGED_EVENT, handleChange);
  return () => {
    window.removeEventListener('storage', handleChange);
    window.removeEventListener(PINNED_MESSAGE_CHANGED_EVENT, handleChange);
  };
}
