import { execFileSync, spawn } from 'node:child_process';
import { randomBytes, timingSafeEqual } from 'node:crypto';
import { mkdir, readFile, rename, unlink, writeFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { homedir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { connect, RpcError } from './app-server.mjs';
import { Sessions, HttpError, sessionId } from './sessions.mjs';
import { receiptStore } from './receipts.mjs';

const cli = process.argv.slice(2);
const verb = cli.shift();
let port = 47931;
while (cli.length) {
  const flag = cli.shift();
  if (flag === '--port') port = Number(cli.shift());
  else throw new Error('未知选项 ' + flag);
}
if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('端口范围为 1 到 65535');
const addr = '127.0.0.1:' + port;
const baseUrl = 'http://' + addr;
const directory = join(process.env.LOCALAPPDATA || join(homedir(), '.local', 'state'), 'CodexChatHttp');
const connectionFile = join(directory, 'connection-' + port + '.json');
const script = fileURLToPath(import.meta.url);

async function config() {
  return JSON.parse(await readFile(connectionFile, 'utf8'));
}

async function control(method, path) {
  const { token } = await config();
  const res = await fetch(baseUrl + path, { method, headers: { Authorization: 'Bearer ' + token },
    redirect: 'error', signal: AbortSignal.timeout(4000) });
  if (!res.ok) throw new Error('HTTP ' + res.status);
  return res.json();
}

async function bodyOf(req) {
  if (!['PUT','PATCH','POST'].includes(req.method)) return {};
  const chunks = [];
  let size = 0;
  for await (const chunk of req.iterator({ destroyOnReturn: false })) {
    size += chunk.length;
    if (size > 128 * 1024) throw new HttpError(413, '请求体过大');
    chunks.push(chunk);
  }
  if (!size) return {};
  if (req.headers['content-type']?.split(';')[0].trim().toLowerCase() !== 'application/json') {
    throw new HttpError(415, '请使用 application/json');
  }
  try {
    const obj = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(Buffer.concat(chunks)));
    if (!obj || Array.isArray(obj) || typeof obj !== 'object') throw new Error();
    return obj;
  } catch { throw new HttpError(400, '请求体必须是 UTF-8 JSON 对象'); }
}

function httpStatus(error) {
  if (error instanceof HttpError) return error.status;
  if (!(error instanceof RpcError)) return 500;
  if (error.code === 'offline') return 503;
  if (error.code === 'timeout') return 504;
  if (/not found|no rollout|thread not loaded|unknown thread/i.test(error.message)) return 404;
  if (/archived|already|in progress|active turn/i.test(error.message)) return 409;
  if (error.code === -32602) return 400;
  return 502;
}

async function serve() {
  const token = randomBytes(32).toString('hex');
  const expected = Buffer.from('Bearer ' + token);
  const current = process.env.CODEX_THREAD_ID ? sessionId(process.env.CODEX_THREAD_ID) : null;
  const executable = process.env.CODEX_APP_RUNTIME_CLI_PATH || (process.platform === 'win32' ? 'codex.exe' : 'codex');
  let sessions, rpc, reconnect, stopping = false;
  async function backend() {
    if (!rpc || rpc.closed) {
      reconnect ??= connect({ executable, onEvent(method, params) {
        if (method === 'turn/completed' && sessions?.monitored.has(params?.threadId)) {
          const id = params.threadId;
          void sessions.serial(id, () => sessions.pump(id)).catch(() => {});
        }
      }}).then(connection => {
        rpc = connection;
        if (sessions) sessions.connection = rpc;
        else sessions = new Sessions(rpc, current, resolve('.'), receiptStore(directory));
      }).finally(() => { reconnect = null; });
      await reconnect;
    }
    return sessions;
  }
  // Check the chosen transport before advertising an HTTP endpoint.
  await backend();
  const server = createServer({ requestTimeout: 15000, headersTimeout: 10000 }, async (req, res) => {
    res.setHeader('Content-Type', 'application/json; charset=utf-8');
    res.setHeader('Cache-Control', 'no-store');
    res.setHeader('X-Content-Type-Options', 'nosniff');
    try {
      if (req.headers.host !== addr || req.headers.origin || !req.url.startsWith('/') || req.url.startsWith('//')) {
        throw new HttpError(403, '仅接受本机客户端请求');
      }
      const provided = Buffer.from(req.headers.authorization || '');
      if (provided.length !== expected.length || !timingSafeEqual(provided, expected)) {
        res.setHeader('WWW-Authenticate', 'Bearer');
        throw new HttpError(401, '需要连接文件中的 Bearer 令牌');
      }
      const url = new URL(req.url, baseUrl);
      let status, data;
      if (req.method === 'GET' && url.pathname === '/control/status') {
        [status, data] = [200, { baseUrl, connectionFile, current: sessions.current, backendConnected: !rpc.closed }];
      } else if (req.method === 'POST' && url.pathname === '/control/stop') {
        [status, data] = [200, { stopping: true }];
        res.once('finish', () => { void stop(); });
      } else {
        const body = await bodyOf(req);
        [status, data] = await (await backend()).route(req.method, url, body);
      }
      res.writeHead(status);
      res.end(JSON.stringify(data));
    } catch (error) {
      const status = httpStatus(error);
      // Raw upstream errors can contain tool input, paths, or provider data.
      const msg = error instanceof HttpError ? error.message : ({
        400: 'Codex 拒绝了参数', 404: '未找到会话', 409: '会话状态冲突',
        502: 'Codex 未能完成请求', 503: 'Codex 连接已关闭', 504: 'Codex 响应超时，操作结果待确认',
      }[status] || '服务内部错误');
      res.writeHead(status, { Connection: 'close' });
      res.end(JSON.stringify({ error: msg }));
    }
  });
  async function stop() {
    if (stopping) return;
    stopping = true;
    try {
      if ((await config()).token === token) await unlink(connectionFile);
    } catch { /* already removed */ }
    server.close();
    server.closeAllConnections();
    rpc?.close();
  }
  try {
    await new Promise((ok, fail) => { server.once('error', fail); server.listen(port, '127.0.0.1', ok); });
    await mkdir(directory, { recursive: true, mode: 0o700 });
    if (process.platform === 'win32') {
      execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-File',
        fileURLToPath(new URL('./private-directory.ps1', import.meta.url)), directory],
        { windowsHide: true, stdio: 'pipe', timeout: 10000 });
    }
    const tmp = connectionFile + '.' + process.pid + '.tmp';
    await writeFile(tmp, JSON.stringify({ baseUrl, token }) + '\n', { flag: 'wx', mode: 0o600 });
    try { await rename(tmp, connectionFile); } finally { await unlink(tmp).catch(() => {}); }
    process.on('SIGINT', stop);
    process.on('SIGTERM', stop);
    process.send?.({ ready: true });
    if (process.connected) process.disconnect();
  } catch (error) {
    await stop();
    throw error;
  }
}

