// 诊断 3：验证「后代溢出撑高滚动容器」假设，并测试 contain/overflow 补丁是否消除塌缩。
import { newPage, delay } from './lib/cdp.mjs';
import { buildLongMarkdown } from './lib/content.mjs';
import { writeFileSync, mkdirSync } from 'node:fs';

const BASE = 'http://localhost';
const OUT = 'temp/perf/out';
mkdirSync(OUT, { recursive: true });
const page = await newPage('about:blank');
const out = {};

const readScroll = () =>
  page.evaluate(`(() => {
    const list = document.querySelector('[data-testid="chat-message-list"]');
    const content = document.querySelector('[data-testid="chat-message-viewport-content"]');
    return {
      scrollTop: list?.scrollTop,
      scrollHeight: list?.scrollHeight,
      clientHeight: list?.clientHeight,
      contentInlineHeight: content?.style?.height ?? '',
      contentOffsetHeight: content?.offsetHeight,
      contentOverflow: content ? getComputedStyle(content).overflowY : null,
      contentContain: content ? getComputedStyle(content).contain : null,
      itemCount: document.querySelectorAll('[data-viewport-item-id]').length,
    };
  })()`);

const toTop = () =>
  page.evaluate(`(() => { const el = document.querySelector('[data-testid="chat-message-list"]'); el.scrollTop = 0; return el.scrollTop; })()`);

async function buildStub() {
  const baseline = await page.evaluate(`(async () => {
    const token = localStorage.getItem('pudding_token');
    const r = await fetch('/api/workspaces/default/agents/default.audit-agent.001/conversation', { headers: { Authorization: 'Bearer ' + token } });
    return await r.json();
  })()`);
  const longText = buildLongMarkdown(50_000);
  const messages = [];
  for (let i = 0; i < 19; i += 1) {
    const isUser = i % 2 === 0;
    messages.push({
      messageId: `contain-msg-${i}`,
      turnId: `contain-turn-${i}`,
      runId: null,
      role: isUser ? 'user' : 'agent',
      sourceKind: isUser ? 'user' : 'agent',
      sourceId: isUser ? 'admin' : 'default.audit-agent.001',
      sourceName: isUser ? 'admin' : '审批审计员',
      messageType: isUser ? 'user_message' : 'agent_output',
      llmRole: isUser ? 'user' : 'agent',
      createdAt: new Date(Date.parse('2026-10-01T02:00:00Z') + i * 60000).toISOString(),
      content: i === 18 ? longText : isUser ? `包含提问 ${i}` : `包含回复 ${i}`,
      status: 'succeeded',
      processItems: [],
      processSummary: null,
      metadata: null,
      contentParts: null,
      turnOutcome: { status: 'succeeded', errorCode: null, errorMessage: null },
    });
  }
  return { ...baseline, messages, activeRun: null, eventCursor: baseline.eventCursor + 50, updatedAt: new Date().toISOString() };
}

try {
  await page.navigate(`${BASE}/admin/user/login`, { waitUntil: 'load' });
  await delay(1000);
  await page.evaluate(`(async () => {
    const res = await fetch('/api/login/account', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username: 'admin', password: 'Admin@123', type: 'account', autoLogin: true }),
    });
    const json = await res.json();
    if (json?.token) localStorage.setItem('pudding_token', json.token);
  })()`);

  const stub = await buildStub();
  await page.send('Fetch.enable', { patterns: [{ urlPattern: '*/conversation*', requestStage: 'Request' }] });
  page.on('Fetch.requestPaused', async (p) => {
    try {
      await page.send('Fetch.fulfillRequest', {
        requestId: p.requestId,
        responseCode: 200,
        responseHeaders: [{ name: 'content-type', value: 'application/json; charset=utf-8' }],
        body: Buffer.from(JSON.stringify(stub), 'utf8').toString('base64'),
      });
    } catch {
      /* ignore */
    }
  });

  // ── 无补丁 ──
  await page.send('Storage.clearDataForOrigin', { origin: BASE, storageTypes: 'indexeddb' }).catch(() => {});
  await page.navigate(`${BASE}/admin/chat?perf=1&workspaceId=default&agentId=default.audit-agent.001`, { waitUntil: 'load' });
  await delay(6000);
  out.baselineMounted = await readScroll();
  await toTop();
  await delay(1500);
  out.baselineAfterTop = await readScroll();

  // ── 补丁：内容容器隔离后代溢出 ──
  await page.evaluate(`(() => {
    const content = document.querySelector('[data-testid="chat-message-viewport-content"]');
    content.style.overflow = 'hidden';
    content.style.contain = 'layout paint';
    return true;
  })()`);
  // 先回到长行可见状态
  await page.evaluate(`(() => {
    const el = document.querySelector('[data-testid="chat-message-list"]');
    const total = el.scrollHeight;
    el.scrollTop = total;
    return el.scrollTop;
  })()`);
  await delay(1500);
  out.patchedMounted = await readScroll();
  await toTop();
  await delay(1500);
  out.patchedAfterTop = await readScroll();

  writeFileSync(`${OUT}/containment.json`, JSON.stringify(out, null, 2), 'utf8');
  console.log(JSON.stringify(out, null, 2));
} catch (err) {
  console.error('FAILED:', err.message);
  process.exitCode = 1;
} finally {
  page.close();
}
