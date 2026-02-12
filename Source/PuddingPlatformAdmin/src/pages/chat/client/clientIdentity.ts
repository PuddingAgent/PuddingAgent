export const DEFAULT_AGENT_CHAT_OWNER_ID = 'single-user';

const CLIENT_ID_KEY = 'pudding.agentChat.clientId';

export function getBrowserClientId(): string {
  if (typeof localStorage === 'undefined') return 'test-client';

  const existing = localStorage.getItem(CLIENT_ID_KEY);
  if (existing) return existing;

  const next = `client-${createId()}`;
  localStorage.setItem(CLIENT_ID_KEY, next);
  return next;
}

function createId(): string {
  if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
    return crypto.randomUUID();
  }

  return Math.random().toString(36).slice(2);
}