async function main() {
  if (verb === 'serve') return serve();
  if (verb === 'status') return console.log(JSON.stringify(await control('GET', '/control/status')));
  if (verb === 'stop') return console.log(JSON.stringify(await control('POST', '/control/stop')));
  if (verb !== 'start') throw new Error('用法：node bridge.mjs start|status|stop [--port 47931]');
  let existing;
  try { existing = await control('GET', '/control/status'); } catch { /* start a new listener */ }
  if (existing) return console.log(JSON.stringify(existing));
  const args = [script, 'serve', '--port', String(port)];
  const child = spawn(process.execPath, args, {
    detached: true, windowsHide: true, stdio: ['ignore','ignore','ignore','ipc'],
  });
  await new Promise((ok, fail) => {
    const timer = setTimeout(() => { child.kill(); fail(new Error('启动超时')); }, 40000);
    child.once('error', e => { clearTimeout(timer); fail(e); });
    child.once('exit', () => { clearTimeout(timer); fail(new Error('服务启动失败')); });
    child.once('message', msg => { clearTimeout(timer); msg.ready ? ok() : fail(new Error(msg.error)); });
  });
  child.unref();
  console.log(JSON.stringify(await control('GET', '/control/status')));
}

main().catch(error => {
  const msg = error.code === 'EADDRINUSE' ? '本机端口已被占用' : error.message;
  if (process.connected) process.send({ error: msg }, () => process.disconnect());
  console.error(msg);
  process.exitCode = 1;
});
