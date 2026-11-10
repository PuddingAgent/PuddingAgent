import { randomUUID } from 'node:crypto';
import { setTimeout as delay } from 'node:timers/promises';

export class HttpError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}

function nonempty(value, key, max) {
  if (typeof value !== 'string' || !value.trim() || value.length > max) {
    throw new HttpError(400, key + ' 必须是非空字符串，长度上限为 ' + max);
  }
  return value;
}

export function sessionId(value) {
  if (typeof value !== 'string' || !/^[\da-f]{8}(?:-[\da-f]{4}){3}-[\da-f]{12}$/i.test(value)) {
    throw new HttpError(400, '无效的会话 ID');
  }
  return value;
}

function summary(t) {
  return { id: t.id, title: t.name, preview: t.preview, createdAt: t.createdAt, updatedAt: t.updatedAt, runtimeStatus: t.status?.type };
}

function page(query) {
  const raw = query.get('limit') ?? '20';
  if (!/^(?:[1-9]|[1-4]\d|50)$/.test(raw)) throw new HttpError(400, 'limit 范围为 1 到 50');
  return { limit: Number(raw), cursor: query.get('cursor') };
}

function textOf(content) {
  return content.map(part => part.type === 'text' ? part.text : '[' + part.type + ']').join('\n');
}

// A new legacy rollout can briefly exist before Codex flushes its metadata.
async function readAfterFlush(rpc, method, params) {
  for (let attempt = 0; ; attempt++) {
    try { return await rpc.call(method, params); }
    catch (error) {
      if (attempt >= 8 || !/rollout[^\n]* is empty/i.test(error.message)) throw error;
      await delay(50 * (attempt + 1));
    }
  }
}

export class Sessions {
  constructor(connection, current, cwd, once) {
    this.connection = connection;
    this.current = current;
    this.cwd = cwd;
    this.once = once;
    this.locks = new Map();
    this.monitored = new Set();
  }

  async serial(id, work) {
    const previous = this.locks.get(id) ?? Promise.resolve();
    const pending = previous.catch(() => {}).then(work);
    this.locks.set(id, pending);
    try { return await pending; }
    finally { if (this.locks.get(id) === pending) this.locks.delete(id); }
  }

  async read(id) {
    return (await readAfterFlush(this.connection, 'thread/read', { threadId: id })).thread;
  }

  async pump(id) {
    const t = await this.read(id);
    if (t.status?.type !== 'idle') return null;
    const rpc = this.connection;
    const q = await rpc.call('thread/queue/list', { threadId: id, limit: 1 });
    if (!q.data.length) return null;
    const queued = q.data[0];
    const started = await rpc.call('thread/queue/start', { threadId: id, queuedSubmissionId: queued.id });
    return { messageId: queued.clientUserMessageId, turnId: started.turn.id };
  }

  async send(id, body) {
    const text = nonempty(body.text, 'text', 32000);
    const delivery = body.delivery ?? 'queue';
    if (!['queue', 'resume'].includes(delivery)) throw new HttpError(400, 'delivery 只接受 queue 或 resume');
    const messageId = body.messageId === undefined ? randomUUID() : sessionId(body.messageId);
    const dispatch = async () => {
      // Persist first. Once the queue ACK arrives, a wakeup failure must not turn into a failed POST.
      const { queuedSubmission } = await this.connection.call('thread/queue/add', {
        threadId: id, clientUserMessageId: messageId, input: [{ type: 'text', text }],
      });
      const receipt = { sessionId: id, messageId, queueId: queuedSubmission.id, status: 'queued' };
      this.monitored.add(id);
      try {
        if (delivery === 'resume') await this.resumeIfUnloaded(id);
        const start = await this.pump(id);
        if (start?.messageId === messageId) return { ...receipt, status: 'started', turnId: start.turnId };
      } catch (error) {
        receipt.wakeup = error instanceof HttpError && error.status === 409 ? 'external_turn_unfinished' : 'unconfirmed';
      }
      return receipt;
    };
    return this.serial(id, () => body.messageId === undefined
      ? dispatch()
      : this.once(id, messageId, [text, delivery], dispatch));
  }

  async resumeIfUnloaded(id) {
    if ((await this.read(id)).status?.type === 'notLoaded') {
      // A different app-server can project a live legacy turn as "interrupted"
      // without a completion timestamp. It is unsafe to resume a second writer.
      const history = await readAfterFlush(this.connection, 'thread/turns/list', {
        threadId: id, itemsView: 'notLoaded', limit: 1, sortDirection: 'desc',
      });
      const last = history.data[0];
      if (last && (last.status === 'inProgress' || (last.status === 'interrupted' && last.completedAt == null))) {
        throw new HttpError(409, '原客户端上一轮尚未确认结束，请从原会话处理队列。');
      }
      await this.connection.call('thread/resume', { threadId: id, excludeTurns: true });
    }
  }

