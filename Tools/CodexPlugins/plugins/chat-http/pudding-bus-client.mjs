import { createHash } from 'node:crypto';
import { HttpError, sessionId } from './sessions.mjs';

const api = '/api/external/v1/agent-adapters';
const terminal = new Set(['completed', 'failed', 'interrupted', 'cancelled']);

function endpoint(value, loopbackOnly = false) {
  const url = new URL(value);
  const local = ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname);
  if (url.username || url.password || url.pathname !== '/' || url.search || url.hash ||
      !(url.protocol === 'https:' || (url.protocol === 'http:' && local)) || (loopbackOnly && !local)) {
    throw new Error('服务地址必须是 HTTPS 或本机 HTTP，不能包含凭据、路径和查询参数。');
  }
  return url.origin;
}

async function request(origin, token, path, method = 'GET', body, signal) {
  const timeout = AbortSignal.timeout(20000);
  const response = await fetch(origin + path, {
    method,
    headers: { Authorization: 'Bearer ' + token, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
    redirect: 'error',
    signal: signal ? AbortSignal.any([signal, timeout]) : timeout,
  });
  if (response.status === 204) return null;
  const data = await response.json().catch(() => null);
  if (!response.ok) {
    const error = new HttpError(response.status, 'HTTP ' + response.status);
    error.code = data?.code;
    throw error;
  }
  if (!data) throw new Error('服务未返回 JSON 响应。');
  return data;
}

export function busClient(config, signal) {
  const origin = endpoint(config.baseUrl);
  if (!/^[\w-]{22}$/.test(config.ak) || !/^[\w-]{43}$/.test(config.sk)) {
    throw new Error('AK/SK 格式与 PuddingAgent 签发的凭据不符。');
  }
  const call = (path, method, body) =>
    request(origin, 'pdt_v1_' + config.ak + '.' + config.sk, api + path, method, body, signal);
  return {
    register: () => call('/register', 'POST', {}),
    renew: leaseId => call('/renew', 'POST', { leaseId }),
    disconnect: leaseId => call('/disconnect', 'POST', { leaseId }),
    inbox: (leaseId, after) => call('/inbox?' + new URLSearchParams({ leaseId, after, limit: 20 })),
    directory: (leaseId, after) =>
      call('/agents?' + new URLSearchParams(after ? { leaseId, after } : { leaseId })),
    send: (leaseId, operationId, targetAgentId, text) =>
      call('/messages', 'POST', { leaseId, operationId, targetAgentId, text }),
    delegate: (leaseId, operationId, targetAgentId, text) =>
      call('/delegations', 'POST', { leaseId, operationId, targetAgentId, text }),
    receipt: (leaseId, messageId) =>
      call('/delegations/' + encodeURIComponent(messageId) + '?' + new URLSearchParams({ leaseId })),
    settle: (leaseId, messageId, result) =>
      call('/delegations/' + encodeURIComponent(messageId) + '/result', 'PUT', { leaseId, ...result }),
  };
}

export function chatClient(connection, configuredSessionId, signal) {
  const origin = endpoint(connection.baseUrl, true);
  const session = '/v1/sessions/' + sessionId(configuredSessionId);
  const call = (path, method, body) => request(origin, connection.token, session + path, method, body, signal);
  return {
    check: () => call(''),
    send: (messageId, text) => call('/messages', 'POST', { messageId, text, delivery: 'resume' }),
    history: cursor => call('/messages?' + new URLSearchParams(cursor ? { limit: 50, cursor } : { limit: 50 })),
  };
}

function localMessageId(messageId) {
  const hex = createHash('sha256').update('pudding-chat-http\0' + messageId).digest('hex');
  return [hex.slice(0, 8), hex.slice(8, 12), '8' + hex.slice(13, 16), '8' + hex.slice(17, 20),
    hex.slice(20, 32)].join('-');
}

async function readTurn(chat, id, text, knownTurnId) {
  let cursor;
  do {
    const page = await chat.history(cursor);
    // Some Codex rollouts give the stored user item a new ID when a queued request starts.
    const turnId = knownTurnId ?? page.messages.find(message => message.role === 'user' &&
      (message.id === id || message.text === text))?.turnId;
    const turn = page.turns.find(candidate => candidate.id === turnId);
    if (turn) {
      if (!terminal.has(turn.status)) return { turnId };
      const assistant = page.messages.filter(message => message.role === 'assistant' && message.turnId === turnId);
      const final = assistant.findLast(message => message.phase === 'finalAnswer' || message.phase === 'final_answer') ??
        assistant.findLast(message => message.phase == null);
      const succeeded = turn.status === 'completed' && !!final?.text?.trim();
      return {
        turnId,
        state: succeeded ? 'completed' : turn.status === 'failed' || turn.status === 'completed' ? 'failed' : 'cancelled',
        text: succeeded ? final.text.slice(0, 16000) : null,
        reason: succeeded ? null : turn.status === 'completed' ? 'codex.empty_reply' : 'codex.' + turn.status,
      };
    }
    if (page.nextCursor && page.nextCursor === cursor) throw new Error('Codex 历史游标没有推进。');
    cursor = page.nextCursor;
  } while (cursor);
  return null;
}

export async function deliverToChat(chat, item) {
  const id = localMessageId(item.messageId);
  const task = item.kind === 'delegation';
  const heading = task
    ? '[PuddingAgent 委派任务] 请处理下面的任务，并在本轮最终答复中给出结果。'
    : '[PuddingAgent 通知] 下面是通过总线收到的消息。';
  const text = heading + '\n发件 Agent ' + item.fromAgentId + '\n接入消息 ID ' + id + '\n\n' + item.text;
  let receipt;
  try {
    receipt = await chat.send(id, text);
    if (!task) return { accepted: true };
  } catch (error) {
    if (error.status !== 409) throw error;
    // A previous request may have reached Codex before Chat HTTP persisted the receipt.
    // Use only the matching user message and turn as evidence.
  }
  const turn = await readTurn(chat, id, text, receipt?.turnId);
  if (!turn) return null;
  return task ? turn.state ? { accepted: true, result: turn } : null : { accepted: true };
}
