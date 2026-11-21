import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import test from 'node:test';
import { busClient, deliverToChat } from '../Tools/CodexPlugins/plugins/chat-http/pudding-bus-client.mjs';

const baseItem = { messageId: 'stable-bus-id', fromAgentId: 'sender', text: 'unique task marker' };

test('text notification queues one deterministic Codex request and needs no model reply', async () => {
  const sent = [];
  const chat = { async send(id, text) { sent.push({ id, text }); return { status: 'queued' }; } };
  assert.deepEqual(await deliverToChat(chat, baseItem), { accepted: true });
  assert.deepEqual(await deliverToChat(chat, baseItem), { accepted: true });
  assert.equal(sent[0].id, sent[1].id);
  assert.match(sent[0].id, /^[0-9a-f-]{36}$/);
  assert.match(sent[0].text, /通知[\s\S]+unique task marker/);
});

test('a queued delegation waits for the exact user message and gets its final answer', async () => {
  let localText;
  let done = false;
  const chat = {
    async send(_, text) { localText = text; return { status: 'queued' }; },
    async history(cursor) {
      if (!cursor) return {
        messages: [{ role: 'assistant', turnId: 'unrelated', text: 'ignore' }],
        turns: [{ id: 'unrelated', status: 'completed' }],
        nextCursor: 'older',
      };
      return {
        messages: [
          { role: 'user', turnId: 'matching', id: 'rewritten-by-codex', text: localText },
          { role: 'assistant', turnId: 'matching', text: 'working', phase: 'commentary' },
          { role: 'assistant', turnId: 'matching', text: 'the result', phase: 'finalAnswer' },
        ],
        turns: [{ id: 'matching', status: done ? 'completed' : 'inProgress' }],
        nextCursor: null,
      };
    },
  };
  assert.equal(await deliverToChat(chat, { ...baseItem, kind: 'delegation' }), null);
  done = true;
  const actual = await deliverToChat(chat, { ...baseItem, kind: 'delegation' });
  assert.deepEqual(actual.result, { state: 'completed', text: 'the result', reason: null, turnId: 'matching' });
});

test('unknown local receipt leaves the cursor behind until history proves acceptance', async () => {
  let localId;
  let found = false;
  const chat = {
    async send(id) { localId = id; throw Object.assign(new Error('uncertain'), { status: 409 }); },
    async history() {
      return {
        messages: found ? [{ id: localId, role: 'user', turnId: 'matching' }] : [],
        turns: found ? [{ id: 'matching', status: 'failed' }] : [],
        nextCursor: null,
      };
    },
  };
  assert.equal(await deliverToChat(chat, baseItem), null);
  found = true;
  assert.deepEqual((await deliverToChat(chat, { ...baseItem, kind: 'delegation' })).result,
    { turnId: 'matching', state: 'failed', text: null, reason: 'codex.failed' });
});

test('credentials are rejected for plain HTTP to a remote host and are not sent through redirects', async t => {
  const config = { ak: 'A'.repeat(22), sk: 's'.repeat(43), baseUrl: 'http://example.invalid' };
  assert.throws(() => busClient(config), /HTTPS/);
  let redirected = false;
  const server = createServer((request, reply) => {
    if (request.url === '/redirect') {
      redirected = true;
      reply.end();
    } else {
      reply.writeHead(307, { Location: '/redirect' }).end();
    }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => server.close());
  await assert.rejects(busClient({ ...config, baseUrl: 'http://127.0.0.1:' + server.address().port }).register());
  assert.equal(redirected, false);
});