  async route(method, url, body) {
    const rpc = this.connection;
    const path = url.pathname;
    if (path === '/v1/sessions' && method === 'GET') {
      const archived = url.searchParams.get('archived') ?? 'false';
      if (!['false','true'].includes(archived)) throw new HttpError(400, 'archived 只接受 true 或 false');
      const r = await rpc.call('thread/list', { ...page(url.searchParams), archived: archived === 'true', modelProviders: [], sourceKinds: ['cli','vscode','appServer','exec'], sortKey: 'updated_at' });
      return [200, { sessions: r.data.map(summary), nextCursor: r.nextCursor }];
    }
    if (path === '/v1/sessions' && method === 'POST') {
      const text = nonempty(body.text, 'text', 32000);
      const { thread } = await rpc.call('thread/start', { cwd: this.cwd });
      this.monitored.add(thread.id);
      try {
        const messageId = randomUUID();
        const { turn } = await rpc.call('turn/start', {
          threadId: thread.id, clientUserMessageId: messageId, input: [{ type: 'text', text }],
        });
        return [201, { sessionId: thread.id, messageId, turnId: turn.id, status: 'started' }];
      } catch {
        return [502, { sessionId: thread.id, status: 'unconfirmed', error: '会话已创建，首条消息的启动结果未确认，请按 ID 查询。' }];
      }
    }
    if (path === '/v1/current' && method === 'PUT') {
      const id = sessionId(body.sessionId);
      const t = await this.read(id);
      this.current = id;
      return [200, { session: summary(t) }];
    }
    const match = /^\/v1\/sessions\/([^/]+)(?:\/(messages|queue|archive|unarchive|resume))?$/.exec(path);
    const shortcut = path === '/v1/current' || path === '/v1/messages';
    if (!match && !shortcut) throw new HttpError(404, '接口不存在');
    if (shortcut && !this.current) throw new HttpError(409, '请先通过 PUT /v1/current 绑定会话');
    const id = shortcut ? this.current : sessionId(match[1]);
    const action = match?.[2] ?? (path === '/v1/messages' ? 'messages' : '');
    if (!action && method === 'GET') return [200, { session: summary(await this.read(id)) }];
    if (!action && method === 'PATCH') {
      await rpc.call('thread/name/set', { threadId: id, name: nonempty(body.title, 'title', 200) });
      return [200, { sessionId: id, title: body.title }];
    }
    if (action === 'messages' && method === 'GET') {
      const r = await readAfterFlush(rpc, 'thread/turns/list', { threadId: id, ...page(url.searchParams), sortDirection: 'desc', itemsView: 'full' });
      const messages = [];
      for (const turn of r.data.reverse()) {
        for (const item of turn.items) {
          if (item.type !== 'userMessage' && item.type !== 'agentMessage') continue;
          const user = item.type === 'userMessage';
          messages.push({ id: item.id, turnId: turn.id, role: user ? 'user' : 'assistant',
            text: user ? textOf(item.content) : item.text, phase: item.phase ?? undefined });
        }
      }
      return [200, { sessionId: id, messages, turns: r.data.map(t => ({ id: t.id, status: t.status })), nextCursor: r.nextCursor }];
    }
    if (action === 'messages' && method === 'POST') return [202, await this.send(id, body)];
    if (action === 'queue' && method === 'GET') {
      const r = await rpc.call('thread/queue/list', { threadId: id, ...page(url.searchParams) });
      return [200, { sessionId: id, messages: r.data.map(q => ({
        id: q.clientUserMessageId, queueId: q.id, text: textOf(q.input), status: 'queued',
      })), nextCursor: r.nextCursor }];
    }
    if (action === 'resume' && method === 'POST') {
      return this.serial(id, async () => {
        await this.resumeIfUnloaded(id);
        this.monitored.add(id);
        const started = await this.pump(id);
        return [200, { sessionId: id, runtimeStatus: (await this.read(id)).status.type, started }];
      });
    }
    if ((action === 'archive' || action === 'unarchive') && method === 'POST') {
      return this.serial(id, async () => {
        await rpc.call('thread/' + action, { threadId: id });
        if (action === 'archive') {
          this.monitored.delete(id);
          if (this.current === id) this.current = null;
        }
        return [200, { sessionId: id, archived: action === 'archive' }];
      });
    }
    throw new HttpError(405, '该接口不支持此 HTTP 方法');
  }
}
