// node --test TestScripts/test-codex-chat-http.mjs (requires the desktop Codex CLI)
import assert from 'node:assert/strict';
import { execFile, fork, spawn } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { dirname, join, resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import test from 'node:test';
import { listenLoopback as listen } from './lib/listen-loopback.mjs';

const entry = fileURLToPath(new URL('../Tools/CodexPlugins/plugins/chat-http/bridge.mjs', import.meta.url));
const busEntry = fileURLToPath(new URL('../Tools/CodexPlugins/plugins/chat-http/pudding-bus.mjs', import.meta.url));
const coreDll = process.env.PUDDING_BUS_TEST_CORE && resolve(process.env.PUDDING_BUS_TEST_CORE);
const run = promisify(execFile);

async function until(check) {
  for (let i = 0; i < 100; i++) {
    const value = await check();
    if (value) return value;
    await delay(150);
  }
  assert.fail('Timed out waiting for Codex');
}

async function busIntegration(t, root, env, chat, chatPort) {
  const dataRoot = join(root, 'pudding');
  await mkdir(join(dataRoot, 'config'), { recursive: true });
  await writeFile(join(dataRoot, 'config', 'system.json'), JSON.stringify({
    externalTaskApi: { enabled: true, requireHttps: true },
  }));
  const allocator = createServer();
  const port = await listen(allocator);
  await new Promise(ok => allocator.close(ok));
  const baseUrl = 'http://127.0.0.1:' + port;
  const core = spawn('dotnet', [coreDll, '--data-root=' + dataRoot, '--urls=' + baseUrl], {
    cwd: dirname(coreDll), windowsHide: true,
    env: { ...process.env, ASPNETCORE_URLS: baseUrl, Pudding__P2P__Port: String(port), PUDDING_DATA_ROOT: dataRoot },
    stdio: ['ignore', 'ignore', 'ignore'],
  });
  t.after(() => { core.kill(); });
  await until(async () => {
    if (core.exitCode !== null) assert.fail('Isolated Pudding Core exited ' + core.exitCode);
    return fetch(baseUrl + '/health/ready').then(response => response.ok, () => false);
  });

  async function api(path, method, body, status, token) {
    const headers = { 'Content-Type': 'application/json' };
    if (token) headers.Authorization = 'Bearer ' + token;
    const response = await fetch(baseUrl + path, { method, headers, body: body && JSON.stringify(body) });
    assert.equal(response.status, status, path);
    return response.json().catch(() => null);
  }
  const { token } = await api('/api/bootstrap/admin', 'POST', {
    userId: 'bus-test-admin',
    email: 'bus-test@example.invalid',
    password: 'FixtureA9-' + randomUUID(),
  }, 200);
  const doc = await api('/agent-adapters/docs/agent-adapters-v1.json', 'GET', null, 200);
  assert.ok(doc.paths['/api/external/v1/agent-adapters/delegations/{messageId}/result'].put);
  const adminVersion = process.env.PUDDING_BUS_TEST_ADMIN_VERSION;
  if (adminVersion) {
    const page = await fetch(baseUrl + '/admin/system-config/agent-adapters');
    assert.equal(page.status, 200);
    const scripts = [...(await page.text()).matchAll(/<script[^>]+src="([^"]+\.js)"/g)];
    const bundles = await Promise.all(scripts.map(async ([, url]) => {
      const asset = await fetch(new URL(url, baseUrl + '/admin/'));
      assert.equal(asset.status, 200);
      return asset.text();
    }));
    assert.ok(bundles.some(bundle => bundle.includes(adminVersion)));
  }

  const refs = await Promise.all(['Peer A', 'Peer B'].map(async name => {
    const { agentId, ak, sk } = await api('/api/admin/agent-adapters', 'POST', { workspaceId: 'default', name }, 201, token);
    const { sessionId } = await chat('/v1/sessions', 'POST', { text: '隔离测试会话 ' + name }, 201);
    const file = join(root, agentId + '.json');
    await writeFile(file, JSON.stringify({ workspaceId: 'default', agentId, baseUrl, ak, sk, sessionId, chatPort }));
    const child = spawn(process.execPath, [busEntry, 'run', file], { cwd: root, env, windowsHide: true, stdio: 'ignore' });
    t.after(() => { child.kill(); });
    return { agentId, file, sessionId, child };
  }));
  async function command(ref, verb, ...args) {
    const { stdout } = await run(process.execPath, [busEntry, verb, ref.file, ...args], { cwd: root, env, windowsHide: true });
    return JSON.parse(stdout);
  }
  const [sender, recipient] = refs;
  await until(async () => {
    for (const ref of refs) {
      if (ref.child.exitCode !== null) assert.fail('Bus subscriber exited ' + ref.child.exitCode);
    }
    const status = await command(sender, 'status');
    if (!status.expiresAtUtc) return false;
    return (await command(sender, 'agents')).filter(agent => agent.online).length === 2;
  });

  const first = '总线文本 ' + randomUUID();
  const second = '总线反向文本 ' + randomUUID();
  const work = '委派标记 ' + randomUUID();
  const textFile = join(root, 'bus-input.txt');
  async function send(from, to, verb, text) {
    await writeFile(textFile, text);
    const operationId = randomUUID();
    const firstReply = await command(from, verb, to.agentId, operationId, textFile);
    const replay = await command(from, verb, to.agentId, operationId, textFile);
    assert.deepEqual(replay, firstReply);
    return firstReply;
  }
  const history = ref => chat('/v1/sessions/' + ref.sessionId + '/messages');
  await send(sender, recipient, 'send', first);
  await until(async () => (await history(recipient)).messages.some(message => message.text.endsWith(first)));
  await send(recipient, sender, 'send', second);
  await until(async () => (await history(sender)).messages.some(message => message.text.endsWith(second)));

  const receipt = await send(sender, recipient, 'delegate', work);
  const result = await until(async () => {
    const value = await command(sender, 'result', receipt.messageId);
    return value.state !== 'queued' && value;
  });
  assert.equal(result.state, 'completed');
  assert.equal(result.result.text, '本机测试回复');
  const inputs = (await history(recipient)).messages.filter(message => message.role === 'user' && message.text.endsWith(work));
  assert.equal(inputs.length, 1);
  assert.equal(inputs[0].turnId, result.result.turnId);
  assert.equal((await history(recipient)).messages.filter(message => message.role === 'user' && message.text.endsWith(first)).length, 1);

  async function credentials(ref) {
    const { ak, sk } = JSON.parse(await readFile(ref.file, 'utf8'));
    const key = createHash('sha256').update(JSON.stringify([baseUrl, 'default', ref.agentId])).digest('hex');
    const { leaseId } = JSON.parse(await readFile(join(root, 'CodexChatHttp', 'pudding-bus-' + key + '.json'), 'utf8'));
    return { token: 'pdt_v1_' + ak + '.' + sk, leaseId };
  }
  const senderIdentity = await credentials(sender);
  const recipientIdentity = await credentials(recipient);
  const delegation = '/api/external/v1/agent-adapters/delegations/' + receipt.messageId;
  const fields = { state: result.state, text: result.result.text, turnId: result.result.turnId };
  await api(delegation + '?leaseId=' + recipientIdentity.leaseId, 'GET', null, 404, recipientIdentity.token);
  await api(delegation + '/result', 'PUT', { ...fields, leaseId: senderIdentity.leaseId }, 404, senderIdentity.token);
  const replay = await api(delegation + '/result', 'PUT', { ...fields, leaseId: recipientIdentity.leaseId }, 200, recipientIdentity.token);
  assert.deepEqual(replay, result.result);
  await api(delegation + '/result', 'PUT', { ...fields, text: 'conflicting', leaseId: recipientIdentity.leaseId }, 409, recipientIdentity.token);
}

test('local HTTP with isolated Codex state and a loopback model fixture', { timeout: 90000 }, async t => {
  const parent = resolve('.work/chat-http');
  await mkdir(parent, { recursive: true });
  const root = await mkdtemp(join(parent, 'integration-'));
  const fixture = createServer(async (req, res) => {
    req.resume();
    if (!req.url.endsWith('/responses')) { res.writeHead(404).end(); return; }
    await delay(500); // leave enough time to enqueue another message during a turn
    const id = randomUUID();
    const item = { id: 'm_' + id, type: 'message', role: 'assistant', status: 'completed',
      content: [{ type: 'output_text', text: '本机测试回复', annotations: [] }] };
    res.writeHead(200, { 'Content-Type': 'text/event-stream' });
    for (const e of [
      { type: 'response.created', response: { id } },
      { type: 'response.output_item.added', item: { ...item, status: 'in_progress', content: [] }, output_index: 0 },
      { type: 'response.output_text.delta', delta: '本机测试回复', item_id: item.id, output_index: 0, content_index: 0 },
      { type: 'response.output_item.done', item, output_index: 0 },
      { type: 'response.completed', response: { id, status: 'completed', output: [item] } },
    ]) res.write('data: ' + JSON.stringify(e) + '\n\n');
    res.end();
  });
  const modelPort = await listen(fixture);
  t.after(() => fixture.close());
  const home = join(root, 'codex');
  await mkdir(home);
  await writeFile(join(home, 'config.toml'), [
    'model_provider="fixture"', 'model="gpt-5.4"', '[model_providers.fixture]', 'name="Fixture"',
    'wire_api="responses"', 'base_url="http://127.0.0.1:' + modelPort + '/v1"',
  ].join('\n'));
  const env = { ...process.env, CODEX_HOME: home, LOCALAPPDATA: root, CODEX_THREAD_ID: '' };

  async function start() {
    const allocator = createServer();
    const port = await listen(allocator);
    await new Promise(ok => allocator.close(ok));
    const child = fork(entry, ['serve', '--port', String(port)], {
      cwd: root, windowsHide: true, env, stdio: ['ignore','ignore','inherit','ipc'],
    });
    t.after(() => child.kill());
    await new Promise((ok, fail) => {
      child.once('message', m => m.ready ? ok() : fail(new Error(m.error)));
      child.once('exit', code => fail(new Error('Service exit ' + code)));
    });
    const { token, baseUrl } = JSON.parse(await readFile(join(root, 'CodexChatHttp', 'connection-' + port + '.json')));
    async function http(path, method = 'GET', body, status = 200, extra = {}) {
      const res = await fetch(baseUrl + path, {
        method, headers: { Authorization: 'Bearer ' + token, 'Content-Type': 'application/json', ...extra },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      const data = await res.json();
      assert.equal(res.status, status, path + ' ' + JSON.stringify(data));
      return data;
    }
    t.after(async () => { await http('/control/stop', 'POST').catch(() => {}); });
    return { http, port };
  }

  const { http: owner } = await start();
  const { http: observer, port: observerPort } = await start();
  await t.test('authentication, browser origin and input validation', async () => {
    await owner('/v1/sessions', 'GET', undefined, 401, { Authorization: 'Bearer wrong' });
    await owner('/v1/sessions', 'GET', undefined, 403, { Origin: 'https://example.invalid' });
    await owner('/v1/sessions', 'POST', { text: '  ' }, 400);
    await owner('/v1/sessions?limit=51', 'GET', undefined, 400);
    await owner('/v1/messages', 'GET', undefined, 409);
    await owner('/v1/current', 'PUT', { sessionId: 'bad-id' }, 400);
    await owner('/v1/sessions/' + randomUUID(), 'GET', undefined, 404);
    await owner('/v1/sessions', 'POST', { text: 'x'.repeat(140000) }, 413);
  });
  const { sessionId } = await owner('/v1/sessions', 'POST', { text: '第一轮' }, 201);
  const target = '/v1/sessions/' + sessionId;
  await owner('/v1/current', 'PUT', { sessionId });
  await t.test('bind, send while busy, consume once and read paginated visible messages', async () => {
    assert.equal((await owner('/v1/current')).session.id, sessionId);
    const receipt = await owner('/v1/messages', 'POST', { text: '第二轮' }, 202);
    assert.equal(receipt.sessionId, sessionId);
    const result = await until(async () => {
      const r = await owner('/v1/messages');
      return r.messages.filter(m => m.role === 'assistant').length >= 2 && r;
    });
    assert.deepEqual(result.messages.filter(m => m.role === 'user').map(m => m.text), ['第一轮','第二轮']);
    assert.equal(result.messages.at(-1).text, '本机测试回复');
    const newer = await owner(target + '/messages?limit=1');
    assert.ok(newer.nextCursor);
    const older = await owner(target + '/messages?limit=1&cursor=' + encodeURIComponent(newer.nextCursor));
    assert.equal(older.messages[0].text, '第一轮');
  });
  await t.test('persist a message to a thread owned by another live client', async () => {
    const body = { text: '跨进程发送', messageId: randomUUID() };
    const received = await observer(target + '/messages', 'POST', body, 202);
    assert.equal(received.status, 'queued');
    const retry = await observer(target + '/messages', 'POST', body, 202);
    assert.equal(retry.queueId, received.queueId);
    await observer(target + '/messages', 'POST', { ...body, text: '相同键冲突' }, 409);
    const queued = await owner(target + '/queue');
    assert.equal(queued.messages.filter(m => m.id === received.messageId && m.text === '跨进程发送').length, 1);
    await owner(target + '/resume', 'POST');
    await until(async () => (await observer(target + '/messages')).messages.filter(m => m.text === '本机测试回复').length === 3);
  });
  await t.test('rename, list, archive, restore, clear selection', async () => {
    await owner(target, 'PATCH', { title: 'HTTP 集成测试' });
    assert.equal((await owner(target)).session.title, 'HTTP 集成测试');
    assert.ok((await owner('/v1/sessions')).sessions.some(s => s.id === sessionId));
    await owner(target + '/archive', 'POST');
    await owner('/v1/current', 'GET', undefined, 409);
    assert.ok((await owner('/v1/sessions?archived=true')).sessions.some(s => s.id === sessionId));
    await owner(target + '/unarchive', 'POST');
    assert.ok((await owner('/v1/sessions')).sessions.some(s => s.id === sessionId));
  });
  await t.test('resume a completed chat and obtain a reply using one HTTP POST', async () => {
    await owner('/control/stop', 'POST');
    await observer('/v1/current', 'PUT', { sessionId });
    const r = await observer('/v1/messages', 'POST', { text: '接续同一会话', delivery: 'resume' }, 202);
    assert.equal(r.sessionId, sessionId);
    assert.equal(r.wakeup, undefined);
    const history = await until(async () => {
      const page = await observer('/v1/messages');
      return page.messages.filter(m => m.text === '本机测试回复').length === 4 && page;
    });
    assert.equal(history.messages.filter(m => m.text === '接续同一会话').length, 1);
  });
  if (coreDll) await t.test('Pudding bus: two Codex adapters register, subscribe, exchange messages and complete delegated work',
    async subtest => busIntegration(subtest, root, env, observer, observerPort));
});
