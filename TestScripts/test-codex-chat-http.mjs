// node --test TestScripts/test-codex-chat-http.mjs (requires the desktop Codex CLI)
import assert from 'node:assert/strict';
import { fork } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { join, resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const entry = fileURLToPath(new URL('../Tools/CodexPlugins/plugins/chat-http/bridge.mjs', import.meta.url));

async function listen(server) {
  await new Promise((ok, fail) => { server.once('error', fail); server.listen(0, '127.0.0.1', ok); });
  return server.address().port;
}

async function until(check) {
  for (let i = 0; i < 100; i++) {
    const value = await check();
    if (value) return value;
    await delay(150);
  }
  assert.fail('Timed out waiting for Codex');
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
    return http;
  }

  const owner = await start();
  const observer = await start();
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
});
