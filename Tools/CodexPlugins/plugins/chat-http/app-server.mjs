import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';

export class RpcError extends Error {
  constructor(code, message) {
    super(message);
    this.code = code;
  }
}

// Each instance is one app-server client. Never replay a mutation on disconnect.
export async function connect({ executable, onEvent = () => {} }) {
  const args = ['app-server', '--listen', 'stdio://'];
  const child = spawn(executable, args, { windowsHide: true, stdio: ['pipe', 'pipe', 'ignore'] });
  const lines = createInterface({ input: child.stdout });
  const pending = new Map();
  let nextId = 0;
  let closed = false;

  function fail() {
    if (closed) return;
    closed = true;
    for (const { reject, timer } of pending.values()) {
      clearTimeout(timer);
      reject(new RpcError('offline', 'Codex 连接已关闭，请先查询消息状态再重试。'));
    }
    pending.clear();
    child.kill();
  }
  child.on('error', fail);
  child.on('exit', fail);
  child.stdin.on('error', fail);
  lines.on('line', line => {
    let msg;
    try { msg = JSON.parse(line); } catch { fail(); return; }
    if (msg.method) {
      if (msg.id != null) {
        // No interactive approval UI on this transport. Never automatically approve tools.
        child.stdin.write(JSON.stringify({ id: msg.id, error: { code: -32601, message: 'Interactive request not supported by chat-http' } }) + '\n');
      } else {
        onEvent(msg.method, msg.params);
      }
      return;
    }
    const waiter = pending.get(msg.id);
    if (!waiter) return;
    pending.delete(msg.id);
    clearTimeout(waiter.timer);
    if (msg.error) waiter.reject(new RpcError(msg.error.code, msg.error.message));
    else waiter.resolve(msg.result);
  });

  function call(method, params) {
    if (closed) return Promise.reject(new RpcError('offline', 'Codex 连接已关闭。'));
    const id = ++nextId;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        pending.delete(id);
        reject(new RpcError('timeout', 'Codex 响应超时，请查询状态后再决定是否重试。'));
      }, 30000);
      pending.set(id, { resolve, reject, timer });
      child.stdin.write(JSON.stringify({ method, id, params }) + '\n');
    });
  }
  try {
    await call('initialize', {
      clientInfo: { name: 'codex_chat_http', title: 'Codex Chat HTTP', version: '0.1.2' },
      capabilities: { experimentalApi: true },
    });
    child.stdin.write('{"method":"initialized"}\n');
    return { call, close: fail, get closed() { return closed; } };
  } catch (error) {
    fail();
    throw error;
  }
}
