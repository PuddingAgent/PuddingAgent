// 诊断 5：验证「viewport-content 需要 flex-shrink:0」假设。
import { newPage, delay } from './lib/cdp.mjs';
import { buildLongMarkdown } from './lib/content.mjs';
import { writeFileSync, mkdirSync } from 'node:fs';

const BASE = 'http://localhost';
const OUT = 'temp/perf/out';
mkdirSync(OUT, { recursive: true });
const page = await newPage('about:blank');
const out = {};

const read = () =>
  page.evaluate(`(() => {
    const list = document.querySelector('[data-testid="chat-message-list"]');
    const content = document.querySelector('[data-testid="chat-message-viewport-content"]');
    const cs = content ? getComputedStyle(content) : null;
    const rows = Array.from(document.querySelectorAll('[data-viewport-item-id]'));
    const longHost = rows.find((n) => (n.textContent || '').includes('性能诊断长回复样本'));
    return {
      listScrollTop: list?.scrollTop,
      listScrollHeight: list?.scrollHeight,
      listClientHeight: list?.clientHeight,
      contentInlineHeight: content?.style?.height ?? null,
      contentComputedHeight: cs?.height ?? null,
      contentFlexShrink: cs?.flexShrink ?? null,
      contentOffsetHeight: content?.offsetHeight ?? null,
      rowCount: rows.length,
      longFound: !!longHost,
      longHeight: longHost ? Math.round(longHost.getBoundingClientRect().height) : 0,
      maxScrollTop: list ? list.scrollHeight - list.clientHeight : null,
    };
  })()`);

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
      messageId: `fs-msg-${i}`,
      turnId: `fs-turn-${i}`,
      runId: null,
      role: isUser ? 'user' : 'agent',
      sourceKind: isUser ? 'user' : 'agent',
      sourceId: isUser ? 'admin' : 'default.audit-agent.001',
      sourceName: isUser ? 'admin' : '审批审计员',
      messageType: isUser ? 'user_message' : 'agent_output',
      llmRole: isUser ? 'user' : 'agent',
      createdAt: new Date(Date.parse('2026-10-01T02:00:00Z') + i * 60000).toISOString(),
      content: i === 18 ? longText : isUser ? `伸缩提问 ${i}` : `伸缩回复 ${i}`,
      status: 'succeeded',
      processItems: [],
      processSummary: null,
      metadata: null,
      contentParts: null,
      turnOutcome: { status: 'succeeded', errorCode: null, errorMessage: null },
    });
  }
  const stub = { ...baseline, messages, activeRun: null, eventCursor: baseline.eventCursor + 50, updatedAt: new Date().toISOString() };
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
  await page.send('Storage.clearDataForOrigin', { origin: BASE, storageTypes: 'indexeddb' }).catch(() => {});
  await page.navigate(`${BASE}/admin/chat?perf=1&workspaceId=default&agentId=default.audit-agent.001`, { waitUntil: 'load' });
  await delay(6000);

  out.beforePatch = await read();
  // 只加 flex-shrink:0
  await page.evaluate(`(() => {
    const content = document.querySelector('[data-testid="chat-message-viewport-content"]');
    content.style.flexShrink = '0';
    return getComputedStyle(content).flexShrink;
  })()`);
  await delay(800);
  out.afterPatchMounted = await read();
  await page.evaluate(`(() => { const el = document.querySelector('[data-testid="chat-message-list"]'); el.scrollTop = 0; })()`);
  await delay(1500);
  out.afterPatchTop = await read();
  // 尝试用程序化贴底回到长行
  await page.evaluate(`(() => { const el = document.querySelector('[data-testid="chat-message-list"]'); el.scrollTop = el.scrollHeight; })()`);
  await delay(1200);
  out.afterPatchBackToBottom = await read();

  writeFileSync(`${OUT}/flex-shrink.json`, JSON.stringify(out, null, 2), 'utf8');
  console.log(JSON.stringify(out, null, 2));
} catch (err) {
  console.error('FAILED:', err.message);
  process.exitCode = 1;
} finally {
  page.close();
}
