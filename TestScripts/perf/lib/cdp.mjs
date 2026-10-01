// 极简 CDP 客户端（Node >= 22 自带全局 WebSocket，无第三方依赖）。
import { setTimeout as delay } from 'node:timers/promises';

const HTTP_BASE = process.env.CDP_HTTP || 'http://127.0.0.1:9333';

export async function httpJson(path, method = 'GET') {
  const res = await fetch(`${HTTP_BASE}${path}`, { method });
  const text = await res.text();
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

export async function newPage(url = 'about:blank') {
  const target = await httpJson(`/json/new?${encodeURIComponent(url)}`, 'PUT');
  if (!target || !target.webSocketDebuggerUrl) {
    throw new Error(`/json/new failed: ${JSON.stringify(target)}`);
  }
  const client = await connect(target.webSocketDebuggerUrl);
  return client;
}

export async function connect(wsUrl) {
  const ws = new WebSocket(wsUrl);
  await new Promise((resolve, reject) => {
    ws.addEventListener('open', resolve, { once: true });
    ws.addEventListener('error', (e) => reject(new Error(`ws error: ${e.message || 'unknown'}`)), {
      once: true,
    });
  });

  let nextId = 1;
  const pending = new Map();
  const listeners = new Map();
  const consoleLines = [];
  const errors = [];

  ws.addEventListener('message', (event) => {
    const msg = JSON.parse(event.data);
    if (msg.id != null) {
      const entry = pending.get(msg.id);
      if (!entry) return;
      pending.delete(msg.id);
      if (msg.error) entry.reject(new Error(`${entry.method}: ${msg.error.message}`));
      else entry.resolve(msg.result);
      return;
    }
    if (msg.method === 'Runtime.consoleAPICalled') {
      const text = (msg.params.args || [])
        .map((a) => (a.value !== undefined ? a.value : a.description || a.type))
        .join(' ');
      consoleLines.push(`[${msg.params.type}] ${text}`);
      if (consoleLines.length > 400) consoleLines.shift();
    }
    if (msg.method === 'Runtime.exceptionThrown') {
      const d = msg.params.exceptionDetails;
      errors.push(d?.exception?.description || d?.text || 'unknown exception');
    }
    for (const fn of listeners.get(msg.method) || []) {
      try {
        fn(msg.params, msg.sessionId);
      } catch (err) {
        errors.push(`listener ${msg.method}: ${err.message}`);
      }
    }
  });

  const client = {
    ws,
    consoleLines,
    errors,
    send(method, params = {}, sessionId) {
      const id = nextId++;
      const payload = { id, method, params };
      if (sessionId) payload.sessionId = sessionId;
      return new Promise((resolve, reject) => {
        pending.set(id, { resolve, reject, method });
        ws.send(JSON.stringify(payload));
      });
    },
    on(method, fn) {
      if (!listeners.has(method)) listeners.set(method, []);
      listeners.get(method).push(fn);
      return () => {
        const arr = listeners.get(method) || [];
        const i = arr.indexOf(fn);
        if (i >= 0) arr.splice(i, 1);
      };
    },
    once(method, timeoutMs = 15000) {
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error(`timeout waiting ${method}`)), timeoutMs);
        const off = client.on(method, (params) => {
          clearTimeout(timer);
          off();
          resolve(params);
        });
      });
    },
    async evaluate(expression, { awaitPromise = true } = {}) {
      const result = await client.send('Runtime.evaluate', {
        expression,
        awaitPromise,
        returnByValue: true,
        userGesture: true,
      });
      if (result.exceptionDetails) {
        const d = result.exceptionDetails;
        throw new Error(`evaluate failed: ${d.exception?.description || d.text}`);
      }
      return result.result?.value;
    },
    async navigate(url, { waitUntil = 'load', timeoutMs = 30000 } = {}) {
      const loaded = client.once('Page.loadEventFired', timeoutMs).catch(() => null);
      await client.send('Page.navigate', { url });
      if (waitUntil === 'load') await loaded;
    },
    close() {
      try {
        ws.close();
      } catch {
        /* ignore */
      }
    },
  };

  await client.send('Page.enable');
  await client.send('Runtime.enable');
  await client.send('Log.enable').catch(() => {});
  return client;
}

/** 轮询直到条件成立（在页面里求值）。 */
export async function waitFor(client, expression, { timeoutMs = 20000, intervalMs = 200, label } = {}) {
  const deadline = Date.now() + timeoutMs;
  let last;
  while (Date.now() < deadline) {
    last = await client.evaluate(expression).catch((err) => `ERR:${err.message}`);
    if (last) return last;
    await delay(intervalMs);
  }
  throw new Error(`waitFor timeout${label ? ` (${label})` : ''}: last=${JSON.stringify(last)}`);
}

export { delay };
